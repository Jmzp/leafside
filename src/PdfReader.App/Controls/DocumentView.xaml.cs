using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PdfReader.App.Services;
using PdfReader.Core.Engine;
using Windows.System;

namespace PdfReader.App.Controls;

public sealed record ThumbnailItem(int Index)
{
    public string Label => (Index + 1).ToString();
    public override string ToString() => Loc.Format("PageName", Index + 1); // accessible name
}

/// <summary>Wraps an outline entry so the TreeView shows its title.</summary>
public sealed class OutlineNode(OutlineItem item)
{
    public OutlineItem Item { get; } = item;
    public override string ToString() => Item.Title;
}

/// <summary>One open document: toolbar, sidebar (thumbnails / outline) and the viewer.</summary>
public sealed partial class DocumentView : UserControl
{
    private bool _syncingThumbnailSelection;

    public DocumentView(IPdfDocument document)
    {
        InitializeComponent();
        Document = document;
        PageCountText.Text = $"/ {document.PageCount}";

        Viewer.CurrentPageChanged += (_, page) => OnCurrentPageChanged(page);
        Viewer.ZoomChanged += (_, zoom) => ZoomText.Text = $"{zoom:P0}";
        Viewer.SearchResultsChanged += (_, _) => UpdateSearchResultText();
        Viewer.ViewStateChanged += (_, _) => AppState.RequestSave();
        Viewer.HistoryChanged += (_, _) => UpdateHistoryButtons();
        Viewer.ThumbnailsInvalidated += (_, _) => ReloadThumbnails();
        Viewer.NightMode = AppState.NightMode;
        NightToggle.IsChecked = AppState.NightMode;
        AppState.NightModeChanged += OnNightModeChanged;
        Viewer.Open(document, AppState.Store.GetView(document.FilePath));
        AppState.Store.Touch(document.FilePath);
        ZoomText.Text = $"{Viewer.ZoomFactor:P0}";

        ThumbnailList.ItemsSource = Enumerable.Range(0, document.PageCount).Select(i => new ThumbnailItem(i)).ToList();
        _ = LoadOutlineAsync();
    }

    public IPdfDocument Document { get; }
    public string Title => Path.GetFileName(Document.FilePath);

    /// <summary>Raised when the user asks to toggle full screen from the toolbar.</summary>
    public event EventHandler? FullScreenRequested;

    /// <summary>Toolbar and sidebar; hidden in full screen.</summary>
    public bool IsChromeVisible
    {
        get => Toolbar.Visibility == Visibility.Visible;
        set
        {
            Toolbar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ApplySidebarVisibility(value && SidebarToggle.IsChecked == true);
        }
    }

    /// <summary>Records the reading position (unless the viewer is still restoring it).</summary>
    public void SaveViewState()
    {
        if (Viewer.CaptureViewState() is { } view) AppState.Store.SetView(Document.FilePath, view);
    }

    public void Close()
    {
        SaveViewState();
        _printCts?.Cancel();
        AppState.NightModeChanged -= OnNightModeChanged;
        Viewer.Close(); // also disposes the document
    }

    public void FocusViewer() => Viewer.Focus(FocusState.Programmatic);

    public Task<string> RunBenchmarkAsync() => Viewer.RunBenchmarkAsync();

