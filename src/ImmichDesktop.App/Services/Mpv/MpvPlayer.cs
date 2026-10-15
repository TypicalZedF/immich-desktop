using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using ImmichDesktop.Core;
using static ImmichDesktop.App.Services.Mpv.MpvNative;

namespace ImmichDesktop.App.Services.Mpv;

/// <summary>
/// A video played by libmpv, drawn straight onto the graphics card by <see cref="Controls.MpvVideoView"/>.
/// Videos from Immich are streamed by mpv itself (with the account's sign-in header), so seeking and
/// buffering work like in any video player.
/// </summary>
public sealed class MpvPlayer : IVideoPlayback
{
    static readonly Lazy<bool> Available = new(MpvNative.IsAvailable);
    static int _logLines;

    readonly IntPtr _mpv;
    readonly string _target;
    readonly Thread _events;
    readonly Lock _gate = new();
    readonly UpdateFn _onUpdate;
    readonly GetProcAddressFn _getProc;
    Func<string, IntPtr>? _glProc;
    IntPtr _render;
    volatile bool _loaded, _paused, _eof, _hasFrame;
    bool _playWanted; // asked to play before the view had set up drawing
    bool _muted, _quit;
    double _time, _duration;

    /// <summary>libmpv is installed (Linux) or shipped with the app (Windows).</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>The engine asks to draw again (any thread): a new frame is ready.</summary>
    public event Action? NeedsRedraw;
    /// <summary>The first picture is ready (UI thread).</summary>
    public event Action? FirstFrame;
    public event Action? StateChanged;
    public event Action? TimeChanged;
    public event Action? Failed;

    public static MpvPlayer ForFile(string path) => new(path, []);

    /// <summary>A video on the server; <paramref name="headers"/> carry the sign-in ("name: value").</summary>
    public static MpvPlayer ForUrl(Uri url, IReadOnlyList<string> headers) => new(url.AbsoluteUri, headers);

    MpvPlayer(string target, IReadOnlyList<string> headers)
    {
        _target = target;
        _onUpdate = _ => NeedsRedraw?.Invoke();
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
        Option("hwdec", "auto-safe");
        Option("keep-open", "yes"); // stay on the last frame at the end
        Option("idle", "yes");
        Option("terminal", "no");
        Option("config", "no");
        Option("load-scripts", "no");
        Option("ytdl", "no");
        Option("osc", "no");
        Option("input-default-bindings", "no");
        Option("cache", "yes");
        Option("demuxer-max-bytes", "128MiB");
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
        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    public bool IsPlaying => _loaded && !_paused && !_eof;
    public bool Ended => _eof;
    public bool HasFrame => _hasFrame;
    public TimeSpan Time => TimeSpan.FromSeconds(Math.Max(0, _time));
    public TimeSpan Length => TimeSpan.FromSeconds(Math.Max(0, _duration));

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

    public void Seek(double position) =>
        Command(_mpv, "seek", (Math.Clamp(position, 0, 1) * 100).ToString("0.###", CultureInfo.InvariantCulture), "absolute-percent");

    public void SeekBy(TimeSpan delta) =>
        Command(_mpv, "seek", delta.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "relative");

    // ---- Drawing (called by MpvVideoView on its OpenGL thread) ----

    internal unsafe void InitGl(Func<string, IntPtr> getProcAddress)
    {
        lock (_gate)
        {
            if (_render != IntPtr.Zero || _quit)
                return;
            _glProc = getProcAddress;
            var api = Marshal.StringToCoTaskMemUTF8("opengl");
            var init = new OpenGlInitParams { GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProc) };
            try
            {
                RenderParam[] parameters =
                [
                    new() { Type = RenderParamApiType, Data = api },
                    new() { Type = RenderParamOpenGlInitParams, Data = (IntPtr)(&init) },
                    new() { Type = RenderParamInvalid },
                ];
                var error = mpv_render_context_create(out _render, _mpv, parameters);
                if (error < 0)
                {
                    _render = IntPtr.Zero;
                    Log.Warn($"mpv couldn't draw with OpenGL: {ErrorText(error)}");
                    Post(() => Failed?.Invoke());
                    return;
                }
                mpv_render_context_set_update_callback(_render, _onUpdate, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeCoTaskMem(api);
            }
        }
        if (_playWanted)
        {
            _playWanted = false;
            Post(Play);
        }
    }

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
    internal void ReleaseGl()
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

    // ---- Events (own thread) ----

    void EventLoop()
    {
        while (true)
        {
            bool quit, drawing;
            lock (_gate)
            {
                quit = _quit;
                drawing = _render != IntPtr.Zero;
            }
            if (quit && !drawing)
            {
                // libmpv's rule: the drawing context goes first, then the player.
                mpv_terminate_destroy(_mpv);
                return;
            }
            var e = Marshal.PtrToStructure<Event>(mpv_wait_event(_mpv, quit ? 0.1 : -1));
            switch (e.EventId)
            {
                case EventPropertyChange:
                    OnProperty(Marshal.PtrToStructure<EventProperty>(e.Data));
                    break;
                case EventPlaybackRestart when !_hasFrame:
                    _hasFrame = true;
                    Post(() => FirstFrame?.Invoke());
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
        lock (_gate)
            _quit = true;
        mpv_wakeup(_mpv);
    }
}
