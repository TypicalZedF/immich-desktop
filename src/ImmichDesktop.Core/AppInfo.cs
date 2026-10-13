using System.Reflection;
using System.Runtime.InteropServices;

namespace ImmichDesktop.Core;

/// <summary>Product identity. Rename the app here.</summary>
public static class AppInfo
{
    public const string Name = "Immich Desktop";

    /// <summary>Stable identifier used for the installer package, IPC names and autostart entries.</summary>
    public const string Id = "ImmichDesktop";

    /// <summary>
    /// GitHub repository ("owner/name") whose Releases provide updates. The releases must be publicly
    /// readable for installed copies to see them. Leave empty to disable update checks.
    /// </summary>
    public const string UpdateRepository = "TypicalZedF/immich-desktop";

    public static string ProjectUrl => $"https://github.com/{UpdateRepository}";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" :
        OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    public static string UserAgent => $"ImmichDesktop/{Version} ({OsName})";
}
