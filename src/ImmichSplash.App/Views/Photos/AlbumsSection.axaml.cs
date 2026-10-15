using Avalonia.Controls;

namespace ImmichSplash.App.Views.Photos;

public partial class AlbumsSection : UserControl
{
    /// <summary>Below this width the right-hand tools take their own line (still on the right).</summary>
    const double OneLineWidth = 1060;

    public AlbumsSection()
    {
        InitializeComponent();
        var toolbar = this.FindControl<Grid>("Toolbar")!;
        var right = this.FindControl<StackPanel>("ToolbarRight")!;
        toolbar.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < OneLineWidth;
            Grid.SetRow(right, narrow ? 1 : 0);
            Grid.SetColumn(right, narrow ? 0 : 1);
            Grid.SetColumnSpan(right, narrow ? 2 : 1);
            right.Margin = new Avalonia.Thickness(0, narrow ? 8 : 0, 0, 0);
        };
    }
}
