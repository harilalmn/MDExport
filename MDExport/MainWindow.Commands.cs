using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MDExport.Services;

namespace MDExport;

public partial class MainWindow
{
    public static readonly RoutedCommand RunActionCommand = new("RunAction", typeof(MainWindow));

    private (string Format, string Path)? _lastExport;

    private void RunAction_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is string id) RunAction(id);
    }

    private void ActionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) RunAction(id);
    }

    private void RunAction(string id)
    {
        switch (id)
        {
            case "view-write": SetViewMode(ViewMode.Write); break;
            case "view-split": SetViewMode(ViewMode.Split); break;
            case "view-read": SetViewMode(ViewMode.Read); break;
            case "cycle-view":
                SetViewMode(_settings.ViewMode switch
                {
                    ViewMode.Split => ViewMode.Write,
                    ViewMode.Write => ViewMode.Read,
                    _ => ViewMode.Split
                });
                break;
            case "outline": SetOutlineVisible(!_settings.ShowOutline); break;
            case "focus": ToggleFocusMode(); break;
            case "word-wrap":
                _settings.WordWrap = !_settings.WordWrap;
                Editor.WordWrap = _settings.WordWrap;
                MenuWordWrap.IsChecked = _settings.WordWrap;
                break;
            case "dark-preview":
                _settings.DarkPreview = !_settings.DarkPreview;
                MenuDarkPreview.IsChecked = _settings.DarkPreview;
                ApplyPreviewTheme();
                _lastRenderedHtml = string.Empty;
                _previewDocumentReady = false; // The theme lives on the page, so reload it.
                RenderPreviewNow();
                break;
            case "zoom-in": SetZoom(_settings.Zoom + ZoomStep); break;
            case "zoom-out": SetZoom(_settings.Zoom - ZoomStep); break;
            case "zoom-reset": SetZoom(1); break;
            case "palette": CommandPaletteWindow.Open(this, BuildPaletteCommands()); break;
            case "export-again": _ = ExportAgainAsync(); break;
            default: throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown action");
        }
    }

    private List<PaletteCommand> BuildPaletteCommands()
    {
        PaletteCommand Routed(string title, string? shortcut, RoutedCommand command) =>
            new(title, shortcut, () => command.Execute(null, this));
        PaletteCommand Action(string title, string? shortcut, string id) =>
            new(title, shortcut, () => RunAction(id));

        var commands = new List<PaletteCommand>
        {
            Routed("File: New", "Ctrl+N", ApplicationCommands.New),
            Routed("File: Open", "Ctrl+O", ApplicationCommands.Open),
            Routed("File: Save", "Ctrl+S", ApplicationCommands.Save),
            Routed("File: Save As", "Ctrl+Shift+S", ApplicationCommands.SaveAs),
            new("Export: HTML", null, () => ExportHtml_Click(this, new RoutedEventArgs())),
            new("Export: PDF", null, () => ExportPdf_Click(this, new RoutedEventArgs())),
            new("Export: Word (DOCX)", null, () => ExportDocx_Click(this, new RoutedEventArgs())),
            Action("Export: Again", "Ctrl+Shift+E", "export-again"),
            new("Edit: Find", "Ctrl+F", () => ShowFindPanel(false)),
            new("Edit: Replace", "Ctrl+H", () => ShowFindPanel(true)),
            Action("View: Write", null, "view-write"),
            Action("View: Split", null, "view-split"),
            Action("View: Read", null, "view-read"),
            Action("View: Toggle Outline", "Ctrl+Shift+O", "outline"),
            Action("View: Toggle Focus Mode", "F11", "focus"),
            Action("View: Toggle Word Wrap", null, "word-wrap"),
            Action("View: Toggle Dark Preview", null, "dark-preview"),
            Action("View: Zoom In", "Ctrl+=", "zoom-in"),
            Action("View: Zoom Out", "Ctrl+-", "zoom-out"),
            Action("View: Reset Zoom", "Ctrl+0", "zoom-reset"),
            Routed("Help: Markdown Syntax", "F1", ApplicationCommands.Help),
        };

        commands.AddRange(SnippetLibrary.All.Select(s =>
            new PaletteCommand($"Insert: {s.Title}", s.Shortcut, () => InsertSnippet(s.Id))));
        return commands;
    }

    // --------------------- Export ---------------------

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = ExportButton.ContextMenu!;
        ExportAgainItem.IsEnabled = _lastExport != null;
        menu.PlacementTarget = ExportButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ExportHtml_Click(object sender, RoutedEventArgs e)
    {
        var path = PickExportPath("HTML files (*.html;*.htm)|*.html;*.htm", ".html");
        if (path != null) _ = RunExportAsync("HTML", path);
    }

    private void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewEnvironment == null)
        {
            ShowInfo("The preview is still starting. Try again in a moment.");
            return;
        }

        var options = PdfOptionsWindow.Ask(this, _settings.Pdf);
        if (options == null) return;
        _settings.Pdf = options;

        var path = PickExportPath("PDF files (*.pdf)|*.pdf", ".pdf");
        if (path != null) _ = RunExportAsync("PDF", path);
    }

    private void ExportDocx_Click(object sender, RoutedEventArgs e)
    {
        var path = PickExportPath("Word documents (*.docx)|*.docx", ".docx");
        if (path != null) _ = RunExportAsync("DOCX", path);
    }

    private Task ExportAgainAsync()
    {
        if (_lastExport is { } last) return RunExportAsync(last.Format, last.Path);
        ShowInfo("Nothing has been exported yet in this document.");
        return Task.CompletedTask;
    }

    private async Task RunExportAsync(string format, string path)
    {
        var name = Path.GetFileName(path);
        ShowInfo($"Exporting {name}...");
        Cursor = Cursors.Wait;
        ExportButton.IsEnabled = false;
        try
        {
            switch (format)
            {
                case "HTML":
                    HtmlExporter.Export(Editor.Text, path, GetTitle(), DocumentDirectory);
                    break;
                case "PDF":
                    await PdfExporter.ExportAsync(_webViewEnvironment!, Editor.Text, path, _settings.Pdf, GetTitle(), DocumentDirectory);
                    break;
                default:
                    DocxExporter.Export(Editor.Text, path, DocumentDirectory);
                    break;
            }

            _lastExport = (format, path);
            ShowInfo($"Exported {name}",
                ("Open", () => OpenPath(path)),
                ("Show in Folder", () => Process.Start("explorer.exe", $"/select,\"{path}\"")));
        }
        catch (Exception ex)
        {
            HideInfo();
            MessageBox.Show(this, $"{format} export failed:\n{ex.Message}",
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Cursor = null;
            ExportButton.IsEnabled = true;
            if (_settings.ViewMode != ViewMode.Read) Editor.Focus();
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ShowInfo("Could not open the file: " + ex.Message);
        }
    }
}
