namespace ImmichSplash.App.Services;

/// <summary>A video playing in the viewer (libmpv; see Services/Mpv). Kept as an interface so tests and other engines can stand in.</summary>
public interface IVideoPlayback : IDisposable
{
    /// <summary>Playing, paused or ended changed. Raised on the UI thread, like the others.</summary>
    event Action? StateChanged;
    event Action? TimeChanged;
    event Action? Failed;

    bool IsPlaying { get; }
    bool Ended { get; }
    /// <summary>At least one picture has been shown.</summary>
    bool HasFrame { get; }
    TimeSpan Time { get; }
    TimeSpan Length { get; }
    bool IsMuted { get; set; }

    void Play();
    void TogglePlay();
    /// <summary>Jumps to a position from 0 to 1.</summary>
    void Seek(double position);
    void SeekBy(TimeSpan delta);
}
