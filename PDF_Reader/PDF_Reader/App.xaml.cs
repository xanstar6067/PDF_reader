using System.Windows;
using System.Windows.Threading;
using PDF_Reader.Services;

namespace PDF_Reader;

public partial class App : Application
{
    private SingleInstanceService? _instance;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = SingleInstanceService.Start(e.Args);
        if (_instance is null)
        {
            // Another PaperDesk window received the files.
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var window = new MainWindow();
        _window = window;
        MainWindow = window;
        window.Loaded += async (_, _) => await window.StartAsync(e.Args);
        window.Show();
        _instance.Listen(args => Dispatcher.InvokeAsync(() => window.ReceiveArgumentsAsync(args)).Task.Unwrap());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("Необработанная ошибка интерфейса", e.Exception);
        _window?.Workspace.EmergencySave();
        // The window keeps working: the failed action is reported and documents stay open.
        e.Handled = true;
        try
        {
            Views.MessageWindow.Show(_window!, "Произошла непредвиденная ошибка: " + e.Exception.Message +
                "\n\nИзменения документов сохранены в резервную копию. Подробности записаны в журнал:\n" + ErrorLog.Path,
                "PaperDesk PDF", MessageBoxButton.OK);
        }
        catch (Exception)
        {
            // The UI itself may be unusable; the log and recovery copies are already written.
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // The process is terminating: keep what can be kept without waiting for the UI thread.
        ErrorLog.Write("Аварийное завершение", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        _window?.Workspace.EmergencySave();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorLog.Write("Ошибка фоновой задачи", e.Exception);
        e.SetObserved();
    }
}
