using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImmichSplash.Core.Tests;

/// <summary>The translation files (copied from the app's Assets/i18n) stay complete and well-formed.</summary>
public partial class TranslationTests
{
    static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "i18n");

    static Dictionary<string, Dictionary<string, string>> Load() =>
        Directory.GetFiles(Dir, "*.json").ToDictionary(
            f => Path.GetFileNameWithoutExtension(f),
            f => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))!);

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    static string[] Placeholders(string s) =>
        Placeholder().Matches(s).Select(m => m.Value).Distinct().Order().ToArray();

    [Fact]
    public void Every_language_has_the_same_texts()
    {
        var languages = Load();
        Assert.True(languages.Count >= 4, $"Expected the translation files in {Dir}");
        var all = languages.Values.SelectMany(d => d.Keys).ToHashSet();
        foreach (var (code, strings) in languages)
        {
            var missing = all.Where(k => !strings.ContainsKey(k)).Order().ToList();
            Assert.True(missing.Count == 0, $"{code}.json is missing: {string.Join(" | ", missing)}");
        }
    }

    [Fact]
    public void Translations_keep_their_placeholders()
    {
        foreach (var (code, strings) in Load())
            foreach (var (english, translated) in strings)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated), $"{code}.json: empty translation for \"{english}\"");
                Assert.True(Placeholders(english).SequenceEqual(Placeholders(translated)),
                    $"{code}.json: \"{translated}\" doesn't have the same {{0}} placeholders as \"{english}\"");
            }
    }
}
