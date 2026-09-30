using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using PdfReader.App.Services;
using PdfReader.Core.Annotations;
using PdfReader.Core.Engine;
using Windows.Storage.Pickers;

namespace PdfReader.App.Controls;

/// <summary>An entry of the Notes pane.</summary>
public sealed record AnnotationItem(PdfAnnotation Annotation, string Header, string Quote, string Comment, Brush Swatch)
{
    public string Glyph => Annotation.Kind == AnnotationKind.Note ? "" : "";
    public bool HasQuote => Quote.Length > 0;
    public bool HasComment => Comment.Length > 0;
    public override string ToString() => string.Join(". ", new[] { Header, Quote, Comment }.Where(s => s.Length > 0)); // accessible name
}

// Highlights and notes: toolbar tools, the Notes pane, the comment editor, undo and saving.
public sealed partial class DocumentView
{
    private const double NotesSidebarWidth = 280;

    private AnnotationEditor _editor = null!;
    // Edits run on the thread pool (they call PDFium); this keeps them, and the UI updates after them, in order.
    private readonly SemaphoreSlim _editLock = new(1, 1);
    // The Notes pane's annotations by page; null until the pane is first shown.
    private Dictionary<int, IReadOnlyList<PdfAnnotation>>? _annotationsByPage;
    private Task? _notesLoad;
    private readonly HashSet<int> _pagesEditedWhileLoading = new();
    private bool _closed;

    /// <summary>True when there are highlights or notes that have not been saved to the file.</summary>
    public bool IsDirty => _editor.IsDirty;

    /// <summary>Raised when <see cref="IsDirty"/> or the file (after Save as) changed.</summary>
    public event EventHandler? DocumentStateChanged;

    private void InitializeAnnotations()
    {
        _editor = new AnnotationEditor(Document);
        AnnotationTools.Visibility = Document.CanEditAnnotations ? Visibility.Visible : Visibility.Collapsed;
        foreach (var color in AnnotationColor.Palette)
        {
            var button = CreateSwatchButton(color, 24);
            button.Click += (_, _) =>
            {
                AppState.HighlightColor = color;
                UpdateHighlightSwatch();
                HighlightColorFlyout.Hide();
                if (Viewer.HasSelection) _ = HighlightSelectionAsync();
            };
            HighlightColorPanel.Children.Add(button);
        }
        UpdateHighlightSwatch();

        Viewer.AnnotationClicked += (_, e) => ShowAnnotationEditor(e.Annotation, Viewer, e.Position);
        Viewer.ContextMenuRequested += (_, e) => ShowContextMenu(e);
        Viewer.NotePlacementRequested += (_, e) =>
        {
            SetNotePlacement(false);
            ShowNewNoteEditor(e);
        };
        Viewer.SelectionCompleted += (_, _) =>
        {
            if (HighlightButton.IsChecked) _ = HighlightSelectionAsync();
        };
        Viewer.PageInvalidated += (_, _) => ReloadThumbnails();
    }

    private static Button CreateSwatchButton(AnnotationColor color, double size)
    {
        var button = new Button
        {
            Padding = new Thickness(4),
            MinWidth = 0,
            MinHeight = 0,
            Content = new Ellipse { Width = size, Height = size, Fill = Brush(color) },
            Tag = color,
        };
        AutomationProperties.SetName(button, ColorName(color));
        ToolTipService.SetToolTip(button, ColorName(color));
        return button;
    }

