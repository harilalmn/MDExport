using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using MarkTable = Markdig.Extensions.Tables.Table;
using MarkTableRow = Markdig.Extensions.Tables.TableRow;
using MarkTableCell = Markdig.Extensions.Tables.TableCell;

namespace MDExport.Services;

internal static class DocxExporter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    /// <param name="baseDir">Folder of the markdown document; relative image paths resolve against it.</param>
    public static void Export(string markdown, string filePath, string? baseDir = null)
    {
        if (File.Exists(filePath)) File.Delete(filePath);

        var astDoc = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        using var word = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
        var mainPart = word.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        var body = mainPart.Document.Body!;
        var ctx = new RenderContext(mainPart, baseDir);

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
        private uint _nextDrawingId = 1;

        // One image part per distinct file, however often the document references it.
        private readonly Dictionary<string, string> _imageRelIds = new(StringComparer.OrdinalIgnoreCase);

        public MainDocumentPart MainPart { get; }
        public string? BaseDir { get; }

        public RenderContext(MainDocumentPart part, string? baseDir)
        {
            MainPart = part;
            BaseDir = baseDir;
        }

        public uint NextDrawingId() => _nextDrawingId++;

        public string AddImage(string path, PartTypeInfo partType)
        {
            if (_imageRelIds.TryGetValue(path, out var existing)) return existing;

            var imagePart = MainPart.AddImagePart(partType);
            using (var stream = File.OpenRead(path))
                imagePart.FeedData(stream);

            var relId = MainPart.GetIdOfPart(imagePart);
            _imageRelIds[path] = relId;
            return relId;
        }

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
        // CT_PPrBase fixes the child order: keepNext precedes spacing, which precedes ind.
        var pp = new ParagraphProperties(
            new KeepNext(),
            new SpacingBetweenLines { Before = "240", After = "120" }
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
            // CT_TblBorders fixes the child order: top, left, bottom, right, insideH, insideV.
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "999999" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "CCCCCC" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "CCCCCC" }
            )
        ));

        // CT_Tbl requires a tblGrid between the properties and the rows, one gridCol per column.
        var columnCount = table.OfType<MarkTableRow>()
            .Select(r => r.OfType<MarkTableCell>().Count())
            .DefaultIfEmpty(0)
            .Max();
        var grid = new TableGrid();
        for (int i = 0; i < columnCount; i++) grid.AppendChild(new GridColumn());
        docTable.AppendChild(grid);

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

                case LinkInline image when image.IsImage:
                    // Fall back to the alt text when the file is missing or Word cannot
                    // display the format (SVG, WebP, AVIF).
                    if (!TryAppendImage(image, parent, ctx))
                        AppendInlines(image, parent, ctx, format with { Italic = true }, extraSize);
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

    // --------------------- Images ---------------------

    private const long EmusPerInch = 914400L;
    private const double MaxContentWidthInches = 6.5;   // Letter width minus the 1" margins

    private static bool TryAppendImage(LinkInline image, OpenXmlElement parent, RenderContext ctx)
    {
        try
        {
            var path = ImageEmbedder.TryResolveLocalPath(image.Url, ctx.BaseDir);
            if (path == null) return false;

            var partType = ImagePartTypeFor(path);
            if (partType == null) return false;

            if (!TryMeasure(path, out var widthInches, out var heightInches)) return false;

            if (widthInches > MaxContentWidthInches)
            {
                heightInches *= MaxContentWidthInches / widthInches;
                widthInches = MaxContentWidthInches;
            }

            var relId = ctx.AddImage(path, partType.Value);

            var name = Path.GetFileName(path);
            var alt = image.FirstChild is LiteralInline lit ? lit.Content.ToString() : name;

            parent.AppendChild(new Run(BuildDrawing(
                relId,
                (long)Math.Round(widthInches * EmusPerInch),
                (long)Math.Round(heightInches * EmusPerInch),
                ctx.NextDrawingId(),
                name,
                alt)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static PartTypeInfo? ImagePartTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImagePartType.Png,
            ".jpg" or ".jpeg" or ".jfif" => ImagePartType.Jpeg,
            ".gif" => ImagePartType.Gif,
            ".bmp" => ImagePartType.Bmp,
            ".tif" or ".tiff" => ImagePartType.Tiff,
            _ => null   // SVG/WebP/AVIF: not reliably rendered by Word
        };

    private static bool TryMeasure(string path, out double widthInches, out double heightInches)
    {
        widthInches = heightInches = 0;
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return false;

            var dpiX = frame.DpiX > 1 ? frame.DpiX : 96.0;
            var dpiY = frame.DpiY > 1 ? frame.DpiY : 96.0;
            widthInches = frame.PixelWidth / dpiX;
            heightInches = frame.PixelHeight / dpiY;
            return widthInches > 0 && heightInches > 0;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static Drawing BuildDrawing(string relationshipId, long cx, long cy, uint id, string name, string alt) =>
        new(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = name, Description = alt },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(
                new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = name, Description = alt },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relationshipId },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U
        });

    private static void AddRun(OpenXmlElement parent, string text, RunFormat format, int extraSize = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var run = new Run();
        var rp = new RunProperties();
        // CT_RPr fixes the child order: rFonts, b, i, color, sz, u, shd.
        if (format.Code)
            rp.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
        if (format.Bold) rp.Append(new Bold());
        if (format.Italic) rp.Append(new Italic());
        if (format.Hyperlink)
            rp.Append(new Color { Val = "0563C1" });
        if (extraSize > 0)
            rp.Append(new FontSize { Val = extraSize.ToString() });
        if (format.Hyperlink)
            rp.Append(new Underline { Val = UnderlineValues.Single });
        if (format.Code)
            rp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "F0F0F0" });
        if (rp.HasChildren) run.AppendChild(rp);
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        parent.AppendChild(run);
    }
}
