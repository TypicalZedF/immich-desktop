using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ImmichDesktop.Core;
using LibVLCSharp.Shared;

namespace ImmichDesktop.App.Services;

/// <summary>
/// Plays a video inside the app with VLC's engine. Frames are drawn into a bitmap the viewer shows
/// (so our controls can sit on top of the picture), and videos in Immich are streamed with the
/// account's sign-in. Windows ships VLC with the app; on Linux the system's libvlc is used.
/// </summary>
public sealed class VideoPlayer : IDisposable
{
    /// <summary>Bigger videos are scaled down while decoding; the viewer never needs more.</summary>
    const uint MaxWidth = 2560, MaxHeight = 1600;

    static readonly Lazy<LibVLC?> Engine = new(CreateEngine);

    // Starting VLC loads its native libraries and plugins, which takes seconds the first time:
    // done on a background thread so the window never freezes (see Prepare).
    static readonly Lazy<Task> EngineLoading = new(() => Task.Run(() => _ = Engine.Value));

    /// <summary>Starts VLC's engine in the background, if it isn't already. Completes when it's ready (or failed).</summary>
    public static Task Prepare() => EngineLoading.Value;

    /// <summary>The engine has finished starting, so creating a player won't block.</summary>
    public static bool IsReady => Engine.IsValueCreated;

    // Kept here so the garbage collector can't free them while VLC still calls them.
    readonly MediaPlayer.LibVLCVideoFormatCb _format;
    readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    readonly MediaPlayer.LibVLCVideoLockCb _lock;
    readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    readonly MediaPlayer _player;
    readonly Media _media;
    readonly Stream? _input;
    readonly Lock _frameLock = new();
    IntPtr _buffer;
    int _width, _height, _pitch;
    double _scale = 1;
    int _visibleHeight; // decoders pad the height (e.g. 720 to 738); 0 until known
    WriteableBitmap? _frame;
    int _framePending;
    bool _disposed;

    /// <summary>
    /// Decode on the graphics card. Switched off for the rest of the session if a video played without
    /// ever delivering a picture (seen on Linux, where some drivers don't hand frames back to the app).
    /// </summary>
    public static bool UseHardwareDecoding { get; set; } = true;

    static int _vlcLogLines;

    /// <summary>Why videos can't play in the app on this PC, if they can't (e.g. VLC isn't installed on Linux).</summary>
    public static string? Unavailable => Engine.Value is null
        ? OperatingSystem.IsLinux() ? Tr.T("Install VLC (libvlc) to play videos in the app.") : Tr.T("The video player couldn't start.")
        : null;

    VideoPlayer(LibVLC engine, Media media, Stream? input)
    {
        _media = media;
        _input = input;
        _player = new MediaPlayer(engine) { EnableHardwareDecoding = UseHardwareDecoding };
        _format = OnFormat;
        _cleanup = OnCleanup;
        _lock = OnLock;
        _display = OnDisplay;
        _player.SetVideoFormatCallbacks(_format, _cleanup);
        _player.SetVideoCallbacks(_lock, null, _display);
        _player.Playing += (_, _) => Post(() => StateChanged?.Invoke());
        _player.Paused += (_, _) => Post(() => StateChanged?.Invoke());
        _player.Stopped += (_, _) => Post(() => StateChanged?.Invoke());
        _player.EndReached += (_, _) => Post(() => { Ended = true; StateChanged?.Invoke(); });
        _player.EncounteredError += (_, _) => Post(() => Failed?.Invoke());
        _player.TimeChanged += (_, _) => Post(() => TimeChanged?.Invoke());
        if (input is ImmichDesktop.Core.Api.RemoteVideoStream remote)
            remote.Failed += () => Post(() => Failed?.Invoke()); // VLC alone would keep retrying
        _player.LengthChanged += (_, _) => Post(() => TimeChanged?.Invoke());
    }

    /// <summary>A video file on this PC.</summary>
    public static VideoPlayer? ForFile(string path) =>
        Engine.Value is { } engine ? new VideoPlayer(engine, new Media(engine, path, FromType.FromPath), null) : null;

    /// <summary>A video streamed from Immich (any seekable stream).</summary>
    public static VideoPlayer? ForStream(Stream stream)
    {
        if (Engine.Value is not { } engine)
        {
            stream.Dispose();
            return null;
        }
        return new VideoPlayer(engine, new Media(engine, new StreamMediaInput(stream)), stream);
    }

    /// <summary>At least one picture has arrived.</summary>
    public bool HasFrame => _frame is not null;

    /// <summary>The latest frame. Redrawn in place, so watch <see cref="FrameReady"/> to repaint.</summary>
    public WriteableBitmap? Frame => _frame;

    public event Action? FrameReady;
    public event Action? FrameChanged; // a new bitmap (size changed)
    public event Action? StateChanged;
    public event Action? TimeChanged;
    public event Action? Failed;

    public bool IsPlaying => _player.IsPlaying;
    public bool Ended { get; private set; }
    public TimeSpan Time => TimeSpan.FromMilliseconds(Math.Max(0, _player.Time));
    public TimeSpan Length => TimeSpan.FromMilliseconds(Math.Max(0, _player.Length));

