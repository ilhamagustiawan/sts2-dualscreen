using System;
using Godot;
using Godot.NativeInterop;

namespace STS2DualScreen;

// One captured part of the bottom screen: a SubViewport that renders only when
// a capture is due, is copied back asynchronously and is posted to libsts2ds,
// either as the whole surface or as a region of it.
//
// Pacing: at the active rate while the part is changing (MarkChanged, or the
// last capture differed), at the ambient rate while something on it loops
// forever, and otherwise at IdleIntervalUsec, if idle checks are on, to notice
// changes nobody announced. A MarkChanged window also owes one capture after
// it ends, so the frame where the motion comes to rest is always taken.
internal sealed class DsCapture
{
    private const ulong IdleIntervalUsec = 1_000_000;
    private const int MaxInFlight = 3;
    // A changed capture keeps the active rate this much longer, until it has
    // changed on every capture for MaxReArmUsec.
    private const double ReArmSeconds = 0.25;
    private const ulong MaxReArmUsec = 3_000_000;
    // An owed capture kept waiting this long (by an urgent page) is let go first.
    private const ulong StarveUsec = 250_000;

    public readonly string Name;
    public readonly SubViewport Viewport;

    // Async readback through the RenderingDevice, so the game thread never
    // waits on the GPU. Null on the Compatibility renderer: read back inline.
    private readonly RenderingDevice _rd;

    // Each readback in flight has a slot, answered through that slot's own
    // callable, so what a frame was requested for (target epoch, size) is
    // its own and never a later request's, in whatever order the answers
    // come. With one shared record, a frame requested before a Retarget
    // could be taken under the new epoch.
    private struct Request
    {
        public bool Busy;
        public int Epoch, Width, Height;
    }

    private readonly Request[] _requests = new Request[MaxInFlight];
    private readonly Callable[] _onFrameData = new Callable[MaxInFlight];
    private readonly string _traceName;

    private ulong _activeUntilUsec;
    private ulong _nextFrameUsec;
    private bool _readbackDue;
    private bool _ambient;
    private int _inFlight;
    private ulong _lastRequestUsec;
    private ulong _dueIntervalUsec;

    // A frame that came back while another part's went out this game frame,
    // held for the next one (see TakeFrame).
    private Variant _pending;
    private bool _hasPending;
    private int _pendingWidth, _pendingHeight, _pendingEpoch;
    private long _pendingSeq;
    private int _deferredSubmits;
    // Since when every capture differed from the last; 0 after an unchanged
    // one, and while nothing tells (looping, or idle with no idle checks).
    private ulong _changedSinceUsec;
    // MarkChanged since the last capture started after its window ended: a
    // window as long as its tween can end a frame before the tween's last
    // step is drawn, so one capture after it is owed (see Due).
    private bool _owed;
    // Since when Due has said yes without a Start; 0 if not waiting.
    private ulong _dueFromUsec;

    // Where frames go. A request keeps the target it was made for; frames
    // from an older epoch (resized, moved, paused) are dropped.
    private bool _whole = true;
    private int _x, _y, _region;
    private int _epoch;

    // The engine's own timing of this viewport's render (CPU and GPU), read
    // for a few frames after each render: the timestamps come back late.
    private const int MeasureFrames = 4;
    private int _measureFrames;
    private double _renderCpuTotal, _renderGpuTotal, _renderCpuMax, _renderGpuMax;
    private int _renderSamples;

    private double _readbackMsTotal;
    private double _submitMsTotal;
    private int _captures;
    private int _submits;
    private int _posted;

    public DsCapture(string name, SubViewport viewport, RenderingDevice rd)
    {
        Name = name;
        Viewport = viewport;
        _rd = rd;
        _traceName = $"ds frame data {name}";
        // Variant, not byte[]: the frame arrives as Godot's own refcounted
        // PackedByteArray and is read in place. A byte[] would be a fresh
        // 5 MB managed array per capture, and that churn forces full GCs.
        for (var i = 0; i < MaxInFlight; i++)
        {
            var slot = i;
            _onFrameData[i] = Callable.From<Variant>(data => OnFrameData(slot, data));
        }
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
    }

