using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MDExport.Services;

internal sealed record RecoverySnapshot(int ProcessId, string? DocumentPath, string Text, DateTime SavedUtc);

/// <summary>
/// Keeps a copy of unsaved work outside the document, one file per running process. A snapshot
/// whose process is gone means that process died with unsaved work.
/// </summary>
internal static class RecoveryStore
{
    private static readonly string Folder = Path.Combine(AppSettings.DataFolder, "Recovery");
    private static string OwnFile => Path.Combine(Folder, $"{Environment.ProcessId}.json");

    public static void Write(string? documentPath, string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = OwnFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                new RecoverySnapshot(Environment.ProcessId, documentPath, text, DateTime.UtcNow)));
            File.Move(temp, OwnFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("Recovery snapshot failed: " + ex.Message);
        }
    }

    public static void Clear() => TryDelete(OwnFile);

    /// <summary>Removes and returns the newest snapshot left behind by a dead process.</summary>
    public static (RecoverySnapshot Snapshot, string File)? FindOrphan()
    {
        if (!Directory.Exists(Folder)) return null;

        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<RecoverySnapshot>(File.ReadAllText(file));
                if (snapshot == null) { TryDelete(file); continue; }
                if (IsRunning(snapshot.ProcessId)) continue;
                return (snapshot, file);
            }
            catch (JsonException)
            {
                TryDelete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still being written by a live instance.
            }
        }
        return null;
    }

    public static void Discard(string file) => TryDelete(file);

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals("MDExport", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
