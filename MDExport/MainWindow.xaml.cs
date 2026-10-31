using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Snippets;
using MDExport.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MDExport;

public partial class MainWindow : Window
{
    public static readonly RoutedCommand InsertSnippetCommand =
        new("InsertSnippet", typeof(MainWindow));

    private readonly DispatcherTimer _previewTimer;
    private CoreWebView2Environment? _webViewEnvironment;
    private string? _currentFilePath;
    private bool _isModified;
    private bool _webViewReady;
    private bool _previewDocumentReady;
    private string _lastRenderedHtml = string.Empty;

    // Relative image paths in the markdown are resolved against the saved file's folder.
    // Untitled documents have no anchor, so only absolute paths resolve for them.
    private string? DocumentDirectory =>
        string.IsNullOrEmpty(_currentFilePath) ? null : Path.GetDirectoryName(_currentFilePath);

    private int _suppressEditorScrollEcho;
    private int _suppressEditorSelectionEcho;
    private int _lastSentEditorScrollLine = -1;
    private int _lastEditorSelStart = -1;
    private int _lastEditorSelEnd = -1;
    private readonly DispatcherTimer _editorScrollDebounce;
    private readonly DispatcherTimer _editorSelectionDebounce;
    private const double WordsPerMinute = 200;

    private const string DefaultMarkdown =
        "# Welcome to MDExport\n\n" +
        "Press **F2** to edit this page directly, or choose **Split** to work in Markdown with a live preview.\n\n" +
        "## Features\n\n" +
        "- Live preview (WebView2)\n" +
        "- Export to **HTML**, **PDF**, and **DOCX**\n" +
        "- Clean, minimalist dark UI\n\n" +
        "## Example\n\n" +
        "```csharp\n" +
        "Console.WriteLine(\"Hello, MDExport!\");\n" +
        "```\n\n" +
        "> Tip: use `Ctrl+S` to save and the **Export** menu for other formats.\n";

    public MainWindow() : this(null) { }

    public MainWindow(string? initialFilePath)
    {
        InitializeComponent();
        InitializeSession();
        InitializeEditor();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _previewTimer.Tick += PreviewTimer_Tick;

        _editorScrollDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _editorScrollDebounce.Tick += EditorScrollDebounce_Tick;

        _editorSelectionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _editorSelectionDebounce.Tick += EditorSelectionDebounce_Tick;

        Editor.TextChanged += Editor_TextChanged;
        Editor.TextArea.TextView.ScrollOffsetChanged += EditorTextView_ScrollOffsetChanged;
        Editor.TextArea.SelectionChanged += EditorTextArea_SelectionChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        if (!string.IsNullOrEmpty(initialFilePath) && File.Exists(initialFilePath))
        {
            try
            {
                Editor.Text = _lastDiskText = File.ReadAllText(initialFilePath);
                _currentFilePath = initialFilePath;
                RememberRecent(initialFilePath);
            }
            catch (Exception ex)
            {
                Editor.Text = DefaultMarkdown;
                MessageBox.Show(this, "Failed to open file:\n" + ex.Message,
                    "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            Editor.Text = DefaultMarkdown;
        }

        _isModified = false;
        WatchCurrentFile();
        UpdateTitle();
        UpdateStatus();
        RefreshOutline();
        PopulateInsertMenu();
        PopulateEditorContextMenu();
    }

    // --------------------- Snippet insertion ---------------------

    private void PopulateInsertMenu()
    {
        InsertMenu.Items.Clear();
        foreach (var category in BuildSnippetCategoryItems())
            InsertMenu.Items.Add(category);
    }

    private void PopulateEditorContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(BuildCommandMenuItem("_Undo", ApplicationCommands.Undo));
        menu.Items.Add(BuildCommandMenuItem("_Redo", ApplicationCommands.Redo));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildCommandMenuItem("Cu_t", ApplicationCommands.Cut));
        menu.Items.Add(BuildCommandMenuItem("_Copy", ApplicationCommands.Copy));
        menu.Items.Add(BuildCommandMenuItem("_Paste", ApplicationCommands.Paste));
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildCommandMenuItem("Select _All", ApplicationCommands.SelectAll));
        menu.Items.Add(new Separator());

