using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ImmichSplash.Core;
using static ImmichSplash.App.Services.Mpv.MpvNative;

namespace ImmichSplash.App.Services.Mpv;

/// <summary>
/// A video played by libmpv. Normally mpv draws straight onto the graphics card in
/// <see cref="Controls.MpvVideoView"/>; with graphics acceleration off it draws into memory instead and
/// <see cref="Frame"/> shows the picture. Videos from Immich are streamed by mpv itself (with the
/// account's sign-in header), so seeking and buffering work like in any video player.
/// </summary>
public sealed class MpvPlayer : IVideoPlayback
{
    /// <summary>Bigger videos are drawn smaller in memory (software drawing only).</summary>
    int _maxWidth = 1920, _maxHeight = 1080;
    readonly bool _preview;
    double? _pendingPreview;

    static readonly Lazy<bool> Available = new(MpvNative.IsAvailable);
    static int _logLines;

    readonly IntPtr _mpv;
    readonly string _target;
    readonly bool _software;
    readonly Lock _gate = new();
    readonly UpdateFn _onUpdate;
    readonly GetProcAddressFn _getProc;
    readonly AutoResetEvent _redraw = new(false);
    Func<string, IntPtr>? _glProc;
    IntPtr _render;
    volatile bool _loaded, _paused, _eof, _hasFrame, _quit;
    bool _muted, _playWanted;
    double _time, _duration;

    // Software drawing: mpv draws into _buffer, which is copied into _frame on the UI thread.
    IntPtr _buffer;
    int _width, _height;
    WriteableBitmap? _frame;
    int _framePending;

    /// <summary>libmpv is installed (Linux) or shipped with the app (Windows).</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>Why videos can't play in the app on this PC, if libmpv is missing.</summary>
    public static string Unavailable => OperatingSystem.IsLinux()
        ? Tr.T("To play videos here, install mpv's library (for example: sudo dnf install mpv-libs, or sudo apt install libmpv2).")
        : Tr.T("The video player couldn't start.");

    /// <summary>The engine asks to draw again (any thread): a new frame is ready (graphics card drawing).</summary>
    public event Action? NeedsRedraw;
    /// <summary>The first picture is ready (UI thread).</summary>
    public event Action? FirstFrame;
    /// <summary>Software drawing: a new bitmap (size changed). UI thread.</summary>
    public event Action? FrameChanged;
    /// <summary>Software drawing: the bitmap was redrawn in place. UI thread.</summary>
    public event Action? FrameReady;
    public event Action? StateChanged;
    public event Action? TimeChanged;
    public event Action? Failed;

    /// <param name="software">Draw into memory (<see cref="Frame"/>) instead of on the graphics card.</param>
    public static MpvPlayer ForFile(string path, bool software = false) => new(path, [], software, preview: false);

    /// <summary>A video on the server; <paramref name="headers"/> carry the sign-in ("name: value").</summary>
    public static MpvPlayer ForUrl(Uri url, IReadOnlyList<string> headers, bool software = false) => new(url.AbsoluteUri, headers, software, preview: false);

    /// <summary>
    /// A small silent copy that only shows still pictures at positions (the seek bar's preview): software
    /// drawing, no sound, jumps to keyframes. Use <see cref="ShowFrameAt"/>.
    /// </summary>
    public static MpvPlayer ForPreview(string target, IReadOnlyList<string> headers) => new(target, headers, software: true, preview: true);

