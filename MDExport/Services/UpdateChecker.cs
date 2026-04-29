using System;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace MDExport.Services;

public sealed record UpdateInfo(Version LatestVersion, string TagName, string? HtmlUrl)
{
    public bool IsNewerThan(Version current) => LatestVersion > current;
}

public static class UpdateChecker
{
    public const string ReleasesRepo = "harilalmn/MDExport-Releases";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/" + ReleasesRepo + "/releases/latest";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        return new Version(v.Major, v.Minor, v.Build);
    }

    public static async Task<UpdateInfo> FetchLatestReleaseAsync()
    {
        using var response = await HttpClient.GetAsync(LatestReleaseApi);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var root = doc.RootElement;
        var tagName = root.TryGetProperty("tag_name", out var tagEl)
            ? tagEl.GetString() ?? string.Empty
            : string.Empty;
        var htmlUrl = root.TryGetProperty("html_url", out var urlEl)
            ? urlEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(tagName))
            throw new InvalidOperationException("Latest release has no tag.");

        var versionString = tagName.TrimStart('v', 'V');
        if (!Version.TryParse(versionString, out var version))
            throw new InvalidOperationException($"Could not parse version from tag '{tagName}'.");

        return new UpdateInfo(NormalizeVersion(version), tagName, htmlUrl);
    }

    public static void OpenReleasePage(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MDExport-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version NormalizeVersion(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
