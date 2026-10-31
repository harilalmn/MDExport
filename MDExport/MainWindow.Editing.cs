using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using MDExport.Services;

namespace MDExport;

public partial class MainWindow
{
    private const string ImageFolder = "img";

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };

    private static readonly string[] DocumentExtensions = { ".md", ".markdown", ".txt" };

    private static readonly Regex ListItem = new(
        @"^(?<indent>\s*)(?<marker>[-*+]|(?<number>\d+)(?<delim>[.)]))(?<task>\s+\[[ xX]\])?\s+(?<content>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Url = new(@"^(https?://|mailto:)\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private CompletionWindow? _slashMenu;

    private void InitializeEditor()
    {
        using (var stream = Application.GetResourceStream(
                   new Uri("pack://application:,,,/Assets/MarkdownDark.xshd")).Stream)
        using (var reader = XmlReader.Create(stream))
        {
            Editor.SyntaxHighlighting = HighlightingLoader.Load(
                reader, ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance);
        }

        var area = Editor.TextArea;
        Editor.Options.HighlightCurrentLine = true;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        area.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        area.TextView.Margin = new Thickness(12, 0, 0, 0);
        area.Caret.CaretBrush = (Brush)FindResource("TextBrush");
        area.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x4F, 0x8F, 0xF7));
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 2;
        foreach (var dottedLine in area.LeftMargins.OfType<Line>())
            dottedLine.Visibility = Visibility.Collapsed;

        area.PreviewKeyDown += EditorArea_PreviewKeyDown;
        area.TextEntered += EditorArea_TextEntered;
        area.Caret.PositionChanged += (_, _) => UpdateStatus();
        area.SelectionChanged += (_, _) => UpdateStatus();
        CommandManager.AddPreviewCanExecuteHandler(area, EditorArea_PreviewCanExecute);
        CommandManager.AddPreviewExecutedHandler(area, EditorArea_PreviewExecuted);

        Editor.PreviewMouseWheel += Editor_PreviewMouseWheel;
        PreviewDragOver += Window_PreviewDragOver;
        PreviewDrop += Window_PreviewDrop;
    }

    // --------------------- Smart lists ---------------------

    private void EditorArea_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;

        // Snippets and the slash menu use Enter themselves.
        var area = Editor.TextArea;
        if (!area.StackedInputHandlers.IsEmpty || !area.Selection.IsEmpty) return;

        var document = Editor.Document;
        var line = document.GetLineByOffset(area.Caret.Offset);
        if (area.Caret.Offset != line.EndOffset) return;

        var match = ListItem.Match(document.GetText(line));
        if (!match.Success) return;

        e.Handled = true;
        if (match.Groups["content"].Value.Length == 0)
        {
            // Enter on an empty item ends the list, as in every word processor.
            document.Replace(line.Offset, line.Length, string.Empty);
            return;
        }

        var marker = match.Groups["number"].Success
            ? (int.Parse(match.Groups["number"].Value) + 1) + match.Groups["delim"].Value
            : match.Groups["marker"].Value;
        var task = match.Groups["task"].Success ? " [ ]" : string.Empty;
        document.Insert(area.Caret.Offset, $"\n{match.Groups["indent"].Value}{marker}{task} ");
    }

    // --------------------- Slash menu ---------------------

    private void EditorArea_TextEntered(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "/" || _slashMenu != null) return;

        var area = Editor.TextArea;
        var line = Editor.Document.GetLineByOffset(area.Caret.Offset);
        var before = Editor.Document.GetText(line.Offset, area.Caret.Offset - line.Offset - 1);
        if (!string.IsNullOrWhiteSpace(before) && before.Length > 0) return;

        var menu = new CompletionWindow(area) { Width = 280 };
        menu.CompletionList.ListBox.Background = (Brush)FindResource("SurfaceBrush");
        menu.CompletionList.ListBox.Foreground = (Brush)FindResource("TextBrush");
        menu.CompletionList.ListBox.BorderThickness = new Thickness(0);
        foreach (var snippet in SnippetLibrary.All)
            menu.CompletionList.CompletionData.Add(new SlashItem(snippet, InsertSnippet));
        menu.CompletionList.SelectedItem = menu.CompletionList.CompletionData[0];
        menu.Closed += (_, _) => _slashMenu = null;
        _slashMenu = menu;
        menu.Show();
    }

    private sealed class SlashItem : ICompletionData
    {
        private readonly SnippetDefinition _snippet;
        private readonly Action<string> _insert;

        public SlashItem(SnippetDefinition snippet, Action<string> insert)
        {
            _snippet = snippet;
            _insert = insert;
        }

        public ImageSource? Image => null;
        public string Text => _snippet.Title;
        public object Content => _snippet.Title;
        public object? Description => null;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            // Includes the "/" that opened the menu, which sits just before the segment.
            textArea.Document.Remove(completionSegment.Offset - 1, completionSegment.Length + 1);
            var id = _snippet.Id;
            textArea.Dispatcher.BeginInvoke(() => _insert(id));
        }
    }

    // --------------------- Paste ---------------------

    // AvalonEdit only enables Paste for text, so images would never reach the handler below.
    private void EditorArea_PreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (!Clipboard.ContainsImage() && ClipboardImageFiles().Count == 0) return;
        }
        catch (ExternalException)
        {
            return;
        }
        e.CanExecute = true;
        e.Handled = true;
    }

    private void EditorArea_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;

        try
        {
            var files = ClipboardImageFiles();
            if (files.Count > 0)
            {
                e.Handled = true;
                foreach (var file in files) InsertDroppedImage(file);
                return;
            }

            if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
            {
                e.Handled = true;
                if (Clipboard.GetImage() is { } image) PasteImage(image);
                return;
            }

            if (!Editor.TextArea.Selection.IsEmpty && Clipboard.ContainsText())
            {
                var url = Clipboard.GetText().Trim();
                var selected = Editor.SelectedText;
                if (Url.IsMatch(url) && !selected.Contains('\n'))
                {
                    e.Handled = true;
                    ReplaceSelection($"[{selected}]({url})");
                }
            }
        }
        catch (ExternalException)
        {
            // Another process has the clipboard open; let the normal paste report nothing.
        }
    }

    /// <summary>Image files copied in Explorer, which arrive as paths rather than as image data.</summary>
    private static List<string> ClipboardImageFiles()
    {
        if (!Clipboard.ContainsFileDropList()) return new List<string>();
        return Clipboard.GetFileDropList().Cast<string>().Where(f => IsAny(f, ImageExtensions)).ToList();
    }

    private void PasteImage(BitmapSource image)
    {
        var url = SavePastedImage(image, out var name);
        if (url != null) InsertImageReference(name, url);
    }

    /// <summary>Saves the image beside the document and returns its Markdown URL.</summary>
    private string? SavePastedImage(BitmapSource image, out string name)
    {
        name = $"image-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
        var folder = RequireImageFolder();
        if (folder == null) return null;

        try
        {
            using var file = File.Create(System.IO.Path.Combine(folder, name));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo("Could not save the pasted image: " + ex.Message);
            return null;
        }

        return $"./{ImageFolder}/{name}";
    }

    /// <summary>The document's image folder, or null (with a prompt) if it has not been saved.</summary>
    private string? RequireImageFolder()
    {
        if (DocumentDirectory is not { } directory)
        {
            ShowInfo("Save this document first, so images have a folder to go in.",
                ("Save As...", () => ApplicationCommands.SaveAs.Execute(null, this)));
            return null;
        }

        var folder = System.IO.Path.Combine(directory, ImageFolder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void InsertImageReference(string alt, string url)
    {
        EnsureEditorVisible();
        ReplaceSelection($"![{alt}]({url.Replace(" ", "%20")})");
        Editor.Focus();
    }

    private void ReplaceSelection(string text)
    {
        var start = Editor.SelectionStart;
        Editor.Document.Replace(start, Editor.SelectionLength, text);
        Editor.Select(start + text.Length, 0);
    }

    // --------------------- Drag and drop ---------------------

    private static bool IsAny(string path, string[] extensions) =>
        extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        e.Effects = files.Any(f => IsAny(f, DocumentExtensions) || IsAny(f, ImageExtensions))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        // The code editor cannot be asked for a position while it is hidden.
        if (files.Any(f => IsAny(f, DocumentExtensions)) || !IsCodeEditorVisible)
        {
            HandleDroppedFiles(files);
            return;
        }

        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position.HasValue)
            Editor.CaretOffset = Editor.Document.GetOffset(position.Value.Location);

        foreach (var image in files.Where(f => IsAny(f, ImageExtensions)))
            InsertDroppedImage(image);
    }

    /// <summary>Files dropped on the page, or a document dropped anywhere in the window.</summary>
    private void HandleDroppedFiles(IReadOnlyList<string> files)
    {
        Activate();

        var document = files.FirstOrDefault(f => IsAny(f, DocumentExtensions));
        if (document != null)
        {
            if (ConfirmDiscardChanges()) LoadFile(document);
            return;
        }

        var images = files.Where(f => IsAny(f, ImageExtensions)).ToList();
        if (images.Count == 0)
        {
            ShowInfo("Only images and Markdown files can be dropped here.");
            return;
        }

        // Dropping an image says what the user wants, so Read view gives way to editing.
        if (_settings.ViewMode == ViewMode.Read) SwitchViewMode(ViewMode.Edit);

        foreach (var image in images)
        {
            if (IsEditView) InsertImageFileOnPage(image);
            else InsertDroppedImage(image);
        }
    }

    private void InsertDroppedImage(string path)
    {
        var url = ImportImage(path, out var name);
        if (url == null) return;

        InsertImageReference(name, url);
        Editor.Document.Insert(Editor.CaretOffset, "\n");
    }

    /// <summary>Returns the Markdown URL for an image, copying it beside the document if needed.</summary>
    private string? ImportImage(string path, out string name)
    {
        name = System.IO.Path.GetFileName(path);
        var folder = RequireImageFolder();
        if (folder == null) return null;

        var directory = DocumentDirectory!;
        if (path.StartsWith(directory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return "./" + System.IO.Path.GetRelativePath(directory, path).Replace('\\', '/');
        }

        try
        {
            // Kept next to the document so it still works if the document folder moves.
            if (File.Exists(System.IO.Path.Combine(folder, name)))
                name = $"{System.IO.Path.GetFileNameWithoutExtension(path)}-{DateTime.Now:HHmmssfff}{System.IO.Path.GetExtension(path)}";
            File.Copy(path, System.IO.Path.Combine(folder, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo($"Could not copy {name}: {ex.Message}");
            return null;
        }

        return $"./{ImageFolder}/{name}";
    }

    // --------------------- Zoom ---------------------

    private void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        SetZoom(_settings.Zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
    }

    private void SetZoom(double zoom)
    {
        _settings.Zoom = Math.Round(Math.Clamp(zoom, 0.6, 2.5), 2);
        Editor.FontSize = BaseEditorFontSize * _settings.Zoom;
        if (_webViewReady) Preview.ZoomFactor = _settings.Zoom;
    }
}
