package dev.sts2ds.dualscreen;

/** JNI entry points into libsts2ds (src/main/cpp/sts2ds.c). */
final class DsNative {
    static final int PHASE_DOWN = 0;
    static final int PHASE_MOVE = 1;
    static final int PHASE_UP = 2;
    static final int PHASE_CANCEL = 3;

    private DsNative() {}

    static native void nativeSetSurface(android.view.Surface surface, int width, int height);
    static native void nativeSetVisible(boolean visible);
    static native boolean nativeTouch(int id, int phase, float x, float y);
}
