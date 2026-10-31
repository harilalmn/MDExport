using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using MDExport.Services;
using Microsoft.Win32;

namespace MDExport;

// Edit view: typing directly on the rendered page. The page sends the Markdown for the blocks
// that changed and the source range they replace; everything else in the document is untouched.
public partial class MainWindow
{
    private static readonly Dictionary<string, (string Command, string? Argument)> SnippetsInEditView = new()
    {
        ["h1"] = ("style", "H1"), ["h2"] = ("style", "H2"), ["h3"] = ("style", "H3"),
        ["h4"] = ("style", "H4"), ["h5"] = ("style", "H5"), ["h6"] = ("style", "H6"),
        ["bold"] = ("bold", null), ["italic"] = ("italic", null), ["strikethrough"] = ("strike", null),
        ["code-inline"] = ("code", null), ["ul"] = ("bullets", null), ["ol"] = ("numbers", null),
        ["blockquote"] = ("quote", null), ["code-block"] = ("codeblock", null), ["table-pipe"] = ("table", null),
    };

    private bool IsEditView => _settings.ViewMode == ViewMode.Edit;

    private void SyncEditViewToPage()
    {
        if (!_previewDocumentReady || Preview.CoreWebView2 == null) return;
        var on = IsEditView ? "true" : "false";
        _ = Preview.CoreWebView2.ExecuteScriptAsync(
            $"window.mdEdit && window.mdEdit.setActive({on}, {Editor.Document.TextLength});");
    }

    /// <summary>Waits until everything typed on the page is in the document.</summary>
    private async Task FlushPageEditsAsync()
    {
        if (!_previewDocumentReady || Preview.CoreWebView2 == null) return;

        var json = await Preview.CoreWebView2.ExecuteScriptAsync("window.mdEdit ? window.mdEdit.flush() : null")
            .ConfigureAwait(true);
        if (string.IsNullOrEmpty(json) || json == "null") return;

        using var doc = JsonDocument.Parse(json);
        ApplyPageEdit(doc.RootElement);
    }

    private void HandleEditMessage(string type, JsonElement root)
    {
        switch (type)
        {
            case "edit-page-ready":
                _previewDocumentReady = true;
                SyncEditViewToPage();
                break;
            case "edit-dirty":
                if (!_isModified)
                {
                    _isModified = true;
                    UpdateTitle();
                }
                break;
            case "edit-commit":
                ApplyPageEdit(root);
                break;
            case "edit-undo":
                if (Editor.CanUndo) Editor.Undo();
                ResetPageFromDocument();
                break;
            case "edit-redo":
                if (Editor.CanRedo) Editor.Redo();
                ResetPageFromDocument();
                break;
            case "edit-locked":
                OpenSourceAt(root.GetProperty("start").GetInt32());
                break;
            case "edit-paste-image":
                PasteIntoPage();
                break;
        }
    }

    private void ApplyPageEdit(JsonElement edit)
    {
        var document = Editor.Document;
        var start = Math.Clamp(edit.GetProperty("start").GetInt32(), 0, document.TextLength);
        var end = Math.Clamp(edit.GetProperty("end").GetInt32(), start, document.TextLength);
        var markdown = edit.GetProperty("md").GetString() ?? string.Empty;

        // A removed block should not leave its blank line behind.
        if (markdown.Length == 0)
        {
            while (end < document.TextLength && document.GetCharAt(end) is '\r' or '\n') end++;
        }

        if (document.Text.Contains("\r\n", StringComparison.Ordinal))
            markdown = markdown.Replace("\n", "\r\n");

        // A new block between others needs its own blank lines. They stay outside the range
        // reported back, which covers only the block.
        var newLine = document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var before = string.Empty;
        var after = string.Empty;
        if (start == end && markdown.Length > 0)
        {
            if (start > 0) before = newLine + newLine;
            if (end < document.TextLength) after = newLine + newLine;
        }

        var previousLength = document.TextLength;
        document.Replace(start, end - start, before + markdown + after);
        start += before.Length;

        var body = MarkdownRenderer.RenderPreviewBody(Editor.Text, DocumentDirectory);
        _lastRenderedHtml = body;
        PostToPage(new
        {
            kind = "ack",
            html = body,
            start,
            end = start + markdown.Length,
            delta = document.TextLength - previousLength,
            docLength = document.TextLength
        });
    }

