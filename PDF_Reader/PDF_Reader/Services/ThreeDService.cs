using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

/// <summary>Extracts model streams from a document snapshot and runs the isolated decoder.</summary>
public static class ThreeDService
{
    private sealed class Cache { public long Revision = -1; public readonly Dictionary<int, Task<ThreeDScene>> Pages = []; }
    private static readonly ConditionalWeakTable<PdfDocument, Cache> CacheByDocument = new();
    private static readonly SemaphoreSlim DecoderSlots = new(1);
    public static long PreferredGpu { get; set; }
    public static string? PreferredGpuName { get; set; }
    public static async Task<ThreeDScene> LoadAsync(PdfDocument document, int pageIndex, CancellationToken cancellation = default)
    {
        var cache = CacheByDocument.GetOrCreateValue(document);
        Task<ThreeDScene> task;
        lock (cache)
        {
            if (cache.Revision != document.Revision) { cache.Pages.Clear(); cache.Revision = document.Revision; }
            if (!cache.Pages.TryGetValue(pageIndex, out task!)) cache.Pages[pageIndex] = task = Task.Run(() => DecodeAsync(document, pageIndex));
        }
        return await task.WaitAsync(cancellation);
    }
    private static async Task<ThreeDScene> DecodeAsync(PdfDocument document, int pageIndex)
    {
        await DecoderSlots.WaitAsync();
        var directory = Path.Combine(Path.GetTempPath(), "PaperDesk3D-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = document.ThreeDInput(pageIndex);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(source.Bytes, new ParsingOptions { Password = source.Password ?? "" });
            IToken? Resolve(IToken? token)
            {
                for (var i = 0; token is IndirectReferenceToken reference && i < 32; i++) token = pdf.Structure.GetObject(reference.Data)?.Data;
                return token;
            }
            var page = pdf.GetPage(pageIndex + 1);
            var annotations = page.GetAnnotations().Where(a => a.AnnotationDictionary.TryGet(NameToken.Create("3DD"), out _)).ToArray();
            if (annotations.Length != 1) throw new NotSupportedException("Просмотр пока поддерживает одну 3D-модель на странице.");
            var annotation = annotations[0];
            annotation.AnnotationDictionary.TryGet(NameToken.Create("3DD"), out var modelToken);
            if (Resolve(modelToken) is not StreamToken stream) throw new InvalidDataException("Не найден поток 3D-модели.");
            var format = stream.StreamDictionary.TryGet<NameToken>(NameToken.Subtype, out var subtype) ? subtype.Data : "";
            if (format is not ("U3D" or "PRC")) throw new NotSupportedException("Неподдерживаемый формат 3D-модели.");
            // Decode only the filters used for geometry, with a bounded output.
            if (stream.Data.Length > 256 * 1024 * 1024) throw new InvalidDataException("Слишком большой 3D-поток.");
            byte[] data;
            if (stream.StreamDictionary.TryGet(NameToken.Filter, out var filter))
            {
                var resolved = Resolve(filter);
                var filters = resolved is ArrayToken array ? array.Data : new[] { resolved! };
                data = stream.Data.ToArray();
                foreach (var item in filters)
                {
                    if (Resolve(item) is not NameToken { Data: "FlateDecode" or "Fl" }) throw new NotSupportedException("Фильтр 3D-потока пока не поддерживается.");
                    using var input = new MemoryStream(data);
                    using var inflater = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    var chunk = new byte[65536]; int n;
                    while ((n = inflater.Read(chunk)) != 0) { if (output.Length + n > 256 * 1024 * 1024) throw new InvalidDataException("Распакованный 3D-поток слишком большой."); output.Write(chunk, 0, n); }
                    data = output.ToArray();
                }
            }
            else data = stream.Data.ToArray();
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "model.bin"), data);
            using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "PaperDeskModelWorker.exe"))
            { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("model.bin"); process.StartInfo.ArgumentList.Add("model.pd3d");
            if (!process.Start()) throw new InvalidOperationException("Не удалось запустить декодер 3D.");
            using var job = new Native.ModelWorkerJob(process);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw new TimeoutException("Чтение 3D-модели заняло больше 90 секунд."); }
            await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new InvalidDataException("Не удалось прочитать 3D-модель: " + error.Trim()[..Math.Min(500, error.Trim().Length)]);
            var bounds = source.Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) bounds = new Rect(0, 0, 1, 1);
            return ReadMesh(Path.Combine(directory, "model.pd3d"), format, bounds);
        }
        finally
        {
            // Only this freshly created, owned temporary directory is removed.
            if (Directory.Exists(directory)) try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            DecoderSlots.Release();
        }
    }
    internal static ThreeDScene ReadMesh(string path, string format, Rect bounds)
    {
        using var input = File.OpenRead(path); using var reader = new BinaryReader(input);
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) != "PD3D0001") throw new InvalidDataException("Некорректный результат декодирования 3D.");
        var flags = reader.ReadUInt32(); var count = reader.ReadUInt32();
        if (count == 0 || count > 10000) throw new InvalidDataException("Некорректное число частей 3D.");
        var parts = new List<ThreeDPart>(); var minimum = new Vector3(float.MaxValue); var maximum = new Vector3(float.MinValue); long total = 0;
        for (var i = 0; i < count; i++)
        {
            var nameLength = reader.ReadUInt32(); if (nameLength > 65536) throw new InvalidDataException("Слишком длинное имя детали.");
            var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes((int)nameLength)); var vertices = reader.ReadUInt32(); total += vertices;
            if (vertices % 3 != 0 || total > 15000000 || input.Length - input.Position < vertices * 40L) throw new InvalidDataException("Некорректная геометрия 3D.");
            var packed = new float[checked((int)vertices * 10)];
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(packed.AsSpan()); input.ReadExactly(bytes);
            for (var j = 0; j < packed.Length; j += 10)
            {
                for (var k = 0; k < 10; k++) if (!float.IsFinite(packed[j + k])) throw new InvalidDataException("Некорректная координата 3D.");
                var p = new Vector3(packed[j], packed[j + 1], packed[j + 2]); minimum = Vector3.Min(minimum, p); maximum = Vector3.Max(maximum, p);
            }
            parts.Add(new(name, packed));
        }
        if (input.Position != input.Length || total == 0) throw new InvalidDataException("Некорректная длина геометрии 3D.");
        var extent = maximum - minimum; var center = minimum + extent / 2; var scale = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
        if (!float.IsFinite(scale) || scale <= 0) throw new InvalidDataException("Пустые границы 3D-модели.");
        foreach (var part in parts) for (var j = 0; j < part.Vertices.Length; j += 10)
        { part.Vertices[j] = (part.Vertices[j] - center.X) / scale; part.Vertices[j + 1] = (part.Vertices[j + 1] - center.Y) / scale; part.Vertices[j + 2] = (part.Vertices[j + 2] - center.Z) / scale; }
        return new(parts, format, bounds, (flags & 1) != 0);
    }
    public static async Task<BitmapSource> PreviewAsync(PdfDocument document, int index, int width, CancellationToken cancellation)
    {
        var scene = await LoadAsync(document, index, cancellation);
        var image = await Task.Run(() => document.Render(index, width, cancellation: cancellation), cancellation);
        return await Task.Run(() =>
        {
            using var renderer = new ThreeDRenderer(scene, PreferredGpu, PreferredGpuName);
            var rect = new Rect(scene.Bounds.X * image.PixelWidth, scene.Bounds.Y * image.PixelHeight, scene.Bounds.Width * image.PixelWidth, scene.Bounds.Height * image.PixelHeight);
            var preview = renderer.Render((int)rect.Width, (int)rect.Height, new());
            // RenderTargetBitmap requires STA; perform composition in a dedicated STA thread.
            BitmapSource? result = null; Exception? failure = null;
            var thread = new Thread(() => { try { var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight)); dc.DrawImage(preview, rect); } var bitmap = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); result = bitmap; } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure; return result!;
        }, cancellation);
    }
}
