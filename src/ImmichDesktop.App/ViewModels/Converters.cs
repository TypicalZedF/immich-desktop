using Avalonia.Data.Converters;

namespace ImmichDesktop.App.ViewModels;

public static class Converters
{
    /// <summary>Dims items that are turned off.</summary>
    public static readonly IValueConverter EnabledOpacity = new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.5);
}
