using System.Globalization;

namespace ImmichDesktop.Core;

/// <summary>
/// Translates text shown to people. The English text is the key, so anything without a translation
/// simply stays English. The app plugs in the chosen language at startup.
/// </summary>
public static class Loc
{
    public static Func<string, string> Translate { get; set; } = s => s;

    public static string T(string english) => Translate(english);

    /// <summary>Translates a format like "Backing up {0}" and fills it in.</summary>
    public static string T(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Translate(englishFormat), args);
}