    public bool IsMuted
    {
        get => _player.Mute;
        set => _player.Mute = value;
    }

    /// <summary>0–100.</summary>
    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 100);
    }

    public void Play()
    {
        if (Ended)
        {
            // Start over after the end, like any player.
            Ended = false;
            _player.Stop();
        }
        _player.Play(_media);
    }

    public void Pause() => _player.SetPause(true);

    public void TogglePlay()
    {
        if (_player.IsPlaying)
            Pause();
        else if (_player.State == VLCState.Paused)
            _player.SetPause(false);
        else
            Play();
    }

    /// <summary>Jumps to a point from 0 (start) to 1 (end).</summary>
    public void Seek(double position)
    {
        if (_player.Length > 0)
        {
            Ended = false;
            _player.Time = (long)(Math.Clamp(position, 0, 1) * _player.Length);
        }
    }

    public void SeekBy(TimeSpan delta)
    {
        if (_player.Length > 0)
            _player.Time = Math.Clamp(_player.Time + (long)delta.TotalMilliseconds, 0, _player.Length);
    }

    // ---- Frames: VLC decodes into our buffer, then we copy it into the bitmap the viewer shows ----

    uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Keep the shape, but no bigger than the viewer needs.
        var scale = Math.Min(1.0, Math.Min(MaxWidth / (double)Math.Max(1, width), MaxHeight / (double)Math.Max(1, height)));
        _scale = scale;
        width = Math.Max(2, (uint)(width * scale)) & ~1u;
        height = Math.Max(2, (uint)(height * scale)) & ~1u;
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4); // BGRA in memory
        pitches = width * 4;
        lines = height;
        lock (_frameLock)
        {
            FreeBuffer();
            _width = (int)width;
            _height = (int)height;
            _pitch = (int)pitches;
            _buffer = Marshal.AllocHGlobal(_pitch * _height);
            _visibleHeight = 0;
        }
        return 1;
    }

    void OnCleanup(ref IntPtr opaque)
    {
        lock (_frameLock)
            FreeBuffer();
    }

    IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        // Repaint at most once per UI frame; if the UI is behind, the newest picture wins.
        if (Interlocked.Exchange(ref _framePending, 1) == 0)
            Post(CopyFrame);
    }

    unsafe void CopyFrame()
    {
        Interlocked.Exchange(ref _framePending, 0);
        if (_disposed)
            return;
        lock (_frameLock)
        {
            if (_buffer == IntPtr.Zero)
                return;
            if (_visibleHeight == 0)
            {
                // The picture's real height (the decoder's buffer can have padding rows below it).
                uint w = 0, h = 0;
                _visibleHeight = _player.Size(0, ref w, ref h) && h > 0
                    ? Math.Clamp((int)Math.Round(h * _scale), 2, _height)
                    : _height;
            }
            if (_frame is not { } frame || frame.PixelSize.Width != _width || frame.PixelSize.Height != _visibleHeight)
            {
                var old = _frame;
                _frame = frame = new WriteableBitmap(new PixelSize(_width, _visibleHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                FrameChanged?.Invoke();
                old?.Dispose();
            }
            using var target = frame.Lock();
            var rowBytes = Math.Min(_pitch, target.RowBytes);
            for (var y = 0; y < _visibleHeight; y++)
                Buffer.MemoryCopy((byte*)_buffer + y * _pitch, (byte*)target.Address + y * target.RowBytes, target.RowBytes, rowBytes);
        }
        FrameReady?.Invoke();
    }

    void FreeBuffer()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    static void Post(Action action) => Dispatcher.UIThread.Post(action);

    static LibVLC? CreateEngine()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Distributions ship "libvlc.so.5" (the plain name only comes with developer packages).
                NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, assembly, path) =>
                    name is "libvlc" or "libvlccore" && NativeLibrary.TryLoad($"{name}.so.{(name == "libvlc" ? 5 : 9)}", assembly, path, out var handle)
                        ? handle
                        : IntPtr.Zero);
            }
            var started = System.Diagnostics.Stopwatch.StartNew();
            LibVLCSharp.Shared.Core.Initialize();
            var engine = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-stats");
            // VLC's own warnings and errors go to the app's log (the first few), to see why a video won't play.
            engine.Log += (_, e) =>
            {
                if (e.Level >= LogLevel.Warning && Interlocked.Increment(ref _vlcLogLines) <= 60)
                    Log.Info($"VLC {e.Level} ({e.Module}): {e.Message}");
            };
            Log.Info($"Video player ready in {started.ElapsedMilliseconds} ms");
            return engine;
        }
        catch (Exception ex)
        {
            Log.Warn("The video player couldn't start", ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Stopping waits for VLC's threads; do it off the UI thread so closing the viewer stays instant.
        var player = _player;
        var media = _media;
        var input = _input;
        var frame = _frame;
        _frame = null;
        Task.Run(() =>
        {
            try
            {
                player.Stop();
                player.Dispose();
                media.Dispose();
                input?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Info($"Video player cleanup: {ex.Message}");
            }
            lock (_frameLock)
                FreeBuffer();
            Post(() => frame?.Dispose());
        });
    }
}
