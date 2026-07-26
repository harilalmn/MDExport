using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace MDExport.Services;

/// <summary>
/// Loads generated HTML into a WebView2 by serving it from a synthetic origin.
///
/// <para>
/// <c>NavigateToString</c> looks like the obvious way to do this, but it caps its argument at
/// 2 MB and throws <see cref="ArgumentException"/> ("Value does not fall within the expected
/// range") beyond that. A document that embeds a couple of screenshots as data: URIs passes
/// the cap easily — a 1 MB PNG is ~1.4 MB of base64 on its own — so the preview died on
/// perfectly ordinary files.
/// </para>
/// <para>
/// Intercepting the request for a URL that never leaves the process has no size limit. The
/// host name is under the reserved <c>.invalid</c> TLD (RFC 2606), so nothing can resolve it
/// even if a request ever escaped the handler.
/// </para>
/// </summary>
internal static class HtmlDocumentHost
{
    private const string Origin = "https://mdexport.invalid";

    private sealed class Hosted
    {
        public byte[] Content = Array.Empty<byte>();
        public string Url = string.Empty;
        public int Revision;
    }

    private static readonly ConditionalWeakTable<CoreWebView2, Hosted> Documents = new();

    /// <summary>Replaces the WebView's current document with <paramref name="html"/>.</summary>
    public static void NavigateToHtml(CoreWebView2 core, string html)
    {
        var hosted = Attach(core);
        hosted.Content = Encoding.UTF8.GetBytes(html);
        // Navigating to the URL already showing can be collapsed into a no-op, and the preview
        // re-renders constantly, so every render gets its own URL.
        hosted.Url = $"{Origin}/preview/{++hosted.Revision}";
        core.Navigate(hosted.Url);
    }

    private static Hosted Attach(CoreWebView2 core)
    {
        if (Documents.TryGetValue(core, out var existing)) return existing;

        var hosted = new Hosted();
        Documents.Add(core, hosted);
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        return hosted;
    }

    private static void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        if (!Documents.TryGetValue(core, out var hosted)) return;

        // Stale revisions (a render that was superseded mid-flight) get a 404 rather than the
        // wrong document.
        if (!string.Equals(e.Request.Uri, hosted.Url, StringComparison.Ordinal))
        {
            e.Response = core.Environment.CreateWebResourceResponse(
                null, 404, "Not Found", "Cache-Control: no-store");
            return;
        }

        e.Response = core.Environment.CreateWebResourceResponse(
            new MemoryStream(hosted.Content, writable: false),
            200, "OK",
            "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
    }
}
