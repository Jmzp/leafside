using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfReader.Core.Engine;

namespace PdfReader.Core.Tests;

/// <summary>
/// Static checks of the app's UI strings (src/PdfReader.App/Strings): every language has the same keys and
/// placeholders, and every key the XAML (x:Uid) and the code (Loc.Get/Format) use exists.
/// </summary>
public sealed partial class LocalizationTests
{
    private static readonly string AppDir = Path.Combine(FindRepoRoot(), "src", "PdfReader.App");
    private static readonly string DefaultLanguage = "en-US";

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PdfReader.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root (PdfReader.sln) not found.");
    }

    private static Dictionary<string, string> Load(string language) =>
        XDocument.Load(Path.Combine(AppDir, "Strings", language, "Resources.resw"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(Path.Combine(AppDir, "Strings"))) data.Add(Path.GetFileName(dir));
        return data;
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_the_same_keys_and_placeholders(string language)
    {
        var reference = Load(DefaultLanguage);
        var strings = Load(language);
        Assert.Equal(reference.Keys.Order(), strings.Keys.Order());
        foreach (var (key, value) in strings)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language}: '{key}' is empty");
            Assert.Equal(Placeholders(reference[key]), Placeholders(value));
        }
    }

    [Fact]
    public void Spanish_is_actually_translated()
    {
        var en = Load(DefaultLanguage);
        var es = Load("es");
        // Shortcut-only or identical words are fine, but most strings must differ.
        Assert.True(en.Count(kv => kv.Value != es[kv.Key]) > en.Count * 0.9);
    }

    [Fact]
    public void Every_xuid_has_strings()
    {
        var keys = Load(DefaultLanguage).Keys;
        var uids = Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => XUidRegex().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToList();
        Assert.NotEmpty(uids);
        Assert.All(uids, uid => Assert.Contains(keys, k => k.StartsWith(uid + ".", StringComparison.Ordinal)));
    }

    [Fact]
    public void Every_key_used_in_code_exists()
    {
        var keys = Load(DefaultLanguage).Keys.ToHashSet();
        var used = SourceFiles()
            .SelectMany(f => LocCallRegex().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.NotEmpty(used);
        // Keys built at run time: one message per open error.
        foreach (var error in Enum.GetNames<PdfOpenError>()) used.Add($"OpenError_{error}");
        Assert.Empty(used.Except(keys));
    }

    [Fact]
    public void Ui_code_has_no_hard_coded_spanish()
    {
        var offenders = SourceFiles()
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => SpanishRegex().IsMatch(x.line) && !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        Assert.Empty(offenders);
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string[] Placeholders(string value) =>
        PlaceholderRegex().Matches(value).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"x:Uid=""([^""]+)""")]
    private static partial Regex XUidRegex();

    [GeneratedRegex(@"Loc\.(?:Get|Format)\(""([^""]+)""")]
    private static partial Regex LocCallRegex();

    // Accented letters or inverted punctuation inside a string literal.
    [GeneratedRegex(@"""[^""]*[áéíóúñÁÉÍÓÚÑ¿¡][^""]*""")]
    private static partial Regex SpanishRegex();
}
