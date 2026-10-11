using System.IO;

namespace PDF_Reader.Services;

/// <summary>Diagnostic log for unexpected failures, kept small in %LOCALAPPDATA%\PaperDesk\Logs.</summary>
public static class ErrorLog
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Sync = new();

    public static string Path { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperDesk", "Logs", "errors.log");

    public static void Write(string context, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxSize) File.Move(Path, Path + ".old", overwrite: true);
                File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never cause a second failure.
        }
    }
}
