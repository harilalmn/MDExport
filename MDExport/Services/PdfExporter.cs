using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MDExport.Services;

internal static class PdfExporter
{
    public static async Task ExportAsync(WebView2 webView, string markdown, string filePath, string? title = null)
    {
        if (webView.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 is not initialized.");

        var html = MarkdownRenderer.RenderFullPage(markdown, title);

        var tcs = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            webView.CoreWebView2.NavigationCompleted -= handler!;
            tcs.TrySetResult(true);
        };
        webView.CoreWebView2.NavigationCompleted += handler;
        webView.CoreWebView2.NavigateToString(html);
        await tcs.Task.ConfigureAwait(true);

        var settings = webView.CoreWebView2.Environment.CreatePrintSettings();
        settings.ShouldPrintBackgrounds = true;
        settings.MarginTop = 0.5;
        settings.MarginBottom = 0.5;
        settings.MarginLeft = 0.5;
        settings.MarginRight = 0.5;
        settings.PageWidth = 8.5;
        settings.PageHeight = 11;

        var ok = await webView.CoreWebView2.PrintToPdfAsync(filePath, settings).ConfigureAwait(true);
        if (!ok)
            throw new InvalidOperationException("PDF export failed.");
    }
}
