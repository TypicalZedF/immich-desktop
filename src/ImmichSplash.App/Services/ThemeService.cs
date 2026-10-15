using Avalonia;
using Avalonia.Styling;
using ImmichSplash.Core.Settings;

namespace ImmichSplash.App.Services;

/// <summary>Light / dark / follow-system theme, shared by the top-bar toggle and Settings.</summary>
public static class ThemeService
{
    /// <summary>Raised on the UI thread after the theme preference changes.</summary>
    public static event Action<AppTheme>? Changed;

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

    /// <summary>Applies and saves a theme preference.</summary>
    public static void Set(AppTheme theme)
    {
        Apply(theme);
        AppServices.Settings.Update(s => s.Theme = theme);
        Changed?.Invoke(theme);
    }

    /// <summary>The theme actually showing right now (resolves "follow system").</summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>The top-bar button: flip between light and dark.</summary>
    public static void Toggle() => Set(IsDark ? AppTheme.Light : AppTheme.Dark);
}
