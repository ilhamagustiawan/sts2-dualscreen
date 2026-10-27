// sts2ds — the native side of the second screen.
//
// Godot on Android draws to exactly one window, so the bottom screen is not a
// Godot window at all. The game renders the bottom-screen UI into an offscreen
// SubViewport, reads the pixels back and hands them here; this copies them
// into the Surface of an Android Presentation on the other display.
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
#include <string.h>

#define TAG "STS2DualScreen"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)
#define EXPORT __attribute__((visibility("default")))

#define TOUCH_CAPACITY 256

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
    g_last_hash = 0;
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
        g_last_hash = 0; // repaint on return, the surface may have been cleared
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
