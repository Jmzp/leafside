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

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        Closed += (_, _) =>
        {
            foreach (var view in DocumentViews().ToList()) view.Close();
            ReaderState.Save();
        };
        UpdateEmptyState();
    }

    private IEnumerable<DocumentView> DocumentViews() =>
        Tabs.TabItems.OfType<TabViewItem>().Select(t => t.Content).OfType<DocumentView>();

    public async Task OpenFileAsync(string path)
    {
        // Already open: just switch to it.
        var existing = Tabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(t => t.Content is DocumentView v && string.Equals(v.Document.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Tabs.SelectedItem = existing;
            return;
        }

        string? password = null;
        while (true)
        {
            try
            {
                var document = await Task.Run(() => PdfiumDocument.Open(path, password));
                AddTab(document);
                return;
            }
            catch (PdfPasswordRequiredException)
            {
                password = await AskPasswordAsync(Path.GetFileName(path), retry: password is not null);
                if (password is null) return;
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
        Tabs.SelectedItem is TabViewItem { Content: DocumentView view } ? view.RunBenchmarkAsync() : Task.FromResult("Sin documento.");

    private void AddTab(IPdfDocument document)
    {
        var view = new DocumentView(document);
        var tab = new TabViewItem
        {
            Header = view.Title,
            Content = view,
            IconSource = new FontIconSource { Glyph = "" },
        };
        ToolTipService.SetToolTip(tab, document.FilePath);
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        UpdateEmptyState();
        view.Loaded += (_, _) => view.FocusViewer();
    }

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Content is DocumentView view) view.Close();
        Tabs.TabItems.Remove(tab);
        ReaderState.Save();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool empty = Tabs.TabItems.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Title = Tabs.SelectedItem is TabViewItem { Content: DocumentView v } ? $"{v.Title} - Lector PDF" : "Lector PDF";
    }

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
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEmptyState();

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
