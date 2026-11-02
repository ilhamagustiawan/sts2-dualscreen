// sts2ds — the native side of the second screen.
//
// Godot on Android draws to exactly one window, so the bottom screen is not a
// Godot window at all. The game renders the bottom-screen UI into an offscreen
// SubViewport, reads the pixels back and hands them here; this copies them
// into the Surface of an Android Presentation on the other display. During a
// run the pages, the dock and the two corner buttons are captured separately,
// as regions of one buffer posted once a frame, so moving buttons never cost
// a full-screen copy. The corner buttons are overlays: drawn with alpha over
// the pages, which go on under them.
//
// With the present thread on (ds_present_thread), the game's thread only
// copies each frame into a mailbox and returns; the thread does the rest
// (compare, compose, lock the window, copy, post), so neither the copies nor
// a wait for a free window buffer land in the game's frame. Off, all of it
// runs on the game's thread, in the call.
//
// Touches go the other way. The Presentation's view queues them here from the
// UI thread, and the game drains the queue once per frame from its own thread.
// The queue is the C form of SilksongAndroid's SecondaryDisplay.TouchBuffer:
// fixed capacity, drained whole, and reset (with a new generation number)
// whenever the surface changes so a gesture never spans two surfaces.
//
// Two locks. g_lock guards what every thread touches briefly: the window
// pointer and size, visibility, touches, the mailboxes. g_render guards
// drawing into the window and everything drawing keeps (shadow, overlays,
// hashes); it is held across ANativeWindow_lock, which can wait a vsync, so
// the game's thread never takes it while the present thread runs. Order:
// g_render, then g_lock. The window is replaced or released only with both
// held, so whoever holds g_render may use the window it saw.

#include <android/log.h>
#include <android/native_window.h>
#include <android/native_window_jni.h>
#include <android/trace.h>
#include <jni.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define TAG "STS2DualScreen"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)
#define EXPORT __attribute__((visibility("default")))
// Sections for a Perfetto/systrace capture (app atrace); no-ops unless tracing.
#define TRACE_BEGIN(name) ATrace_beginSection(name)
#define TRACE_END() ATrace_endSection()

#define TOUCH_CAPACITY 256
#define REGION_COUNT 4
// Regions from here up are overlays, blended over the ones below.
#define FIRST_OVERLAY 2
// Regions below the overlays are compared with what they last drew in bands
// of this many rows; only the bands that changed are copied and posted.
#define BAND_ROWS 16
// Mailboxes: one per region, and one for whole frames.
#define WHOLE REGION_COUNT
#define SOURCE_COUNT (REGION_COUNT + 1)

typedef struct {
    int32_t id;
    int32_t phase; // 0 down, 1 move, 2 up, 3 cancel
    float x;       // normalised 0..1 across the surface
    float y;
} ds_touch;

// ── g_lock ─────────────────────────────────────────────────────────────────

static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static ANativeWindow *g_window;
static int32_t g_width, g_height;
static int32_t g_visible;
static int32_t g_generation;
// Visibility changed: drawing starts over (hashes reset) at its next turn.
static int32_t g_render_reset;
static ds_touch g_touches[TOUCH_CAPACITY];
static int32_t g_touch_count;
static int32_t g_touch_overflow;

// A frame handed to the present thread. Three slots per source: the game
// fills `write`, the thread draws from `work`, and `ready` (the newest
// complete frame, if `full`) is swapped between them under g_lock, so
// neither ever waits on the other. A frame not yet drawn when a newer one
// arrives is dropped.
typedef struct {
    uint8_t *pixels;
    size_t capacity;
    int32_t w, h, x, y;
} ds_slot;
typedef struct {
    ds_slot slot[3];
    int32_t write, ready, work;
    int32_t full;
    // What drawing its frames gave, for the game: 1 changed since it last
    // asked, else the last result.
    int32_t changed, last;
    // The sampled hash of the frame the game last sent (0: none yet).
    uint64_t sent_hash;
} ds_mailbox;
static ds_mailbox g_mail[SOURCE_COUNT] = {[0 ... SOURCE_COUNT - 1] = {.write = 0, .ready = 1, .work = 2}};
static pthread_cond_t g_wake = PTHREAD_COND_INITIALIZER;
static pthread_t g_thread;
static int32_t g_thread_on, g_thread_quit;
// The game ended a frame (ds_present) or sent a whole one: the thread has work.
static int32_t g_commit, g_whole_due;
#ifdef DS_HOST_TEST
static int64_t g_pass_begin, g_pass_end;
#endif