    private void ResetPageFromDocument()
    {
        var body = MarkdownRenderer.RenderPreviewBody(Editor.Text, DocumentDirectory);
        _lastRenderedHtml = body;
        PostToPage(new { kind = "reset", html = body, docLength = Editor.Document.TextLength });
    }

    private void PostToPage(object message) =>
        Preview.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message));

    private void OpenSourceAt(int offset)
    {
        SetViewMode(ViewMode.Split);
        ShowInfo("This block can't be edited on the page, so it's open in the code editor.");
        if (offset < 0) return;

        var caret = Math.Min(offset, Editor.Document.TextLength);
        Editor.CaretOffset = caret;
        Editor.ScrollToLine(Editor.Document.GetLineByOffset(caret).LineNumber);
        Editor.Focus();
    }

    private void RunPageCommand(string command, string? argument = null, string? alt = null, string? display = null)
    {
        if (!_previewDocumentReady) return;
        Preview.Focus();
        var script = "window.mdEdit && window.mdEdit.command("
                     + JsonSerializer.Serialize(command) + ","
                     + JsonSerializer.Serialize(argument) + ","
                     + JsonSerializer.Serialize(alt) + ","
                     + JsonSerializer.Serialize(display) + ");";
        _ = Preview.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PasteImageIntoPage()
    {
        if (Clipboard.GetImage() is not { } image) return;
        var url = SavePastedImage(image, out var name);
        if (url != null) InsertImageOnPage(url, name);
    }

    // --------------------- Edit menu, when the page has focus ---------------------

    private void PageCommand_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        // Copying and selecting work on any page; the rest change the document.
        e.CanExecute = _previewDocumentReady
            && (IsEditView || e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.SelectAll);
        e.Handled = true;
    }

    private async void PageCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Undo || e.Command == ApplicationCommands.Redo)
        {
            await FlushPageEditsAsync();
            if (e.Command == ApplicationCommands.Undo) Editor.Undo();
            else Editor.Redo();
            ResetPageFromDocument();
            return;
        }

        Preview.Focus();
        if (e.Command == ApplicationCommands.Paste) PasteIntoPage();
        else if (e.Command == ApplicationCommands.Cut) RunPageCommand("cut");
        else if (IsEditView) RunPageCommand(e.Command == ApplicationCommands.Copy ? "copy" : "selectAll");
        else
        {
            var name = e.Command == ApplicationCommands.Copy ? "copy" : "selectAll";
            _ = Preview.CoreWebView2.ExecuteScriptAsync($"document.execCommand('{name}');");
        }
    }

    private void PasteIntoPage()
    {
        try
        {
            var files = ClipboardImageFiles();
            if (files.Count > 0)
            {
                foreach (var file in files) InsertImageFileOnPage(file);
            }
            else if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
            {
                PasteImageIntoPage();
            }
            else if (Clipboard.ContainsText())
            {
                RunPageCommand("insert-text", Clipboard.GetText());
            }
            else if (Clipboard.ContainsFileDropList())
            {
                ShowInfo("Only image files can be pasted into a document.");
            }
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            ShowInfo("The clipboard is busy: " + ex.Message);
        }
    }

    // --------------------- Toolbar ---------------------

    private void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var parts = tag.Split(':', 2);
        RunPageCommand(parts[0], parts.Length > 1 ? parts[1] : null);
    }

    private void EditMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void EditLink_Click(object sender, RoutedEventArgs e)
    {
        var url = LinkDialog.Ask(this);
        if (url != null) RunPageCommand("link", url);
        else Preview.Focus();
    }

    private void EditImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Insert image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        InsertImageFileOnPage(dialog.FileName);
    }

    private void InsertImageFileOnPage(string path)
    {
        var url = ImportImage(path, out var name);
        if (url != null) InsertImageOnPage(url, name);
    }

    // The page has no folder of its own, so a relative path would show as a broken image.
    private void InsertImageOnPage(string url, string alt) =>
        RunPageCommand("image", url, alt, ImageEmbedder.TryGetDisplayUrl(url, DocumentDirectory));
}
