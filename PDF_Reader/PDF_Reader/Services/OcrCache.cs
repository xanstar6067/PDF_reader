using System.Text.Json;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

/// <summary>Per-tab bounded cache. Pending work is cancelled when its view, revision, or settings change.</summary>
internal sealed class OcrCache(PdfDocument document) : IDisposable
{
    internal const int MaxPages = 24;
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<int, Entry> _pages = [];
    private long _revision = -1, _sequence;
    private string? _settings;
    private bool _disposed;
    private sealed record Entry(Task<OcrPage?> Task, CancellationTokenSource Cancellation, CancellationToken ViewToken)
    {
        public long Used { get; set; }
        public CancellationToken Token { get; } = Cancellation.Token;
        public void Stop()
        {
            Cancellation.Cancel();
            _ = Task.ContinueWith(_ => Cancellation.Dispose(), TaskScheduler.Default);
        }
    }

    private void Configure(OcrOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = JsonSerializer.Serialize(options) + ":" + OcrService.LanguageRevision;
        if (_revision == document.Revision && _settings == settings) return;
        ClearCore(); _revision = document.Revision; _settings = settings;
    }

    public Task<OcrPage?> EnsureAsync(int index, OcrOptions options, CancellationToken cancellation)
    {
        lock (_gate)
        {
            Configure(options);
            if (!options.Enabled) return Task.FromResult<OcrPage?>(null);
            if (_pages.TryGetValue(index, out var previous))
            {
                if (!previous.Token.IsCancellationRequested && !previous.Task.IsCanceled &&
                    (previous.Task.IsCompletedSuccessfully || !previous.ViewToken.IsCancellationRequested))
                { previous.Used = ++_sequence; return previous.Task.WaitAsync(cancellation); }
                _pages.Remove(index); previous.Stop();
            }
            var source = new CancellationTokenSource();
            var token = source.Token;
            var snapshot = options with { Languages = options.Languages.ToArray() };
            var task = Task.Run(async () =>
            {
                // Detach view cancellation when completed: scrolling away must retain completed cache entries.
                using var registration = cancellation.Register(source.Cancel);
                return await OcrService.RecognizePageAsync(document, index, snapshot, token).ConfigureAwait(false);
            }, token);
            _pages[index] = new(task, source, cancellation) { Used = ++_sequence };
            Trim(index);
            return CompleteAsync(task, index, cancellation);
        }
    }

    private async Task<OcrPage?> CompleteAsync(Task<OcrPage?> task, int index, CancellationToken cancellation)
    {
        try { return await task.WaitAsync(cancellation).ConfigureAwait(false); }
        finally { lock (_gate) { if (!_disposed) Trim(index); } }
    }

    public OcrPage? Get(int index, OcrOptions options)
    {
        lock (_gate)
        {
            Configure(options);
            if (!_pages.TryGetValue(index, out var entry) || !entry.Task.IsCompletedSuccessfully || entry.Token.IsCancellationRequested) return null;
            entry.Used = ++_sequence;
            return entry.Task.Result;
        }
    }

    private void Trim(int keep)
    {
        long Size() => _pages.Values.Where(e => e.Task.IsCompletedSuccessfully).Sum(e => (long)(e.Task.Result?.ByteCount ?? 0));
        while (_pages.Count > MaxPages || Size() > MaxBytes && _pages.Count > 1)
        {
            var oldest = _pages.Where(p => p.Key != keep).OrderBy(p => p.Value.Used).First();
            _pages.Remove(oldest.Key); oldest.Value.Stop();
        }
    }

    public void Clear() { lock (_gate) { ClearCore(); _revision = -1; } }
    private void ClearCore() { foreach (var entry in _pages.Values) entry.Stop(); _pages.Clear(); }
    public void Dispose() { lock (_gate) { ClearCore(); _disposed = true; } }
}
