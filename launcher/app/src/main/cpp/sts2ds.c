// sts2ds — the native side of the second screen.
//
// Godot on Android draws to exactly one window, so the bottom screen is not a
// Godot window at all. The game renders the bottom-screen UI into an offscreen
// SubViewport, reads the pixels back and hands them here; this copies them
// into the Surface of an Android Presentation on the other display. During a
// run the pages and the dock are captured separately, as regions of one
// buffer posted once a frame, so a moving dock never costs a full-screen copy.
//
// Touches go the other way. The Presentation's view queues them here from the
// UI thread, and the game drains the queue once per frame from its own thread.
// The queue is the C form of SilksongAndroid's SecondaryDisplay.TouchBuffer:
// fixed capacity, drained whole, and reset (with a new generation number)
// whenever the surface changes so a gesture never spans two surfaces.

#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include <jni.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define TAG "STS2DualScreen"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)
#define EXPORT __attribute__((visibility("default")))

#define TOUCH_CAPACITY 256
#define REGION_COUNT 4

typedef struct {
    int32_t id;
    int32_t phase; // 0 down, 1 move, 2 up, 3 cancel
    float x;       // normalised 0..1 across the surface
    float y;
} ds_touch;

static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static ANativeWindow *g_window;
static int32_t g_width, g_height;
static int32_t g_visible;
static int32_t g_generation;
static int32_t g_buffer_w, g_buffer_h;
static uint64_t g_last_hash;
// Region posts (the pages and the dock, captured separately) are composed in
// a full-surface shadow, so whatever the window asks to have repainted can be.
static uint8_t *g_shadow;
static int32_t g_shadow_w, g_shadow_h;
static uint64_t g_region_hash[REGION_COUNT];
static ARect g_pending_rect;
static int32_t g_pending;
static ds_touch g_touches[TOUCH_CAPACITY];
static int32_t g_touch_count;
static int32_t g_touch_overflow;
static int64_t g_frames_posted, g_frames_skipped;

// The generation tells the game to set the bottom screen up again (a new
// surface, or a gesture cut off mid-way). Dropping queued touches alone must
// not bump it: visibility is re-announced on every display change, and each
// bump had the game rebuild the whole bottom screen.
static void reset_touches_locked(void) {
    g_touch_count = 0;
}

static void reset_hashes_locked(void) {
    g_last_hash = 0;
    g_pending = 0;
    memset(g_region_hash, 0, sizeof(g_region_hash));
}

// ── Java side (dev.sts2ds.dualscreen.DsNative) ─────────────────────────────

JNIEXPORT void JNICALL
Java_dev_sts2ds_dualscreen_DsNative_nativeSetSurface(JNIEnv *env, jclass cls, jobject surface,
                                                     jint width, jint height) {
    (void)cls;
    ANativeWindow *next = surface ? ANativeWindow_fromSurface(env, surface) : NULL;
    pthread_mutex_lock(&g_lock);
    if (g_window) ANativeWindow_release(g_window);
    g_window = next;
    g_width = next ? width : 0;
    g_height = next ? height : 0;
    g_buffer_w = g_buffer_h = 0;
    if (!next) {
        free(g_shadow);
        g_shadow = NULL;
        g_shadow_w = g_shadow_h = 0;
    }
    reset_hashes_locked();
    reset_touches_locked();
    g_generation++;
    pthread_mutex_unlock(&g_lock);
    LOGI("surface %s %dx%d", next ? "attached" : "detached", width, height);
}

JNIEXPORT void JNICALL
Java_dev_sts2ds_dualscreen_DsNative_nativeSetVisible(JNIEnv *env, jclass cls, jboolean visible) {
    (void)env; (void)cls;
    pthread_mutex_lock(&g_lock);
    if (g_visible != (visible ? 1 : 0)) {
        g_visible = visible ? 1 : 0;
        reset_hashes_locked(); // repaint on return, the surface may have been cleared
        reset_touches_locked();
    }
    pthread_mutex_unlock(&g_lock);
}

JNIEXPORT jboolean JNICALL
Java_dev_sts2ds_dualscreen_DsNative_nativeTouch(JNIEnv *env, jclass cls, jint id, jint phase,
                                                jfloat x, jfloat y) {
    (void)env; (void)cls;
    jboolean ok = JNI_TRUE;
    pthread_mutex_lock(&g_lock);
    if (g_window && g_visible) {
        if (g_touch_count == TOUCH_CAPACITY) {
            // The game stopped draining (paused, loading). Drop the gesture
            // rather than deliver half of it later.
            reset_touches_locked();
            g_generation++; // the game holds half a gesture; let it start over
            g_touch_overflow++;
            ok = JNI_FALSE;
        } else {
            ds_touch *t = &g_touches[g_touch_count++];
            t->id = id;
            t->phase = phase;
            t->x = x;
            t->y = y;
        }
    }
    pthread_mutex_unlock(&g_lock);
    return ok;
}

