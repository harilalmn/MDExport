using System.IO;
using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MDExport.Services;

internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static readonly MarkdownPipeline PreviewPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .UsePreciseSourceLocation()
        .Build();

    private static string? _templateCache;

    public static string RenderBodyHtml(string markdown)
        => Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    public static string RenderBodyHtmlForPreview(string markdown)
    {
        var doc = Markdown.Parse(markdown ?? string.Empty, PreviewPipeline);
        AnnotateBlocks(doc);
        using var sw = new StringWriter();
        var renderer = new HtmlRenderer(sw);
        PreviewPipeline.Setup(renderer);
        renderer.Render(doc);
        sw.Flush();
        return sw.ToString();
    }

    /// <param name="baseDir">
    /// Folder the markdown document lives in; local images are resolved against it and
    /// embedded as data: URIs so they render inside the about:blank preview document.
    /// </param>
    public static string RenderFullPage(string markdown, string? title = null, string? baseDir = null)
    {
        var body = ImageEmbedder.InlineLocalImages(RenderBodyHtmlForPreview(markdown), baseDir);
        var template = LoadTemplate();
        return template
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title ?? "Preview"))
            .Replace("{{CONTENT}}", body);
    }

    public static string RenderExportPage(string markdown, string? title = null, string? baseDir = null)
    {
        var body = ImageEmbedder.InlineLocalImages(RenderBodyHtml(markdown), baseDir);
        var template = LoadTemplate();
        return template
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title ?? "Preview"))
            .Replace("{{CONTENT}}", body);
    }

    private static void AnnotateBlocks(MarkdownDocument doc)
    {
        foreach (var block in doc.Descendants<Block>())
        {
            if (block.Line < 0) continue;
            var attrs = block.GetAttributes();
            attrs.AddPropertyIfNotExist("data-source-line", (block.Line + 1).ToString());
        }
    }

    private static string LoadTemplate()
    {
        if (_templateCache != null) return _templateCache;
        _templateCache = LoadResourceText("pack://application:,,,/Assets/PreviewTemplate.html");
        return _templateCache;
    }

    public static string LoadReference()
        => LoadResourceText("pack://application:,,,/Assets/MarkdownReference.md");

    private static string LoadResourceText(string packUri)
    {
        var info = System.Windows.Application.GetResourceStream(new System.Uri(packUri, System.UriKind.Absolute));
        if (info == null)
            throw new FileNotFoundException($"Resource not found: {packUri}");
        using var reader = new StreamReader(info.Stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
