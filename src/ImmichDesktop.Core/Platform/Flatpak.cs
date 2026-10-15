namespace ImmichDesktop.Core.Platform;

/// <summary>Running as a Flatpak (Linux): some desktop integration goes through the sandbox's rules.</summary>
public static class Flatpak
{
    /// <summary>The Flatpak app ID when running inside one (e.g. io.github.TypicalZedF.ImmichDesktop), else null.</summary>
    public static string? AppId { get; } =
        OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("FLATPAK_ID") is { Length: > 0 } id ? id : null;

    public static bool IsRunning => AppId is not null;

    /// <summary>The command that starts the app from outside the sandbox (autostart).</summary>
    public static string LaunchCommand => $"flatpak run {AppId}";
}