static int64_t g_frames_posted, g_frames_skipped; // atomics

// ── g_render ───────────────────────────────────────────────────────────────

static pthread_mutex_t g_render = PTHREAD_MUTEX_INITIALIZER;
static int32_t g_buffer_w, g_buffer_h;
static uint64_t g_last_hash;
// Region posts (the pages and the dock, captured separately) are composed in
// a full-surface shadow, so whatever the window asks to have repainted can be.
static uint8_t *g_shadow;
static int32_t g_shadow_w, g_shadow_h;
static uint64_t g_region_hash[REGION_COUNT];
// Where each region below the overlays last drew into the shadow. While it
// is valid, the shadow (or an overlay's under, beneath one) holds exactly
// those pixels, so a new frame needs copying only where it differs.
typedef struct {
    int32_t x, y, w, h;
    int32_t valid;
} ds_drawn;
static ds_drawn g_drawn[REGION_COUNT];
static ARect g_pending_rect;
static int32_t g_pending;
// An overlay keeps its own pixels and what the regions below drew under it,
// so either side can change on its own and the blend is redone from both.
// Pixels are premultiplied, as a transparent viewport renders them.
typedef struct {
    uint8_t *pixels;
    uint8_t *under;
    size_t capacity; // in pixels, of both buffers
    int32_t x, y, w, h;
    int32_t valid;
} ds_overlay;
static ds_overlay g_overlays[REGION_COUNT];

// What a drawing pass draws to, read once under g_lock at its start. The
// window stays valid while g_render is held (see the top).
typedef struct {
    ANativeWindow *window;
    int32_t width, height;
    int32_t visible;
} ds_target;

static void count(int64_t *counter) { __atomic_fetch_add(counter, 1, __ATOMIC_RELAXED); }

// The generation tells the game to set the bottom screen up again (a new
// surface, or a gesture cut off mid-way). Dropping queued touches alone must
// not bump it: visibility is re-announced on every display change, and each
// bump had the game rebuild the whole bottom screen.
static void reset_touches_locked(void) {
    g_touch_count = 0;
}

// These regions post again, in full, with their next frame.
static void forget_regions_locked(int32_t from, int32_t to) {
    for (int32_t i = from; i < to; i++) {
        g_region_hash[i] = 0;
        g_drawn[i].valid = 0;
    }
}

static void reset_hashes_locked(void) {
    g_last_hash = 0;
    g_pending = 0;
    forget_regions_locked(0, REGION_COUNT);
    for (int32_t i = 0; i < REGION_COUNT; i++) g_overlays[i].valid = 0;
}

// The overlay over what is under it, into the shadow, within the clip rect.
static void blend_overlay_locked(const ds_overlay *o, int32_t cl, int32_t ct, int32_t cr, int32_t cb) {
    int32_t left = o->x > cl ? o->x : cl, top = o->y > ct ? o->y : ct;
    int32_t right = o->x + o->w < cr ? o->x + o->w : cr, bottom = o->y + o->h < cb ? o->y + o->h : cb;
    size_t shadow_stride = (size_t)g_shadow_w * 4;
    for (int32_t row = top; row < bottom; row++) {
        const uint8_t *src = o->pixels + ((size_t)(row - o->y) * o->w + (left - o->x)) * 4;
        const uint8_t *und = o->under + ((size_t)(row - o->y) * o->w + (left - o->x)) * 4;
        uint8_t *dst = g_shadow + (size_t)row * shadow_stride + (size_t)left * 4;
        for (int32_t col = left; col < right; col++, src += 4, und += 4, dst += 4) {
            uint32_t keep = 255u - src[3];
            dst[0] = (uint8_t)(src[0] + (und[0] * keep + 127) / 255);
            dst[1] = (uint8_t)(src[1] + (und[1] * keep + 127) / 255);
            dst[2] = (uint8_t)(src[2] + (und[2] * keep + 127) / 255);
            dst[3] = 255;
        }
    }
}

