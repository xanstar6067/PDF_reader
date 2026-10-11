using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace PDF_Reader.Services;

/// <summary>
/// Keeps one PaperDesk window per user session. A second launch (for example from Explorer) passes its
/// arguments to the running instance through a named pipe restricted to the current user, then exits.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string Header = "PaperDesk/1";
    private const int MaxMessage = 64 * 1024;
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstanceService(Mutex mutex, string pipe)
    {
        _mutex = mutex;
        _pipe = pipe;
    }

    private static string Identity()
    {
        using var user = WindowsIdentity.GetCurrent();
        return (user.User?.Value ?? Environment.UserName) + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    }

    /// <summary>Returns the service when this process is the first instance; otherwise forwards the arguments and returns null.</summary>
    public static SingleInstanceService? Start(string[] arguments)
    {
        var identity = Identity();
        var mutex = new Mutex(true, @"Local\PaperDesk-" + identity, out var created);
        var pipe = "PaperDesk-" + identity;
        if (created) return new SingleInstanceService(mutex, pipe);
        mutex.Dispose();
        if (Forward(pipe, arguments)) return null;
        // The first instance does not answer (still starting or hung): keep working in a separate window.
        return new SingleInstanceService(new Mutex(false), pipe + "-" + Environment.ProcessId);
    }

    private static bool Forward(string pipe, string[] arguments)
    {
        var lines = arguments.Select(a => a.StartsWith('/') || a.StartsWith("--") ? a : Path.GetFullPath(a));
        var message = Encoding.UTF8.GetBytes(string.Join('\n', lines.Prepend(Header)));
        if (message.Length > MaxMessage) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(2000);
                // The running window may come to the foreground only if this process allows it.
                AllowSetForegroundWindow(-1);
                client.Write(message);
                client.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
        return false;
    }

    public void Listen(Func<string[], Task> onArguments) => _ = ListenAsync(onArguments);

    private async Task ListenAsync(Func<string[], Task> onArguments)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                int read;
                while ((read = await server.ReadAsync(chunk, timeout.Token)) > 0 && buffer.Length <= MaxMessage) buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxMessage) continue;
                var lines = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n');
                if (lines.Length == 0 || lines[0] != Header) continue;
                await onArguments(lines.Skip(1).Where(l => l.Length > 0).ToArray());
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Ошибка приёма аргументов от второго запуска", ex);
                await Task.Delay(500);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (fallback instance).
        }
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
