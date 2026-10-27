using System;
using System.Runtime.InteropServices;

namespace STS2DualScreen;

// P/Invoke into libsts2ds.so (launcher/app/src/main/cpp/sts2ds.c). Java loads
// the library before the engine starts; these calls bind to that copy.
internal static unsafe class DsNative
{
    private const string Lib = "libsts2ds.so";

    public const int PhaseDown = 0;
    public const int PhaseMove = 1;
    public const int PhaseUp = 2;
    public const int PhaseCancel = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Touch
    {
        public int Id;
        public int Phase;
        public float X;
        public float Y;
    }

    [DllImport(Lib, EntryPoint = "ds_state")]
    private static extern int ds_state(int* outState);

    [DllImport(Lib, EntryPoint = "ds_poll_touches")]
    public static extern int PollTouches(Touch* touches, int max);

    [DllImport(Lib, EntryPoint = "ds_submit_frame")]
    public static extern int SubmitFrame(byte* rgba, int width, int height, int stride);

    [DllImport(Lib, EntryPoint = "ds_stats")]
    private static extern void ds_stats(long* outStats);

    [DllImport(Lib, EntryPoint = "ds_log")]
    private static extern void ds_log([MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    public static void Log(string message) => ds_log(message);

    /// <summary>Ready, width, height, generation of the bottom-screen surface.</summary>
    public static (bool Ready, int Width, int Height, int Generation) State()
    {
        int* s = stackalloc int[4];
        ds_state(s);
        return (s[0] != 0, s[1], s[2], s[3]);
    }

    public static (long Posted, long Skipped, long Overflows) Stats()
    {
        long* s = stackalloc long[3];
        ds_stats(s);
        return (s[0], s[1], s[2]);
    }
}
