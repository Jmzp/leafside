using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfReader.App.Services;
using PdfReader.Core.Updates;

namespace PdfReader.App;

/// <summary>
/// The update check: once a day at startup (unless turned off on the start page) or on demand, a bar under the
/// tabs offers a newer release from GitHub. The installed app downloads the installer and runs it; a portable
/// copy downloads the zip to the Downloads folder.
/// </summary>
public sealed partial class MainWindow
{
    private enum UpdateState { Offer, Downloading, Error }

    private UpdateRelease? _update;
    private UpdateAsset? _updateAsset;
    private UpdateState _updateState;
    private CancellationTokenSource? _updateDownload;
    private bool _checkingForUpdates;

    private void InitializeUpdates()
    {
        VersionText.Text = Loc.Format("VersionText", UpdateService.CurrentVersion.ToString(3));
        AutoUpdateCheck.IsChecked = AppState.Store.Settings.CheckForUpdates;
        Closed += (_, _) => _updateDownload?.Cancel();
    }

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        bool on = AutoUpdateCheck.IsChecked == true;
        if (AppState.Store.Settings.CheckForUpdates == on) return;
        AppState.Store.Settings.CheckForUpdates = on;
        AppState.RequestSave();
    }

    private void OnCheckUpdatesClick(object sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// Asks GitHub for the latest release. The automatic check is silent (no network, no news: nothing shown)
    /// and skips a version the user dismissed; a manual one always reports its result.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var settings = AppState.Store.Settings;
        if (!AppState.PersistenceEnabled || _checkingForUpdates || _updateState == UpdateState.Downloading) return;
        if (!manual && (!settings.CheckForUpdates || !UpdateCheck.IsDue(settings.LastUpdateCheck, DateTimeOffset.Now))) return;

        _checkingForUpdates = true;
        CheckUpdatesLink.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var release = await Task.Run(() => UpdateService.CheckAsync(timeout.Token));
            settings.LastUpdateCheck = DateTimeOffset.Now;
            AppState.RequestSave();
            if (_closing) return;

            if (release is null)
            {
                if (manual) ShowUpdateMessage(InfoBarSeverity.Success, Loc.Get("UpdateNoneTitle"),
                    Loc.Format("UpdateNoneMessage", UpdateService.CurrentVersion.ToString(3)));
                return;
            }
            if (!manual && settings.SkippedVersion == release.Version.ToString(3)) return;
            OfferUpdate(release);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline or GitHub unreachable: the daily check tries again tomorrow without bothering anyone.
            if (manual && !_closing) ShowUpdateMessage(InfoBarSeverity.Warning, Loc.Get("UpdateCheckFailedTitle"), Loc.Get("UpdateCheckFailedMessage"));
        }
        finally
        {
            _checkingForUpdates = false;
            CheckUpdatesLink.IsEnabled = true;
        }
    }

    private void OfferUpdate(UpdateRelease release)
    {
        _update = release;
        _updateAsset = UpdateCheck.PickAsset(release, UpdateService.RuntimeId, UpdateService.IsInstalled);
        _updateState = UpdateState.Offer;
        string version = release.Version.ToString(3);
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.Title = Loc.Format("UpdateAvailableTitle", version);
        UpdateBar.Message = Loc.Format("UpdateAvailableMessage", UpdateService.CurrentVersion.ToString(3));
        // Without a file for this machine, the button opens the release page instead.
        UpdateActionButton.Content = _updateAsset is null ? Loc.Get("UpdateOpenPage")
            : _updateAsset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? Loc.Get("UpdateInstall") : Loc.Get("UpdateDownloadZip");
        UpdateActionButton.Visibility = Visibility.Visible;
        UpdateActionButton.IsEnabled = true;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateLinks.Visibility = Visibility.Visible;
        UpdateSkipLink.Visibility = Visibility.Visible;
        UpdateBar.IsClosable = true;
        UpdateBar.IsOpen = true;
    }

    private void ShowUpdateMessage(InfoBarSeverity severity, string title, string message)
    {
        _updateState = UpdateState.Error;
        UpdateBar.Severity = severity;
        UpdateBar.Title = title;
        UpdateBar.Message = message;
        UpdateActionButton.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Collapsed;
        // The release page stays reachable after a failed download.
        UpdateLinks.Visibility = _update is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSkipLink.Visibility = Visibility.Collapsed;
        UpdateBar.IsClosable = true;
        UpdateBar.IsOpen = true;
    }

    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_update is null || _updateState != UpdateState.Offer) return;
        if (_updateAsset is not { } asset)
        {
            OpenReleasePage();
            return;
        }
        bool installer = asset.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

        _updateState = UpdateState.Downloading;
        _updateDownload = new CancellationTokenSource();
        UpdateBar.Title = Loc.Format("UpdateDownloadingTitle", _update.Version.ToString(3));
        UpdateBar.Message = "";
        UpdateActionButton.IsEnabled = false;
        UpdateSkipLink.Visibility = Visibility.Collapsed;
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(p => UpdateProgress.Value = p);
        string path;
        try
        {
            string folder = UpdateService.DownloadFolder(installer);
            var token = _updateDownload.Token;
            path = await Task.Run(() => UpdateService.DownloadAsync(asset, folder, progress, token));
        }
        catch (OperationCanceledException) when (_updateDownload.IsCancellationRequested)
        {
            // Cancelled by closing the bar (or the window).
            _updateState = UpdateState.Offer;
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or
                                       OperationCanceledException or InvalidDataException)
        {
            if (!_closing) ShowUpdateMessage(InfoBarSeverity.Error, Loc.Get("UpdateDownloadFailedTitle"),
                Loc.Get(ex is InvalidDataException ? "UpdateDamagedMessage" : "UpdateCheckFailedMessage"));
            return;
        }
        if (_closing) return;

        if (!installer)
        {
            UpdateService.ShowInFolder(path);
            ShowUpdateMessage(InfoBarSeverity.Success, Loc.Get("UpdateZipReadyTitle"), Loc.Get("UpdateZipReadyMessage"));
            return;
        }

        // The installer replaces the app's files, so the app closes first (asking about unsaved notes).
        UpdateBar.Title = Loc.Get("UpdateReadyTitle");
        UpdateBar.Message = Loc.Get("UpdateReadyMessage");
        if (!await ConfirmCloseAllAsync())
        {
            _updateState = UpdateState.Offer;
            UpdateActionButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            UpdateService.RunInstaller(path);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowUpdateMessage(InfoBarSeverity.Error, Loc.Get("UpdateDownloadFailedTitle"), Loc.Get("UpdateCheckFailedMessage"));
            return;
        }
        _closeConfirmed = true;
        Close();
    }

    private void OnUpdateNotesClick(object sender, RoutedEventArgs e) => OpenReleasePage();

    private void OpenReleasePage() =>
        _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_update?.PageUrl ?? UpdateCheck.ReleasesPage));

    private void OnUpdateSkipClick(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        AppState.Store.Settings.SkippedVersion = _update.Version.ToString(3);
        AppState.RequestSave();
        UpdateBar.IsOpen = false;
    }

    private void OnUpdateBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (_updateState == UpdateState.Downloading) _updateDownload?.Cancel();
    }
}
