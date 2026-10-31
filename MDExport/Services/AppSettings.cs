using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MDExport.Services;

internal enum ViewMode { Write, Split, Read }

internal sealed record PdfOptions(string PageSize, bool Landscape, double MarginInches, bool PageNumbers)
{
    public static PdfOptions Default { get; } = new(
        System.Globalization.RegionInfo.CurrentRegion.IsMetric ? "A4" : "Letter", false, 0.75, true);
}

/// <summary>User preferences, kept in a JSON file under %LocalAppData%\MDExport.</summary>
internal sealed class AppSettings
{
    private const int MaxRecentFiles = 10;

    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MDExport");

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public ViewMode ViewMode { get; set; } = ViewMode.Split;
    public bool ShowOutline { get; set; }
    public bool WordWrap { get; set; } = true;
    public bool DarkPreview { get; set; }
    public double Zoom { get; set; } = 1.0;
    public PdfOptions Pdf { get; set; } = PdfOptions.Default;
    public List<string> RecentFiles { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged settings file must never stop the app opening.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are not worth interrupting the user over.
        }
    }

    public void AddRecent(string path)
    {
        RecentFiles = RecentFiles
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            .Prepend(path)
            .Take(MaxRecentFiles)
            .ToList();
    }
}
