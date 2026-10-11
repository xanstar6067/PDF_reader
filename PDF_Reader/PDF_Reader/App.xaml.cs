using System.Windows;

namespace PDF_Reader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window;
        window.Loaded += async (_, _) => await window.OpenArgumentsAsync(e.Args);
        window.Show();
    }
}
