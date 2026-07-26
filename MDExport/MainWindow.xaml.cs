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
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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

    private const string DefaultMarkdown =
        "# Welcome to MDExport\n\n" +
        "Start typing Markdown on the **left** and see the live preview on the **right**.\n\n" +
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
                Editor.Text = File.ReadAllText(initialFilePath);
                _currentFilePath = initialFilePath;
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
        UpdateTitle();
        UpdateStatus();
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
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MDExport", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder).ConfigureAwait(true);
            await Preview.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Preview.CoreWebView2.WebMessageReceived += Preview_WebMessageReceived;
            Preview.CoreWebView2.NavigationCompleted += Preview_NavigationCompleted;
            _webViewReady = true;
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
            StartBadgePulse();
        }
        catch
        {
            // Silent: no network, rate-limited, etc. — user can still trigger from About.
        }
    }

    // Pulse the badge's glow from code. Animating the effect object directly with
    // BeginAnimation avoids XAML storyboard name-scope resolution, which throws in
    // Release/BAML builds when targeting template children.
    private void StartBadgePulse()
    {
        if (UpdateBadge.Effect is not DropShadowEffect glow) return;

        var duration = new Duration(TimeSpan.FromSeconds(1.2));
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(6, 22, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.35, 0.9, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
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
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        RenderPreviewNow();
    }

    private void RenderPreviewNow()
    {
        if (!_webViewReady || Preview.CoreWebView2 == null) return;
        var html = MarkdownRenderer.RenderFullPage(Editor.Text, GetTitle(), DocumentDirectory);
        if (html == _lastRenderedHtml) return;
        _lastRenderedHtml = html;
        _previewDocumentReady = false;
        HtmlDocumentHost.NavigateToHtml(Preview.CoreWebView2, html);
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
        _isModified = false;
        UpdateTitle();
        UpdateStatus();
        RenderPreviewNow();
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
        try
        {
            Editor.Text = File.ReadAllText(dlg.FileName);
            _currentFilePath = dlg.FileName;
            _isModified = false;
            UpdateTitle();
            UpdateStatus();
            RenderPreviewNow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to open file:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
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
            File.WriteAllText(path, Editor.Text);
            _currentFilePath = path;
            _isModified = false;
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

    private void ExportHtml_Click(object sender, RoutedEventArgs e)
    {
        var path = PickExportPath("HTML files (*.html;*.htm)|*.html;*.htm", ".html");
        if (path == null) return;
        try
        {
            HtmlExporter.Export(Editor.Text, path, GetTitle(), DocumentDirectory);
            ShowExportSuccess("HTML", path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "HTML export failed:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady)
        {
            MessageBox.Show(this, "Preview is still initializing. Please try again in a moment.",
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var path = PickExportPath("PDF files (*.pdf)|*.pdf", ".pdf");
        if (path == null) return;
        try
        {
            Cursor = Cursors.Wait;
            await PdfExporter.ExportAsync(Preview, Editor.Text, path, GetTitle(), DocumentDirectory);
            ShowExportSuccess("PDF", path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "PDF export failed:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = Cursors.Arrow;
            // Restore live preview
            _lastRenderedHtml = string.Empty;
            RenderPreviewNow();
        }
    }

    private void ExportDocx_Click(object sender, RoutedEventArgs e)
    {
        var path = PickExportPath("Word documents (*.docx)|*.docx", ".docx");
        if (path == null) return;
        try
        {
            DocxExporter.Export(Editor.Text, path, DocumentDirectory);
            ShowExportSuccess("DOCX", path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "DOCX export failed:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

    private void ShowExportSuccess(string format, string path)
    {
        StatusInfoText.Text = $"Exported {format}: {Path.GetFileName(path)}";
    }

    // --------------------- View ---------------------

    private void TogglePreview_Click(object sender, RoutedEventArgs e) => UpdatePaneLayout();

    private void ToggleEditor_Click(object sender, RoutedEventArgs e) => UpdatePaneLayout();

    private void UpdatePaneLayout()
    {
        var editor = MenuToggleEditor.IsChecked;
        var preview = MenuTogglePreview.IsChecked;

        EditorColumn.MinWidth = editor ? 120 : 0;
        EditorColumn.Width = editor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        SplitterColumn.Width = editor && preview ? new GridLength(1) : new GridLength(0);

        PreviewColumn.MinWidth = preview ? 120 : 0;
        PreviewColumn.Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void ToggleWordWrap_Click(object sender, RoutedEventArgs e)
    {
        Editor.WordWrap = MenuWordWrap.IsChecked;
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
        var result = MessageBox.Show(this,
            "You have unsaved changes. Save before continuing?",
            "MDExport", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes)
        {
            SaveCommand_Executed(this, null!);
            return !_isModified;
        }
        return true;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscardChanges()) e.Cancel = true;
    }

    private string GetTitle() =>
        string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileNameWithoutExtension(_currentFilePath);

    private void UpdateTitle()
    {
        var name = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : Path.GetFileName(_currentFilePath);
        var modMark = _isModified ? " •" : string.Empty;
        Title = $"{name}{modMark} — MDExport";
        StatusFileText.Text = string.IsNullOrEmpty(_currentFilePath) ? "Untitled" : _currentFilePath;
        StatusModifiedText.Text = _isModified ? "modified" : string.Empty;
    }

    private void UpdateStatus()
    {
        var text = Editor.Text ?? string.Empty;
        var words = string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"\S+").Count;
        StatusInfoText.Text = $"{words:N0} words   {text.Length:N0} chars";
    }
}
