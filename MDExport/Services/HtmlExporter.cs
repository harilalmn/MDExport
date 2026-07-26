using System.IO;
using System.Text;

namespace MDExport.Services;

internal static class HtmlExporter
{
    public static void Export(string markdown, string filePath, string? title = null, string? baseDir = null)
    {
        var html = MarkdownRenderer.RenderExportPage(markdown, title ?? Path.GetFileNameWithoutExtension(filePath), baseDir);
        File.WriteAllText(filePath, html, new UTF8Encoding(false));
    }
}
