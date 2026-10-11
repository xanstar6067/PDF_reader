using System.Diagnostics;
using Microsoft.Win32;

namespace PDF_Reader.Services;

public static class WindowsIntegration
{
    public static void RegisterAndOpenDefaultApps()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к приложению.");
        var command = "\"" + executable + "\" \"%1\"";
        Write(@"Software\Classes\PaperDesk.PDF", "", "PDF документ PaperDesk");
        Write(@"Software\Classes\PaperDesk.PDF\DefaultIcon", "", "\"" + executable + "\",0");
        Write(@"Software\Classes\PaperDesk.PDF\shell\open\command", "", command);
        Write(@"Software\Classes\PaperDesk.PDF\shell\print\command", "", "\"" + executable + "\" /p \"%1\"");
        Write(@"Software\Classes\.pdf\OpenWithProgids", "PaperDesk.PDF", "");
        Write(@"Software\Classes\Applications\PaperDesk.exe", "FriendlyAppName", "PaperDesk PDF");
        Write(@"Software\Classes\Applications\PaperDesk.exe\SupportedTypes", ".pdf", "");
        Write(@"Software\Classes\Applications\PaperDesk.exe\shell\open\command", "", command);
        Write(@"Software\PaperDesk\Capabilities", "ApplicationName", "PaperDesk PDF");
        Write(@"Software\PaperDesk\Capabilities", "ApplicationDescription", "Просмотр, редактирование и печать PDF документов");
        Write(@"Software\PaperDesk\Capabilities\FileAssociations", ".pdf", "PaperDesk.PDF");
        Write(@"Software\RegisteredApplications", "PaperDesk PDF", @"Software\PaperDesk\Capabilities");
        // UserChoice is managed by Windows. The user chooses the association in Settings.
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString("PaperDesk PDF")) { UseShellExecute = true });
    }
    private static void Write(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }
}
