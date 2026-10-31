using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MDExport.Services;

/// <summary>
/// Resolves local image references and inlines them into rendered HTML as data: URIs.
///
/// The preview is served from a synthetic origin (see <see cref="HtmlDocumentHost"/>), so
/// paths relative to the markdown file cannot resolve against it. Exported HTML/PDF has the
/// same problem the moment the output lands somewhere other than the markdown file's folder.
/// Embedding the bytes fixes both and makes exports self-contained.
/// </summary>
internal static class ImageEmbedder
{
    // Guard against turning a huge file into an even huger base64 string.
    private const long MaxEmbedBytes = 20L * 1024 * 1024;

    private static readonly Regex ImgSrcPattern = new(
        @"(<img\b[^>]*?\bsrc\s*=\s*)(""(?<dq>[^""]*)""|'(?<sq>[^']*)')",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record CacheEntry(long Length, DateTime LastWriteUtc, string DataUri);

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites every &lt;img src&gt; that points at a readable local file into a data: URI.
    /// Remote (http/https), protocol-relative and already-inlined sources are left alone,
    /// as are references that cannot be resolved — those keep their original markup.
    /// </summary>
    /// <param name="keepOriginalUrl">
    /// Also records the reference as written in data-md-src, which Edit view needs in order to
    /// write the image back as a path rather than as the whole embedded file.
    /// </param>
    public static string InlineLocalImages(string html, string? baseDir, bool keepOriginalUrl = false)
    {
        if (string.IsNullOrEmpty(html)) return html;
        if (html.IndexOf("<img", StringComparison.OrdinalIgnoreCase) < 0) return html;

        return ImgSrcPattern.Replace(html, match =>
        {
            var quoted = match.Groups[2].Value;
            var quote = quoted.Length > 0 ? quoted[0] : '"';
            var src = match.Groups["dq"].Success ? match.Groups["dq"].Value : match.Groups["sq"].Value;

            var path = TryResolveLocalPath(src, baseDir);
            if (path == null) return match.Value;

            var dataUri = TryGetDataUri(path);
            if (dataUri == null) return match.Value;

            var original = keepOriginalUrl ? $" data-md-src={quote}{src}{quote}" : string.Empty;
            return match.Groups[1].Value + quote + dataUri + quote + original;
        });
    }

    /// <summary>
    /// Maps a markdown image URL onto an existing local file, or returns null when the URL is
    /// remote, unresolvable, or points at nothing. <paramref name="baseDir"/> is the folder of
    /// the markdown document; relative URLs are resolved against it.
    /// </summary>
    public static string? TryResolveLocalPath(string? url, string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var value = System.Net.WebUtility.HtmlDecode(url).Trim();
        if (value.Length == 0) return null;

        // Fragment-only or remote references are not ours to resolve.
        if (value.StartsWith("#", StringComparison.Ordinal)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) return null;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.StartsWith("http:", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return null;

        // Strip any query/fragment suffix that a browser would ignore for a local file.
        var cut = value.IndexOfAny(new[] { '?', '#' });
        if (cut > 0) value = value[..cut];

        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var fileUri) || !fileUri.IsFile) return null;
            return Exists(fileUri.LocalPath);
        }

        // Any other explicit scheme (mailto:, ftp:, custom) is out of scope. A bare Windows
        // drive letter ("C:/pics/a.png") looks like a scheme to Uri, so check for that first.
        if (!(value.Length > 1 && value[1] == ':') && HasScheme(value)) return null;

        // Markdig percent-encodes spaces and other specials in URLs; try the decoded form
        // first and fall back to the literal text for the rare name containing a real '%'.
        var candidates = new List<string> { value };
        try
        {
            var unescaped = Uri.UnescapeDataString(value);
            if (!string.Equals(unescaped, value, StringComparison.Ordinal))
                candidates.Insert(0, unescaped);
        }
        catch (UriFormatException) { /* keep the literal form */ }

        foreach (var candidate in candidates)
        {
            var normalized = candidate.Replace('/', Path.DirectorySeparatorChar);
            string full;
            try
            {
                full = Path.IsPathRooted(normalized)
                    ? Path.GetFullPath(normalized)
                    : string.IsNullOrEmpty(baseDir)
                        ? string.Empty
                        : Path.GetFullPath(Path.Combine(baseDir, normalized));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (full.Length == 0) continue;
            var hit = Exists(full);
            if (hit != null) return hit;
        }

        return null;
    }

    /// <summary>The embedded form of a local image URL, or null if it cannot be read.</summary>
    public static string? TryGetDisplayUrl(string url, string? baseDir)
    {
        var path = TryResolveLocalPath(url, baseDir);
        return path == null ? null : TryGetDataUri(path);
    }

    public static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" or ".svgz" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream"
    };

    private static string? TryGetDataUri(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxEmbedBytes) return null;

            if (Cache.TryGetValue(path, out var cached) &&
                cached.Length == info.Length &&
                cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                return cached.DataUri;
            }

            var bytes = File.ReadAllBytes(path);
            var dataUri = "data:" + MimeTypeFor(path) + ";base64," + Convert.ToBase64String(bytes);

            // Bounded cache: a document rarely references more than a handful of images,
            // and dropping everything is cheaper than tracking usage.
            if (Cache.Count > 64) Cache.Clear();
            Cache[path] = new CacheEntry(info.Length, info.LastWriteTimeUtc, dataUri);
            return dataUri;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static string? Exists(string path)
    {
        try
        {
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;
        for (int i = 0; i < colon; i++)
        {
            var c = value[i];
            var ok = char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '.';
            if (!ok) return false;
        }
        return char.IsLetter(value[0]);
    }
}