// ── Game side (P/Invoke from STS2DualScreen.dll) ───────────────────────────

// out[0]=ready out[1]=width out[2]=height out[3]=generation
EXPORT int32_t ds_state(int32_t *out) {
    pthread_mutex_lock(&g_lock);
    out[0] = (g_window && g_visible && g_width > 0 && g_height > 0) ? 1 : 0;
    out[1] = g_width;
    out[2] = g_height;
    out[3] = g_generation;
    pthread_mutex_unlock(&g_lock);
    return out[0];
}

// Copies up to max queued touches into out and empties the queue.
EXPORT int32_t ds_poll_touches(ds_touch *out, int32_t max) {
    pthread_mutex_lock(&g_lock);
    int32_t n = g_touch_count < max ? g_touch_count : max;
    memcpy(out, g_touches, (size_t)n * sizeof(ds_touch));
    g_touch_count = 0;
    pthread_mutex_unlock(&g_lock);
    return n;
}

// Sampled FNV-1a over every 16th row: enough to notice a changed frame,
// cheap enough to run on every submit.
static uint64_t frame_hash(const uint8_t *rgba, int32_t w, int32_t h, int32_t stride) {
    uint64_t hash = 1469598103934665603ULL;
    for (int32_t y = 0; y < h; y += 16) {
        const uint64_t *row = (const uint64_t *)(rgba + (size_t)y * stride);
        for (int32_t i = 0; i < (w * 4) / 8; i += 3) {
            hash ^= row[i];
            hash *= 1099511628211ULL;
        }
    }
    return hash ^ ((uint64_t)w << 32 | (uint32_t)h);
}

// Posts one RGBA8 frame to the bottom screen. Returns 1 if posted, 0 if
// skipped (unchanged or no surface), negative on error.
EXPORT int32_t ds_submit_frame(const uint8_t *rgba, int32_t w, int32_t h, int32_t stride) {
    if (!rgba || w <= 0 || h <= 0 || stride < w * 4) return -1;
    pthread_mutex_lock(&g_lock);
    int32_t result = 0;
    if (!g_window || !g_visible) goto out;

    uint64_t hash = frame_hash(rgba, w, h, stride);
    if (hash == g_last_hash) {
        g_frames_skipped++;
        goto out;
    }

    if (g_buffer_w != w || g_buffer_h != h) {
        if (ANativeWindow_setBuffersGeometry(g_window, w, h, WINDOW_FORMAT_RGBA_8888) != 0) {
            LOGW("setBuffersGeometry %dx%d failed", w, h);
            result = -2;
            goto out;
        }
        g_buffer_w = w;
        g_buffer_h = h;
    }

    ANativeWindow_Buffer buffer;
    if (ANativeWindow_lock(g_window, &buffer, NULL) != 0) {
        result = -3;
        goto out;
    }
    int32_t rows = buffer.height < h ? buffer.height : h;
    int32_t cols = buffer.width < w ? buffer.width : w;
    uint8_t *dst = (uint8_t *)buffer.bits;
    for (int32_t y = 0; y < rows; y++) {
        memcpy(dst + (size_t)y * buffer.stride * 4, rgba + (size_t)y * stride, (size_t)cols * 4);
    }
    ANativeWindow_unlockAndPost(g_window);
    g_last_hash = hash;
    // The buffers now hold this frame, not the regions.
    memset(g_region_hash, 0, sizeof(g_region_hash));
    g_pending = 0;
    g_frames_posted++;
    result = 1;

out:
    pthread_mutex_unlock(&g_lock);
    return result;
}