        var insertHeader = new MenuItem { Header = "_Insert" };
        foreach (var category in BuildSnippetCategoryItems())
            insertHeader.Items.Add(category);
        menu.Items.Add(insertHeader);

        Editor.ContextMenu = menu;
    }

    private IEnumerable<MenuItem> BuildSnippetCategoryItems()
    {
        foreach (var group in SnippetLibrary.All.GroupBy(s => s.Category))
        {
            var categoryItem = new MenuItem { Header = group.Key };
            foreach (var snip in group)
            {
                var item = new MenuItem
                {
                    Header = snip.Title,
                    InputGestureText = snip.Shortcut ?? string.Empty,
                    Tag = snip.Id
                };
                item.Click += SnippetMenuItem_Click;
                categoryItem.Items.Add(item);
            }
            yield return categoryItem;
        }
    }

    private MenuItem BuildCommandMenuItem(string header, RoutedUICommand command) =>
        new()
        {
            Header = header,
            Command = command,
            CommandTarget = Editor.TextArea
        };

    private void SnippetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.Tag is string id)
            InsertSnippet(id);
    }

    private void InsertSnippet_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string id)
        {
            InsertSnippet(id);
            e.Handled = true;
        }
    }

    private void InsertSnippet(string id)
    {
        var def = SnippetLibrary.Get(id);
        if (def == null) return;

        if (IsEditView && SnippetsInEditView.TryGetValue(id, out var pageCommand))
        {
            RunPageCommand(pageCommand.Command, pageCommand.Argument);
            return;
        }

        EnsureEditorVisible();

        var ta = Editor.TextArea;
        var sel = ta.Selection.IsEmpty ? string.Empty : ta.Selection.GetText();
        var snippet = def.Build(sel);

        if (def.IsBlock)
        {
            var caret = ta.Caret.Offset;
            var line = Editor.Document.GetLineByOffset(caret);
            var prefix = Editor.Document.GetText(line.Offset, caret - line.Offset);
            if (!string.IsNullOrEmpty(prefix))
                snippet.Elements.Insert(0, new SnippetTextElement { Text = "\n" });
        }

        snippet.Insert(ta);
        Editor.Focus();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(AppSettings.DataFolder, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder).ConfigureAwait(true);
            await Preview.EnsureCoreWebView2Async(_webViewEnvironment).ConfigureAwait(true);
            Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            // Must stay enabled for ContextMenuRequested to fire; the handler replaces the
            // native menu with the themed one.
            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Otherwise Ctrl+P, Ctrl+F and friends open browser UI when the preview has focus.
            Preview.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            Preview.CoreWebView2.ContextMenuRequested += Preview_ContextMenuRequested;
            Preview.CoreWebView2.WebMessageReceived += Preview_WebMessageReceived;
            Preview.CoreWebView2.NavigationCompleted += Preview_NavigationCompleted;
            _webViewReady = true;
            Preview.ZoomFactor = _settings.Zoom;
            ApplyPreviewTheme();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to initialize preview:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Kept out of the block above so a render failure is not reported as a WebView2
        // startup failure — they have entirely different causes.
        if (_webViewReady)
        {
            try
            {
                RenderPreviewNow();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to render preview:\n" + ex.Message,
                    "MDExport", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        OfferRecovery();
        _ = CheckForUpdatesSilentlyAsync();
    }

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            var info = await Services.UpdateChecker.FetchLatestReleaseAsync();
            if (!info.IsNewerThan(Services.UpdateChecker.GetCurrentVersion())) return;

            UpdateBadge.Content = $"Update available — {info.TagName}";
            UpdateBadge.Visibility = Visibility.Visible;
        }
        catch
        {
            // Silent: no network, rate-limited, etc. — user can still trigger from About.
        }
    }

    private void UpdateBadge_Click(object sender, RoutedEventArgs e)
    {
        Services.UpdateChecker.OpenReleasePage(Services.UpdateChecker.LatestReleasePageUrl);
    }

    // --------------------- Editor / preview ---------------------

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        _isModified = true;
        _searchDirty = true;
        UpdateTitle();
        UpdateStatus();
        _previewTimer.Stop();
        _previewTimer.Start();
        _recoveryTimer.Stop();
        _recoveryTimer.Start();
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        RenderPreviewNow();
        RefreshOutline();
    }

    private void RenderPreviewNow()
    {
        if (!_webViewReady || Preview.CoreWebView2 == null) return;
        var body = MarkdownRenderer.RenderPreviewBody(Editor.Text, DocumentDirectory);
        if (body == _lastRenderedHtml) return;
        _lastRenderedHtml = body;

        // Once a page is up, patch it in place so editing does not reload the document.
        if (_previewDocumentReady)
        {
            PostToPage(new { kind = IsEditView ? "reset" : "body", html = body, docLength = Editor.Document.TextLength });
            return;
        }

        HtmlDocumentHost.NavigateToHtml(Preview.CoreWebView2,
            MarkdownRenderer.RenderFullPage(Editor.Text, GetTitle(), DocumentDirectory, _settings.DarkPreview));
    }

    private void RenderPreviewFresh()
    {
        _lastRenderedHtml = string.Empty;
        _previewDocumentReady = false;
        RenderPreviewNow();
    }

    // --------------------- Sync scroll / selection ---------------------

    private void Preview_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _previewDocumentReady = true;
        // Reset tracking so we force-send current state after re-render.
        _lastSentEditorScrollLine = -1;
        _lastEditorSelStart = -2;
        _lastEditorSelEnd = -2;
        PushEditorScrollToPreview();
        PushEditorSelectionToPreview();
        SyncEditViewToPage();
    }

    private void EditorTextView_ScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorScrollEcho > 0) { _suppressEditorScrollEcho--; return; }
        _editorScrollDebounce.Stop();
        _editorScrollDebounce.Start();
    }

    private void EditorScrollDebounce_Tick(object? sender, EventArgs e)
    {
        _editorScrollDebounce.Stop();
        PushEditorScrollToPreview();
    }

    private void PushEditorScrollToPreview()
    {
        if (!_previewDocumentReady || Preview.CoreWebView2 == null) return;
        var line = GetEditorTopVisibleLine();
        if (line <= 0 || line == _lastSentEditorScrollLine) return;
        _lastSentEditorScrollLine = line;
        _ = Preview.CoreWebView2.ExecuteScriptAsync(
            $"window.mdScrollToLine && window.mdScrollToLine({line});");
    }

    private int GetEditorTopVisibleLine()
    {
        var tv = Editor.TextArea.TextView;
        if (tv.VisualLines.Count == 0) return 1;
        var first = tv.VisualLines[0];
        return first.FirstDocumentLine.LineNumber;
    }

    private void EditorTextArea_SelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorSelectionEcho > 0) { _suppressEditorSelectionEcho--; return; }
        _editorSelectionDebounce.Stop();
        _editorSelectionDebounce.Start();
    }

    private void EditorSelectionDebounce_Tick(object? sender, EventArgs e)
    {
        _editorSelectionDebounce.Stop();
        PushEditorSelectionToPreview();
    }

    private void PushEditorSelectionToPreview()
    {
        if (!_previewDocumentReady || Preview.CoreWebView2 == null) return;
        int startLine, endLine;
        var sel = Editor.TextArea.Selection;
        if (sel.IsEmpty)
        {
            startLine = endLine = -1;
        }
        else
        {
            startLine = Editor.Document.GetLineByOffset(sel.SurroundingSegment.Offset).LineNumber;
            endLine = Editor.Document.GetLineByOffset(sel.SurroundingSegment.EndOffset).LineNumber;
        }
        if (startLine == _lastEditorSelStart && endLine == _lastEditorSelEnd) return;
        _lastEditorSelStart = startLine;
        _lastEditorSelEnd = endLine;
        _ = Preview.CoreWebView2.ExecuteScriptAsync(
            $"window.mdHighlightLines && window.mdHighlightLines({startLine}, {endLine});");
    }

    private void Preview_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string payload;
        try { payload = e.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(payload)) return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();

            if (type == "edit-drop")
            {
                HandleDroppedFiles(e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).ToList());
                return;
            }

            if (type?.StartsWith("edit-", StringComparison.Ordinal) == true)
            {
                HandleEditMessage(type, root);
                return;
            }

            if (type == "scroll" && root.TryGetProperty("line", out var lineEl) && lineEl.TryGetInt32(out var line))
            {
                ScrollEditorToLine(line);
            }
            else if (type == "selection"
                     && root.TryGetProperty("startLine", out var s) && s.TryGetInt32(out var sLine)
                     && root.TryGetProperty("endLine", out var ep) && ep.TryGetInt32(out var eLine))
            {
                SelectEditorLines(sLine, eLine);
            }
            else if (type == "updated")
            {
                // Patched blocks lost their highlight.
                _lastEditorSelStart = _lastEditorSelEnd = -2;
                PushEditorSelectionToPreview();
            }
        }
        catch
        {
            // ignore malformed messages
        }
    }

    private void ScrollEditorToLine(int line)
    {
        if (line < 1) return;
        if (line > Editor.Document.LineCount) line = Editor.Document.LineCount;
        if (GetEditorTopVisibleLine() == line) return;
        _suppressEditorScrollEcho = 2;
        Editor.ScrollToLine(line);
    }

    private void SelectEditorLines(int startLine, int endLine)
    {
        if (startLine < 1 || endLine < 1)
        {
            if (Editor.TextArea.Selection.IsEmpty) return;
            _suppressEditorSelectionEcho = 2;
            Editor.TextArea.ClearSelection();
            return;
        }
        var doc = Editor.Document;
        startLine = Math.Max(1, Math.Min(doc.LineCount, startLine));
        endLine = Math.Max(1, Math.Min(doc.LineCount, endLine));
        if (endLine < startLine) (startLine, endLine) = (endLine, startLine);
        var startSeg = doc.GetLineByNumber(startLine);
        var endSeg = doc.GetLineByNumber(endLine);
        var startOffset = startSeg.Offset;
        var endOffset = endSeg.EndOffset;

        if (_lastEditorSelStart == startLine && _lastEditorSelEnd == endLine) return;

        _suppressEditorSelectionEcho = 2;
        _suppressEditorScrollEcho = 2;
        _lastEditorSelStart = startLine;
        _lastEditorSelEnd = endLine;
        Editor.Select(startOffset, endOffset - startOffset);
    }

    // --------------------- File commands ---------------------

    private void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        Editor.Text = string.Empty;
        _currentFilePath = null;
        _lastDiskText = null;
        _lastExport = null;
        _isModified = false;
        RecoveryStore.Clear();
        WatchCurrentFile();
        UpdateTitle();
        UpdateStatus();
        RenderPreviewNow();
        RefreshOutline();
    }

    private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ConfirmDiscardChanges()) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Markdown files (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt|All files (*.*)|*.*",
            Title = "Open Markdown file"
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName);
    }

    /// <summary>Opens a file handed over by a second launch of the app, in this window.</summary>
    public void OpenFromOtherInstance(string path)
    {
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Activate();

        // Reloading the open document would only risk the user's unsaved edits.
        if (string.Equals(path, _currentFilePath, StringComparison.OrdinalIgnoreCase)) return;

        if (ConfirmDiscardChanges()) LoadFile(path);
    }

    private void LoadFile(string path)
    {
        try
        {
            Editor.Text = _lastDiskText = File.ReadAllText(path);
            _currentFilePath = path;
            _lastExport = null;
            _isModified = false;
            RecoveryStore.Clear();
            WatchCurrentFile();
            RememberRecent(path);
            UpdateTitle();
            UpdateStatus();
            RenderPreviewNow();
            RefreshOutline();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to open file:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsEditView) await FlushPageEditsAsync();

        if (string.IsNullOrEmpty(_currentFilePath))
            SaveAsCommand_Executed(sender, e);
        else
            SaveTo(_currentFilePath);
    }

    private void SaveAsCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md",
            Title = "Save Markdown file",
            FileName = string.IsNullOrEmpty(_currentFilePath) ? "Untitled.md" : Path.GetFileName(_currentFilePath)
        };
        if (dlg.ShowDialog(this) != true) return;
        SaveTo(dlg.FileName);
    }

    private void SaveTo(string path)
    {
        try
        {
            var newLocation = !string.Equals(path, _currentFilePath, StringComparison.OrdinalIgnoreCase);
            _lastDiskText = Editor.Text;
            File.WriteAllText(path, _lastDiskText);
            _currentFilePath = path;
            _isModified = false;
            RecoveryStore.Clear();
            if (newLocation)
            {
                WatchCurrentFile();
                RememberRecent(path);
            }
            UpdateTitle();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to save file:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs e) => Close();

    // --------------------- Export ---------------------

    private string? PickExportPath(string filter, string defaultExt)
    {
        var dlg = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = defaultExt,
            Title = "Export",
            FileName = (string.IsNullOrEmpty(_currentFilePath)
                ? "Untitled"
                : Path.GetFileNameWithoutExtension(_currentFilePath)) + defaultExt
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private SyntaxReferenceWindow? _referenceWindow;

    private void ShowReference_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_referenceWindow == null || !_referenceWindow.IsLoaded)
        {
            _referenceWindow = new SyntaxReferenceWindow { Owner = this };
            _referenceWindow.Closed += (_, _) => _referenceWindow = null;
            _referenceWindow.Show();
        }
        else
        {
            if (_referenceWindow.WindowState == WindowState.Minimized)
                _referenceWindow.WindowState = WindowState.Normal;
            _referenceWindow.Activate();
        }
        e.Handled = true;
    }

    // --------------------- Helpers ---------------------

    private bool ConfirmDiscardChanges()
    {
        if (!_isModified) return true;
        var name = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
        var result = ConfirmDialog.Show(this, $"Save changes to {name}?",
            "Your changes will be lost if you don't save them.", "Save", "Don't Save");
        if (result == ConfirmResult.Cancel) return false;
        if (result == ConfirmResult.Primary)
        {
            SaveCommand_Executed(this, null!);
            return !_isModified;
        }
        RecoveryStore.Clear();
        return true;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }
        SaveSession();
        RecoveryStore.Clear();
    }

    private string GetTitle() =>
        string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileNameWithoutExtension(_currentFilePath);

    private void UpdateTitle()
    {
        var name = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
        var modMark = _isModified ? " •" : string.Empty;
        Title = $"{name}{modMark} — MDExport";
        StatusFileText.Text = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : _currentFilePath;
        DocStateText.Text = _isModified
            ? "Unsaved changes"
            : string.IsNullOrEmpty(_currentFilePath) ? "New document" : "Saved";
    }

    private void UpdateStatus()
    {
        var text = Editor.Text ?? string.Empty;
        var words = string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"\S+").Count;
        var minutes = Math.Max(1, (int)Math.Ceiling(words / WordsPerMinute));
        var caret = Editor.TextArea.Caret;
        var selected = Editor.SelectionLength > 0 ? $"{Editor.SelectionLength:N0} selected   ·   " : string.Empty;
        StatusInfoText.Text =
            $"{selected}Ln {caret.Line}, Col {caret.Column}   ·   {words:N0} words   ·   {minutes} min read";
    }
}
