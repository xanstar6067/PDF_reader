using System.IO;
using System.Text.Json;
using PDF_Reader.Models;
using Tesseract;

namespace PDF_Reader.Services;

// Runs only in the child PaperDesk process, before single-instance routing or any window is created.
internal static class OcrWorker
{
    internal sealed record Request(string DataDirectory, OcrOptions Options, bool ValidateOnly = false);

    internal static int Run(string directory)
    {
        try
        {
            Directory.SetCurrentDirectory(directory);
            var request = JsonSerializer.Deserialize<Request>(File.ReadAllText("request.json")) ?? throw new InvalidDataException("Нет параметров OCR.");
            request.Options.Validate();
            // Limit CPU usage, as well as the job's process memory and lifetime.
            Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "2");
            var variables = new Dictionary<string, object>
            {
                ["load_system_dawg"] = request.Options.UseDictionaries,
                ["load_freq_dawg"] = request.Options.UseDictionaries
            };
            using var engine = new TesseractEngine(request.DataDirectory, string.Join('+', request.Options.Languages), EngineMode.LstmOnly, [], variables, false);
            if (request.ValidateOnly) return 0;
            if (!engine.SetVariable("user_defined_dpi", request.Options.Dpi) ||
                !engine.SetVariable("preserve_interword_spaces", request.Options.PreserveSpaces ? 1 : 0))
                throw new InvalidOperationException("Не удалось применить параметры Tesseract.");
            using var image = Pix.LoadFromFile("page.png");
            using var page = engine.Process(image, (PageSegMode)request.Options.PageSegmentation);
            if (string.IsNullOrWhiteSpace(page.GetText()))
            {
                File.WriteAllText("empty", "");
                return 0;
            }
            // Tesseract's glyphless font/ToUnicode supports scripts outside Segoe UI as well.
            using var renderer = ResultRenderer.CreatePdfRenderer("text", request.DataDirectory, textonly: true);
            using (renderer.BeginDocument("OCR"))
                if (!renderer.AddPage(page)) throw new InvalidOperationException("Не удалось создать текстовый слой OCR.");
            return 0;
        }
        catch (Exception ex)
        {
            // The WPF executable has no console. Report a bounded diagnostic through the owned directory.
            File.WriteAllText(Path.Combine(directory, "error.txt"), ex.Message[..Math.Min(ex.Message.Length, 1500)]);
            return 1;
        }
    }
}
