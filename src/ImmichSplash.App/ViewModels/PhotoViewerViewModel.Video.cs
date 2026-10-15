using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImmichSplash.App.Services;
using ImmichSplash.App.Services.Mpv;
using ImmichSplash.Core;

namespace ImmichSplash.App.ViewModels;

public sealed partial class PhotoViewerViewModel
{
    // ---- Volume ----

    /// <summary>0 to 100 (remembered between videos).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIcon))]
    public partial int Volume { get; set; } = Math.Clamp(AppServices.Settings.Current.VideoVolume, 0, 100);

    public Material.Icons.MaterialIconKind VolumeIcon => IsMuted || Volume == 0 ? Material.Icons.MaterialIconKind.VolumeOff
        : Volume < 34 ? Material.Icons.MaterialIconKind.VolumeLow
        : Volume < 67 ? Material.Icons.MaterialIconKind.VolumeMedium
        : Material.Icons.MaterialIconKind.VolumeHigh;

    CancellationTokenSource? _saveVolume;

    partial void OnVolumeChanged(int value)
    {
        if (_video is not null)
            _video.Volume = value;
        // Turning it up un-mutes; remember the level once the slider settles.
        if (value > 0 && IsMuted)
            IsMuted = false;
        _saveVolume?.Cancel();
        var cts = _saveVolume = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                AppServices.Settings.Update(s => s.VideoVolume = value);
        }, TaskScheduler.Default);
    }

    /// <summary>Volume up or down by a step (arrow keys, wheel).</summary>
    public void ChangeVolume(int delta)
    {
        IsMuted = false;
        Volume = Math.Clamp(Volume + delta, 0, 100);
    }

    // ---- The progress bar: dragging, and pictures from the video where you point ----

    /// <summary>The bar is being dragged: it shows where the pointer is, and the video's own time is ignored.</summary>
    [ObservableProperty]
    public partial bool IsScrubbing { get; set; }

    double _scrubTarget;
    bool _scrubSeekQueued;
    DateTime _holdPositionUntil;
    double _holdTarget;

    public void ScrubStarted(double fraction)
    {
        IsScrubbing = true;
        _scrubTarget = fraction;
        ShowScrubTime(fraction);
        QueueScrubSeek();
    }

    public void ScrubMoved(double fraction)
    {
        _scrubTarget = fraction;
        ShowScrubTime(fraction);
        QueueScrubSeek();
    }

    public void ScrubCompleted(double fraction)
    {
        _scrubTarget = fraction;
        // The final jump is exact. The video's own time catches up a moment later; until it does, keep
        // the bar where it was dropped instead of letting it flick back to the old time.
        _video?.Seek(fraction, exact: true);
        _holdTarget = fraction;
        _holdPositionUntil = DateTime.UtcNow.AddMilliseconds(900);
        _updatingPosition = true;
        VideoPosition = fraction;
        _updatingPosition = false;
        IsScrubbing = false;
        ShowScrubTime(fraction);
    }

    void ShowScrubTime(double fraction)
    {
        if (_video is not { } video || video.Length <= TimeSpan.Zero)
            return;
        VideoTimeText = $"{AssetCellViewModel.FormatDuration((long)(video.Length * fraction).TotalMilliseconds)} / {AssetCellViewModel.FormatDuration((long)video.Length.TotalMilliseconds)}";
    }

    /// <summary>While dragging, jump to the nearest keyframe at most every 70 ms (the newest position wins): quick, and the picture follows the pointer.</summary>
    void QueueScrubSeek()
    {
        if (_scrubSeekQueued)
            return;
        _scrubSeekQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _scrubSeekQueued = false;
            if (IsScrubbing)
                _video?.Seek(_scrubTarget, exact: false);
        }, TimeSpan.FromMilliseconds(70));
    }

    /// <summary>True while the bar should ignore the video's reported time (dragging, or just dropped).</summary>
    bool HoldingPosition(IVideoPlayback player)
    {
        if (IsScrubbing)
            return true;
        if (DateTime.UtcNow >= _holdPositionUntil)
            return false;
        // Let go early once the video has actually arrived near the drop point.
        var length = player.Length;
        return length <= TimeSpan.Zero || Math.Abs(player.Time / length - _holdTarget) > 0.01;
    }

    // Pictures from the video at the pointer (a second, small, silent player that only jumps and shows a still).

    MpvPlayer? _preview;
    DispatcherTimer? _previewTimer;
    double _previewTarget;

    [ObservableProperty]
    public partial bool ShowPreview { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? PreviewFrame { get; set; }

    [ObservableProperty]
    public partial string PreviewTimeText { get; set; } = "";

    /// <summary>A new picture was drawn into <see cref="PreviewFrame"/> (the view repaints it).</summary>
    public event Action? PreviewFrameUpdated;

    public void HoverVideo(double? fraction)
    {
        if (fraction is not { } f || _video is null)
        {
            ShowPreview = false;
            return;
        }
        var length = _video.Length;
        if (length <= TimeSpan.Zero)
            return;
        ShowPreview = true;
        PreviewTimeText = AssetCellViewModel.FormatDuration((long)(length * f).TotalMilliseconds);
        _previewTarget = f;
        // Not on every pixel of movement: about every 90 ms, the newest position.
        _previewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Background, (_, _) => RequestPreviewFrame());
        if (!_previewTimer.IsEnabled)
        {
            RequestPreviewFrame();
            _previewTimer.Start();
        }
    }

    double _lastPreviewAsked = -1;

    void RequestPreviewFrame()
    {
        if (!ShowPreview)
        {
            _previewTimer?.Stop();
            return;
        }
        if (Math.Abs(_previewTarget - _lastPreviewAsked) < 0.0005)
            return;
        _lastPreviewAsked = _previewTarget;
        EnsurePreviewPlayer()?.ShowFrameAt(_previewTarget);
    }

    MpvPlayer? EnsurePreviewPlayer()
    {
        if (_preview is not null)
            return _preview;
        if (!MpvPlayer.IsAvailable || Asset.IsImage)
            return null;
        try
        {
            string target;
            IReadOnlyList<string> headers = [];
            if (DevicePath is { } local && File.Exists(local))
                target = local;
            else if (AppServices.Account.Client is { } client)
            {
                var (url, h) = client.VideoPlaybackRequest(Asset.Id);
                target = url.AbsoluteUri;
                headers = h;
            }
            else
                return null;
            var player = MpvPlayer.ForPreview(target, headers);
            player.FrameChanged += () => { if (ReferenceEquals(_preview, player)) PreviewFrame = player.Frame; };
            player.FrameReady += () => { if (ReferenceEquals(_preview, player)) PreviewFrameUpdated?.Invoke(); };
            player.Failed += () =>
            {
                if (ReferenceEquals(_preview, player))
                    ClosePreviewPlayer(); // no pictures; the time still shows
            };
            _preview = player;
            return player;
        }
        catch (Exception ex)
        {
            Log.Info($"The seek bar's preview couldn't start: {ex.Message}");
            return null;
        }
    }

    void ClosePreviewPlayer()
    {
        _previewTimer?.Stop();
        ShowPreview = false;
        var player = _preview;
        _preview = null;
        _lastPreviewAsked = -1;
        PreviewFrame = null;
        player?.Dispose();
    }
}