    /// <summary>Frames are posted (false: dropped, as while the surface is gone).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Post frames as the whole surface (the compositor scales them to fit).</summary>
    public void TargetWhole()
    {
        if (_whole)
            return;
        _whole = true;
        Retarget();
    }

    /// <summary>Post frames as a region at (x, y) in surface pixels.</summary>
    public void TargetRegion(int x, int y, int region)
    {
        if (!_whole && _x == x && _y == y && _region == region)
            return;
        _whole = false;
        _x = x;
        _y = y;
        _region = region;
        Retarget();
    }

    /// <summary>Frames already requested are for an old size or place: drop them.</summary>
    public void Retarget()
    {
        _epoch++;
        _readbackDue = false;
        DropPending();
        MarkChanged();
    }

    /// <summary>Capture at the active rate for a while: something here is moving.</summary>
    public void MarkChanged(double seconds = 1.0)
    {
        var now = Time.GetTicksUsec();
        if (now >= _activeUntilUsec)
            _nextFrameUsec = 0;
        _activeUntilUsec = Math.Max(_activeUntilUsec, now + (ulong)(Math.Max(seconds, 0.05) * 1_000_000));
        _owed = true;
    }

    /// <summary>
    /// A capture still owed by a MarkChanged has waited long enough (or its
    /// window is over): it should have this frame's render, even over an
    /// urgent page, or a page scrolled without pause would freeze it mid-way.
    /// </summary>
    public bool Starved(ulong now) =>
        _owed && _dueFromUsec != 0 && (now >= _activeUntilUsec || now - _dueFromUsec >= StarveUsec);

    /// <summary>
    /// Once per game frame, before Due: asks for the copy of what the
    /// viewport drew last frame.
    /// </summary>
    public void Tick(ulong now)
    {
        if (_measureFrames > 0)
        {
            _measureFrames--;
            SampleRenderTime();
        }
        // Last frame drew the viewport; its pixels are ready to copy.
        if (_readbackDue)
        {
            _readbackDue = false;
            RequestReadback(now);
        }
        if (_inFlight > 0 && now - _lastRequestUsec > 2_000_000)
        {
            DsLog.Info($"{Name} readback: {_inFlight} request(s) never answered; resetting");
            _inFlight = 0;
            // An answer that still comes is dropped (its slot is no longer busy).
            Array.Clear(_requests);
        }
    }

    /// <summary>
    /// A new frame is due now; dueSince says since when (the longest waiting
    /// goes first). ambientFps &gt; 0: something here loops forever, so changed
    /// frames are expected and don't raise the rate above it.
    /// </summary>
    public bool Due(ulong now, int activeFps, int ambientFps, bool idleChecks, out ulong dueSince)
    {
        _ambient = ambientFps > 0;
        // A loop changes every frame by design: that says nothing about
        // whether something else keeps changing (Submit's re-arm cap).
        if (_ambient)
            _changedSinceUsec = 0;
        dueSince = _nextFrameUsec;
        if (!Enabled)
        {
            _dueFromUsec = 0;
            return false;
        }
        if (now < _nextFrameUsec || _inFlight >= MaxInFlight)
            return false;
        if (now < _activeUntilUsec)
        {
            _dueIntervalUsec = (ulong)(1_000_000 / Math.Max(1, activeFps));
        }
        else if (ambientFps > 0)
        {
            _dueIntervalUsec = (ulong)(1_000_000 / ambientFps);
        }
        else if (_owed || idleChecks)
        {
            // The owed capture, then (with idle checks) the idle rate.
            _dueIntervalUsec = IdleIntervalUsec;
        }
        else
        {
            // Idle, and no capture comes until someone says so: a streak of
            // changed captures ends here, or the next motion after a long
            // one would never re-arm.
            _changedSinceUsec = 0;
            _dueFromUsec = 0;
            return false;
        }
        if (_dueFromUsec == 0)
            _dueFromUsec = now;
        return true;
    }

