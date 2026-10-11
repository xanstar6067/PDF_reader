using System.Windows;
using Microsoft.Win32;
using PDF_Reader.Views;

namespace PDF_Reader.Services;

public interface IUserDialogs
{
    string[] ChooseOpenFiles();
    string? ChooseSaveFile(string suggestedName);
    string? AskText(string title, string description, string value = "", bool multiline = false, bool password = false);
    (string Text, float Size)? EditText(string title, string description, string value, float fontSize, bool multiline);
    MessageBoxResult AskSave(string name);
    bool Confirm(string title, string message);
    void Error(Exception exception);
    void Info(string title, string message);
}

public sealed class DialogService(Func<Window> owner) : IUserDialogs
{
    public string[] ChooseOpenFiles()
    {
        var dialog = new OpenFileDialog { Filter = "PDF документы (*.pdf)|*.pdf", Multiselect = true, CheckFileExists = true, Title = "Открыть PDF" };
        return dialog.ShowDialog(owner()) == true ? dialog.FileNames : [];
    }

    public string? ChooseSaveFile(string suggestedName)
    {
        var dialog = new SaveFileDialog { Filter = "PDF документы (*.pdf)|*.pdf", FileName = suggestedName, DefaultExt = ".pdf", AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? AskText(string title, string description, string value = "", bool multiline = false, bool password = false)
    {
        var dialog = new TextInputWindow(title, description, value, multiline, password) { Owner = owner() };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    public (string Text, float Size)? EditText(string title, string description, string value, float fontSize, bool multiline)
    {
        var dialog = new TextInputWindow(title, description, value, multiline, fontSize: fontSize) { Owner = owner() };
        return dialog.ShowDialog() == true ? (dialog.Value, dialog.TextFontSize) : null;
    }

    public MessageBoxResult AskSave(string name) =>
        MessageWindow.Show(owner(), $"Сохранить изменения в «{name}»?", "Несохранённые изменения", MessageBoxButton.YesNoCancel);

    public bool Confirm(string title, string message) =>
        MessageWindow.Show(owner(), message, title, MessageBoxButton.YesNo) == MessageBoxResult.Yes;

    public void Error(Exception exception) =>
        MessageWindow.Show(owner(), exception.Message, "Не удалось выполнить действие", MessageBoxButton.OK);

    public void Info(string title, string message) =>
        MessageWindow.Show(owner(), message, title, MessageBoxButton.OK);
}
