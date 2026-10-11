using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

public sealed record RecoveryEntry(string Id, string? OriginalPath, string? DiskHash, DateTime SavedAt, int ProcessId, long ProcessStartTicks)
{
    public string Name => OriginalPath is null ? "Новый документ" : Path.GetFileName(OriginalPath);
}

/// <summary>
/// Autosaved copies of modified documents. Each copy is a PDF next to a JSON description; copies of a
/// process that is still running are never offered for restore, so two windows cannot steal each other's files.
/// Passwords are never stored: encrypted documents stay encrypted in the copy.
/// </summary>
public sealed class RecoveryService(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperDesk", "Recovery");
    private static readonly Process Current = Process.GetCurrentProcess();

    public string NewId() => $"{Environment.ProcessId}-{Guid.NewGuid():N}";

    private string DataPath(string id) => Path.Combine(_directory, id + ".pdf");
    private string InfoPath(string id) => Path.Combine(_directory, id + ".json");

    public void Write(string id, RecoverySnapshot snapshot)
    {
        Directory.CreateDirectory(_directory);
        var entry = new RecoveryEntry(id, snapshot.OriginalPath, snapshot.DiskHash is null ? null : Convert.ToHexString(snapshot.DiskHash),
            DateTime.Now, Environment.ProcessId, Current.StartTime.ToUniversalTime().Ticks);
        AtomicFile.Write(DataPath(id), snapshot.Bytes);
        AtomicFile.Write(InfoPath(id), JsonSerializer.SerializeToUtf8Bytes(entry));
    }

    public void Delete(string id)
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in new[] { InfoPath(id), DataPath(id) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write("Не удалось удалить резервную копию " + path, ex);
            }
        }
    }

    public byte[] Read(RecoveryEntry entry) => File.ReadAllBytes(DataPath(entry.Id));

    /// <summary>Copies left behind by processes that are no longer running.</summary>
    public IReadOnlyList<RecoveryEntry> FindOrphaned()
    {
        if (!Directory.Exists(_directory)) return [];
        var result = new List<RecoveryEntry>();
        foreach (var info in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllBytes(info));
                if (entry is null || Path.GetFileNameWithoutExtension(info) != entry.Id || !File.Exists(DataPath(entry.Id))) continue;
                if (!IsRunning(entry.ProcessId, entry.ProcessStartTicks)) result.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                ErrorLog.Write("Не удалось прочитать резервную копию " + info, ex);
            }
        }
        return result.OrderBy(e => e.SavedAt).ToList();
    }

    private static bool IsRunning(int processId, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static byte[]? ParseHash(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
