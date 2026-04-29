using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkTable = Markdig.Extensions.Tables.Table;
using MarkTableRow = Markdig.Extensions.Tables.TableRow;
using MarkTableCell = Markdig.Extensions.Tables.TableCell;

namespace MDExport.Services;

internal static class DocxExporter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static void Export(string markdown, string filePath)
    {
        if (File.Exists(filePath)) File.Delete(filePath);

        var astDoc = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        using var word = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var mainPart = word.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        var body = mainPart.Document.Body!;
        var ctx = new RenderContext(mainPart);

        foreach (var block in astDoc)
            RenderBlock(block, body, ctx, indent: 0, listPrefix: null);

        body.Append(new SectionProperties(
            new PageSize { Width = 12240U, Height = 15840U },
            new PageMargin
            {
                Top = 1440,
                Right = 1440U,
                Bottom = 1440,
                Left = 1440U,
                Header = 720U,
                Footer = 720U,
                Gutter = 0U
            }
        ));
    }

    private record RunFormat(bool Bold = false, bool Italic = false, bool Code = false, bool Hyperlink = false);

    private sealed class RenderContext
    {
        public MainDocumentPart MainPart { get; }
        public RenderContext(MainDocumentPart part) => MainPart = part;
        public string? AddHyperlink(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            var rel = MainPart.AddHyperlinkRelationship(uri, true);
            return rel.Id;
        }
    }

    private static void RenderBlock(Block block, OpenXmlElement parent, RenderContext ctx, int indent, string? listPrefix)
    {
        switch (block)
        {
            case HeadingBlock h:
                RenderHeading(h, parent, ctx, indent);
                break;

            case ParagraphBlock pb:
                RenderParagraph(pb, parent, ctx, indent, listPrefix);
                break;

            case QuoteBlock qb:
                foreach (var sub in qb)
                {
                    if (sub is ParagraphBlock subP)
                        RenderParagraph(subP, parent, ctx, indent + 1, null, italic: true);
                    else
                        RenderBlock(sub, parent, ctx, indent + 1, null);
                }
                break;

            case ListBlock lb:
                RenderList(lb, parent, ctx, indent);
                break;

            case CodeBlock cb:
                RenderCodeBlock(cb, parent, indent);
                break;

            case ThematicBreakBlock _:
                {
                    var p = new Paragraph();
                    p.AppendChild(new ParagraphProperties(
                        new ParagraphBorders(new BottomBorder
                        {
                            Val = BorderValues.Single,
                            Size = 6U,
                            Space = 1U,
                            Color = "AAAAAA"
                        })
                    ));
                    parent.AppendChild(p);
                }
                break;

            case MarkTable table:
                RenderTable(table, parent, ctx);
                break;

            case HtmlBlock _:
                // Skip raw HTML blocks in DOCX
                break;
        }
    }

    private static void RenderHeading(HeadingBlock h, OpenXmlElement parent, RenderContext ctx, int indent)
    {
        var size = h.Level switch
        {
            1 => 36,
            2 => 32,
            3 => 28,
            4 => 24,
            5 => 22,
            _ => 20
        };
        var p = new Paragraph();
        var pp = new ParagraphProperties(
            new SpacingBetweenLines { Before = "240", After = "120" },
            new KeepNext()
        );
        if (indent > 0) pp.AppendChild(new Indentation { Left = (720 * indent).ToString() });
        p.AppendChild(pp);
        if (h.Inline != null)
            AppendInlines(h.Inline, p, ctx, new RunFormat(Bold: true), extraSize: size);
        parent.AppendChild(p);
    }

    private static void RenderParagraph(ParagraphBlock pb, OpenXmlElement parent, RenderContext ctx, int indent, string? listPrefix, bool italic = false)
    {
        var p = new Paragraph();
        var pp = new ParagraphProperties();
        if (indent > 0)
        {
            if (listPrefix != null)
                pp.AppendChild(new Indentation { Left = (720 * indent).ToString(), Hanging = "360" });
            else
                pp.AppendChild(new Indentation { Left = (720 * indent).ToString() });
        }
        if (pp.HasChildren) p.AppendChild(pp);

        var format = new RunFormat(Italic: italic);
        if (listPrefix != null)
            AddRun(p, listPrefix, format);
        if (pb.Inline != null)
            AppendInlines(pb.Inline, p, ctx, format);
        parent.AppendChild(p);
    }

    private static void RenderList(ListBlock lb, OpenXmlElement parent, RenderContext ctx, int indent)
    {
        int counter = int.TryParse(lb.OrderedStart, out var s) ? s : 1;
        foreach (var item in lb)
        {
            if (item is not ListItemBlock itemBlock) continue;
            bool firstChild = true;
            foreach (var sub in itemBlock)
            {
                string? prefix = null;
                if (firstChild)
                {
                    prefix = lb.IsOrdered ? $"{counter}.\t" : "•\t";
                    firstChild = false;
                }
                if (sub is ParagraphBlock subP)
                    RenderParagraph(subP, parent, ctx, indent + 1, prefix);
                else
                    RenderBlock(sub, parent, ctx, indent + 1, null);
            }
            counter++;
        }
    }

    private static void RenderCodeBlock(CodeBlock cb, OpenXmlElement parent, int indent)
    {
        var lines = cb.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            var p = new Paragraph();
            var pp = new ParagraphProperties(
                new Shading { Val = ShadingPatternValues.Clear, Fill = "F6F8FA", Color = "auto" },
                new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }
            );
            if (indent > 0) pp.AppendChild(new Indentation { Left = (720 * indent).ToString() });
            p.AppendChild(pp);
            AddRun(p, lines.Lines[i].ToString() ?? string.Empty, new RunFormat(Code: true));
            parent.AppendChild(p);
        }
    }

    private static void RenderTable(MarkTable table, OpenXmlElement parent, RenderContext ctx)
    {
        var docTable = new Table();
        docTable.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "CCCCCC" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "CCCCCC" }
            )
        ));

        foreach (var rowObj in table)
        {
            if (rowObj is not MarkTableRow row) continue;
            var docRow = new TableRow();
            foreach (var cellObj in row)
            {
                if (cellObj is not MarkTableCell cell) continue;
                var docCell = new TableCell();
                foreach (var cellBlock in cell)
                    RenderBlock(cellBlock, docCell, ctx, 0, null);
                if (!docCell.Elements<Paragraph>().Any())
                    docCell.AppendChild(new Paragraph());
                docRow.AppendChild(docCell);
            }
            docTable.AppendChild(docRow);
        }
        parent.AppendChild(docTable);

        // Word requires a paragraph after a table at the body level
        if (parent is Body) parent.AppendChild(new Paragraph());
    }

    private static void AppendInlines(ContainerInline container, OpenXmlElement parent, RenderContext ctx, RunFormat format, int extraSize = 0)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    AddRun(parent, lit.Content.ToString(), format, extraSize);
                    break;

                case EmphasisInline em:
                    {
                        var nf = em.DelimiterCount switch
                        {
                            1 => format with { Italic = true },
                            2 => format with { Bold = true },
                            _ => format with { Bold = true, Italic = true }
                        };
                        AppendInlines(em, parent, ctx, nf, extraSize);
                    }
                    break;

                case CodeInline ci:
                    AddRun(parent, ci.Content, format with { Code = true }, extraSize);
                    break;

                case LinkInline link:
                    {
                        var relId = !string.IsNullOrEmpty(link.Url) ? ctx.AddHyperlink(link.Url) : null;
                        if (relId != null)
                        {
                            var hyperlink = new Hyperlink { Id = relId };
                            AppendInlines(link, hyperlink, ctx, format with { Hyperlink = true }, extraSize);
                            parent.AppendChild(hyperlink);
                        }
                        else
                        {
                            AppendInlines(link, parent, ctx, format, extraSize);
                        }
                    }
                    break;

                case AutolinkInline auto:
                    {
                        var relId = ctx.AddHyperlink(auto.Url);
                        if (relId != null)
                        {
                            var hyperlink = new Hyperlink { Id = relId };
                            AddRun(hyperlink, auto.Url, format with { Hyperlink = true }, extraSize);
                            parent.AppendChild(hyperlink);
                        }
                        else
                        {
                            AddRun(parent, auto.Url, format, extraSize);
                        }
                    }
                    break;

                case LineBreakInline lb:
                    if (lb.IsHard)
                        parent.AppendChild(new Run(new Break()));
                    else
                        AddRun(parent, " ", format, extraSize);
                    break;

                case HtmlEntityInline he:
                    AddRun(parent, he.Transcoded.ToString(), format, extraSize);
                    break;

                case HtmlInline _:
                    // Skip raw HTML inlines
                    break;
            }
        }
    }

    private static void AddRun(OpenXmlElement parent, string text, RunFormat format, int extraSize = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var run = new Run();
        var rp = new RunProperties();
        if (format.Bold) rp.Append(new Bold());
        if (format.Italic) rp.Append(new Italic());
        if (format.Code)
        {
            rp.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
            rp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F0F0F0" });
        }
        if (format.Hyperlink)
        {
            rp.Append(new Color { Val = "0563C1" });
            rp.Append(new Underline { Val = UnderlineValues.Single });
        }
        if (extraSize > 0)
            rp.Append(new FontSize { Val = extraSize.ToString() });
        if (rp.HasChildren) run.AppendChild(rp);
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        parent.AppendChild(run);
    }
}
