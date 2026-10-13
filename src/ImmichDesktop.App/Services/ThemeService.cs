using Avalonia;
using Avalonia.Styling;
using ImmichDesktop.Core.Settings;

namespace ImmichDesktop.App.Services;

public static class ThemeService
{
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app)
            return;
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