    private static SolidColorBrush Brush(AnnotationColor color) => new(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));

    private static string ColorName(AnnotationColor color)
    {
        int index = AnnotationColor.Palette.ToList().IndexOf(color);
        return Loc.Get(index switch { 1 => "ColorGreen", 2 => "ColorBlue", 3 => "ColorPink", _ => "ColorYellow" });
    }

    private void UpdateHighlightSwatch() => HighlightSwatch.Fill = Brush(AppState.HighlightColor);

    // --- modes: highlighter and note placement

    private void OnHighlightCheckedChanged(ToggleSplitButton sender, ToggleSplitButtonIsCheckedChangedEventArgs args)
    {
        if (!sender.IsChecked) return;
        SetNotePlacement(false);
        if (Viewer.HasSelection)
        {
            // Something is already selected: highlight it, no need to stay in highlighter mode.
            sender.IsChecked = false;
            _ = HighlightSelectionAsync();
        }
    }

    private void OnNoteButtonClick(object sender, RoutedEventArgs e) => SetNotePlacement(NoteButton.IsChecked == true);

    private void SetNotePlacement(bool on)
    {
        NoteButton.IsChecked = on;
        Viewer.IsPlacingNote = on;
        if (on) HighlightButton.IsChecked = false;
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (!Viewer.IsPlacingNote && !HighlightButton.IsChecked) return; // let the window handle it (full screen)
        SetNotePlacement(false);
        HighlightButton.IsChecked = false;
        e.Handled = true;
    }

    // --- creating annotations

    private async Task HighlightSelectionAsync(bool openEditor = false)
    {
        if (!Document.CanEditAnnotations) return;
        var parts = await Viewer.GetSelectionAsync();
        var color = AppState.HighlightColor;
        PdfAnnotation? last = null;
        foreach (var part in parts)
        {
            var areas = HighlightGeometry.MergeLines(part.Rects);
            if (areas.Count == 0) continue;
            var draft = new AnnotationDraft(AnnotationKind.Highlight, areas, color);
            last = await EditAsync(editor =>
            {
                var added = editor.Add(part.Page, draft);
                return (added, added.PageIndex);
            }) ?? last;
        }
        Viewer.ClearSelection();
        if (openEditor && last is not null) ShowAnnotationEditor(last, Viewer, null);
    }

    private void ShowContextMenu(PagePointEventArgs e)
    {
        var menu = new MenuFlyout();
        if (Viewer.HasSelection)
        {
            menu.Items.Add(MenuItem("MenuCopy", "", () => _ = Viewer.CopySelectionAsync()));
            if (Document.CanEditAnnotations)
            {
                menu.Items.Add(MenuItem("MenuHighlight", "", () => _ = HighlightSelectionAsync()));
                menu.Items.Add(MenuItem("MenuHighlightComment", "", () => _ = HighlightSelectionAsync(openEditor: true)));
            }
        }
        if (Document.CanEditAnnotations && e.Page >= 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("MenuAddNote", "", () => ShowNewNoteEditor(e)));
        }
        if (menu.Items.Count > 0) menu.ShowAt(Viewer, e.Position);
    }

    private static MenuFlyoutItem MenuItem(string key, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key), Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A note is only created once its text is written (an empty note would be clutter).</summary>
    private void ShowNewNoteEditor(PagePointEventArgs e)
    {
        // The icon is centered on the point.
        var area = new PageRect(e.X - 10, e.Y - 10, e.X + 10, e.Y + 10);
        ShowEditorFlyout(null, Viewer, e.Position, e.Page, area);
    }

    private void ShowAnnotationEditor(PdfAnnotation annotation, FrameworkElement target, Windows.Foundation.Point? position) =>
        ShowEditorFlyout(annotation, target, position, annotation.PageIndex, null);

    /// <summary>
    /// The comment editor: text, color and delete. Changes to the text are applied when it closes; a new note
    /// (<paramref name="annotation"/> null) is created then, if it has text.
    /// </summary>
    private void ShowEditorFlyout(PdfAnnotation? annotation, FrameworkElement target, Windows.Foundation.Point? position, int page, PageRect? newNoteArea)
    {
        if (!Document.CanEditAnnotations && annotation is null) return;
        var kind = annotation?.Kind ?? AnnotationKind.Note;
        var color = annotation?.Color ?? (kind == AnnotationKind.Note ? AnnotationColor.Yellow : AppState.HighlightColor);
        bool deleted = false;

        var panel = new StackPanel { Spacing = 10, Width = 280 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Format("AnnotationHeader", Loc.Get(kind == AnnotationKind.Note ? "Note" : "Highlight"), page + 1),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        if (annotation is { Kind: AnnotationKind.Highlight, Text.Length: > 0 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"“{annotation.Text}”",
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        var box = new TextBox
        {
            Text = annotation?.Contents ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            PlaceholderText = Loc.Get(annotation is null ? "NotePlaceholder" : "CommentPlaceholder"),
            IsReadOnly = !Document.CanEditAnnotations,
        };
        AutomationProperties.SetName(box, Loc.Get("CommentLabel"));
        panel.Children.Add(box);

        var bottom = new Grid();
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void MarkSelected()
        {
            foreach (var child in swatches.Children.OfType<Button>())
                child.BorderBrush = child.Tag is AnnotationColor c && c == color
                    ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                    : null;
        }
        foreach (var option in AnnotationColor.Palette)
        {
            var swatch = CreateSwatchButton(option, 16);
            swatch.BorderThickness = new Thickness(2);
            swatch.Click += async (_, _) =>
            {
                color = option;
                MarkSelected();
                if (annotation is null) return; // a new note takes it when created
                if (kind == AnnotationKind.Highlight) AppState.HighlightColor = option;
                var current = annotation;
                annotation = await EditAsync(editor =>
                {
                    var updated = editor.Update(current, current.ToDraft() with { Color = option });
                    return (updated, updated.PageIndex);
                }) ?? annotation;
            };
            swatches.Children.Add(swatch);
        }
        MarkSelected();
        if (Document.CanEditAnnotations) bottom.Children.Add(swatches);

        var flyout = new Flyout { Content = panel };
        if (annotation is not null && Document.CanEditAnnotations)
        {
            var delete = new Button { Content = Loc.Get("DeleteAnnotation"), HorizontalAlignment = HorizontalAlignment.Right };
            delete.Click += (_, _) =>
            {
                deleted = true;
                flyout.Hide();
                var current = annotation;
                _ = EditAsync(editor =>
                {
                    editor.Remove(current);
                    return (true, current.PageIndex);
                });
            };
            bottom.Children.Add(delete);
        }
        panel.Children.Add(bottom);

        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
        flyout.Closed += (_, _) =>
        {
            string text = box.Text.Trim();
            if (deleted || !Document.CanEditAnnotations) return;
            if (annotation is null)
            {
                if (text.Length == 0 || newNoteArea is not { } area) return;
                var draft = new AnnotationDraft(AnnotationKind.Note, [area], color, text);
                _ = EditAsync(editor =>
                {
                    var added = editor.Add(page, draft);
                    return (added, added.PageIndex);
                });
            }
            else if (text != annotation.Contents)
            {
                var current = annotation;
                _ = EditAsync(editor =>
                {
                    var updated = editor.Update(current, current.ToDraft() with { Contents = text });
                    return (updated, updated.PageIndex);
                });
            }
            FocusViewer();
        };
        flyout.ShowAt(target, new FlyoutShowOptions { Position = position, Placement = FlyoutPlacementMode.Auto });
    }

    // --- editing plumbing

    /// <summary>Runs an edit off the UI thread, then refreshes the page, the Notes pane and the toolbar.</summary>
    private async Task<T?> EditAsync<T>(Func<AnnotationEditor, (T Result, int? Page)> edit)
    {
        await _editLock.WaitAsync();
        try
        {
            if (_closed) return default;
            var editor = _editor;
            var (result, page) = await Task.Run(() => edit(editor));
            if (page is int p) OnPageAnnotationsChanged(p);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            if (!_closed) ShowAnnotationError(ex.Message);
            return default;
        }
        catch (ObjectDisposedException)
        {
            return default; // the tab was closed meanwhile
        }
        finally
        {
            _editLock.Release();
            if (!_closed) UpdateEditState();
        }
    }

    private void OnPageAnnotationsChanged(int page)
    {
        Viewer.InvalidatePage(page);
        if (_annotationsByPage is null)
        {
            if (_notesLoad is not null) _pagesEditedWhileLoading.Add(page);
            return;
        }
        _ = RefreshNotesPageAsync(page);
    }

    private void UpdateEditState()
    {
        UndoButton.IsEnabled = _editor.CanUndo;
        SaveButton.IsEnabled = _editor.IsDirty;
        DocumentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void ShowAnnotationError(string message)
    {
        AnnotationBar.Severity = InfoBarSeverity.Error;
        AnnotationBar.Title = Loc.Get("AnnotationFailed");
        AnnotationBar.Message = message;
        AnnotationBar.IsOpen = true;
        await Task.Delay(TimeSpan.FromSeconds(6));
        AnnotationBar.IsOpen = false;
    }

    private static bool IsTyping(UIElement? root) =>
        root?.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is TextBox or PasswordBox or AutoSuggestBox;

    // Focus goes back to the page: the pressed button may just have become disabled (focus would move to the search box).
    private void OnUndo(object sender, RoutedEventArgs e)
    {
        FocusViewer();
        _ = UndoAsync(redo: false);
    }

    private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (IsTyping(this)) return; // the text box's own undo
        e.Handled = true;
        _ = UndoAsync(redo: false);
    }

    private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (IsTyping(this)) return;
        e.Handled = true;
        _ = UndoAsync(redo: true);
    }

    private async Task UndoAsync(bool redo)
    {
        int? page = await EditAsync(editor =>
        {
            int? changed = redo ? editor.Redo() : editor.Undo();
            return (changed, changed);
        });
        if (page is int p && (p < Viewer.CurrentPage - 1 || p > Viewer.CurrentPage + 1)) Viewer.GoToPage(p);
    }

    // --- saving

    private void OnSave(object sender, RoutedEventArgs e)
    {
        FocusViewer(); // the button disables itself once saved
        _ = SaveAsync();
    }

    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (IsDirty) _ = SaveAsync();
    }

    private void OnSaveAsAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (Document.CanEditAnnotations) _ = SaveAsync(saveAs: true);
    }

    /// <summary>
    /// Saves the highlights and notes into the PDF. If the file cannot be written (read-only, open elsewhere),
    /// offers to save a copy somewhere else. Returns false if nothing was saved.
    /// </summary>
    public async Task<bool> SaveAsync(bool saveAs = false)
    {
        string? target = null;
        if (saveAs && (target = await PickSaveTargetAsync()) is null) return false;
        while (true)
        {
            Exception? failure = null;
            await _editLock.WaitAsync();
            try
            {
                var editor = _editor;
                string? path = target;
                await Task.Run(() => editor.Save(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                _editLock.Release();
            }

            if (failure is null)
            {
                // After Save as, the tab shows the new file.
                AppState.Store.Touch(Document.FilePath);
                SaveViewState();
                AppState.RequestSave();
                UpdateEditState();
                return true;
            }
            string reason = failure is UnauthorizedAccessException ? Loc.Get("SaveReadOnly") : Loc.Get("SaveInUse");
            var answer = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Loc.Format("SaveFailedTitle", Title),
                Content = $"{reason}\n\n{Loc.Get("SaveElsewhere")}",
                PrimaryButtonText = Loc.Get("SaveAsButton"),
                CloseButtonText = Loc.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();
            if (answer != ContentDialogResult.Primary || (target = await PickSaveTargetAsync()) is null) return false;
        }
    }

    private async Task<string?> PickSaveTargetAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(Document.FilePath) };
        picker.FileTypeChoices.Add(Loc.Get("PdfDocument"), [".pdf"]);
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>Asks whether to save unsaved highlights and notes. False means the user cancelled closing.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty) return true;
        var answer = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.Format("SaveChangesTitle", Title),
            Content = Loc.Get("SaveChangesText"),
            PrimaryButtonText = Loc.Get("Save"),
            SecondaryButtonText = Loc.Get("DontSave"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        return answer switch
        {
            ContentDialogResult.Primary => await SaveAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    // --- Notes pane

    private void EnsureNotesLoaded() => _notesLoad ??= LoadNotesAsync();

    private async Task LoadNotesAsync()
    {
        NotesProgress.IsActive = true;
        var document = Document;
        var map = new Dictionary<int, IReadOnlyList<PdfAnnotation>>();
        try
        {
            await Task.Run(() =>
            {
                // Page by page, so rendering (which shares the PDFium lock) is never held up for long.
                for (int p = 0; p < document.PageCount; p++)
                {
                    try
                    {
                        var list = document.GetAnnotations(p);
                        if (list.Count > 0) map[p] = list;
                    }
                    catch (InvalidDataException) { } // a broken page
                }
            });
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            NotesProgress.IsActive = false;
        }
        _annotationsByPage = map;
        foreach (int page in _pagesEditedWhileLoading) await RefreshNotesPageAsync(page);
        _pagesEditedWhileLoading.Clear();
        RebuildNotesList();
    }

    private async Task RefreshNotesPageAsync(int page)
    {
        var document = Document;
        IReadOnlyList<PdfAnnotation> list;
        try { list = await Task.Run(() => document.GetAnnotations(page)); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidDataException) { return; }
        if (_annotationsByPage is null) return;
        if (list.Count > 0) _annotationsByPage[page] = list;
        else _annotationsByPage.Remove(page);
        RebuildNotesList();
    }

    private void RebuildNotesList()
    {
        if (_annotationsByPage is null) return;
        var items = _annotationsByPage.OrderBy(kv => kv.Key)
            .SelectMany(kv => kv.Value.OrderBy(a => a.Bounds.Top).ThenBy(a => a.Bounds.Left))
            .Select(a => new AnnotationItem(
                a,
                Loc.Format("AnnotationHeader", Loc.Get(a.Kind == AnnotationKind.Note ? "Note" : "Highlight"), a.PageIndex + 1),
                a.Kind == AnnotationKind.Highlight && a.Text.Length > 0 ? $"“{a.Text}”" : string.Empty,
                a.Contents,
                a.Color is { } c ? Brush(c) : new SolidColorBrush(Microsoft.UI.Colors.Gray)))
            .ToList();
        NotesList.ItemsSource = items;
        NoNotesText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNoteItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AnnotationItem item) Viewer.RevealRect(item.Annotation.PageIndex, item.Annotation.Bounds);
    }

    private void OnEditAnnotationItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AnnotationItem item }) return;
        Viewer.RevealRect(item.Annotation.PageIndex, item.Annotation.Bounds);
        var container = NotesList.ContainerFromItem(item) as FrameworkElement ?? NotesList;
        ShowAnnotationEditor(item.Annotation, container, null);
    }

    private void OnDeleteAnnotationItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AnnotationItem item } || !Document.CanEditAnnotations) return;
        var annotation = item.Annotation;
        _ = EditAsync(editor =>
        {
            editor.Remove(annotation);
            return (true, annotation.PageIndex);
        });
    }
}
