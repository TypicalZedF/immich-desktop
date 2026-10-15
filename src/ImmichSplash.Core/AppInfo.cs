using System.Reflection;
using System.Runtime.InteropServices;

namespace ImmichSplash.Core;

/// <summary>Product identity. Rename the app here.</summary>
public static class AppInfo
{
    public const string Name = "Immich Splash";

    /// <summary>Stable identifier used for the installer package, IPC names and autostart entries.</summary>
    public const string Id = "ImmichSplash";

    /// <summary>
    /// GitHub repository ("owner/name") whose Releases provide updates. The releases must be publicly
    /// readable for installed copies to see them. Leave empty to disable update checks.
    /// </summary>
    public const string UpdateRepository = "TypicalZedF/immich-splash";

    public static string ProjectUrl => $"https://github.com/{UpdateRepository}";

    /// <summary>
    /// The exact commit this copy was built from (the build adds it to the version as "+sha"), so
    /// links to the source show the code that's actually running. Null for builds outside git.
    /// </summary>
    public static string? SourceCommit { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+') is [_, var sha, ..]
        && System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{7,40}$")
            ? sha
            : null;

    /// <summary>A file or folder in the source code, at the commit this copy was built from.</summary>
    public static string SourceUrl(string path) => $"{ProjectUrl}/tree/{SourceCommit ?? "beta"}/{path}";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" :
        OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    public static string UserAgent => $"ImmichSplash/{Version} ({OsName})";
}