    /// <summary>Renders the viewport this frame (after Due said so); it is copied back next frame.</summary>
    public void Start(ulong now)
    {
        _nextFrameUsec = now + _dueIntervalUsec;
        _dueFromUsec = 0;
        // Drawn after the window's end: what it owed is paid.
        if (now >= _activeUntilUsec)
            _owed = false;
        RenderingServer.ViewportSetUpdateMode(Viewport.GetViewportRid(), RenderingServer.ViewportUpdateMode.Once);
        _readbackDue = true;
        _measureFrames = MeasureFrames;
    }

    private void SampleRenderTime()
    {
        var rid = Viewport.GetViewportRid();
        var cpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid);
        var gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
        _renderCpuTotal += cpu;
        _renderGpuTotal += gpu;
        _renderCpuMax = Math.Max(_renderCpuMax, cpu);
        _renderGpuMax = Math.Max(_renderGpuMax, gpu);
        _renderSamples++;
    }

    public string TakeStats()
    {
        var readback = _captures > 0 ? _readbackMsTotal / _captures : 0;
        var submit = _submits > 0 ? _submitMsTotal / _submits : 0;
        var samples = Math.Max(1, _renderSamples);
        var line = $"{Name} captures={_captures} posted={_posted} held={_deferredSubmits} readback_avg={readback:F1}ms submit_avg={submit:F1}ms"
            + $" render_cpu={_renderCpuTotal / samples:F1}/{_renderCpuMax:F1}ms render_gpu={_renderGpuTotal / samples:F1}/{_renderGpuMax:F1}ms size={Viewport.Size}";
        _readbackMsTotal = _submitMsTotal = 0;
        _renderCpuTotal = _renderGpuTotal = _renderCpuMax = _renderGpuMax = 0;
        _renderSamples = 0;
        _captures = _submits = _posted = _deferredSubmits = 0;
        return line;
    }

    // Queues a GPU copy of what the viewport drew last frame. The copy lands in
    // OnFrameData a couple of frames later, once the GPU is done with it.
    private void RequestReadback(ulong now)
    {
        if (!Enabled)
            return;
        if (_rd == null)
        {
            ReadbackInline();
            return;
        }
        var texture = RenderingServer.TextureGetRdTexture(Viewport.GetTexture().GetRid());
        if (!texture.IsValid)
            return;
        var slot = Array.FindIndex(_requests, r => !r.Busy);
        if (slot < 0)
            return;
        var size = Viewport.Size;
        _requests[slot] = new Request { Busy = true, Epoch = _epoch, Width = size.X, Height = size.Y };
        if (_rd.TextureGetDataAsync(texture, 0, _onFrameData[slot]) != Error.Ok)
        {
            _requests[slot].Busy = false;
            return;
        }
        _inFlight++;
        _lastRequestUsec = now;
        _readbackMsTotal += (Time.GetTicksUsec() - now) / 1000.0;
        _captures++;
    }

    // Timed whole for the stats line: this runs inside the game's frame.
    private void OnFrameData(int slot, Variant data)
    {
        var started = Time.GetTicksUsec();
        DsNative.TraceBegin(_traceName);
        try
        {
            TakeFrame(slot, data);
        }
        finally
        {
            DsNative.TraceEnd();
            DsPerf.NoteCallback(Time.GetTicksUsec() - started);
        }
    }

    private void TakeFrame(int slot, Variant data)
    {
        var request = _requests[slot];
        _requests[slot].Busy = false;
        if (request.Busy && _inFlight > 0)
            _inFlight--;
        if (!request.Busy || !Enabled || request.Epoch != _epoch)
        {
            // Release the frame now, not when a finalizer gets to it.
            data.Dispose();
            return;
        }
        // Another part's frame went out this frame already: this one waits
        // for the next (it is a couple of frames old anyway), so two full
        // copies never land in one game frame. A newer one replaces it.
        if (Stagger && _submittedThisFrame && !NeverHold)
        {
            DropPending();
            _pending = data;
            _hasPending = true;
            _pendingWidth = request.Width;
            _pendingHeight = request.Height;
            _pendingEpoch = request.Epoch;
            _pendingSeq = ++_pendingCounter;
            _deferredSubmits++;
            return;
        }
        SubmitFrameData(data, request.Width, request.Height);
    }

    /// <summary>One capture's frame went out this game frame; the rest wait.</summary>
    public static bool Stagger { get; set; } = true;

    private static bool _submittedThisFrame;
    private static long _pendingCounter;

    /// <summary>A new game frame: one frame may go out again.</summary>
    public static void BeginFrame() => _submittedThisFrame = false;

    /// <summary>
    /// Frames go out as they come, never held: the pages under a finger or
    /// scrolling, where a frame's wait would put the content a frame behind it.
    /// </summary>
    public bool NeverHold { get; set; }

    public bool HasPending => _hasPending;
    public long PendingSeq => _pendingSeq;

    /// <summary>Posts the frame held back from an earlier game frame, if it is still for this target.</summary>
    public void SubmitPending()
    {
        if (!_hasPending)
            return;
        var data = _pending;
        _pending = default;
        _hasPending = false;
        if (!Enabled || _pendingEpoch != _epoch)
        {
            data.Dispose();
            return;
        }
        SubmitFrameData(data, _pendingWidth, _pendingHeight);
    }

    private void DropPending()
    {
        if (!_hasPending)
            return;
        _pending.Dispose();
        _pending = default;
        _hasPending = false;
    }

    // Takes ownership of data and releases it.
    private unsafe void SubmitFrameData(Variant data, int width, int height)
    {
        _submittedThisFrame = true;
        using var frame = data;
        var native = frame.CopyNativeVariant();
        var bytes = NativeFuncs.godotsharp_variant_as_packed_byte_array(in native);
        try
        {
            if (bytes.Buffer == null || bytes.Size != width * height * 4)
                return; // resized while in flight
            Submit(bytes.Buffer, width, height);
        }
        finally
        {
            bytes.Dispose();
            native.Dispose();
        }
    }

    private unsafe void ReadbackInline()
    {
        var started = Time.GetTicksUsec();
        using var image = Viewport.GetTexture()?.GetImage();
        if (image == null || image.IsEmpty())
            return;
        if (image.GetFormat() != Image.Format.Rgba8)
            image.Convert(Image.Format.Rgba8);
        var data = image.GetData();
        _readbackMsTotal += (Time.GetTicksUsec() - started) / 1000.0;
        _captures++;
        fixed (byte* pixels = data)
        {
            Submit(pixels, image.GetWidth(), image.GetHeight());
        }
    }

    private unsafe void Submit(byte* pixels, int width, int height)
    {
        var started = Time.GetTicksUsec();
        var posted = _whole
            ? DsNative.SubmitFrame(pixels, width, height, width * 4)
            : DsNative.SubmitRegion(pixels, width, height, width * 4, _x, _y, _region);
        _submitMsTotal += (Time.GetTicksUsec() - started) / 1000.0;
        _submits++;
        if (posted == 0)
            _changedSinceUsec = 0;
        if (posted != 1)
            return;
        _posted++;
        var now = Time.GetTicksUsec();
        if (_changedSinceUsec == 0)
            _changedSinceUsec = now;
        // It differed from the last frame: it's moving, keep up with it for a
        // moment. Not while it loops forever (that's what the ambient rate is
        // for), nor once it has changed on every capture for a while: that is
        // a loop nobody announced, and whoever starts real motion says so
        // (MarkChanged) for as long as it runs.
        if (!_ambient && now - _changedSinceUsec < MaxReArmUsec)
            MarkChanged(ReArmSeconds);
    }
}
