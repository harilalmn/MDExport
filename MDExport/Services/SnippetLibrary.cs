using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AvalonEdit.Snippets;

namespace MDExport.Services;

internal sealed record SnippetDefinition(
    string Id,
    string Category,
    string Title,
    string? Shortcut,
    bool IsBlock,
    Func<string, Snippet> Build);

internal static class SnippetLibrary
{
    public static IReadOnlyList<SnippetDefinition> All { get; } = Build();

    private static readonly Dictionary<string, SnippetDefinition> _byId =
        All.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    public static SnippetDefinition? Get(string id) =>
        _byId.TryGetValue(id, out var def) ? def : null;

    private static IReadOnlyList<SnippetDefinition> Build() => new SnippetDefinition[]
    {
        // ---------- Headings ----------
        new("h1", "Headings", "Heading 1", "Ctrl+1", true,
            sel => Block(("# ", null), Hold(sel, "Heading 1"), ("\n", null))),
        new("h2", "Headings", "Heading 2", "Ctrl+2", true,
            sel => Block(("## ", null), Hold(sel, "Heading 2"), ("\n", null))),
        new("h3", "Headings", "Heading 3", "Ctrl+3", true,
            sel => Block(("### ", null), Hold(sel, "Heading 3"), ("\n", null))),
        new("h4", "Headings", "Heading 4", "Ctrl+4", true,
            sel => Block(("#### ", null), Hold(sel, "Heading 4"), ("\n", null))),
        new("h5", "Headings", "Heading 5", "Ctrl+5", true,
            sel => Block(("##### ", null), Hold(sel, "Heading 5"), ("\n", null))),
        new("h6", "Headings", "Heading 6", "Ctrl+6", true,
            sel => Block(("###### ", null), Hold(sel, "Heading 6"), ("\n", null))),

        // ---------- Inline emphasis ----------
        new("bold", "Inline", "Bold", "Ctrl+B", false,
            sel => Wrap("**", "**", sel, "bold text")),
        new("italic", "Inline", "Italic", "Ctrl+I", false,
            sel => Wrap("*", "*", sel, "italic text")),
        new("bold-italic", "Inline", "Bold + Italic", null, false,
            sel => Wrap("***", "***", sel, "bold italic text")),
        new("strikethrough", "Inline", "Strikethrough", null, false,
            sel => Wrap("~~", "~~", sel, "struck text")),
        new("code-inline", "Inline", "Inline code", "Ctrl+`", false,
            sel => Wrap("`", "`", sel, "code")),
        new("highlight", "Inline", "Highlight (==text==)", null, false,
            sel => Wrap("==", "==", sel, "highlighted")),
        new("subscript", "Inline", "Subscript (~text~)", null, false,
            sel => Wrap("~", "~", sel, "sub")),
        new("superscript", "Inline", "Superscript (^text^)", null, false,
            sel => Wrap("^", "^", sel, "sup")),

        // ---------- Links / images / refs ----------
        new("link", "Links", "Link", "Ctrl+K", false,
            sel =>
            {
                var s = new Snippet();
                s.Elements.Add(Lit("["));
                s.Elements.Add(HoldEl(sel, "link text"));
                s.Elements.Add(Lit("]("));
                s.Elements.Add(Replace("https://"));
                s.Elements.Add(Lit(")"));
                return s;
            }),
        new("image", "Links", "Image", null, false,
            sel =>
            {
                var s = new Snippet();
                s.Elements.Add(Lit("!["));
                s.Elements.Add(HoldEl(sel, "alt text"));
                s.Elements.Add(Lit("]("));
                s.Elements.Add(Replace("https://"));
                s.Elements.Add(Lit(")"));
                return s;
            }),
        new("link-titled", "Links", "Link with title", null, false,
            sel =>
            {
                var s = new Snippet();
                s.Elements.Add(Lit("["));
                s.Elements.Add(HoldEl(sel, "link text"));
                s.Elements.Add(Lit("]("));
                s.Elements.Add(Replace("https://"));
                s.Elements.Add(Lit(" \""));
                s.Elements.Add(Replace("title"));
                s.Elements.Add(Lit("\")"));
                return s;
            }),
        new("autolink", "Links", "Autolink (<url>)", null, false,
            sel => Wrap("<", ">", sel, "https://example.com")),
        new("ref-link", "Links", "Reference link [text][id]", null, false,
            sel =>
            {
                var s = new Snippet();
                s.Elements.Add(Lit("["));
                s.Elements.Add(HoldEl(sel, "link text"));
                s.Elements.Add(Lit("]["));
                s.Elements.Add(Replace("ref-id"));
                s.Elements.Add(Lit("]"));
                return s;
            }),
        new("ref-link-def", "Links", "Reference link definition", null, true,
            sel => Block(
                ("[", null),
                ("ref-id", "ref-id"),
                ("]: ", null),
                ("https://example.com", "https://example.com"),
                (" \"", null),
                ("Optional title", "Optional title"),
                ("\"\n", null))),
        new("footnote", "Links", "Footnote marker", null, false,
            sel =>
            {
                var s = new Snippet();
                s.Elements.Add(Lit("[^"));
                s.Elements.Add(Replace("1"));
                s.Elements.Add(Lit("]"));
                return s;
            }),
        new("footnote-def", "Links", "Footnote definition", null, true,
            sel => Block(
                ("[^", null),
                ("1", "1"),
                ("]: ", null),
                Hold(sel, "Footnote text"),
                ("\n", null))),

        // ---------- Block elements ----------
        new("code-block", "Blocks", "Fenced code block", "Ctrl+Shift+`", true,
            sel =>
            {
                var s = new Snippet();
                s.Elements.Add(Lit("```"));
                s.Elements.Add(Replace("language"));
                s.Elements.Add(Lit("\n"));
                if (string.IsNullOrEmpty(sel))
                    s.Elements.Add(Replace("code"));
                else
                    s.Elements.Add(Lit(sel));
                s.Elements.Add(Lit("\n```\n"));
                return s;
            }),
        new("blockquote", "Blocks", "Blockquote", "Ctrl+Q", true,
            sel => Block(("> ", null), Hold(sel, "Quoted text"), ("\n", null))),
        new("nested-blockquote", "Blocks", "Nested blockquote", null, true,
            sel => Block(
                ("> ", null),
                Hold(sel, "Outer quote"),
                ("\n>\n>> ", null),
                ("Nested quote", "Nested quote"),
                ("\n", null))),
        new("hr", "Blocks", "Horizontal rule", null, true,
            sel => Block(("---\n", null))),
        new("comment", "Blocks", "HTML comment", "Ctrl+/", false,
            sel => Wrap("<!-- ", " -->", sel, "comment")),

        // ---------- Lists ----------
        new("ul", "Lists", "Bullet list", "Ctrl+L", true,
            sel => Block(
                ("- ", null),
                ("First item", "First item"),
                ("\n- ", null),
                ("Second item", "Second item"),
                ("\n- ", null),
                ("Third item", "Third item"),
                ("\n", null))),
        new("ol", "Lists", "Numbered list", "Ctrl+Shift+L", true,
            sel => Block(
                ("1. ", null),
                ("First item", "First item"),
                ("\n2. ", null),
                ("Second item", "Second item"),
                ("\n3. ", null),
                ("Third item", "Third item"),
                ("\n", null))),
        new("task", "Lists", "Task list", null, true,
            sel => Block(
                ("- [ ] ", null),
                ("Pending task", "Pending task"),
                ("\n- [x] ", null),
                ("Completed task", "Completed task"),
                ("\n- [ ] ", null),
                ("Another task", "Another task"),
                ("\n", null))),
        new("nested-list", "Lists", "Nested bullet list", null, true,
            sel => Block(
                ("- ", null),
                ("Top item", "Top item"),
                ("\n  - ", null),
                ("Nested item 1", "Nested item 1"),
                ("\n  - ", null),
                ("Nested item 2", "Nested item 2"),
                ("\n- ", null),
                ("Sibling item", "Sibling item"),
                ("\n", null))),
        new("def-list", "Lists", "Definition list", null, true,
            sel => Block(
                ("Term", "Term"),
                ("\n: ", null),
                ("Definition of the term", "Definition of the term"),
                ("\n", null))),

        // ---------- Tables ----------
        new("table-pipe", "Tables", "Pipe table (3×3)", "Ctrl+T", true,
            sel => Block(
                ("| ", null), ("Header 1", "Header 1"), (" | ", null),
                ("Header 2", "Header 2"), (" | ", null), ("Header 3", "Header 3"), (" |\n", null),
                ("|---|---|---|\n| ", null),
                ("a1", "a1"), (" | ", null), ("a2", "a2"), (" | ", null), ("a3", "a3"), (" |\n| ", null),
                ("b1", "b1"), (" | ", null), ("b2", "b2"), (" | ", null), ("b3", "b3"), (" |\n", null))),
        new("table-aligned", "Tables", "Aligned table (left/center/right)", null, true,
            sel => Block(
                ("| Left | Center | Right |\n", null),
                ("|:-----|:------:|------:|\n", null),
                ("| ", null), ("a", "a"), (" | ", null), ("b", "b"), (" | ", null), ("c", "c"), (" |\n| ", null),
                ("d", "d"), (" | ", null), ("e", "e"), (" | ", null), ("f", "f"), (" |\n", null))),
        new("table-grid", "Tables", "Grid table (with spans)", null, true,
            sel => Block(
                ("+---------+---------+---------+\n", null),
                ("| ", null), ("Header A", "Header A"), (" | ", null), ("Header B", "Header B"), (" | ", null), ("Header C", "Header C"), (" |\n", null),
                ("+=========+=========+=========+\n", null),
                ("| ", null), ("merged columns", "merged columns"), ("              | ", null), ("c1", "c1"), (" |\n", null),
                ("+---------+---------+---------+\n", null),
                ("| ", null), ("a2", "a2"), (" | ", null), ("b2", "b2"), (" | ", null), ("c2", "c2"), (" |\n", null),
                ("+         +---------+---------+\n", null),
                ("|         | ", null), ("merged row above", "merged row above"), ("    |\n", null),
                ("+---------+---------+---------+\n", null))),
        new("table-html", "Tables", "HTML table (rowspan / colspan)", null, true,
            sel => Block(
                ("<table>\n", null),
                ("  <thead>\n    <tr><th>", null), ("Region", "Region"),
                ("</th><th>", null), ("Q1", "Q1"),
                ("</th><th>", null), ("Q2", "Q2"),
                ("</th></tr>\n  </thead>\n  <tbody>\n", null),
                ("    <tr><td rowspan=\"2\">", null), ("North", "North"),
                ("</td><td>", null), ("100", "100"),
                ("</td><td>", null), ("120", "120"),
                ("</td></tr>\n", null),
                ("    <tr><td colspan=\"2\" align=\"center\">", null), ("merged Q1+Q2", "merged Q1+Q2"),
                ("</td></tr>\n  </tbody>\n</table>\n", null))),

        // ---------- Other ----------
        new("front-matter", "Other", "YAML front matter", null, true,
            sel => Block(
                ("---\n", null),
                ("title: ", null), ("Document title", "Document title"), ("\n", null),
                ("author: ", null), ("Your name", "Your name"), ("\n", null),
                ("date: ", null), ("2026-01-01", "2026-01-01"), ("\n", null),
                ("---\n\n", null))),
        new("math-inline", "Other", "Math (inline)", null, false,
            sel => Wrap("$", "$", sel, "x^2 + y^2 = z^2")),
        new("math-block", "Other", "Math (block)", null, true,
            sel =>
            {
                var s = new Snippet();
                s.Elements.Add(Lit("$$\n"));
                if (string.IsNullOrEmpty(sel))
                    s.Elements.Add(Replace("\\int_a^b f(x)\\,dx"));
                else
                    s.Elements.Add(Lit(sel));
                s.Elements.Add(Lit("\n$$\n"));
                return s;
            }),
        new("mermaid", "Other", "Mermaid diagram", null, true,
            sel => Block(
                ("```mermaid\n", null),
                ("graph TD\n", "graph TD\n"),
                ("  A[", null), ("Start", "Start"), ("] --> B[", null),
                ("End", "End"), ("]\n```\n", null))),
        new("admonition", "Other", "Admonition / callout", null, true,
            sel => Block(
                ("> [!", null),
                ("NOTE", "NOTE"),
                ("] ", null),
                ("Title", "Title"),
                ("\n> ", null),
                Hold(sel, "Body of the callout."),
                ("\n", null))),
    };