// A region below the overlays was written at this rect: what it drew under
// each overlay is kept, and the overlay laid back over it.
static void restore_overlays_locked(int32_t l, int32_t t, int32_t r, int32_t b) {
    size_t shadow_stride = (size_t)g_shadow_w * 4;
    for (int32_t i = FIRST_OVERLAY; i < REGION_COUNT; i++) {
        ds_overlay *o = &g_overlays[i];
        if (!o->valid) continue;
        int32_t left = o->x > l ? o->x : l, top = o->y > t ? o->y : t;
        int32_t right = o->x + o->w < r ? o->x + o->w : r, bottom = o->y + o->h < b ? o->y + o->h : b;
        if (left >= right || top >= bottom) continue;
        for (int32_t row = top; row < bottom; row++) {
            memcpy(o->under + ((size_t)(row - o->y) * o->w + (left - o->x)) * 4,
                   g_shadow + (size_t)row * shadow_stride + (size_t)left * 4, (size_t)(right - left) * 4);
        }
        blend_overlay_locked(o, left, top, right, bottom);
    }
}

// Takes an overlay's new pixels. Moved or resized, what is under it is not
// known yet: the regions below are asked to post again (their hashes reset).
static int32_t store_overlay_locked(int32_t region, const uint8_t *rgba, int32_t stride,
                                    int32_t x, int32_t y, int32_t cw, int32_t ch) {
    ds_overlay *o = &g_overlays[region];
    size_t need = (size_t)cw * ch;
    if (need > o->capacity) {
        free(o->pixels);
        free(o->under);
        o->pixels = malloc(need * 4);
        o->under = calloc(need, 4);
        o->capacity = o->pixels && o->under ? need : 0;
        if (!o->capacity) {
            o->valid = 0;
            return -4;
        }
        o->valid = 0;
    }
    if (!o->valid || o->x != x || o->y != y || o->w != cw || o->h != ch) {
        o->x = x;
        o->y = y;
        o->w = cw;
        o->h = ch;
        // The best guess until the regions below post again: what is there.
        size_t shadow_stride = (size_t)g_shadow_w * 4;
        for (int32_t row = 0; row < ch; row++)
            memcpy(o->under + (size_t)row * cw * 4, g_shadow + (size_t)(y + row) * shadow_stride + (size_t)x * 4, (size_t)cw * 4);
        forget_regions_locked(0, FIRST_OVERLAY);
        o->valid = 1;
    }
    for (int32_t row = 0; row < ch; row++)
        memcpy(o->pixels + (size_t)row * cw * 4, rgba + (size_t)row * stride, (size_t)cw * 4);
    blend_overlay_locked(o, x, y, x + cw, y + ch);
    return 1;
}

// Whether one row of a region's new pixels (surface row `row`, columns x0 to
// x1) is what the region last drew there: the shadow, except beneath an
// overlay, where the shadow holds the blend and the overlay's under holds
// the region's own pixels. Exact, unlike the sampled hash.
static int32_t row_same_locked(const uint8_t *src, int32_t row, int32_t x0, int32_t x1) {
    const uint8_t *shadow = g_shadow + (size_t)row * g_shadow_w * 4;
    int32_t col = x0;
    while (col < x1) {
        // The first overlay on this row from col on.
        const ds_overlay *next = NULL;
        int32_t next_left = x1;
        for (int32_t i = FIRST_OVERLAY; i < REGION_COUNT; i++) {
            const ds_overlay *o = &g_overlays[i];
            if (!o->valid || row < o->y || row >= o->y + o->h || o->x + o->w <= col || o->x >= x1) continue;
            int32_t left = o->x > col ? o->x : col;
            if (left < next_left) {
                next_left = left;
                next = o;
            }
        }
        if (next_left > col && memcmp(src + (size_t)(col - x0) * 4, shadow + (size_t)col * 4, (size_t)(next_left - col) * 4) != 0)
            return 0;
        if (!next) return 1;
        int32_t right = next->x + next->w < x1 ? next->x + next->w : x1;
        const uint8_t *under = next->under + ((size_t)(row - next->y) * next->w + (next_left - next->x)) * 4;
        if (memcmp(src + (size_t)(next_left - x0) * 4, under, (size_t)(right - next_left) * 4) != 0) return 0;
        col = right;
    }
    return 1;
}

