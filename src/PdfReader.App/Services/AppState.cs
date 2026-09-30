using Microsoft.UI.Dispatching;
using PdfReader.Core.State;

namespace PdfReader.App.Services;

/// <summary>
/// The app's persisted state (%LocalAppData%\PdfReader\state.json): reading positions, recent files,
/// the open tabs and settings. Saved shortly after changes so that a crash or a killed process loses little.
/// </summary>
public static class AppState
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PdfReader", "state.json");

    public static AppStateStore Store { get; } = new(FilePath);

    /// <summary>Raised on the UI thread right before saving, so open documents can record their position.</summary>
    public static event Action? Saving;

    private static DispatcherQueueTimer? _saveTimer;

    /// <summary>Schedules a save (at most one every couple of seconds). Call on the UI thread.</summary>
    public static void RequestSave()
    {
        if (_saveTimer is null)
        {
            _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _saveTimer.Interval = SaveDelay;
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveNow();
        }
        if (!_saveTimer.IsRunning) _saveTimer.Start();
    }

    public static void SaveNow()
    {
        _saveTimer?.Stop();
        Saving?.Invoke();
        Store.Save();
    }
}
