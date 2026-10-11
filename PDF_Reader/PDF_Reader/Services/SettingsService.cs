using System.IO;
using System.Text.Json;

namespace PDF_Reader.Services;

public record RecentFile(string Path, int Page, DateTime OpenedAt)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}
public sealed class AppSettings
{
    public List<RecentFile> RecentFiles { get; set; } = [];
    public double Zoom { get; set; } = 1;
}
public sealed class SettingsService
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperDesk", "settings.json");
    public AppSettings Value { get; }
    public SettingsService(string? path = null)
    {
        if (path is not null) _path = path;
        try { Value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Value = new(); }
        Value.RecentFiles ??= [];
        if (!double.IsFinite(Value.Zoom) || Value.Zoom < .25 || Value.Zoom > 4) Value.Zoom = 1;
    }
    public void Touch(string path, int page)
    {
        Value.RecentFiles.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        Value.RecentFiles.Insert(0, new(path, page, DateTime.Now));
        Value.RecentFiles = Value.RecentFiles.Take(12).ToList();
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(Value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