    private void OnCurrentPageChanged(int page)
    {
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), PageBox)) PageBox.Text = (page + 1).ToString();

        _syncingThumbnailSelection = true;
        ThumbnailList.SelectedIndex = page;
        if (Sidebar.Visibility == Visibility.Visible && ThumbnailList.Visibility == Visibility.Visible)
            ThumbnailList.ScrollIntoView(ThumbnailList.SelectedItem);
        _syncingThumbnailSelection = false;
        ZoomText.Text = $"{Viewer.ZoomFactor:P0}";
    }

    // --- page box

    private void OnPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (int.TryParse(PageBox.Text, out int page)) Viewer.GoToPage(Math.Clamp(page, 1, Document.PageCount) - 1);
            FocusViewer();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            FocusViewer();
            e.Handled = true;
        }
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e) => PageBox.Text = (Viewer.CurrentPage + 1).ToString();

    private void OnGoToPageAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        PageBox.Focus(FocusState.Keyboard);
        PageBox.SelectAll();
        e.Handled = true;
    }

    // --- back / forward

    private void UpdateHistoryButtons()
    {
        ForwardButton.IsEnabled = Viewer.CanGoForward;
        BackButton.IsEnabled = Viewer.CanGoBack;
    }

    // Focus goes back to the page: the pressed button may just have become disabled.
    private void OnBack(object sender, RoutedEventArgs e) { Viewer.GoBack(); FocusViewer(); }
    private void OnForward(object sender, RoutedEventArgs e) { Viewer.GoForward(); FocusViewer(); }
    private void OnBackAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Viewer.GoBack(); e.Handled = true; }
    private void OnForwardAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Viewer.GoForward(); e.Handled = true; }

    // --- printing

    private CancellationTokenSource? _printCts;
    private bool _printBusy; // dialog open or job running: one at a time

    private void OnPrint(object sender, RoutedEventArgs e) => _ = PrintAsync();
    private void OnPrintAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { _ = PrintAsync(); e.Handled = true; }
    private void OnCancelPrint(object sender, RoutedEventArgs e) => _printCts?.Cancel();

    private async Task PrintAsync()
    {
        if (_printBusy || XamlRoot is null) return;
        _printBusy = true;
        try
        {
            await PrintCoreAsync();
        }
        finally
        {
            _printBusy = false;
        }
    }

    private async Task PrintCoreAsync()
    {
        // The dialog is modal but keeps pumping messages, so this method can be re-entered; _printBusy prevents that.
        var owner = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        PrintJob? job;
        try
        {
            job = PrintService.ShowDialog(owner, Document.PageCount, Math.Max(0, Viewer.CurrentPage));
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            ShowPrintResult(InfoBarSeverity.Error, Loc.Get("PrintFailed"), ex.Message);
            return;
        }
        if (job is null) return;

        using (job)
        {
            var cts = _printCts = new CancellationTokenSource();
            int total = job.Pages.Count;
            PrintBar.Severity = InfoBarSeverity.Informational;
            PrintBar.Title = Loc.Get("Printing");
            PrintBar.Message = Loc.Format("PrintProgress", 0, total);
            PrintCancelButton.Visibility = Visibility.Visible;
            PrintBar.IsOpen = true;
            var progress = new Progress<int>(done => PrintBar.Message = Loc.Format("PrintProgress", done, total));
            try
            {
                var document = Document;
                string name = Title;
                await Task.Run(() => PrintService.Print(document, name, job, progress, cts.Token));
                ShowPrintResult(InfoBarSeverity.Success, Loc.Get("PrintDone"), Loc.Format("PrintDonePages", total));
            }
            catch (OperationCanceledException)
            {
                PrintBar.IsOpen = false;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
            {
                ShowPrintResult(InfoBarSeverity.Error, Loc.Get("PrintFailed"), ex.Message);
            }
            finally
            {
                _printCts = null;
                cts.Dispose();
            }
        }
    }

    private async void ShowPrintResult(InfoBarSeverity severity, string title, string message)
    {
        PrintBar.Severity = severity;
        PrintBar.Title = title;
        PrintBar.Message = message;
        PrintCancelButton.Visibility = Visibility.Collapsed;
        PrintBar.IsOpen = true;
        await Task.Delay(TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 8 : 4));
        if (_printCts is null) PrintBar.IsOpen = false;
    }

    // --- night mode

    private void OnNightModeClick(object sender, RoutedEventArgs e) => AppState.NightMode = NightToggle.IsChecked == true;

    private void OnNightModeChanged()
    {
        NightToggle.IsChecked = AppState.NightMode;
        Viewer.NightMode = AppState.NightMode;
    }

    /// <summary>The viewer discarded its thumbnails: ask the visible sidebar items for new ones.</summary>
    private void ReloadThumbnails()
    {
        for (int i = 0; i < ThumbnailList.Items.Count; i++)
        {
            if (ThumbnailList.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: StackPanel panel } &&
                panel.Children[0] is ThumbnailView thumb)
                thumb.Show(Viewer, i);
        }
    }

    // --- zoom

    private void OnZoomIn(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void OnZoomOut(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void OnFitWidth(object sender, RoutedEventArgs e) => Viewer.FitWidth();
    private void OnFitPage(object sender, RoutedEventArgs e) => Viewer.FitPage();
    private void OnZoomInAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Viewer.ZoomIn(); e.Handled = true; }
    private void OnZoomOutAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Viewer.ZoomOut(); e.Handled = true; }
    private void OnFitWidthAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Viewer.FitWidth(); e.Handled = true; }

    // --- search

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        SearchBox.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Enter on the same query moves to the next match, like browsers do.
        if (Viewer.MatchCount > 0 && _lastQuery == args.QueryText) Viewer.NextMatch();
        else
        {
            _lastQuery = args.QueryText;
            _ = Viewer.SearchAsync(args.QueryText);
        }
    }

    private string? _lastQuery;

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrEmpty(sender.Text))
        {
            _lastQuery = null;
            Viewer.ClearSearch();
        }
    }

    private void OnNextMatch(object sender, RoutedEventArgs e) => Viewer.NextMatch();
    private void OnPreviousMatch(object sender, RoutedEventArgs e) => Viewer.PreviousMatch();
    private void OnNextMatchAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Viewer.NextMatch(); e.Handled = true; }
    private void OnPreviousMatchAccelerator(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Viewer.PreviousMatch(); e.Handled = true; }

    private void UpdateSearchResultText()
    {
        if (string.IsNullOrEmpty(_lastQuery)) SearchResultText.Text = string.Empty;
        else if (Viewer.MatchCount == 0) SearchResultText.Text = Loc.Get("NoResults");
        else SearchResultText.Text = Loc.Format("MatchPosition", Viewer.CurrentMatchIndex + 1, Viewer.MatchCount);
    }

    // --- sidebar

    private void OnSidebarToggled(object sender, RoutedEventArgs e)
    {
        if (Sidebar is null) return; // raised while the XAML is still loading
        ApplySidebarVisibility(SidebarToggle.IsChecked == true);
    }

    private void ApplySidebarVisibility(bool show)
    {
        Sidebar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = show ? new GridLength(200) : new GridLength(0);
    }

    private void OnFullScreen(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke(this, EventArgs.Empty);

    private void OnSidebarSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ThumbnailList is null || OutlinePane is null) return;
        bool thumbnails = sender.SelectedItem == ThumbnailsTab;
        ThumbnailList.Visibility = thumbnails ? Visibility.Visible : Visibility.Collapsed;
        OutlinePane.Visibility = thumbnails ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnThumbnailContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not StackPanel panel || panel.Children[0] is not ThumbnailView thumb) return;
        if (args.InRecycleQueue) thumb.Clear();
        else if (args.Item is ThumbnailItem item) thumb.Show(Viewer, item.Index);
    }

    private void OnThumbnailClicked(object sender, ItemClickEventArgs e)
    {
        if (!_syncingThumbnailSelection && e.ClickedItem is ThumbnailItem item) Viewer.GoToPage(item.Index);
    }

    private async Task LoadOutlineAsync()
    {
        var document = Document;
        IReadOnlyList<OutlineItem> outline;
        try { outline = await Task.Run(document.GetOutline); }
        catch (ObjectDisposedException) { return; }

        foreach (var item in outline) OutlineTree.RootNodes.Add(CreateNode(item));
        NoOutlineText.Visibility = outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TreeViewNode CreateNode(OutlineItem item)
    {
        var node = new TreeViewNode { Content = new OutlineNode(item) };
        foreach (var child in item.Children) node.Children.Add(CreateNode(child));
        return node;
    }

    private void OnOutlineItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: OutlineNode node } && node.Item.PageIndex >= 0)
            Viewer.GoToPage(node.Item.PageIndex);
    }
}
