using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PdfReader.App.Controls;
using PdfReader.App.Services;
using PdfReader.Core.Engine;
using PdfReader.Core.Engine.Pdfium;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PdfReader.App;

/// <summary>An entry of the start page's recent list.</summary>
public sealed record RecentItem(string Path, string Name, string Folder, string When);

public sealed partial class MainWindow : Window
{
    private const int RecentCount = 10;

    private readonly DispatcherQueueTimer _exitButtonTimer;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));

        _exitButtonTimer = DispatcherQueue.CreateTimer();
        _exitButtonTimer.Interval = TimeSpan.FromSeconds(2.5);
        _exitButtonTimer.IsRepeating = false;
        _exitButtonTimer.Tick += (_, _) => ExitFullScreenButton.Visibility = Visibility.Collapsed;
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);
        Root.AddHandler(UIElement.TappedEvent, new TappedEventHandler(OnRootTapped), handledEventsToo: true);

        AppState.Saving += () =>
        {
            foreach (var view in DocumentViews()) view.SaveViewState();
        };
        Closed += (_, _) =>
        {
            _closing = true;
            AppState.SaveNow();
            foreach (var view in DocumentViews().ToList()) view.Close();
        };
        UpdateEmptyState();
    }

    private IEnumerable<DocumentView> DocumentViews() =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Tag).OfType<DocumentView>();

    private DocumentView? SelectedView => (Tabs.SelectedItem as TabViewItem)?.Tag as DocumentView;

    /// <summary>
    /// Reopens the previous session's tabs (silently skipping files that are gone or need a password),
    /// then opens <paramref name="files"/>, the last of which ends up selected.
    /// </summary>
    public async Task StartAsync(IReadOnlyList<string> files)
    {
        var session = AppState.Store.Session;
        var previous = session.OpenFiles.ToList();
        int selected = session.SelectedIndex;
        foreach (var path in previous.Where(File.Exists))
            await OpenFileAsync(path, interactive: false);
        if (Tabs.TabItems.Count > 0)
            Tabs.SelectedIndex = Math.Clamp(selected, 0, Tabs.TabItems.Count - 1);
        foreach (var path in files)
            await OpenFileAsync(path);
        UpdateSession();
    }

    public Task OpenFileAsync(string path) => OpenFileAsync(path, interactive: true);

    /// <param name="interactive">False when restoring a session: failures and password prompts are skipped.</param>
    private async Task OpenFileAsync(string path, bool interactive)
    {
        path = Path.GetFullPath(path);
        // Already open: just switch to it.
        var existing = Tabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => t.Tag is DocumentView v && string.Equals(v.Document.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (interactive) Tabs.SelectedItem = existing;
            return;
        }

        string? password = null;
        while (true)
        {
            try
            {
                var document = await Task.Run(() => PdfiumDocument.Open(path, password));
                AddTab(document, select: interactive);
                return;
            }
            catch (PdfPasswordRequiredException) when (interactive)
            {
                password = await AskPasswordAsync(Path.GetFileName(path), retry: password is not null);
                if (password is null) return;
            }
            catch (Exception ex) when (!interactive)
            {
                if (ex is not (PdfPasswordRequiredException or IOException or InvalidDataException or UnauthorizedAccessException))
                    ErrorLog.Write(ex);
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await ShowErrorAsync(Path.GetFileName(path), ex.Message);
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                await ShowErrorAsync(Path.GetFileName(path), $"Error inesperado: {ex.Message}");
                return;
            }
        }
    }

    public Task<string> RunBenchmarkAsync() =>
        SelectedView is { } view ? view.RunBenchmarkAsync() : Task.FromResult("Sin documento.");

    private void AddTab(IPdfDocument document, bool select)
    {
        var view = new DocumentView(document) { Visibility = Visibility.Collapsed };
        view.FullScreenRequested += (_, _) => SetFullScreen(!IsFullScreen);
        var tab = new TabViewItem
        {
            Header = view.Title,
            Tag = view,
            IconSource = new FontIconSource { Glyph = "" },
        };
        ToolTipService.SetToolTip(tab, document.FilePath);
        DocumentHost.Children.Add(view);
        Tabs.TabItems.Add(tab);
        if (select || Tabs.TabItems.Count == 1) Tabs.SelectedItem = tab;
        UpdateEmptyState();
    }

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is DocumentView view)
        {
            view.Close();
            DocumentHost.Children.Remove(view);
        }
        Tabs.TabItems.Remove(tab);
        if (Tabs.TabItems.Count == 0 && IsFullScreen) SetFullScreen(false);
        UpdateEmptyState();
        AppState.RequestSave();
    }

    private void UpdateEmptyState()
    {
        bool empty = Tabs.TabItems.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty) UpdateRecentList();
        var selected = SelectedView;
        foreach (var view in DocumentViews())
            view.Visibility = view == selected ? Visibility.Visible : Visibility.Collapsed;
        Title = selected is not null ? $"{selected.Title} - Lector PDF" : "Lector PDF";
    }

    /// <summary>Keeps the list of open tabs in the saved state, so the next launch can restore them.</summary>
    private void UpdateSession()
    {
        if (_closing) return;
        var session = AppState.Store.Session;
        session.OpenFiles = DocumentViews().Select(v => v.Document.FilePath).ToList();
        session.SelectedIndex = Math.Max(0, Tabs.SelectedIndex);
        AppState.RequestSave();
    }

    // --- recent files

    private void UpdateRecentList()
    {
        var items = AppState.Store.Recent(RecentCount, File.Exists)
            .Select(d => new RecentItem(d.Path, Path.GetFileName(d.Path), Path.GetDirectoryName(d.Path) ?? string.Empty, FormatWhen(d.LastOpened)))
            .ToList();
        RecentList.ItemsSource = items;
        RecentPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatWhen(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var culture = CultureInfo.GetCultureInfo("es-ES");
        if (local.Date == DateTime.Today) return $"Hoy, {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Ayer, {local:HH:mm}";
        return local.Year == DateTime.Today.Year ? local.ToString("d MMM", culture) : local.ToString("d MMM yyyy", culture);
    }

    private void OnRecentClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItem item) _ = OpenFileAsync(item.Path);
    }

    private void OnRemoveRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            AppState.Store.RemoveFromRecent(path);
            AppState.RequestSave();
            UpdateRecentList();
        }
    }

    private void OnShowRecentInFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && File.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // --- full screen

    private bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private void SetFullScreen(bool on)
    {
        if (on == IsFullScreen || (on && SelectedView is null)) return;
        AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        Tabs.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        foreach (var view in DocumentViews()) view.IsChromeVisible = !on;
        if (on) ShowExitFullScreenButton();
        else
        {
            _exitButtonTimer.Stop();
            ExitFullScreenButton.Visibility = Visibility.Collapsed;
        }
        SelectedView?.FocusViewer();
    }

    private void ShowExitFullScreenButton()
    {
        ExitFullScreenButton.Visibility = Visibility.Visible;
        _exitButtonTimer.Stop();
        _exitButtonTimer.Start();
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsFullScreen && e.GetCurrentPoint(Root).Position.Y < 48) ShowExitFullScreenButton();
    }

    private void OnRootTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsFullScreen && e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) ShowExitFullScreenButton();
    }

    private void OnExitFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(false);

    private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SetFullScreen(!IsFullScreen);
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!IsFullScreen) return;
        args.Handled = true;
        SetFullScreen(false);
    }

    // --- dialogs

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files) await OpenFileAsync(file.Path);
    }

    private async Task<string?> AskPasswordAsync(string fileName, bool retry)
    {
        var box = new PasswordBox { PlaceholderText = "Contraseña" };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = retry ? "La contraseña no es correcta. Inténtalo de nuevo." : $"\"{fileName}\" está protegido con contraseña.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Documento protegido",
            Content = content,
            PrimaryButtonText = "Abrir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    private async Task ShowErrorAsync(string fileName, string message)
    {
        await new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"No se pudo abrir \"{fileName}\"",
            Content = message,
            CloseButtonText = "Aceptar",
        }.ShowAsync();
    }

    // --- event handlers

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();
    private void OnAddTabClick(TabView sender, object args) => _ = PickAndOpenAsync();
    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => CloseTab(args.Tab);

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEmptyState();
        UpdateSession();
        var view = SelectedView;
        if (view is not null) DispatcherQueue.TryEnqueue(view.FocusViewer);
    }

    private void OnTabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args) => UpdateSession();

    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = PickAndOpenAsync();
    }

    private void OnCloseTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Tabs.SelectedItem is TabViewItem tab) CloseTab(tab);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Abrir PDF";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var file in items.OfType<StorageFile>().Where(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
            await OpenFileAsync(file.Path);
    }
}
