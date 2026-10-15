using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using ImmichSplash.App.Services.Mpv;

namespace ImmichSplash.App.Controls;

/// <summary>
/// Shows an <see cref="MpvPlayer"/>: libmpv draws each frame straight into this control on the graphics
/// card (no copying through the CPU), like a video player does.
/// </summary>
public sealed class MpvVideoView : OpenGlControlBase
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, MpvPlayer?>(nameof(Player));

    MpvPlayer? _attached; // the player that has a drawing context on this control

    public MpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PlayerProperty)
            return;
        if (change.OldValue is MpvPlayer old)
            old.NeedsRedraw -= OnNeedsRedraw;
        if (change.NewValue is MpvPlayer player)
            player.NeedsRedraw += OnNeedsRedraw;
        RequestNextFrameRendering(); // swap players (and free the old one's drawing context) on the GL thread
    }

    void OnNeedsRedraw() => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!ReferenceEquals(_attached, Player))
        {
            _attached?.ReleaseGl();
            _attached = Player;
            _attached?.InitGl(gl.GetProcAddress);
        }
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = (int)Math.Round(Bounds.Width * scale);
        var height = (int)Math.Round(Bounds.Height * scale);
        if (_attached is null)
        {
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
            return;
        }
        _attached.RenderGl(fb, width, height);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _attached?.ReleaseGl();
        _attached = null;
    }
}
