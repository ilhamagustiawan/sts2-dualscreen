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
// changes nobody announced.
internal sealed class DsCapture
{
    private const ulong IdleIntervalUsec = 1_000_000;
    private const ulong ActiveWindowUsec = 1_000_000;
    private const int MaxInFlight = 3;
    // A changed capture keeps the active rate this much longer, until it has
    // changed on every capture for MaxReArmUsec.
    private const double ReArmSeconds = 0.25;
    private const ulong MaxReArmUsec = 3_000_000;

    public readonly string Name;
    public readonly SubViewport Viewport;

    // Async readback through the RenderingDevice, so the game thread never
    // waits on the GPU. Null on the Compatibility renderer: read back inline.
    private readonly RenderingDevice _rd;
    private readonly Callable _onFrameData;
    private readonly string _traceName;

    private ulong _activeUntilUsec;
    private ulong _nextFrameUsec;
    private bool _readbackDue;
    private bool _ambient;
    private int _inFlight;
    private ulong _lastRequestUsec;
    // Since when every capture differed from the last; 0 after an unchanged one.
    private ulong _changedSinceUsec;

    // Where frames go. A request keeps the target it was made for; frames
    // from an older epoch (resized, moved, paused) are dropped.
    private bool _whole = true;
    private int _x, _y, _region;
    private int _epoch;
    private int _requestEpoch, _requestWidth, _requestHeight;

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
        _onFrameData = Callable.From<Variant>(OnFrameData);
        RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
    }

    /// <summary>Frames are posted (false: dropped, as while the surface is gone).</summary>
    public bool Enabled { get; set; } = true;

    public bool Active(ulong now) => now < _activeUntilUsec;

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
        MarkChanged();
    }

    /// <summary>Capture at the active rate for a while: something here is moving.</summary>
    public void MarkChanged(double seconds = 1.0)
    {
        var now = Time.GetTicksUsec();
        if (now >= _activeUntilUsec)
            _nextFrameUsec = 0;
        _activeUntilUsec = Math.Max(_activeUntilUsec, now + (ulong)(Math.Max(seconds, 0.05) * 1_000_000));
    }

    /// <summary>
    /// Once per game frame. ambientFps &gt; 0: something here loops forever, so
    /// changed frames are expected and don't raise the rate above it.
    /// </summary>
    public void Tick(ulong now, int activeFps, int ambientFps, bool idleChecks)
    {
        _ambient = ambientFps > 0;
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
        }
        if (!Enabled || now < _nextFrameUsec || _inFlight >= MaxInFlight)
            return;
        ulong interval;
        if (now < _activeUntilUsec)
            interval = (ulong)(1_000_000 / Math.Max(1, activeFps));
        else if (ambientFps > 0)
            interval = (ulong)(1_000_000 / ambientFps);
        else if (idleChecks)
            interval = IdleIntervalUsec;
        else
            return;
        _nextFrameUsec = now + interval;
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
        var line = $"{Name} captures={_captures} posted={_posted} readback_avg={readback:F1}ms submit_avg={submit:F1}ms"
            + $" render_cpu={_renderCpuTotal / samples:F1}/{_renderCpuMax:F1}ms render_gpu={_renderGpuTotal / samples:F1}/{_renderGpuMax:F1}ms size={Viewport.Size}";
        _readbackMsTotal = _submitMsTotal = 0;
        _renderCpuTotal = _renderGpuTotal = _renderCpuMax = _renderGpuMax = 0;
        _renderSamples = 0;
        _captures = _submits = _posted = 0;
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
        var size = Viewport.Size;
        _requestWidth = size.X;
        _requestHeight = size.Y;
        _requestEpoch = _epoch;
        if (_rd.TextureGetDataAsync(texture, 0, _onFrameData) != Error.Ok)
            return;
        _inFlight++;
        _lastRequestUsec = now;
        _readbackMsTotal += (Time.GetTicksUsec() - now) / 1000.0;
        _captures++;
    }

    // Timed whole for the stats line: this runs inside the game's frame.
    private void OnFrameData(Variant data)
    {
        var started = Time.GetTicksUsec();
        DsNative.TraceBegin(_traceName);
        try
        {
            TakeFrame(data);
        }
        finally
        {
            DsNative.TraceEnd();
            DsPerf.NoteCallback(Time.GetTicksUsec() - started);
        }
    }

    private unsafe void TakeFrame(Variant data)
    {
        if (_inFlight > 0)
            _inFlight--;
        // Release the frame now, not when a finalizer gets to it.
        using var frame = data;
        if (!Enabled || _requestEpoch != _epoch)
            return;
        var width = _requestWidth;
        var height = _requestHeight;
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
