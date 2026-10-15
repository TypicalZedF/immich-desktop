using Avalonia.Data.Converters;

namespace ImmichSplash.App.ViewModels;

public static class Converters
{
    /// <summary>Dims items that are turned off.</summary>
    public static readonly IValueConverter EnabledOpacity = new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.5);

    /// <summary>Videos play in the Immich web app.</summary>
    public static readonly IValueConverter VideoOpenText = new FuncValueConverter<bool, string>(video => video ? Tr.T("Play in Immich") : Tr.T("Open in Immich"));

    public static readonly IValueConverter SlideshowText = new FuncValueConverter<bool, string>(on => on ? Tr.T("Stop slideshow") : Tr.T("Slideshow"));

    public static readonly IValueConverter ArchiveText = new FuncValueConverter<bool, string>(archived => archived ? Tr.T("Unarchive") : Tr.T("Archive"));

    public static readonly IValueConverter LockedText = new FuncValueConverter<bool, string>(locked => locked ? Tr.T("Move out of the locked folder") : Tr.T("Move to locked folder"));

    /// <summary>Placeholder icon for a file on this PC that has no preview.</summary>
    public static readonly IValueConverter LocalFileIcon = new FuncValueConverter<bool, Material.Icons.MaterialIconKind>(video =>
        video ? Material.Icons.MaterialIconKind.FileVideoOutline : Material.Icons.MaterialIconKind.FileImageOutline);
}
