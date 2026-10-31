using System.IO;
using System.Text;
using Markdig;
using Markdig.Extensions.Footnotes;
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
    private static string? _editScriptCache;

    public static string RenderBodyHtml(string markdown)
        => Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    public static string RenderBodyHtmlForPreview(string markdown)
    {
        var doc = Markdown.Parse(markdown ?? string.Empty, PreviewPipeline);
        AnnotateBlocks(doc, markdown ?? string.Empty);
        using var sw = new StringWriter();
        var renderer = new HtmlRenderer(sw);
        PreviewPipeline.Setup(renderer);
        renderer.Render(doc);
        sw.Flush();
        return sw.ToString();
    }

    public static string RenderPreviewBody(string markdown, string? baseDir) =>
        ImageEmbedder.InlineLocalImages(RenderBodyHtmlForPreview(markdown), baseDir, keepOriginalUrl: true);

    /// <param name="baseDir">
    /// Folder the markdown document lives in; local images are resolved against it and
    /// embedded as data: URIs so they render inside the about:blank preview document.
    /// </param>
    public static string RenderFullPage(string markdown, string? title = null, string? baseDir = null, bool dark = false)
    {
        var body = RenderPreviewBody(markdown, baseDir);
        var template = LoadTemplate();
        return template
            .Replace("{{HTML_CLASS}}", dark ? "preview-dark" : string.Empty)
            .Replace("{{EDIT_SCRIPT}}", LoadEditScript())
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title ?? "Preview"))
            .Replace("{{CONTENT}}", body);
    }

    public static string RenderExportPage(string markdown, string? title = null, string? baseDir = null)
    {
        var body = ImageEmbedder.InlineLocalImages(RenderBodyHtml(markdown), baseDir);
        var template = LoadTemplate();
        return template
            .Replace("{{HTML_CLASS}}", string.Empty)
            .Replace("{{EDIT_SCRIPT}}", string.Empty)
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title ?? "Preview"))
            .Replace("{{CONTENT}}", body);
    }

    private static void AnnotateBlocks(MarkdownDocument doc, string markdown)
    {
        foreach (var block in doc.Descendants<Block>())
        {
            if (block.Line < 0) continue;
            var attrs = block.GetAttributes();
            attrs.AddPropertyIfNotExist("data-source-line", (block.Line + 1).ToString());
        }

        AnnotateSourceRanges(doc, markdown);
    }

    /// <summary>
    /// Gives top-level blocks their source range, so Edit view can replace one block of the file
    /// without touching the rest. Parser spans are not reliable enough to use as they stand: a
    /// grid table's starts at the top of the file and a pipe table's starts after its first
    /// pipe. So the range runs from the block's own line to the end of its last line, and any
    /// block whose range would overlap the previous one gets none, which locks it on the page.
    /// </summary>
    private static void AnnotateSourceRanges(MarkdownDocument doc, string markdown)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < markdown.Length; i++)
        {
            var endsLine = markdown[i] == '\n' || (markdown[i] == '\r' && (i + 1 == markdown.Length || markdown[i + 1] != '\n'));
            if (endsLine) lineStarts.Add(i + 1);
        }

        var previousEnd = 0;
        foreach (var block in doc)
        {
            // Footnotes and link references are gathered from all over the document.
            if (block is LinkReferenceDefinitionGroup or FootnoteGroup) continue;
            if (block.Span.IsEmpty || block.Line < 0 || block.Line >= lineStarts.Count) continue;

            var start = lineStarts[block.Line];
            var end = Math.Clamp(block.Span.End + 1, start, markdown.Length);
            while (end > start && IsLineBreak(markdown[end - 1])) end--; // Lists include theirs.
            while (end < markdown.Length && !IsLineBreak(markdown[end])) end++;
            if (start < previousEnd || end <= start) continue;
            previousEnd = end;

            var attrs = block.GetAttributes();
            attrs.AddPropertyIfNotExist("data-src-start", start.ToString());
            attrs.AddPropertyIfNotExist("data-src-end", end.ToString());
        }
    }

    private static bool IsLineBreak(char c) => c is '\n' or '\r';

    private static string LoadTemplate()
    {
        if (_templateCache != null) return _templateCache;
        _templateCache = LoadResourceText("pack://application:,,,/Assets/PreviewTemplate.html");
        return _templateCache;
    }

    private static string LoadEditScript() =>
        _editScriptCache ??= LoadResourceText("pack://application:,,,/Assets/EditMode.js");

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
