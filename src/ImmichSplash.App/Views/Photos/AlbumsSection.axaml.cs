using Avalonia;
using Avalonia.Controls;

namespace ImmichSplash.App.Views.Photos;

public partial class AlbumsSection : UserControl
{
    public AlbumsSection()
    {
        InitializeComponent();
        var toolbar = this.FindControl<Grid>("Toolbar")!;
        var left = this.FindControl<StackPanel>("ToolbarLeft")!;
        var right = this.FindControl<StackPanel>("ToolbarRight")!;

        // The right-hand tools take their own line when both sides don't fit next to each other (measured, so
        // it follows the language and which tools are showing, e.g. expand/collapse only while grouping).
        bool? narrow = null;
        void Arrange()
        {
            if (toolbar.Bounds.Width <= 0)
                return;
            left.Measure(Size.Infinity);
            right.Measure(Size.Infinity);
            var needed = left.DesiredSize.Width + right.DesiredSize.Width;
            var nowNarrow = needed > toolbar.Bounds.Width;
            if (nowNarrow == narrow)
                return;
            narrow = nowNarrow;
            Grid.SetRow(right, nowNarrow ? 1 : 0);
            Grid.SetColumn(right, nowNarrow ? 0 : 1);
            Grid.SetColumnSpan(right, nowNarrow ? 2 : 1);
            right.Margin = new Thickness(0, nowNarrow ? 8 : 0, 0, 0);
        }
        toolbar.SizeChanged += (_, _) => Arrange();
        right.SizeChanged += (_, _) => Arrange();
    }
}