    MpvPlayer(string target, IReadOnlyList<string> headers, bool software, bool preview)
    {
        _target = target;
        _software = software;
        _preview = preview;
        if (preview)
            (_maxWidth, _maxHeight) = (320, 180);
        _onUpdate = _ =>
        {
            if (_software)
                _redraw.Set(); // no mpv calls allowed inside this callback
            else
                NeedsRedraw?.Invoke();
        };
        _getProc = (_, name) => _glProc?.Invoke(Marshal.PtrToStringUTF8(name) ?? "") ?? IntPtr.Zero;
        _mpv = mpv_create();
        if (_mpv == IntPtr.Zero)
            throw new InvalidOperationException("libmpv couldn't start");
        void Option(string name, string value)
        {
            var error = mpv_set_option_string(_mpv, name, value);
            if (error < 0)
                Log.Info($"mpv option {name} not set: {ErrorText(error)}");
        }
        Option("vo", "libmpv");
        Option("hwdec", software ? "no" : "auto-safe");
        Option("keep-open", "yes"); // stay on the last frame at the end
        Option("idle", "yes");
        Option("terminal", "no");
        Option("config", "no");
        Option("load-scripts", "no");
        Option("ytdl", "no");
        Option("osc", "no");
        Option("input-default-bindings", "no");
        Option("cache", "yes");
        Option("demuxer-max-bytes", preview ? "24MiB" : "128MiB");
        if (preview)
        {
            Option("aid", "no"); // no sound
            Option("ao", "null");
            Option("hr-seek", "no"); // keyframes only: quick
            Option("vd-lavc-skiploopfilter", "all");
            Option("vd-lavc-fast", "yes");
            Option("sub", "no");
            Option("pause", "yes");
        }
        Option("audio-client-name", AppInfo.Name);
        if (headers.Count > 0)
            Option("http-header-fields", string.Join(",", headers));
        var init = mpv_initialize(_mpv);
        if (init < 0)
        {
            mpv_terminate_destroy(_mpv);
            throw new InvalidOperationException($"libmpv couldn't start: {ErrorText(init)}");
        }
        mpv_request_log_messages(_mpv, "warn");
        mpv_observe_property(_mpv, 1, "time-pos", FormatDouble);
        mpv_observe_property(_mpv, 2, "duration", FormatDouble);
        mpv_observe_property(_mpv, 3, "pause", FormatFlag);
        mpv_observe_property(_mpv, 4, "eof-reached", FormatFlag);
        new Thread(EventLoop) { IsBackground = true, Name = "mpv events" }.Start();
        if (software)
        {
            CreateRenderContext("sw", null);
            new Thread(SoftwareLoop) { IsBackground = true, Name = "mpv drawing" }.Start();
        }
    }

    public bool IsPlaying => _loaded && !_paused && !_eof;
    public bool Ended => _eof;
    public bool HasFrame => _hasFrame;
    public TimeSpan Time => TimeSpan.FromSeconds(Math.Max(0, _time));
    public TimeSpan Length => TimeSpan.FromSeconds(Math.Max(0, _duration));

    /// <summary>Software drawing: the latest picture (redrawn in place; see <see cref="FrameReady"/>).</summary>
    public WriteableBitmap? Frame => _frame;

