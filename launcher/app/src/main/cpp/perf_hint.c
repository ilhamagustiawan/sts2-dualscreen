// perf_hint.c — Android's performance hint (ADPF) for the game thread.
//
// The C# side (DsPerfHint) calls ds_perf_start on the Godot main thread once,
// then ds_perf_frame once per frame. Each frame reports how much CPU time the
// thread spent since the last one, against a target of one display refresh,
// so the scheduler raises clocks as soon as a burst of work starts instead of
// after it has already cost frames.
//
// APerformanceHint is API 33; the app's minSdk is 26, so the functions are
// looked up at run time and everything is a no-op where they are missing or
// fail. Thread CPU time, not wall time, is what is reported: the time the
// thread spends blocked on vsync or a buffer is not work, and counting it
// would always read as one full frame.

#include <android/log.h>
#include <dlfcn.h>
#include <errno.h>
#include <stdint.h>
#include <time.h>
#include <unistd.h>

#define DS_EXPORT __attribute__((visibility("default")))
#define TAG "STS2DualScreen"
// Status lines, dev build only (see CMakeLists.txt).
#ifdef DS_VERBOSE_LOG
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#else
#define LOGI(...) ((void)0)
#endif

typedef struct ds_hint_manager ds_hint_manager;
typedef struct ds_hint_session ds_hint_session;

static ds_hint_manager *(*p_get_manager)(void);
static ds_hint_session *(*p_create_session)(ds_hint_manager *, const int32_t *, size_t, int64_t);
static int (*p_update_target)(ds_hint_session *, int64_t);
static int (*p_report_actual)(ds_hint_session *, int64_t);
static void (*p_close_session)(ds_hint_session *);

static ds_hint_session *session;
static int64_t last_cpu_ns;
static int64_t target_ns;
static int failures;

static int64_t thread_cpu_ns(void) {
    struct timespec ts;
    if (clock_gettime(CLOCK_THREAD_CPUTIME_ID, &ts) != 0) return -1;
    return (int64_t)ts.tv_sec * 1000000000LL + ts.tv_nsec;
}

static int resolve(void) {
    void *lib = dlopen("libandroid.so", RTLD_NOW);
    if (!lib) return 0;
    p_get_manager = (ds_hint_manager * (*)(void)) dlsym(lib, "APerformanceHint_getManager");
    p_create_session = (ds_hint_session * (*)(ds_hint_manager *, const int32_t *, size_t, int64_t))
        dlsym(lib, "APerformanceHint_createSession");
    p_update_target = (int (*)(ds_hint_session *, int64_t)) dlsym(lib, "APerformanceHint_updateTargetWorkDuration");
    p_report_actual = (int (*)(ds_hint_session *, int64_t)) dlsym(lib, "APerformanceHint_reportActualWorkDuration");
    p_close_session = (void (*)(ds_hint_session *)) dlsym(lib, "APerformanceHint_closeSession");
    return p_get_manager && p_create_session && p_update_target && p_report_actual && p_close_session;
}

/**
 * Opens a session for the calling thread with a target of [target] ns per
 * frame. 1 if the hint is now active, 0 where the device has none.
 */
DS_EXPORT int ds_perf_start(int64_t target) {
    if (session) return 1;
    if (target <= 0 || !resolve()) {
        LOGI("perf hint: not available on this device");
        return 0;
    }
    ds_hint_manager *manager = p_get_manager();
    if (!manager) {
        LOGI("perf hint: no manager (ADPF not supported)");
        return 0;
    }
    int32_t tid = (int32_t)gettid();
    session = p_create_session(manager, &tid, 1, target);
    if (!session) {
        LOGI("perf hint: could not create a session");
        return 0;
    }
    target_ns = target;
    last_cpu_ns = thread_cpu_ns();
    failures = 0;
    LOGI("perf hint: session for thread %d, target %.2f ms",
                        tid, target / 1e6);
    return 1;
}

/** Changes the per-frame target, for a new refresh rate or frame cap. */
DS_EXPORT void ds_perf_target(int64_t target) {
    if (!session || target <= 0 || target == target_ns) return;
    if (p_update_target(session, target) == 0) target_ns = target;
}

/**
 * Called on the session's thread once per frame: reports the thread's CPU
 * time since the previous call. 0 once the session has been given up.
 */
DS_EXPORT int ds_perf_frame(void) {
    if (!session) return 0;
    int64_t now = thread_cpu_ns();
    if (now < 0) return 1;
    int64_t work = now - last_cpu_ns;
    last_cpu_ns = now;
    if (work <= 0) return 1;
    int result = p_report_actual(session, work);
    if (result == 0) {
        failures = 0;
        return 1;
    }
    // EPIPE: the system closed the session. Anything else repeated: stop
    // asking rather than pay for a failing call every frame.
    if (result == EPIPE || result == -EPIPE || ++failures >= 30) {
        __android_log_print(ANDROID_LOG_WARN, TAG, "perf hint: reporting failed (%d); stopped", result);
        p_close_session(session);
        session = NULL;
        return 0;
    }
    return 1;
}
