using System.Text.Json;

namespace PdfReader.App.Services;

/// <summary>Remembers the last page read for each file, in %LocalAppData%\PdfReader\state.json.</summary>
public static class ReaderState
{
    private const int MaxEntries = 500;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfReader", "state.json");

    private static Dictionary<string, Entry>? _entries;

    public sealed record Entry(int Page, DateTime LastOpened);

    public static int GetLastPage(string pdfPath) =>
        Load().TryGetValue(Normalize(pdfPath), out var e) ? e.Page : 0;

    public static void SetLastPage(string pdfPath, int page)
    {
        var entries = Load();
        entries[Normalize(pdfPath)] = new Entry(page, DateTime.UtcNow);
        if (entries.Count > MaxEntries)
        {
            foreach (var old in entries.OrderBy(e => e.Value.LastOpened).Take(entries.Count - MaxEntries).ToList())
                entries.Remove(old.Key);
        }
    }

    public static void Save()
    {
        if (_entries is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Dictionary<string, Entry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            if (File.Exists(FilePath))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return _entries ??= new Dictionary<string, Entry>();
    }

    private static string Normalize(string path) => Path.GetFullPath(path).ToUpperInvariant();
}
