using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using PDF_Reader.Models;
using PDF_Reader.Native;

namespace PDF_Reader.Services;

public static class OcrService
{
    private static readonly SemaphoreSlim Slots = new(1);
    private static long _languageRevision;
    internal static long LanguageRevision => Interlocked.Read(ref _languageRevision);
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "tessdata");
    public static string UserDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperDesk", "tessdata");

    public static IReadOnlyList<string> Languages() => new[] { BundledDirectory, UserDirectory }
        .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.traineddata"))
        .Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(OcrOptions.IsLanguageCode)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string LanguageName(string code) => code switch
    {
        "rus" => "Русский", "eng" => "Английский", "bel" => "Белорусский", "ukr" => "Украинский",
        "deu" => "Немецкий", "fra" => "Французский", "spa" => "Испанский", "ita" => "Итальянский",
        "pol" => "Польский", "chi_sim" => "Китайский (упрощённый)", "chi_tra" => "Китайский (традиционный)",
        "jpn" => "Японский", "kor" => "Корейский", "ara" => "Арабский", "heb" => "Иврит", _ => code
    };

    public static async Task ImportLanguageAsync(string source, CancellationToken cancellation = default)
        => await ImportLanguageAsync(source, UserDirectory, cancellation).ConfigureAwait(false);

    internal static async Task ImportLanguageAsync(string source, string targetDirectory, CancellationToken cancellation = default)
    {
        var code = Path.GetFileNameWithoutExtension(source);
        if (!Path.GetExtension(source).Equals(".traineddata", StringComparison.OrdinalIgnoreCase) || !OcrOptions.IsLanguageCode(code))
            throw new ArgumentException("Выберите языковой файл Tesseract вида deu.traineddata с исходным именем языка.");
        if (new FileInfo(source).Length is < 1024 or > 64 * 1024 * 1024)
            throw new ArgumentException("Размер языкового пакета должен быть от 1 КБ до 64 МБ.");
        await Slots.WaitAsync(cancellation).ConfigureAwait(false);
        var directory = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, code + ".traineddata");
            File.Copy(source, staged);
            await RunWorkerAsync(directory, new(directory, new OcrOptions { Languages = [code] }, ValidateOnly: true), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetDirectory);
            AtomicFile.Write(Path.Combine(targetDirectory, code + ".traineddata"), await File.ReadAllBytesAsync(staged, cancellation).ConfigureAwait(false));
            Interlocked.Increment(ref _languageRevision);
        }
        finally { Cleanup(directory); Slots.Release(); }
    }

    internal static async Task<OcrPage?> RecognizePageAsync(PdfDocument document, int index, OcrOptions options, CancellationToken cancellation = default)
    {
        var bytes = await RecognizeLayerAsync(document, index, options, cancellation).ConfigureAwait(false);
        return bytes is null ? null : new OcrPage(bytes);
    }

    internal static async Task<int> SaveSearchableCopyAsync(PdfDocument document, string path, IReadOnlyList<int> pages,
        OcrOptions options, IProgress<(int Current, int Total)>? progress, CancellationToken cancellation)
    {
        using var copy = document.CopyForOcr();
        var added = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report((i + 1, pages.Count));
            var index = pages[i];
            var bytes = await RecognizeLayerAsync(copy, index, options with { Enabled = true }, cancellation, skipPagesWithText: true).ConfigureAwait(false);
            if (bytes is not { Length: > 0 }) continue;
            copy.InsertOcrLayer(index, bytes);
            added++;
        }
        cancellation.ThrowIfCancellationRequested();
        if (added > 0)
        {
            var output = copy.GetBytes();
            cancellation.ThrowIfCancellationRequested();
            AtomicFile.Write(path, output);
        }
        return added;
    }

    private static async Task<byte[]?> RecognizeLayerAsync(PdfDocument document, int index, OcrOptions options,
        CancellationToken cancellation, bool skipPagesWithText = false)
    {
        options.Validate();
        options = options with { Languages = options.Languages.ToArray() };
        if (index < 0 || index >= document.PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (!options.Enabled) return null;
        await Slots.WaitAsync(cancellation).ConfigureAwait(false);
        var directory = TemporaryDirectory();
        try
        {
            var input = document.PrepareOcrPage(index, options.Dpi, cancellation, skipPagesWithText);
            if (input is null) return null;
            Directory.CreateDirectory(directory);
            var data = Path.Combine(directory, "tessdata"); Directory.CreateDirectory(data);
            foreach (var code in options.Languages)
            {
                var file = code + ".traineddata";
                var source = Path.Combine(UserDirectory, file);
                if (!File.Exists(source)) source = Path.Combine(BundledDirectory, file);
                if (!File.Exists(source)) throw new FileNotFoundException($"Не установлен язык OCR: {LanguageName(code)} ({code}). Откройте настройки OCR.");
                File.Copy(source, Path.Combine(data, file));
            }
            File.Copy(Path.Combine(BundledDirectory, "pdf.ttf"), Path.Combine(data, "pdf.ttf"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(input.Image));
            using (var stream = File.Create(Path.Combine(directory, "page.png"))) encoder.Save(stream);
            await RunWorkerAsync(directory, new(data, options), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (document.Revision != input.Revision) throw new OperationCanceledException("Страница изменилась во время OCR.");
            if (File.Exists(Path.Combine(directory, "empty"))) return [];
            var result = Path.Combine(directory, "text.pdf");
            if (!File.Exists(result) || new FileInfo(result).Length is < 100 or > 16 * 1024 * 1024)
                throw new InvalidDataException("Некорректный результат OCR.");
            var bytes = await File.ReadAllBytesAsync(result, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return bytes;
        }
        finally { Cleanup(directory); Slots.Release(); }
    }

    private static async Task RunWorkerAsync(string directory, OcrWorker.Request request, CancellationToken cancellation)
    {
        await File.WriteAllBytesAsync(Path.Combine(directory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(request), cancellation).ConfigureAwait(false);
        var executable = Path.Combine(AppContext.BaseDirectory, "PaperDesk.exe");
        using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory } };
        process.StartInfo.ArgumentList.Add("--ocr-worker"); process.StartInfo.ArgumentList.Add(directory);
        cancellation.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Не удалось запустить Tesseract.");
        using var job = new ModelWorkerJob(process, "OCR");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException("Распознавание страницы заняло больше трёх минут. Попробуйте уменьшить разрешение OCR.");
        }
        if (process.ExitCode != 0)
        {
            var error = Path.Combine(directory, "error.txt");
            var message = File.Exists(error) ? await File.ReadAllTextAsync(error, cancellation).ConfigureAwait(false)
                : $"Процесс завершился с кодом {process.ExitCode}. Проверьте совместимость языкового пакета и установку Microsoft Visual C++ Redistributable x64 (2019 или новее).";
            throw new InvalidOperationException("Ошибка Tesseract: " + message[..Math.Min(message.Length, 1500)]);
        }
    }

    private static string TemporaryDirectory() => Path.Combine(Path.GetTempPath(), "PaperDeskOCR-" + Guid.NewGuid().ToString("N"));
    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorLog.Write("Удаление временных данных OCR", ex); }
    }
}
