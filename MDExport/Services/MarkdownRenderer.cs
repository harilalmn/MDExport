using System.IO;
using System.Reflection;
using System.Text;
using Markdig;

namespace MDExport.Services;

internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private static string? _templateCache;

    public static string RenderBodyHtml(string markdown)
        => Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    public static string RenderFullPage(string markdown, string? title = null)
    {
        var body = RenderBodyHtml(markdown);
        var template = LoadTemplate();
        return template
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title ?? "Preview"))
            .Replace("{{CONTENT}}", body);
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
