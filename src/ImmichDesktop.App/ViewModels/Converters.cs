using Avalonia.Data.Converters;

namespace ImmichDesktop.App.ViewModels;

public static class Converters
{
    /// <summary>Dims items that are turned off.</summary>
    public static readonly IValueConverter EnabledOpacity = new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.5);

    /// <summary>Videos play in the Immich web app.</summary>
    public static readonly IValueConverter VideoOpenText = new FuncValueConverter<bool, string>(video => video ? Tr.T("Play in Immich") : Tr.T("Open in Immich"));
}