    // -------- helpers --------

    private static SnippetTextElement Lit(string text) =>
        new() { Text = text };

    private static SnippetReplaceableTextElement Replace(string defaultText) =>
        new() { Text = defaultText };

    private static (string text, string? placeholder) Hold(string selection, string fallback) =>
        string.IsNullOrEmpty(selection) ? (fallback, fallback) : (selection, null);

    private static SnippetElement HoldEl(string selection, string fallback) =>
        string.IsNullOrEmpty(selection)
            ? new SnippetReplaceableTextElement { Text = fallback }
            : new SnippetTextElement { Text = selection };

    /// <summary>
    /// Builds a snippet from (text, placeholder) tuples. If placeholder is non-null, the text
    /// becomes a Tab-able placeholder; if null, it's literal.
    /// </summary>
    private static Snippet Block(params (string text, string? placeholder)[] parts)
    {
        var s = new Snippet();
        foreach (var (text, placeholder) in parts)
        {
            if (placeholder == null)
                s.Elements.Add(new SnippetTextElement { Text = text });
            else
                s.Elements.Add(new SnippetReplaceableTextElement { Text = text });
        }
        return s;
    }

    private static Snippet Wrap(string left, string right, string selection, string placeholder)
    {
        var s = new Snippet();
        s.Elements.Add(new SnippetTextElement { Text = left });
        if (string.IsNullOrEmpty(selection))
            s.Elements.Add(new SnippetReplaceableTextElement { Text = placeholder });
        else
            s.Elements.Add(new SnippetTextElement { Text = selection });
        s.Elements.Add(new SnippetTextElement { Text = right });
        return s;
    }
}
