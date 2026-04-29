using System.IO;
using System.Text;

namespace MDExport.Services;

internal static class HtmlExporter
{
    public static void Export(string markdown, string filePath, string? title = null)
    {
        var html = MarkdownRenderer.RenderFullPage(markdown, title ?? Path.GetFileNameWithoutExtension(filePath));
        File.WriteAllText(filePath, html, new UTF8Encoding(false));
    }
}
