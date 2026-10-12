using System.IO;
using System.Text.Json;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

public record RecentFile(string Path, int Page, DateTime OpenedAt)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed class AppSettings
{
    public List<RecentFile> RecentFiles { get; set; } = [];
    public AppTheme Theme { get; set; } = AppTheme.System;
    public double Zoom { get; set; } = 1;
    public PrintScaling PrintScaling { get; set; } = PrintScaling.Fit;
    /// <summary>0: prefer discrete hardware; -1: WARP; otherwise DXGI adapter LUID.</summary>
    public long Preferred3DGpu { get; set; }
    public string? Preferred3DGpuName { get; set; }
}

public sealed class SettingsService
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperDesk", "settings.json");

    public AppSettings Value { get; }

    public SettingsService(string? path = null)
    {
        if (path is not null) _path = path;
        try
        {
            Value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Value = new();
        }
        Value.RecentFiles ??= [];
        if (!Enum.IsDefined(Value.Theme)) Value.Theme = AppTheme.System;
        if (!double.IsFinite(Value.Zoom) || Value.Zoom < .25 || Value.Zoom > 4) Value.Zoom = 1;
        if (!Enum.IsDefined(Value.PrintScaling)) Value.PrintScaling = PrintScaling.Fit;
    }

    /// <summary>Records a recent document and writes settings immediately, so a crash does not lose the list.</summary>
    public void Touch(string path, int page)
    {
        Value.RecentFiles.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        Value.RecentFiles.Insert(0, new(path, page, DateTime.Now));
        Value.RecentFiles = Value.RecentFiles.Take(12).ToList();
        TrySave();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(Value, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void TrySave()
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Не удалось сохранить настройки", ex);
        }
    }
}
