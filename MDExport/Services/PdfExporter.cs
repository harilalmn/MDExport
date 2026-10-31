using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MDExport.Services;

internal static class PdfExporter
{
    /// <summary>
    /// Renders in an off-screen WebView so the live preview keeps its document and scroll
    /// position while the export runs.
    /// </summary>
    public static async Task ExportAsync(
        CoreWebView2Environment environment, string markdown, string filePath,
        PdfOptions options, string? title = null, string? baseDir = null)
    {
        var html = MarkdownRenderer.RenderExportPage(markdown, title, baseDir);

        var webView = new WebView2();
        var host = new Window
        {
            Width = 900,
            Height = 1200,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = webView
        };

        try
        {
            host.Show();
            await webView.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            var core = webView.CoreWebView2;

            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            HtmlDocumentHost.NavigateToHtml(core, html);
            if (!await loaded.Task.ConfigureAwait(true))
                throw new InvalidOperationException("The document could not be rendered.");

            var ok = await core.PrintToPdfAsync(filePath, BuildSettings(environment, options, title))
                .ConfigureAwait(true);
            if (!ok) throw new InvalidOperationException("PDF export failed.");
        }
        finally
        {
            host.Close();
            webView.Dispose();
        }
    }

    private static CoreWebView2PrintSettings BuildSettings(
        CoreWebView2Environment environment, PdfOptions options, string? title)
    {
        var settings = environment.CreatePrintSettings();
        settings.ShouldPrintBackgrounds = true;
        settings.MarginTop = settings.MarginBottom = options.MarginInches;
        settings.MarginLeft = settings.MarginRight = options.MarginInches;
        settings.Orientation = options.Landscape
            ? CoreWebView2PrintOrientation.Landscape
            : CoreWebView2PrintOrientation.Portrait;

        (settings.PageWidth, settings.PageHeight) = options.PageSize == "A4" ? (8.27, 11.69) : (8.5, 11.0);

        // Chromium's footer carries the page number; the URI would only show the internal address.
        settings.ShouldPrintHeaderAndFooter = options.PageNumbers;
        settings.HeaderTitle = title ?? string.Empty;
        settings.FooterUri = string.Empty;
        return settings;
    }
}