// Writes one RGBA8 region of the surface at (x, y), in surface pixels, into
// the shadow, to be posted by the next ds_present. Each region keeps its own
// hash, so an unchanged one costs nothing. Returns 1 if it changed, 0 if
// skipped, negative on error.
EXPORT int32_t ds_submit_region(const uint8_t *rgba, int32_t w, int32_t h, int32_t stride,
                                int32_t x, int32_t y, int32_t region) {
    if (!rgba || w <= 0 || h <= 0 || stride < w * 4 || x < 0 || y < 0
        || region < 0 || region >= REGION_COUNT) return -1;
    pthread_mutex_lock(&g_lock);
    int32_t result = 0;
    if (!g_window || !g_visible || g_width <= 0 || g_height <= 0) goto out;
    if (x >= g_width || y >= g_height) goto out;
    int32_t cw = w < g_width - x ? w : g_width - x;
    int32_t ch = h < g_height - y ? h : g_height - y;

    uint64_t hash = frame_hash(rgba, w, h, stride) ^ ((uint64_t)x << 48 | (uint64_t)y << 32);
    if (hash == g_region_hash[region]) {
        g_frames_skipped++;
        goto out;
    }

    if (g_buffer_w != g_width || g_buffer_h != g_height) {
        if (ANativeWindow_setBuffersGeometry(g_window, g_width, g_height, WINDOW_FORMAT_RGBA_8888) != 0) {
            LOGW("setBuffersGeometry %dx%d failed", g_width, g_height);
            result = -2;
            goto out;
        }
        g_buffer_w = g_width;
        g_buffer_h = g_height;
        g_last_hash = 0;
    }
    if (g_shadow_w != g_width || g_shadow_h != g_height) {
        free(g_shadow);
        g_shadow = calloc((size_t)g_width * g_height, 4);
        if (!g_shadow) {
            g_shadow_w = g_shadow_h = 0;
            result = -4;
            goto out;
        }
        g_shadow_w = g_width;
        g_shadow_h = g_height;
        // The other regions are not in the new shadow yet.
        memset(g_region_hash, 0, sizeof(g_region_hash));
    }
    size_t shadow_stride = (size_t)g_shadow_w * 4;
    for (int32_t row = 0; row < ch; row++) {
        memcpy(g_shadow + (size_t)(y + row) * shadow_stride + (size_t)x * 4,
               rgba + (size_t)row * stride, (size_t)cw * 4);
    }
    if (!g_pending) {
        g_pending_rect = (ARect){x, y, x + cw, y + ch};
        g_pending = 1;
    } else {
        if (x < g_pending_rect.left) g_pending_rect.left = x;
        if (y < g_pending_rect.top) g_pending_rect.top = y;
        if (x + cw > g_pending_rect.right) g_pending_rect.right = x + cw;
        if (y + ch > g_pending_rect.bottom) g_pending_rect.bottom = y + ch;
    }
    g_region_hash[region] = hash;
    result = 1;

out:
    pthread_mutex_unlock(&g_lock);
    return result;
}

// Posts the regions written since the last call, once, as one buffer: two
// posts a frame would outrun the display and block in the buffer queue. The
// window is locked for the changed rect, and whatever rect it asks to have
// drawn (all of it, after a new buffer) is copied from the shadow. Returns 1
// if posted, 0 if nothing was pending, negative on error.
EXPORT int32_t ds_present(void) {
    pthread_mutex_lock(&g_lock);
    int32_t result = 0;
    if (!g_pending) goto out;
    g_pending = 0;
    if (!g_window || !g_visible || !g_shadow || g_buffer_w != g_shadow_w || g_buffer_h != g_shadow_h) goto out;

    ARect dirty = g_pending_rect;
    ANativeWindow_Buffer buffer;
    if (ANativeWindow_lock(g_window, &buffer, &dirty) != 0) {
        result = -3;
        goto out;
    }
    int32_t left = dirty.left > 0 ? dirty.left : 0;
    int32_t top = dirty.top > 0 ? dirty.top : 0;
    int32_t right = dirty.right < buffer.width ? dirty.right : buffer.width;
    int32_t bottom = dirty.bottom < buffer.height ? dirty.bottom : buffer.height;
    if (right > g_shadow_w) right = g_shadow_w;
    if (bottom > g_shadow_h) bottom = g_shadow_h;
    size_t shadow_stride = (size_t)g_shadow_w * 4;
    uint8_t *dst = (uint8_t *)buffer.bits;
    for (int32_t row = top; row < bottom && left < right; row++) {
        memcpy(dst + ((size_t)row * buffer.stride + left) * 4,
               g_shadow + (size_t)row * shadow_stride + (size_t)left * 4, (size_t)(right - left) * 4);
    }
    ANativeWindow_unlockAndPost(g_window);
    g_last_hash = 0;
    g_frames_posted++;
    result = 1;

out:
    pthread_mutex_unlock(&g_lock);
    return result;
}

// out[0]=posted out[1]=skipped out[2]=touch overflows
EXPORT void ds_stats(int64_t *out) {
    pthread_mutex_lock(&g_lock);
    out[0] = g_frames_posted;
    out[1] = g_frames_skipped;
    out[2] = g_touch_overflow;
    pthread_mutex_unlock(&g_lock);
}

EXPORT void ds_log(const char *message) {
    if (message) __android_log_write(ANDROID_LOG_INFO, TAG, message);
}