    int _volume = 100;

    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            mpv_set_property_string(_mpv, "volume", _volume.ToString(CultureInfo.InvariantCulture));
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            mpv_set_property_string(_mpv, "mute", value ? "yes" : "no");
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            // libmpv needs somewhere to draw before it opens the video: wait for the view.
            if (_render == IntPtr.Zero)
            {
                _playWanted = true;
                return;
            }
        }
        if (!_loaded)
        {
            _loaded = true;
            Command(_mpv, "loadfile", _target);
        }
        else if (_eof)
            Command(_mpv, "seek", "0", "absolute");
        mpv_set_property_string(_mpv, "pause", "no");
    }

    public void TogglePlay()
    {
        if (IsPlaying)
            mpv_set_property_string(_mpv, "pause", "yes");
        else
            Play();
    }

    public void Seek(double position) => Seek(position, exact: true);

    public void Seek(double position, bool exact) =>
        Command(_mpv, "seek", (Math.Clamp(position, 0, 1) * 100).ToString("0.###", CultureInfo.InvariantCulture),
            exact ? "absolute-percent+exact" : "absolute-percent+keyframes");

    /// <summary>Preview players: shows the picture at a position from 0 to 1 (the newest request wins).</summary>
    public void ShowFrameAt(double position)
    {
        if (!_preview)
            return;
        position = Math.Clamp(position, 0, 1);
        if (!_loaded)
        {
            // libmpv needs its drawing context first; the software one exists from the start.
            _loaded = true;
            _pendingPreview = position;
            Command(_mpv, "loadfile", _target);
            return;
        }
        if (!_hasFrame)
        {
            _pendingPreview = position; // the file isn't open yet: jump there once it is
            return;
        }
        Seek(position, exact: false);
    }

    public void SeekBy(TimeSpan delta) =>
        Command(_mpv, "seek", delta.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "relative");

    unsafe void CreateRenderContext(string api, Func<string, IntPtr>? getProcAddress)
    {
        lock (_gate)
        {
            if (_render != IntPtr.Zero || _quit)
                return;
            _glProc = getProcAddress;
            var apiName = Marshal.StringToCoTaskMemUTF8(api);
            var init = new OpenGlInitParams { GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProc) };
            try
            {
                RenderParam[] parameters = getProcAddress is null
                    ? [new() { Type = RenderParamApiType, Data = apiName }, new() { Type = RenderParamInvalid }]
                    : [new() { Type = RenderParamApiType, Data = apiName }, new() { Type = RenderParamOpenGlInitParams, Data = (IntPtr)(&init) }, new() { Type = RenderParamInvalid }];
                var error = mpv_render_context_create(out _render, _mpv, parameters);
                if (error < 0)
                {
                    _render = IntPtr.Zero;
                    Log.Warn($"mpv couldn't set up drawing ({api}): {ErrorText(error)}");
                    Post(() => Failed?.Invoke());
                    return;
                }
                mpv_render_context_set_update_callback(_render, _onUpdate, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeCoTaskMem(apiName);
            }
        }
        if (_playWanted)
        {
            _playWanted = false;
            Post(Play);
        }
    }

    // ---- Graphics card drawing (called by MpvVideoView on its OpenGL thread) ----

    internal void InitGl(Func<string, IntPtr> getProcAddress) => CreateRenderContext("opengl", getProcAddress);

    internal unsafe void RenderGl(int framebuffer, int width, int height)
    {
        lock (_gate)
        {
            if (_render == IntPtr.Zero || width <= 0 || height <= 0)
                return;
            mpv_render_context_update(_render);
            var fbo = new OpenGlFbo { Fbo = framebuffer, W = width, H = height };
            var flip = 1;
            RenderParam[] parameters =
            [
                new() { Type = RenderParamOpenGlFbo, Data = (IntPtr)(&fbo) },
                new() { Type = RenderParamFlipY, Data = (IntPtr)(&flip) },
                new() { Type = RenderParamInvalid },
            ];
            mpv_render_context_render(_render, parameters);
        }
    }

    /// <summary>Frees the drawing context (on the OpenGL thread). The player can then shut down.</summary>
    internal void ReleaseGl() => FreeRenderContext();

    void FreeRenderContext()
    {
        lock (_gate)
        {
            if (_render == IntPtr.Zero)
                return;
            mpv_render_context_set_update_callback(_render, null, IntPtr.Zero);
            mpv_render_context_free(_render);
            _render = IntPtr.Zero;
            _glProc = null;
        }
        mpv_wakeup(_mpv); // lets a pending shutdown go ahead
    }

    // ---- Software drawing (own thread) ----

    unsafe void SoftwareLoop()
    {
        var size = stackalloc int[2];
        while (!_quit)
        {
            _redraw.WaitOne(250);
            lock (_gate)
            {
                if (_quit || _render == IntPtr.Zero || (mpv_render_context_update(_render) & RenderUpdateFrame) == 0)
                    continue;
                if (mpv_get_property_long(_mpv, "dwidth", FormatInt64, out var w) < 0 || mpv_get_property_long(_mpv, "dheight", FormatInt64, out var h) < 0 || w <= 0 || h <= 0)
                    continue;
                var scale = Math.Min(1.0, Math.Min(_maxWidth / (double)w, _maxHeight / (double)h));
                var width = Math.Max(2, (int)(w * scale)) & ~1;
                var height = Math.Max(2, (int)(h * scale)) & ~1;
                if (width != _width || height != _height)
                {
                    if (_buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(_buffer);
                    _buffer = Marshal.AllocHGlobal(width * height * 4);
                    (_width, _height) = (width, height);
                }
                size[0] = width;
                size[1] = height;
                var stride = (nuint)(width * 4);
                var format = Marshal.StringToCoTaskMemUTF8("bgr0"); // BGRA in memory, alpha unused
                try
                {
                    RenderParam[] parameters =
                    [
                        new() { Type = RenderParamSwSize, Data = (IntPtr)size },
                        new() { Type = RenderParamSwFormat, Data = format },
                        new() { Type = RenderParamSwStride, Data = (IntPtr)(&stride) },
                        new() { Type = RenderParamSwPointer, Data = _buffer },
                        new() { Type = RenderParamInvalid },
                    ];
                    if (mpv_render_context_render(_render, parameters) < 0)
                        continue;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(format);
                }
            }
            // Show it at most once per UI frame; if the UI is behind, the newest picture wins.
            if (Interlocked.Exchange(ref _framePending, 1) == 0)
                Post(CopyFrame);
        }
        FreeRenderContext();
    }

    unsafe void CopyFrame()
    {
        Interlocked.Exchange(ref _framePending, 0);
        lock (_gate)
        {
            if (_quit || _buffer == IntPtr.Zero)
                return;
            if (_frame is not { } frame || frame.PixelSize.Width != _width || frame.PixelSize.Height != _height)
            {
                var old = _frame;
                _frame = frame = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                FrameChanged?.Invoke();
                old?.Dispose();
            }
            using var target = frame.Lock();
            for (var y = 0; y < _height; y++)
                Buffer.MemoryCopy((byte*)_buffer + y * _width * 4, (byte*)target.Address + y * target.RowBytes, target.RowBytes, _width * 4);
        }
        FrameReady?.Invoke();
    }

    // ---- Events (own thread) ----

    void EventLoop()
    {
        while (true)
        {
            bool drawing;
            lock (_gate)
                drawing = _render != IntPtr.Zero;
            if (_quit && !drawing)
            {
                // libmpv's rule: the drawing context goes first, then the player.
                mpv_terminate_destroy(_mpv);
                lock (_gate)
                {
                    if (_buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                }
                return;
            }
            var e = Marshal.PtrToStructure<Event>(mpv_wait_event(_mpv, _quit ? 0.1 : -1));
            switch (e.EventId)
            {
                case EventPropertyChange:
                    OnProperty(Marshal.PtrToStructure<EventProperty>(e.Data));
                    break;
                case EventPlaybackRestart when !_hasFrame:
                    _hasFrame = true;
                    Post(() => FirstFrame?.Invoke());
                    if (_pendingPreview is { } pending)
                    {
                        _pendingPreview = null;
                        Seek(pending, exact: false);
                    }
                    break;
                case EventEndFile when Marshal.PtrToStructure<EndFileData>(e.Data) is { Reason: EndFileError } end:
                    Log.Warn($"mpv couldn't play the video: {ErrorText(end.Error)}");
                    Post(() => Failed?.Invoke());
                    break;
                case EventLogMessage when Interlocked.Increment(ref _logLines) <= 60:
                    var log = Marshal.PtrToStructure<LogMessageData>(e.Data);
                    Log.Info($"mpv {Marshal.PtrToStringUTF8(log.Level)} ({Marshal.PtrToStringUTF8(log.Prefix)}): {Marshal.PtrToStringUTF8(log.Text)?.TrimEnd()}");
                    break;
            }
        }
    }

    unsafe void OnProperty(EventProperty property)
    {
        if (property.Data == IntPtr.Zero)
            return;
        switch (Marshal.PtrToStringUTF8(property.Name))
        {
            case "time-pos":
                _time = *(double*)property.Data;
                Post(() => TimeChanged?.Invoke());
                break;
            case "duration":
                _duration = *(double*)property.Data;
                Post(() => TimeChanged?.Invoke());
                break;
            case "pause":
                _paused = *(int*)property.Data != 0;
                Post(() => StateChanged?.Invoke());
                break;
            case "eof-reached":
                _eof = *(int*)property.Data != 0;
                Post(() => StateChanged?.Invoke());
                break;
        }
    }

    static void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void Dispose()
    {
        _quit = true;
        _redraw.Set(); // software drawing frees its context on its own thread
        mpv_wakeup(_mpv);
    }
}