// A region below the overlays into the shadow, band by band: a band whose
// rows all match what the region last drew is left alone. Returns the rows
// that changed as [*top, *bottom), in surface rows; none if *top == *bottom.
static void compose_region_locked(int32_t region, const uint8_t *rgba, int32_t stride,
                                  int32_t x, int32_t y, int32_t cw, int32_t ch, int32_t *top, int32_t *bottom) {
    ds_drawn *d = &g_drawn[region];
    int32_t full = !d->valid || d->x != x || d->y != y || d->w != cw || d->h != ch;
    size_t shadow_stride = (size_t)g_shadow_w * 4;
    *top = *bottom = y;
    int32_t any = 0;
    for (int32_t band = 0; band < ch; band += BAND_ROWS) {
        int32_t end = band + BAND_ROWS < ch ? band + BAND_ROWS : ch;
        int32_t changed = full;
        for (int32_t row = band; !changed && row < end; row++)
            changed = !row_same_locked(rgba + (size_t)row * stride, y + row, x, x + cw);
        if (!changed) continue;
        for (int32_t row = band; row < end; row++) {
            memcpy(g_shadow + (size_t)(y + row) * shadow_stride + (size_t)x * 4,
                   rgba + (size_t)row * stride, (size_t)cw * 4);
        }
        restore_overlays_locked(x, y + band, x + cw, y + end);
        if (!any) *top = y + band;
        *bottom = y + end;
        any = 1;
    }
    *d = (ds_drawn){x, y, cw, ch, 1};
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


// Starts a drawing pass (g_render held): what to draw to, and a reset if
// visibility changed since the last pass.
static void begin_render(ds_target *t) {
    pthread_mutex_lock(&g_lock);
    if (g_render_reset) {
        g_render_reset = 0;
        reset_hashes_locked();
    }
    t->window = g_window;
    t->width = g_width;
    t->height = g_height;
    t->visible = g_visible;
    pthread_mutex_unlock(&g_lock);
}

// One whole frame straight to the window (g_render held). 1 posted, 0
// skipped (unchanged or no surface), negative on error.
static int32_t render_frame(const ds_target *t, const uint8_t *rgba, int32_t w, int32_t h, int32_t stride) {
    if (!t->window || !t->visible) return 0;

    TRACE_BEGIN("ds hash");
    uint64_t hash = frame_hash(rgba, w, h, stride);
    TRACE_END();
    if (hash == g_last_hash) {
        count(&g_frames_skipped);
        return 0;
    }

    if (g_buffer_w != w || g_buffer_h != h) {
        if (ANativeWindow_setBuffersGeometry(t->window, w, h, WINDOW_FORMAT_RGBA_8888) != 0) {
            LOGW("setBuffersGeometry %dx%d failed", w, h);
            return -2;
        }
        g_buffer_w = w;
        g_buffer_h = h;
    }

    ANativeWindow_Buffer buffer;
    TRACE_BEGIN("ds lock");
    int32_t locked = ANativeWindow_lock(t->window, &buffer, NULL);
    TRACE_END();
    if (locked != 0) return -3;
    TRACE_BEGIN("ds copy");
    int32_t rows = buffer.height < h ? buffer.height : h;
    int32_t cols = buffer.width < w ? buffer.width : w;
    uint8_t *dst = (uint8_t *)buffer.bits;
    for (int32_t y = 0; y < rows; y++) {
        memcpy(dst + (size_t)y * buffer.stride * 4, rgba + (size_t)y * stride, (size_t)cols * 4);
    }
    TRACE_END();
    TRACE_BEGIN("ds post");
    ANativeWindow_unlockAndPost(t->window);
    TRACE_END();
    g_last_hash = hash;
    // The buffers now hold this frame, not the regions.
    forget_regions_locked(0, REGION_COUNT);
    g_pending = 0;
    count(&g_frames_posted);
    return 1;
}

// One region into the shadow, for the next present (g_render held). 1 if it
// changed, 0 if skipped, negative on error.
static int32_t render_region(const ds_target *t, const uint8_t *rgba, int32_t w, int32_t h, int32_t stride,
                             int32_t x, int32_t y, int32_t region) {
    if (!t->window || !t->visible || t->width <= 0 || t->height <= 0) return 0;
    if (x >= t->width || y >= t->height) return 0;
    int32_t cw = w < t->width - x ? w : t->width - x;
    int32_t ch = h < t->height - y ? h : t->height - y;

    TRACE_BEGIN("ds hash");
    uint64_t hash = frame_hash(rgba, w, h, stride) ^ ((uint64_t)x << 48 | (uint64_t)y << 32);
    TRACE_END();
    if (hash == g_region_hash[region]) {
        count(&g_frames_skipped);
        return 0;
    }

    if (g_buffer_w != t->width || g_buffer_h != t->height) {
        if (ANativeWindow_setBuffersGeometry(t->window, t->width, t->height, WINDOW_FORMAT_RGBA_8888) != 0) {
            LOGW("setBuffersGeometry %dx%d failed", t->width, t->height);
            return -2;
        }
        g_buffer_w = t->width;
        g_buffer_h = t->height;
        g_last_hash = 0;
    }
    if (g_shadow_w != t->width || g_shadow_h != t->height) {
        free(g_shadow);
        g_shadow = calloc((size_t)t->width * t->height, 4);
        if (!g_shadow) {
            g_shadow_w = g_shadow_h = 0;
            return -4;
        }
        g_shadow_w = t->width;
        g_shadow_h = t->height;
        // The other regions are not in the new shadow yet.
        forget_regions_locked(0, REGION_COUNT);
        for (int32_t i = 0; i < REGION_COUNT; i++) g_overlays[i].valid = 0;
    }
    TRACE_BEGIN("ds compose");
    int32_t result = 1;
    int32_t top = y, bottom = y + ch;
    if (region >= FIRST_OVERLAY)
        result = store_overlay_locked(region, rgba, stride, x, y, cw, ch);
    else
        compose_region_locked(region, rgba, stride, x, y, cw, ch, &top, &bottom);
    TRACE_END();
    if (result != 1) return result;
    g_region_hash[region] = hash;
    if (top == bottom) {
        // The sampled hash moved, but not one row did.
        count(&g_frames_skipped);
        return 0;
    }
    if (!g_pending) {
        g_pending_rect = (ARect){x, top, x + cw, bottom};
        g_pending = 1;
    } else {
        if (x < g_pending_rect.left) g_pending_rect.left = x;
        if (top < g_pending_rect.top) g_pending_rect.top = top;
        if (x + cw > g_pending_rect.right) g_pending_rect.right = x + cw;
        if (bottom > g_pending_rect.bottom) g_pending_rect.bottom = bottom;
    }
    return 1;
}

// Posts the regions written since the last present, once, as one buffer:
// two posts a frame would outrun the display and block in the buffer queue.
// The window is locked for the changed rect, and whatever rect it asks to
// have drawn (all of it, after a new buffer) is copied from the shadow
// (g_render held). 1 if posted, 0 if nothing was pending, negative on error.
static int32_t render_present(const ds_target *t) {
    if (!g_pending) return 0;
    if (!t->window || !t->visible || !g_shadow || g_buffer_w != g_shadow_w || g_buffer_h != g_shadow_h) {
        g_pending = 0;
        return 0;
    }

    ARect dirty = g_pending_rect;
    ANativeWindow_Buffer buffer;
    TRACE_BEGIN("ds lock");
    int32_t locked = ANativeWindow_lock(t->window, &buffer, &dirty);
    TRACE_END();
    // Kept pending on failure: the shadow has it, the window not yet; next time.
    if (locked != 0) return -3;
    g_pending = 0;
    TRACE_BEGIN("ds copy");
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
    TRACE_END();
    TRACE_BEGIN("ds post");
    ANativeWindow_unlockAndPost(t->window);
    TRACE_END();
    g_last_hash = 0;
    count(&g_frames_posted);
    return 1;
}

// ── present thread ─────────────────────────────────────────────────────────

static void note_result_locked(ds_mailbox *m, int32_t result) {
    m->last = result;
    if (result == 1) m->changed = 1;
}

// Draws what the game sent, newest frame per source: whole frames as they
// come, regions when the game ends a frame (so they post together).
static void *present_main(void *arg) {
    (void)arg;
#ifdef __ANDROID__
    pthread_setname_np(pthread_self(), "sts2ds-present");
#endif
    int32_t take[SOURCE_COUNT];
    pthread_mutex_lock(&g_lock);
    for (;;) {
        while (!g_thread_quit && !g_commit && !g_whole_due) pthread_cond_wait(&g_wake, &g_lock);
        if (g_thread_quit) break;
        int32_t commit = g_commit;
        g_commit = g_whole_due = 0;
#ifdef DS_HOST_TEST
        g_pass_begin++;
#endif
        pthread_mutex_unlock(&g_lock);

        pthread_mutex_lock(&g_render);
        TRACE_BEGIN("ds present thread");
        ds_target t;
        begin_render(&t);
        pthread_mutex_lock(&g_lock);
        for (int32_t s = 0; s < SOURCE_COUNT; s++) {
            ds_mailbox *m = &g_mail[s];
            take[s] = m->full && (s == WHOLE || commit);
            if (!take[s]) continue;
            int32_t ready = m->ready;
            m->ready = m->work;
            m->work = ready;
            m->full = 0;
        }
        pthread_mutex_unlock(&g_lock);
        int32_t results[SOURCE_COUNT] = {0};
        // The whole frame first: it posts at once and has the regions
        // repaint. Then the overlays: one that moved has the regions under
        // it repaint, and they come right after.
        _Static_assert(REGION_COUNT == 4 && FIRST_OVERLAY == 2, "order lists every source");
        static const int32_t order[SOURCE_COUNT] = {WHOLE, 2, 3, 0, 1};
        for (int32_t s = 0; s < SOURCE_COUNT; s++) {
            int32_t source = order[s];
            if (!take[source]) continue;
            const ds_slot *f = &g_mail[source].slot[g_mail[source].work];
            results[source] = source == WHOLE
                ? render_frame(&t, f->pixels, f->w, f->h, f->w * 4)
                : render_region(&t, f->pixels, f->w, f->h, f->w * 4, f->x, f->y, source);
        }
        if (commit) render_present(&t);
        TRACE_END();
        pthread_mutex_unlock(&g_render);

        pthread_mutex_lock(&g_lock);
        for (int32_t s = 0; s < SOURCE_COUNT; s++)
            if (take[s]) note_result_locked(&g_mail[s], results[s]);
#ifdef DS_HOST_TEST
        g_pass_end++;
#endif
    }
    pthread_mutex_unlock(&g_lock);
    return NULL;
}

// The game's side of a mailbox: copies the frame into its own slot (no lock
// held: only the game touches it) and hands it over. Returns, for the game's
// pacing, 1 if this frame's sampled hash differs from the last one sent, or
// if drawing the earlier frames from this source changed anything since the
// last call; else the last result. 0 if there's no surface to draw to.
static int32_t send_frame(int32_t source, const uint8_t *rgba, int32_t w, int32_t h, int32_t stride,
                          int32_t x, int32_t y) {
    ds_mailbox *m = &g_mail[source];
    pthread_mutex_lock(&g_lock);
    int32_t live = g_window && g_visible;
    int32_t result = m->changed ? 1 : m->last;
    m->changed = 0;
    ds_slot *slot = &m->slot[m->write];
    pthread_mutex_unlock(&g_lock);
    if (!live) return 0;

    // What the thread reports is about older frames. A change nobody
    // announced (found by an idle check) would wait for the next capture to
    // be noticed, which for an idle part is a second away. The sampled hash
    // tells now; the thread still compares exactly, band by band.
    TRACE_BEGIN("ds hash");
    uint64_t hash = frame_hash(rgba, w, h, stride) ^ ((uint64_t)x << 48 | (uint64_t)y << 32);
    TRACE_END();

    size_t need = (size_t)w * h * 4;
    if (need > slot->capacity) {
        free(slot->pixels);
        slot->pixels = malloc(need);
        slot->capacity = slot->pixels ? need : 0;
        if (!slot->pixels) return -4;
    }
    TRACE_BEGIN("ds slot copy");
    if (stride == w * 4) {
        memcpy(slot->pixels, rgba, need);
    } else {
        for (int32_t row = 0; row < h; row++)
            memcpy(slot->pixels + (size_t)row * w * 4, rgba + (size_t)row * stride, (size_t)w * 4);
    }
    TRACE_END();
    slot->w = w;
    slot->h = h;
    slot->x = x;
    slot->y = y;

    pthread_mutex_lock(&g_lock);
    if (hash != m->sent_hash) {
        m->sent_hash = hash;
        result = 1;
    }
    int32_t ready = m->ready;
    m->ready = m->write;
    m->write = ready;
    m->full = 1;
    if (source == WHOLE) {
        g_whole_due = 1;
        pthread_cond_signal(&g_wake);
    }
    pthread_mutex_unlock(&g_lock);
    return result;
}

static int32_t thread_on(void) {
    pthread_mutex_lock(&g_lock);
    int32_t on = g_thread_on;
    pthread_mutex_unlock(&g_lock);
    return on;
}

// ── Java side (dev.sts2ds.dualscreen.DsNative) ─────────────────────────────

// Called from SurfaceHolder callbacks. Holding g_render, it waits for a
// drawing pass in progress (the present thread's included) to finish, so
// once it returns nothing uses the old window: surfaceDestroyed may let the
// surface go.
JNIEXPORT void JNICALL
Java_dev_sts2ds_dualscreen_DsNative_nativeSetSurface(JNIEnv *env, jclass cls, jobject surface,
                                                     jint width, jint height) {
    (void)cls;
    ANativeWindow *next = surface ? ANativeWindow_fromSurface(env, surface) : NULL;
    pthread_mutex_lock(&g_render);
    pthread_mutex_lock(&g_lock);
    ANativeWindow *old = g_window;
    g_window = next;
    g_width = next ? width : 0;
    g_height = next ? height : 0;
    reset_touches_locked();
    // Frames waiting to be drawn were for the old surface.
    for (int32_t s = 0; s < SOURCE_COUNT; s++) {
        g_mail[s].full = 0;
        g_mail[s].sent_hash = 0;
    }
    g_render_reset = 0;
    g_generation++;
    pthread_mutex_unlock(&g_lock);
    g_buffer_w = g_buffer_h = 0;
    if (!next) {
        free(g_shadow);
        g_shadow = NULL;
        g_shadow_w = g_shadow_h = 0;
    }
    reset_hashes_locked();
    if (old) ANativeWindow_release(old);
    pthread_mutex_unlock(&g_render);
    LOGI("surface %s %dx%d", next ? "attached" : "detached", width, height);
}

// Not under g_render: a hide must not wait for a post. Drawing starts over
// at its next pass instead (g_render_reset).
JNIEXPORT void JNICALL
Java_dev_sts2ds_dualscreen_DsNative_nativeSetVisible(JNIEnv *env, jclass cls, jboolean visible) {
    (void)env; (void)cls;
    pthread_mutex_lock(&g_lock);
    if (g_visible != (visible ? 1 : 0)) {
        g_visible = visible ? 1 : 0;
        g_render_reset = 1; // repaint on return, the surface may have been cleared
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

// Starts (on != 0) or stops the present thread. Returns 1 if it runs.
EXPORT int32_t ds_present_thread(int32_t on) {
    pthread_mutex_lock(&g_lock);
    int32_t running = g_thread_on;
    pthread_mutex_unlock(&g_lock);
    if (on && !running) {
        pthread_mutex_lock(&g_lock);
        g_thread_quit = 0;
        pthread_mutex_unlock(&g_lock);
        if (pthread_create(&g_thread, NULL, present_main, NULL) != 0) {
            LOGW("present thread failed to start; drawing on the game's thread");
            return 0;
        }
        pthread_mutex_lock(&g_lock);
        g_thread_on = 1;
        pthread_mutex_unlock(&g_lock);
        LOGI("present thread started");
        return 1;
    }
    if (!on && running) {
        pthread_mutex_lock(&g_lock);
        g_thread_on = 0;
        g_thread_quit = 1;
        pthread_cond_signal(&g_wake);
        pthread_mutex_unlock(&g_lock);
        pthread_join(g_thread, NULL);
        // Frames it hadn't drawn are dropped; the game's next ones repaint.
        pthread_mutex_lock(&g_lock);
        for (int32_t s = 0; s < SOURCE_COUNT; s++) g_mail[s].full = 0;
        pthread_mutex_unlock(&g_lock);
        LOGI("present thread stopped");
        return 0;
    }
    return running;
}

// Posts one RGBA8 frame to the bottom screen. Returns 1 if posted, 0 if
// skipped (unchanged or no surface), negative on error. With the present
// thread on, it is posted soon after, and the result is send_frame's.
EXPORT int32_t ds_submit_frame(const uint8_t *rgba, int32_t w, int32_t h, int32_t stride) {
    if (!rgba || w <= 0 || h <= 0 || stride < w * 4) return -1;
    TRACE_BEGIN("ds submit frame");
    int32_t result;
    if (thread_on()) {
        result = send_frame(WHOLE, rgba, w, h, stride, 0, 0);
    } else {
        pthread_mutex_lock(&g_render);
        ds_target t;
        begin_render(&t);
        result = render_frame(&t, rgba, w, h, stride);
        pthread_mutex_unlock(&g_render);
    }
    TRACE_END();
    return result;
}

// Writes one RGBA8 region of the surface at (x, y), in surface pixels, into
// the shadow, to be posted by the next ds_present. Each region keeps its own
// hash, so an unchanged one costs nothing, and a changed one below the
// overlays is copied and posted only in the bands of rows that changed (a
// card moving in a pile is a small part of the page). Regions FIRST_OVERLAY
// and up are premultiplied overlays, blended over the rest. Returns 1 if it
// changed, 0 if skipped, negative on error; with the present thread on, the
// result is send_frame's.
EXPORT int32_t ds_submit_region(const uint8_t *rgba, int32_t w, int32_t h, int32_t stride,
                                int32_t x, int32_t y, int32_t region) {
    if (!rgba || w <= 0 || h <= 0 || stride < w * 4 || x < 0 || y < 0
        || region < 0 || region >= REGION_COUNT) return -1;
    TRACE_BEGIN("ds submit region");
    int32_t result;
    if (thread_on()) {
        result = send_frame(region, rgba, w, h, stride, x, y);
    } else {
        pthread_mutex_lock(&g_render);
        ds_target t;
        begin_render(&t);
        result = render_region(&t, rgba, w, h, stride, x, y, region);
        pthread_mutex_unlock(&g_render);
    }
    TRACE_END();
    return result;
}

// Ends the game's frame: the regions written since the last call are posted
// as one buffer (by the present thread, if on, soon after). Returns 1 if
// posted, 0 if nothing was pending or it is left to the thread, negative on
// error.
EXPORT int32_t ds_present(void) {
    TRACE_BEGIN("ds present");
    int32_t result = 0;
    pthread_mutex_lock(&g_lock);
    int32_t threaded = g_thread_on;
    if (threaded) {
        g_commit = 1;
        pthread_cond_signal(&g_wake);
    }
    pthread_mutex_unlock(&g_lock);
    if (!threaded) {
        pthread_mutex_lock(&g_render);
        ds_target t;
        begin_render(&t);
        result = render_present(&t);
        pthread_mutex_unlock(&g_render);
    }
    TRACE_END();
    return result;
}

// out[0]=posted out[1]=skipped out[2]=touch overflows
EXPORT void ds_stats(int64_t *out) {
    out[0] = __atomic_load_n(&g_frames_posted, __ATOMIC_RELAXED);
    out[1] = __atomic_load_n(&g_frames_skipped, __ATOMIC_RELAXED);
    pthread_mutex_lock(&g_lock);
    out[2] = g_touch_overflow;
    pthread_mutex_unlock(&g_lock);
}

// For the game's own trace sections (DsBottomScreen.TraceBegin/TraceEnd).
// Begin and end must pair up on one thread.
EXPORT void ds_trace_begin(const char *name) {
    if (name) TRACE_BEGIN(name);
}

EXPORT void ds_trace_end(void) {
    TRACE_END();
}

EXPORT void ds_log(const char *message) {
    if (message) __android_log_write(ANDROID_LOG_INFO, TAG, message);
}
