using System.Text.Json;
using System.Text.Json.Nodes;

namespace PdfReader.Core.State;

public sealed class DocumentState
{
    /// <summary>Path with its original casing, for display.</summary>
    public string Path { get; set; } = string.Empty;
    public ViewState? View { get; set; }
    public DateTime LastOpened { get; set; }
    /// <summary>False once the user removed it from the recent list (its position is still remembered).</summary>
    public bool ShowInRecent { get; set; } = true;
}

public sealed class SessionState
{
    public List<string> OpenFiles { get; set; } = [];
    public int SelectedIndex { get; set; }
}

public sealed class AppSettings
{
    public bool NightMode { get; set; }
    /// <summary>Index in <see cref="Engine.AnnotationColor.Palette"/> of the last highlight color used.</summary>
    public int HighlightColor { get; set; }
    /// <summary>Check GitHub for a newer version once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    /// <summary>A version the user chose to skip: not offered again by the automatic check.</summary>
    public string? SkippedVersion { get; set; }
}

public sealed class AppStateData
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, DocumentState> Documents { get; set; } = new();
    public SessionState Session { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

/// <summary>
/// Per-user reader state (positions, recent files, open tabs, settings) persisted as JSON.
/// Not thread-safe: use it from one thread (the UI thread).
/// </summary>
public sealed class AppStateStore
{
    public const int MaxDocuments = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _filePath;
    private AppStateData? _data;

    public AppStateStore(string filePath) => _filePath = filePath;

    public AppStateData Data => _data ??= Load();
    public SessionState Session => Data.Session;
    public AppSettings Settings => Data.Settings;

    public ViewState? GetView(string pdfPath) =>
        Data.Documents.TryGetValue(Key(pdfPath), out var entry) ? entry.View : null;

    public void SetView(string pdfPath, ViewState view) => GetOrAdd(pdfPath).View = view;

    /// <summary>Records that a file was opened now: it moves to the top of the recent list.</summary>
    public void Touch(string pdfPath)
    {
        var entry = GetOrAdd(pdfPath);
        entry.Path = Path.GetFullPath(pdfPath);
        entry.LastOpened = DateTime.UtcNow;
        entry.ShowInRecent = true;
    }

    public void RemoveFromRecent(string pdfPath)
    {
        if (Data.Documents.TryGetValue(Key(pdfPath), out var entry)) entry.ShowInRecent = false;
    }

    /// <summary>Most recently opened files first.</summary>
    public IReadOnlyList<DocumentState> Recent(int count, Func<string, bool>? exists = null) =>
        Data.Documents.Values
            .Where(d => d.ShowInRecent && d.LastOpened > DateTime.MinValue && (exists?.Invoke(d.Path) ?? true))
            .OrderByDescending(d => d.LastOpened)
            .Take(count)
            .ToList();

    /// <summary>Writes the state atomically (temp file + rename). Returns false if it could not be written.</summary>
    public bool Save()
    {
        if (_data is null) return true;
        Trim(_data);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private DocumentState GetOrAdd(string pdfPath)
    {
        string key = Key(pdfPath);
        if (!Data.Documents.TryGetValue(key, out var entry))
        {
            entry = new DocumentState { Path = Path.GetFullPath(pdfPath), LastOpened = DateTime.UtcNow };
            Data.Documents[key] = entry;
        }
        return entry;
    }

    private static void Trim(AppStateData data)
    {
        if (data.Documents.Count <= MaxDocuments) return;
        foreach (var old in data.Documents.OrderBy(d => d.Value.LastOpened).Take(data.Documents.Count - MaxDocuments).ToList())
            data.Documents.Remove(old.Key);
    }

    internal static string Key(string path) => Path.GetFullPath(path).ToUpperInvariant();

    private AppStateData Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new AppStateData();
            return Parse(File.ReadAllText(_filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppStateData();
        }
    }

    /// <summary>
    /// Reads the current format, or the first one (a map of path → { Page, LastOpened }), which is migrated.
    /// Unreadable content yields an empty state rather than an error.
    /// </summary>
    internal static AppStateData Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return new AppStateData();
            if (root.ContainsKey(nameof(AppStateData.Version)))
            {
                var data = root.Deserialize<AppStateData>(JsonOptions) ?? new AppStateData();
                data.Documents ??= new();
                data.Session ??= new();
                data.Session.OpenFiles ??= [];
                data.Settings ??= new();
                return data;
            }

            var migrated = new AppStateData();
            foreach (var (key, value) in root)
            {
                if (value is not JsonObject old) continue;
                int page = old["Page"]?.GetValue<int>() ?? 0;
                var lastOpened = old["LastOpened"]?.GetValue<DateTime>() ?? DateTime.MinValue;
                migrated.Documents[key.ToUpperInvariant()] = new DocumentState
                {
                    Path = key,
                    View = new ViewState(page),
                    LastOpened = lastOpened,
                };
            }
            return migrated;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new AppStateData();
        }
    }
}
