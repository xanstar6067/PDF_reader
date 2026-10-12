using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDF_Reader.Controls;
using PDF_Reader.Models;
using PDF_Reader.Services;
using PDF_Reader.ViewModels;

namespace PDF_Reader.Tests;

internal static class Program
{
    private static int _assertions;
    private static Exception? _dispatcherFailure;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/tests");
            Directory.CreateDirectory(output);
            ErrorLog.Path = Path.Combine(output, "errors.log");

            TestDocument(output);
            Console.WriteLine("PASS: editing, search, outlines, annotations, pages");
            TestHistory(output);
            TestProtection(output);
            TestAtomicSave(output);
            TestParallelAccess();
            TestForms(output);
            Console.WriteLine("PASS: undo/redo, encryption, permissions, atomic save, concurrency, AcroForm");
            TestFonts(output);
            Console.WriteLine("PASS: font subsetting, original font reuse, text style");
            TestIncrementalSave(output);
            Console.WriteLine("PASS: signed documents saved incrementally, unchanged save is byte-identical");
            TestRendering(output);
            TestPrintLayout();
            TestExtendedPrintLayout();
            if (args.Contains("--printer-check")) TestPrinterProfiles();
            Console.WriteLine("PASS: region rendering, aspect ratio limits, print placement");
            TestAnnotations();
            TestAnnotationEditing(output);
            TestRecovery(output);
            TestThreeD(output, args);
            Console.WriteLine("PASS: markup annotations, crash recovery");
            TestUi(output, args);
            Console.WriteLine($"PASS: {_assertions} assertions, native PDFium and WPF integration.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _assertions++;
    }

    private static void TestThreeD(string output, string[] args)
    {
        foreach(var compressed in new[]{false,true})
        {
            using var document=PdfDocument.FromBytes(PdfFixture.Create3D(compressed));
            Check(document.GetPages()[0].Has3D,"generated U3D PDF detected");
            var decoded=Task.Run(()=>ThreeDService.LoadAsync(document,0)).GetAwaiter().GetResult();
            Check(decoded.TriangleCount==1 && decoded.Parts[0].Name=="Triangle","independent U3D worker decodes generated triangle");
            Check(!document.IsDirty,"3D extraction is read only");
            document.RotatePage(0,1);
            var rotated=Task.Run(()=>ThreeDService.LoadAsync(document,0)).GetAwaiter().GetResult();
            Check(rotated.Bounds.Width>rotated.Bounds.Height,"3D annotation coordinates follow PDF page rotation");
        }
        // A tiny neutral scene exercises D3D11 without depending on user data or a discrete GPU.
        float[] vertices = [-.45f,-.4f,0, 0,0,1, 1,0,0,1, .45f,-.4f,0, 0,0,1, 0,1,0,1, 0,.4f,0, 0,0,1, 0,0,1,1];
        var scene = new ThreeDScene([new("Triangle", vertices)], "test", new Rect(0,0,1,1), false);
        using (var renderer = new ThreeDRenderer(scene, -1))
        {
            Check(renderer.AdapterId == -1, "explicit software adapter");
            var state = new ThreeDViewState { Pitch = 0, Yaw = 0 };
            var image = renderer.Render(320, 240, state);
            var pixels = new byte[320*240*4]; image.CopyPixels(pixels,320*4,0);
            Check(pixels.Where((p,i) => i%4 == 0).Distinct().Count() > 20, "D3D11 renders interpolated mesh colors");
            state.Style = ThreeDStyle.Wireframe;
            var wire = renderer.Render(320,240,state); var wirePixels = new byte[pixels.Length];wire.CopyPixels(wirePixels,320*4,0);
            Check(!pixels.SequenceEqual(wirePixels), "wireframe changes actual rendering");
            state.Style = ThreeDStyle.Glossy;state.Section=true;state.SectionPosition=-.1f;
            var clipped = renderer.Render(320,240,state);var clippedPixels=new byte[pixels.Length];clipped.CopyPixels(clippedPixels,320*4,0);
            Check(!pixels.SequenceEqual(clippedPixels), "section clipping removes triangles");
        }
        var settingsPath = Path.Combine(output,"3d-settings.json");var settings = new SettingsService(settingsPath);settings.Value.Preferred3DGpu=-1;settings.Save();
        Check(new SettingsService(settingsPath).Value.Preferred3DGpu == -1,"3D GPU preference persists");
        var hardwareAdapters = ThreeDRenderer.GetAdapters();
        if (hardwareAdapters.Count > 0)
        {
            using var remapped = new ThreeDRenderer(scene, long.MaxValue, hardwareAdapters[0].Name);
            Check(remapped.AdapterId == hardwareAdapters[0].Id,"GPU preference survives changed DXGI LUID");
        }
        var badMesh = Path.Combine(output,"bad.pd3d");File.WriteAllBytes(badMesh,"PD3D0001"u8.ToArray());Throws<EndOfStreamException>(() => ThreeDService.ReadMesh(badMesh,"U3D",new(0,0,1,1)),"truncated worker output rejected");
        var samplesIndex = Array.IndexOf(args,"--3d-samples");
        if (samplesIndex >= 0 && samplesIndex + 1 < args.Length)
        {
            var samples = Directory.GetFiles(args[samplesIndex+1]).Where(p => p.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase));
            var index=0;
            foreach (var sample in samples)
            {
                var original=File.ReadAllBytes(sample);using var document=PdfDocument.Open(sample);var pages=document.GetPages();
                Check(pages.Any(p=>p.Has3D),"3D annotations detected");
                var page=pages.First(p=>p.Has3D);var model=Task.Run(()=>ThreeDService.LoadAsync(document,page.Index)).GetAwaiter().GetResult();
                Check(model.TriangleCount>100,"real 3D model decoded");Check(model.Parts.All(p=>p.Vertices.All(float.IsFinite)),"finite model coordinates");
                using var renderer=new ThreeDRenderer(model);var image=renderer.Render(1000,700,new());
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(output,$"3d-sample-{++index}.png")))encoder.Save(file);
                var preview=Task.Run(()=>ThreeDService.PreviewAsync(document,page.Index,600,CancellationToken.None)).GetAwaiter().GetResult();
                Check(preview.PixelWidth==600,"3D preview preserves page width");
                Check(!document.IsDirty && original.SequenceEqual(File.ReadAllBytes(sample)),"3D viewing preserves PDF bytes");
                Console.WriteLine($"3D: {Path.GetFileName(sample)}, {model.Format}, {model.Parts.Count} parts, {model.TriangleCount} triangles, adapter {renderer.AdapterId}");
            }
        }
        Console.WriteLine("PASS: D3D11/WARP, mesh validation, effects, GPU settings, optional real 3D PDFs");
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            _assertions++;
            return;
        }
        throw new InvalidOperationException("FAIL: " + name);
    }

    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    private static bool Contains(byte[] data, string text) => Encoding.Latin1.GetString(data).Contains(text, StringComparison.Ordinal);

    private static void TestDocument(string output)
    {
        var sample = Path.Combine(output, "sample.pdf");
        File.WriteAllBytes(sample, PdfFixture.Create());
        using var doc = PdfDocument.Open(sample);
        Check(doc.PageCount == 2 && !doc.IsDirty, "open two pages, clean state");
        var pages = doc.GetPages();
        Check(pages[1].Width > pages[1].Height, "landscape geometry");
        var outline = doc.GetOutline();
        Check(outline.Count == 2 && outline[0].Children.Count == 1 && outline[1].PageIndex == 1, "nested outlines");
        var matches = doc.Search("hello", false, false, default);
        Check(matches.Count == 3, "case-insensitive document search");
        Check(doc.Search("hello", true, false, default).Count == 0, "case-sensitive search");
        Check(doc.Search("Hell", false, true, default).Count == 0, "whole word search");
        Check(matches.All(m => m.Rectangles.Count > 0 && m.Rectangles.All(r => r.X >= 0 && r.Y >= 0)), "search display rectangles");

        var hit = doc.HitText(0, Center(matches[0].Rectangles[0]));
        Check(hit is not null && hit.Text.Contains("Hello"), "hit text object");
        doc.ReplaceText(hit!, "Привет, PDF. Новый текст.", 13);
        Check(doc.ExtractText(0).Contains("Привет, PDF") && !doc.ExtractText(0).Contains("Hello PDF"), "real replacement with Cyrillic");
        Throws<InvalidOperationException>(() => doc.ReplaceText(hit!, "stale", 12), "reject stale object token");
        Check(doc.ExtractText(0).Contains("Привет"), "rollback preserves text after failure");
        doc.AddText(0, new(.12, .4), "Добавленная строка\nВторая строка", 14);
        Check(doc.ExtractText(0).Contains("Вторая строка"), "multiline Unicode addition");
        doc.AddNote(0, new(.8, .2), "Проверка заметки");
        var search = doc.Search("Привет", false, false, default).Single();
        doc.Highlight(0, search.Rectangles);
        Check(doc.GetComments().Count == 2 && doc.GetComments(0).Count == 2 && doc.GetComments(1).Count == 0, "standard note and highlight annotations");

        var saved = Path.Combine(output, "edited.pdf");
        doc.Save(saved);
        using (var reopen = PdfDocument.Open(saved))
        {
            Check(reopen.GetOutline().Count == 2 && reopen.GetComments().Count == 2, "outlines and annotations survive save");
            Check(reopen.ExtractText(0).Contains("Привет") && !reopen.ExtractText(0).Contains("Hello PDF"), "text survives save without original");
            SaveImage(reopen.Render(0, 794), Path.Combine(output, "edited-page.png"));
        }
        Check(!doc.IsDirty, "save marks clean");

        doc.RotatePage(0, 1);
        Check(doc.GetPages()[0].Width > doc.GetPages()[0].Height, "page rotation updates geometry");
        var rotatedMatch = doc.Search("Привет", false, false, default).Single();
        Check(doc.HitText(0, Center(rotatedMatch.Rectangles[0])) is not null, "hit test on rotated page");
        doc.AddText(0, new(.2, .5), "Поворот", 12);
        Check(doc.ExtractText(0).Contains("Поворот"), "addition to rotated page");
        doc.MovePage(0, 1);
        Check(doc.ExtractText(0).Contains("Second chapter"), "move pages");
        doc.InsertBlankPage(1);
        Check(doc.PageCount == 3, "insert blank page");
        doc.DeletePage(1);
        Check(doc.PageCount == 2, "delete page");
        using var imported = PdfDocument.FromBytes(PdfFixture.Create());
        doc.ImportPages(imported, 1);
        Check(doc.PageCount == 4, "merge PDF pages");
        using var extracted = PdfDocument.FromBytes(doc.ExportPages("2-3"));
        Check(extracted.PageCount == 2 && extracted.ExtractText(0).Contains("PaperDesk"), "extract range to new PDF");
        var note = doc.GetComments().First(c => c.Type == 1);
        doc.DeleteComment(note);
        Check(!doc.GetComments().Any(c => c.Type == 1), "delete annotation");
    }

    private static void TestHistory(string output)
    {
        using var doc = PdfDocument.FromBytes(PdfFixture.Create());
        doc.RotatePage(0, 1);
        Check(doc.IsDirty && doc.CanUndo, "dirty history");
        doc.Undo();
        Check(!doc.IsDirty && doc.CanRedo, "undo to initial clean state");
        doc.Redo();
        Check(doc.IsDirty && doc.GetPages()[0].Width > doc.GetPages()[0].Height, "redo restores modification");
        doc.Save(Path.Combine(output, "history.pdf"));
        doc.Undo();
        Check(doc.IsDirty, "undo away from saved state");
        doc.Redo();
        Check(!doc.IsDirty, "redo to saved state");
        doc.Undo();
        doc.InsertBlankPage(1);
        Check(!doc.CanRedo, "new edit discards redo branch");
        var count = doc.PageCount;
        Throws<ArgumentOutOfRangeException>(() => doc.MovePage(999, 0), "invalid edit fails transaction");
        Check(doc.PageCount == count, "failed edit rollback");
        using var blank = PdfDocument.Create();
        Check(blank.PageCount == 1 && blank.IsDirty, "new unsaved PDF");
        Throws<InvalidOperationException>(() => blank.DeletePage(0), "cannot delete final page");
        Throws<ArgumentException>(() => blank.AddText(0, new(.1, .1), "test", float.NaN), "reject invalid font size");
    }

    private static void TestProtection(string output)
    {
        var bytes = PdfFixture.Create(encrypted: true);
        Throws<PdfPasswordException>(() => PdfDocument.FromBytes(bytes), "password required");
        Throws<PdfPasswordException>(() => PdfDocument.FromBytes(bytes, "wrong"), "wrong password");
        using var doc = PdfDocument.FromBytes(bytes, "reader-secret");
        Check(doc.GetInfo().IsEncrypted && doc.ExtractText(0).Contains("Hello"), "open encrypted PDF");
        doc.AddText(0, new(.1, .4), "Protected edit", 12);
        var saved = Path.Combine(output, "protected.pdf");
        doc.Save(saved);
        Throws<PdfPasswordException>(() => PdfDocument.Open(saved), "save retains encryption");
        using var reopened = PdfDocument.Open(saved, "reader-secret");
        Check(reopened.ExtractText(0).Contains("Protected edit"), "encrypted edits survive save");
        using var restricted = PdfDocument.FromBytes(PdfFixture.Create(true, -64), "reader-secret");
        var info = restricted.GetInfo();
        Check(!info.CanEdit && !info.CanPrint && !info.CanCopy && !info.CanAssemble, "permission flags including legacy security");
        Throws<UnauthorizedAccessException>(() => restricted.AddText(0, new(.1, .1), "blocked", 12), "enforce edit restriction");
        Throws<UnauthorizedAccessException>(() => restricted.ExtractText(0), "enforce copy restriction");
        Throws<UnauthorizedAccessException>(() => restricted.RotatePage(0, 1), "enforce legacy page assembly restriction");
        Throws<UnauthorizedAccessException>(() => PrintService.CreatePaginator(restricted, [0], new Size(816, 1056), new Rect(24, 24, 768, 1008)), "enforce print restriction");
        using var owner = PdfDocument.FromBytes(PdfFixture.Create(true, -64), "owner-secret");
        Check(owner.GetInfo().CanEdit, "owner unlocks permissions");
    }

    private static void TestAtomicSave(string output)
    {
        var path = Path.Combine(output, "atomic.pdf");
        File.WriteAllBytes(path, PdfFixture.Create());
        using var doc = PdfDocument.Open(path);
        doc.AddText(0, new(.1, .5), "saved", 12);
        doc.Save(path);
        using (var again = PdfDocument.Open(path)) Check(again.ExtractText(0).Contains("saved"), "replace original while open");
        var external = PdfFixture.Create();
        File.WriteAllBytes(path, external);
        Throws<IOException>(() => doc.Save(path), "protect external file changes");
        Check(File.ReadAllBytes(path).SequenceEqual(external), "conflict leaves external file intact");
        var blocked = Path.Combine(output, "missing-folder", "file.pdf");
        Throws<DirectoryNotFoundException>(() => doc.Save(blocked), "save failure is reported");
        Check(File.ReadAllBytes(path).SequenceEqual(external), "failed save preserves original");
    }

    private static void TestParallelAccess()
    {
        using var first = PdfDocument.FromBytes(PdfFixture.Create());
        using var second = PdfDocument.FromBytes(PdfFixture.Create());
        Task.WaitAll(Task.Run(() => first.Render(0, 400)), Task.Run(() => second.Search("Hello", false, false, default)), Task.Run(() => first.GetBytes()));
        Check(true, "serialize simultaneous native operations");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => first.Search("Hello", false, false, cancellation.Token), "cancel search");

        // UI state must stay readable while another thread holds the native lock (long render or save).
        first.RotatePage(0, 1);
        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (PdfDocument.Gate)
            {
                locked.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        });
        holder.Start();
        locked.Wait();
        var watch = Stopwatch.StartNew();
        var state = (first.IsDirty, first.CanUndo, first.CanRedo, first.PageCount, first.Revision);
        watch.Stop();
        release.Set();
        holder.Join();
        Check(watch.ElapsedMilliseconds < 200 && state is (true, true, false, 2, > 0), "history and dirty state do not wait for the native lock");
    }

    private static void TestForms(string output)
    {
        var path = Path.Combine(output, "form.pdf");
        File.WriteAllBytes(path, PdfFixture.CreateForm());
        using var doc = PdfDocument.Open(path);
        Check(doc.GetInfo().FormType == 1, "AcroForm detection");
        var point = new Point(.2, .271);
        var field = doc.HitFormField(0, point);
        Check(field is { Name: "Name", Value: "Initial", Type: 6 }, "AcroForm text field discovery");
        doc.FillFormField(field!, "Filled PDF form");
        Check(doc.HitFormField(0, point)?.Value == "Filled PDF form", "fill text form field");
        var checkPoint = new Point(.1, .335);
        var checkbox = doc.HitFormField(0, checkPoint);
        Check(checkbox is { Type: 2, Checked: false }, "checkbox discovery");
        doc.FillFormField(checkbox!, "");
        Check(doc.HitFormField(0, checkPoint)?.Checked == true, "toggle checkbox");
        doc.Save(path);
        using var reopened = PdfDocument.Open(path);
        Check(reopened.HitFormField(0, point)?.Value == "Filled PDF form" && reopened.HitFormField(0, checkPoint)?.Checked == true, "form values persist after save");
        SaveImage(reopened.Render(0, 794), Path.Combine(output, "form-page.png"));
        doc.Undo();
        Check(doc.HitFormField(0, checkPoint)?.Checked == false, "form undo");
    }

    private static void TestFonts(string output)
    {
        var segoe = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"));
        var subsetter = new TrueTypeSubsetter(segoe);
        var subset = subsetter.Subset("Привет AÄ".EnumerateRunes().Select(r => r.Value));
        Check(subset.Length < 20_000 && subset.Length < segoe.Length / 20, "subset is a small fraction of the font");
        Check(new TrueTypeSubsetter(subset).Supports('П') && !new TrueTypeSubsetter(subset).Supports('Z'), "subset keeps exactly the requested characters");

        // Repeated editing sessions must not embed the whole font each time.
        var path = Path.Combine(output, "fonts.pdf");
        File.WriteAllBytes(path, PdfFixture.Create());
        var sizes = new List<long> { new FileInfo(path).Length };
        for (var i = 0; i < 3; i++)
        {
            using var session = PdfDocument.Open(path);
            session.AddText(0, new(.1, .3 + i * .05), "Слово " + i, 12);
            session.Save(path);
            sizes.Add(new FileInfo(path).Length);
        }
        using (var session = PdfDocument.Open(path))
        {
            session.AddText(0, new(.5, .5), "a", 12);
            session.Undo();
            session.AddText(0, new(.5, .6), "Ещё", 12);
            session.Save(path);
            sizes.Add(new FileInfo(path).Length);
            Check(session.ExtractText(0).Contains("Слово 2") && session.ExtractText(0).Contains("Ещё"), "subset text extracts as Unicode");
        }
        Check(sizes.Zip(sizes.Skip(1)).All(p => p.Second - p.First < 20_000), "file grows by kilobytes per edit session: " + string.Join(", ", sizes));

        // ASCII replacement keeps the original Helvetica; Cyrillic needs an embedded font.
        using var doc = PdfDocument.FromBytes(PdfFixture.Create());
        var match = doc.Search("Edit text", false, false, default).Single();
        var hit = doc.HitText(0, Center(match.Rectangles[0]))!;
        doc.ReplaceText(hit, "Edited in Helvetica", 12);
        var bytes = doc.GetBytes();
        Check(doc.ExtractText(0).Contains("Edited in Helvetica") && !Contains(bytes, "SegoeUI") && !Contains(bytes, "FontFile2"), "original font is reused when it has the glyphs");
        match = doc.Search("Edited in Helvetica", false, false, default).Single();
        hit = doc.HitText(0, Center(match.Rectangles[0]))!;
        doc.ReplaceText(hit, "Кириллица", 12);
        Check(doc.ExtractText(0).Contains("Кириллица") && Contains(doc.GetBytes(), "+SegoeUI"), "missing glyphs fall back to an embedded subset");
        Throws<ArgumentException>(() => doc.AddText(0, new(.1, .9), "漢字", 12), "characters missing from Segoe UI are reported");
        Check(!doc.ExtractText(0).Contains("漢"), "failed addition is rolled back");

        // OCR layers stay invisible after correction.
        using var ocr = PdfDocument.FromBytes(PdfFixture.CreateOcrLayer());
        var blank = ocr.Render(0, 400);
        var hidden = ocr.HitText(0, Center(ocr.Search("Hidden", false, false, default).Single().Rectangles[0]))!;
        ocr.ReplaceText(hidden, "Corrected OCR text", 24);
        Check(ocr.ExtractText(0).Contains("Corrected OCR text") && SameImage(blank, ocr.Render(0, 400)), "invisible text render mode is preserved");
    }

    private static void TestIncrementalSave(string output)
    {
        var original = PdfFixture.CreateSigned();
        var path = Path.Combine(output, "signed.pdf");
        File.WriteAllBytes(path, original);
        using (var doc = PdfDocument.Open(path))
        {
            Check(doc.GetInfo().Signatures == 1 && doc.SavesIncrementally, "signature detected");
            doc.AddNote(0, new(.5, .5), "Approved");
            doc.Undo();
            doc.Redo();
            var copy = Path.Combine(output, "signed-annotated.pdf");
            doc.Save(copy);
            var saved = File.ReadAllBytes(copy);
            Check(saved.Length > original.Length && saved.AsSpan(0, original.Length).SequenceEqual(original), "signed revision is kept byte-for-byte");
            using var reopened = PdfDocument.Open(copy);
            Check(reopened.GetComments().Count == 1 && reopened.GetInfo().Signatures == 1, "incremental update is readable");
        }

        var plain = Path.Combine(output, "unchanged.pdf");
        File.WriteAllBytes(plain, PdfFixture.Create());
        using (var doc = PdfDocument.Open(plain))
        {
            doc.Render(0, 300);
            var copy = Path.Combine(output, "unchanged-copy.pdf");
            doc.Save(copy);
            Check(File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(plain)) && !doc.SavesIncrementally, "saving without changes keeps the file byte-identical");
        }
    }

    private static void TestRendering(string output)
    {
        using var doc = PdfDocument.FromBytes(PdfFixture.Create());
        var full = doc.Render(0, 1200);
        var region = new Int32Rect(100, 150, 400, 300);
        var part = doc.RenderRegion(0, full.PixelWidth, full.PixelHeight, region);
        Check(part.PixelWidth == 400 && part.PixelHeight == 300 && SameImage(new CroppedBitmap(full, region), part), "region rendering matches the full page");
        var clipped = doc.RenderRegion(0, full.PixelWidth, full.PixelHeight, new Int32Rect(full.PixelWidth - 50, full.PixelHeight - 50, 400, 400));
        Check(clipped.PixelWidth == 50 && clipped.PixelHeight == 50, "regions are clipped to the page");
        var deep = doc.RenderRegion(0, 20000, 28297, new Int32Rect(1000, 1500, 800, 600));
        Check(deep.PixelWidth == 800 && deep.PixelHeight == 600, "sharp region at very high zoom without a full bitmap");
        SaveImage(deep, Path.Combine(output, "deep-zoom-region.png"));

        using var tall = PdfDocument.FromBytes(PdfFixture.CreateTall());
        var image = tall.Render(0, 3000);
        var ratio = (double)image.PixelHeight / image.PixelWidth;
        Check(Math.Abs(ratio - 30) < .5 && image.PixelHeight <= PdfDocument.MaxRenderSide && (long)image.PixelWidth * image.PixelHeight <= PdfDocument.MaxRenderPixels,
            "tall pages keep their aspect ratio within render limits");
    }

    private static void TestPrintLayout()
    {
        var portraitArea = new Rect(24, 24, 768, 1008);
        var landscape = new PdfPageInfo(1, 842, 595, "2");
        var fit = PrintService.Place(landscape, portraitArea, PrintScaling.Fit);
        Check(fit.Rotated && fit.Height <= portraitArea.Width + .01 && fit.Width <= portraitArea.Height + .01 && Math.Abs(fit.Width - portraitArea.Height) < 1,
            "landscape page is rotated onto portrait paper and fills it");
        var small = new PdfPageInfo(0, 300, 400, "1");
        Check(Math.Abs(PrintService.Place(small, portraitArea, PrintScaling.ActualSize).Width - 400) < .01, "actual size keeps 100% scale");
        Check(Math.Abs(PrintService.Place(small, portraitArea, PrintScaling.ShrinkOversized).Width - 400) < .01, "small pages are not enlarged when shrinking only");
        Check(PrintService.Place(small, portraitArea, PrintScaling.Fit).Width > 700, "fit enlarges small pages");

        using var doc = PdfDocument.FromBytes(PdfFixture.Create());
        var paginator = PrintService.CreatePaginator(doc, [0, 1], new Size(816, 1056), portraitArea);
        var bounds = ((DrawingVisual)paginator.GetPage(1).Visual).ContentBounds;
        Check(paginator.PageCount == 2 && paginator.GetPage(0).Size == new Size(816, 1056), "printer pagination with imageable area");
        Check(bounds.Height > bounds.Width && Rect.Inflate(portraitArea, 1, 1).Contains(bounds), "landscape page printed rotated inside the printable area");
    }

    private static void TestExtendedPrintLayout()
    {
        var mm = PrintLayout.Mm;
        var paper = new Size(210 * mm, 297 * mm);
        var area = new Rect(5 * mm, 5 * mm, 200 * mm, 287 * mm);
        var a1 = new[] { new PdfPageInfo(0, 594 * 72 / 25.4, 841 * 72 / 25.4, "1") };
        var poster = new PrintOptions { Mode = PrintMode.Poster, GlueMarginsMm = new(0), OverlapMm = 0, Labels = false };
        var plan = PrintLayout.Create(a1, [0], paper, area, poster);
        Check(plan.Sheets.Count == 9 && plan.Sheets[0].Label.Contains("1-1") && plan.Sheets[^1].Label.Contains("3-3"), "A1 poster tiles 3 x 3 without margins or overlap");
        Check(Math.Abs(plan.Sheets[0].Slots[0].Size.Width / mm - 594) < .001, "poster preserves physical size at 100 percent");
        var glue = PrintLayout.Create(a1, [0], paper, area, poster with { GlueMarginsMm = new(8), OverlapMm = 5, Labels = true });
        Check(glue.Sheets.Count == 16 && Math.Abs(glue.ContentArea.Left - area.Left - 8 * mm) < .001 && Math.Abs(glue.ContentArea.Width - area.Width + 16 * mm) < .001, "glue margins reserve white strips and change tile count");
        var first = glue.Sheets[0].Slots[0];
        var right = glue.Sheets[1].Slots[0];
        Check(Math.Abs(first.Center.X - right.Center.X - (glue.ContentArea.Width - 5 * mm)) < .001, "adjacent tiles duplicate exactly the requested overlap");
        var down = glue.Sheets[4].Slots[0];
        Check(Math.Abs(first.Center.Y - down.Center.Y - (glue.ContentArea.Height - 5 * mm)) < .001, "vertical overlap uses the same physical units");
        var asymmetric = PrintLayout.Create(a1, [0], paper, area, poster with { GlueMarginsMm = new(3, 7, 11, 15) });
        Check(Math.Abs(asymmetric.ContentArea.Top - area.Top - 7 * mm) < .001 && Math.Abs(asymmetric.ContentArea.Width - area.Width + 14 * mm) < .001, "four glue margins are independent");
        Throws<ArgumentException>(() => PrintLayout.Create(a1, [0], paper, area, poster with { OverlapMm = 100, GlueMarginsMm = new(70) }), "reject overlap wider than tile");
        Throws<ArgumentException>(() => PrintLayout.Create(a1, [0], paper, area, poster with { ScalePercent = double.NaN }), "reject nonfinite print scale");
        Throws<ArgumentException>(() => PrintLayout.Create([new(0, 1e7, 1e7, "1")], [0], paper, area, poster), "limit enormous print jobs before allocating tiles");
        Check(PrintLayout.SelectPages("1, 3 - 5, 3", 6).SequenceEqual(new[] { 0, 2, 3, 4 }), "mixed ranges preserve order and remove duplicates");
        Check(PrintLayout.SelectPages("1-6", 6, PrintPageFilter.Even, true).SequenceEqual(new[] { 5, 3, 1 }), "even pages in reverse order");
        Throws<ArgumentException>(() => PrintLayout.SelectPages("0, 9", 6), "reject pages outside document");
        Throws<ArgumentException>(() => PrintLayout.SelectPages("1,", 6), "reject incomplete ranges");
        Throws<ArgumentException>(() => PrintLayout.SelectPages("1", 6, PrintPageFilter.Even), "reject an empty filtered selection");

        var info = Enumerable.Range(0, 5).Select(i => new PdfPageInfo(i, 300, 400, (i + 1).ToString())).ToArray();
        var custom = PrintLayout.Create(info, [0], paper, area, new PrintOptions { Scaling = PrintScaling.Custom, ScalePercent = 150, AutoRotate = false });
        Check(Math.Abs(custom.Sheets[0].Slots[0].Size.Width - 600) < .001, "custom print scale changes physical dimensions");
        var multi = PrintLayout.Create(info, [0, 1, 2, 3, 4], paper, area, new PrintOptions { Mode = PrintMode.Multiple });
        Check(multi.Sheets.Count == 2 && multi.Sheets[0].Slots.Count == 4 && multi.Sheets[1].Slots.Count == 1, "n-up groups pages including incomplete final sheet");
        Check(multi.Sheets[0].Slots[1].Center.X > multi.Sheets[0].Slots[0].Center.X && multi.Sheets[0].Slots[2].Center.Y > multi.Sheets[0].Slots[0].Center.Y, "horizontal page ordering");
        var vertical = PrintLayout.Create(info, [0, 1, 2, 3], paper, area, new PrintOptions { Mode = PrintMode.Multiple, PageOrder = PrintPageOrder.Vertical });
        Check(vertical.Sheets[0].Slots[1].Center.Y > vertical.Sheets[0].Slots[0].Center.Y && vertical.Sheets[0].Slots[2].Center.X > vertical.Sheets[0].Slots[0].Center.X, "vertical page ordering");
        var booklet = PrintLayout.Create(info, [0, 1, 2, 3, 4], paper, area, new PrintOptions { Mode = PrintMode.Booklet });
        Check(booklet.Sheets.Count == 4 && booklet.Sheets[0].Slots.Single().PageIndex == 0 && booklet.Sheets[0].Slots.Single().Center.X > area.X + area.Width / 2, "booklet pads to a multiple of four with blank first left slot");
        Check(booklet.Sheets[2].Slots.Single().PageIndex == 2 && booklet.Sheets[3].Slots.Select(s => s.PageIndex).SequenceEqual(new[] { 3, 4 }), "booklet imposition pairs inner pages correctly");
        var backs = PrintLayout.Create(info, [0, 1, 2, 3, 4], paper, area, new PrintOptions { Mode = PrintMode.Booklet, BookletSide = BookletSide.Back, RightBinding = true });
        Check(backs.Sheets.Count == 2 && backs.Sheets[^1].Slots.Select(s => s.PageIndex).SequenceEqual(new[] { 4, 3 }), "manual booklet backs and right binding");

        using var doc = PdfDocument.FromBytes(PdfFixture.CreatePoster());
        var actual = PrintLayout.Create(doc.GetPages(), [0], paper, area, new PrintOptions { Mode = PrintMode.Poster });
        Check(actual.Sheets.Count == 9 && actual.Sheets[^1] is { Row: 3, Column: 3 }, "real poster PDF has the expected 3 x 3 coordinates");
        var paginator = PrintService.CreatePaginator(doc, actual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(paper.Width), (int)Math.Ceiling(paper.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(paginator.GetPage(0).Visual);
        var pixels = Pixels(bitmap);
        var center = ((int)(actual.ContentArea.Top + 180) * bitmap.PixelWidth + (int)(actual.ContentArea.Left + 80)) * 4;
        Check(pixels[center + 2] > pixels[center + 1] + 60, "poster first tile contains the correct red PDF region");
        var margin = ((int)(actual.ContentArea.Top + 100) * bitmap.PixelWidth + (int)(actual.ImageableArea.Left + 4)) * 4;
        Check(pixels[margin] > 245 && pixels[margin + 1] > 245 && pixels[margin + 2] > 245, "glue strip remains white on the printed sheet");
        var lastBitmap = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        lastBitmap.Render(paginator.GetPage(8).Visual);
        Check(!SameImage(bitmap, lastBitmap), "last tile renders a different PDF region");
        Check(!doc.IsDirty, "printing layout never edits the original PDF");
    }

    private static void TestPrinterProfiles()
    {
        var printers = PrinterSettingsService.List();
        Console.WriteLine($"PRINTERS: {printers.Names.Length} queues, checking capabilities without submitting jobs");
        foreach (var name in printers.Names)
        {
            var profile = PrinterSettingsService.Load(name);
            Check(profile.Papers.Count > 0, "printer exposes at least one paper choice");
            foreach (var orientation in new[] { System.Printing.PageOrientation.Portrait, System.Printing.PageOrientation.Landscape })
            {
                var ticket = profile.Ticket.Clone();
                ticket.PageMediaSize = profile.Papers.FirstOrDefault(p => p.Media.PageMediaSizeName == System.Printing.PageMediaSizeName.ISOA4)?.Media ?? profile.Papers[0].Media;
                ticket.PageOrientation = orientation;
                ticket.PagesPerSheet = 1;
                ticket.PageScalingFactor = 100;
                var geometry = PrinterSettingsService.Resolve(name, ticket);
                Check(geometry.Area.Width > 0 && geometry.Area.Height > 0 && new Rect(geometry.Paper).Contains(geometry.Area), "validated driver geometry is inside physical paper");
                Console.WriteLine($"PRINTER: {name}, {orientation}, {geometry.Paper.Width / PrintLayout.Mm:0.#} x {geometry.Paper.Height / PrintLayout.Mm:0.#} mm");
            }
        }
    }

    private static void TestAnnotations()
    {
        using var doc = PdfDocument.FromBytes(PdfFixture.CreateMarkup());
        var comments = doc.GetComments();
        Check(comments.Count == 1 && comments[0] is { Kind: "Прямоугольник", Text: "Review box", Author: "Reviewer" }, "all markup types are listed, popups and links are not");
        doc.DeleteComment(comments[0]);
        Check(doc.GetComments().Count == 0 && doc.HitLink(0, new Point(100 / 595.0, 1 - 65 / 842.0))?.Uri == "https://example.com/", "deleting markup removes its popup and keeps other annotations");
        Throws<InvalidOperationException>(() => doc.DeleteComment(comments[0]), "stale annotation is rejected");
    }

    private static readonly Color Red = Color.FromRgb(229, 57, 53);
    private static readonly Color Blue = Color.FromRgb(30, 136, 229);
    private static readonly Color Green = Color.FromRgb(67, 160, 71);

    /// <summary>Colour at a normalized point of a page rendered 595 pixels wide.</summary>
    private static Color PixelAt(PdfDocument doc, int page, Point point)
    {
        var image = new FormatConvertedBitmap(doc.Render(page, 595), PixelFormats.Bgra32, null, 0);
        var x = Math.Clamp((int)(point.X * image.PixelWidth), 0, image.PixelWidth - 1);
        var y = Math.Clamp((int)(point.Y * image.PixelHeight), 0, image.PixelHeight - 1);
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 60;

    private static bool Near(Rect a, Rect b) => Math.Abs(a.X - b.X) < .004 && Math.Abs(a.Y - b.Y) < .004 && Math.Abs(a.Width - b.Width) < .008 && Math.Abs(a.Height - b.Height) < .008;

    private static Point LeftEdge(Rect bounds) => new(bounds.Left + 1.0 / 595, bounds.Top + bounds.Height / 2);

    private static void TestAnnotationEditing(string output)
    {
        using var doc = PdfDocument.FromBytes(PdfFixture.Create());
        var text = doc.Search("Hello reader", false, false, default).Single().Rectangles;
        doc.AddTextMarkup(0, text, AnnotationType.Underline, Green);
        doc.AddTextMarkup(0, text, AnnotationType.StrikeOut, Red);
        doc.AddShape(0, new Rect(.2, .4, .3, .15), AnnotationType.Square, Red, 3);
        doc.AddShape(0, new Rect(.55, .4, .25, .15), AnnotationType.Circle, Blue, 2);
        doc.AddInk(0, [new(.1, .8), new(.2, .85), new(.3, .8)], Blue, 3);
        doc.AddNote(0, new(.85, .9), "Проверить");
        var comments = doc.GetComments(0);
        Check(comments.Select(c => c.Kind).SequenceEqual(["Подчёркивание", "Зачёркивание", "Прямоугольник", "Овал", "Рисунок", "Заметка"]), "all annotation tools create standard PDF annotations");
        var square = comments.Single(c => c.Type == AnnotationType.Square);
        var circle = comments.Single(c => c.Type == AnnotationType.Circle);
        var ink = comments.Single(c => c.Type == AnnotationType.Ink);
        var underline = comments.Single(c => c.Type == AnnotationType.Underline);
        Check(Near(square.Bounds, new Rect(.2, .4, .3, .15)) && square.Color == Red && circle.Color == Blue, "annotation geometry and colour are read back");
        Check(Near(PixelAt(doc, 0, LeftEdge(square.Bounds)), Red) && Near(PixelAt(doc, 0, new Point(.2, .85)), Blue), "shapes and ink are drawn");
        Check(square.CanMove && square.CanResize && ink.CanMove && !ink.CanResize && !underline.CanMove && underline.CanRecolor, "editing capabilities by type");

        doc.MoveAnnotation(square, new Vector(.1, .05));
        var moved = doc.GetComments(0).Single(c => c.Type == AnnotationType.Square);
        Check(Near(moved.Bounds, new Rect(.3, .45, .3, .15)) && !Near(PixelAt(doc, 0, LeftEdge(square.Bounds)), Red) && Near(PixelAt(doc, 0, LeftEdge(moved.Bounds)), Red),
            "moving a shape moves its rectangle and its picture");
        Throws<InvalidOperationException>(() => doc.SetAnnotationColor(square, Blue), "an outdated annotation is rejected");
        doc.MoveAnnotation(ink, new Vector(0, -.1));
        var movedInk = doc.GetComments(0).Single(c => c.Type == AnnotationType.Ink);
        Check(Near(movedInk.Bounds, new Rect(ink.Bounds.X, ink.Bounds.Y - .1, ink.Bounds.Width, ink.Bounds.Height)) && Near(PixelAt(doc, 0, new Point(.2, .75)), Blue),
            "moving a drawing moves its strokes");
        Throws<NotSupportedException>(() => doc.MoveAnnotation(underline, new Vector(.1, 0)), "text markup stays attached to its text");

        doc.ResizeAnnotation(circle, new Rect(.5, .25, .4, .1));
        Check(Near(doc.GetComments(0).Single(c => c.Type == AnnotationType.Circle).Bounds, new Rect(.5, .25, .4, .1)), "resize a shape");
        doc.SetAnnotationColor(moved, Green);
        var green = doc.GetComments(0).Single(c => c.Type == AnnotationType.Square);
        Check(green.Color == Green && Near(PixelAt(doc, 0, LeftEdge(green.Bounds)), Green), "recolour regenerates the appearance");
        var note = doc.GetComments(0).Single(c => c.Type == AnnotationType.Note);
        Check(note.CanMove && !note.CanRecolor, "notes are movable, their icon colour is fixed");
        doc.MoveAnnotation(note, new Vector(-.1, 0));
        doc.SetAnnotationText(doc.GetComments(0).Single(c => c.Type == AnnotationType.Ink), "Подпись руководителя");
        Check(doc.GetComments(0).Single(c => c.Type == AnnotationType.Ink).Text == "Подпись руководителя", "edit annotation comment");

        doc.Undo();
        doc.Undo();
        doc.Undo();
        Check(Near(doc.GetComments(0).Single(c => c.Type == AnnotationType.Square).Bounds, moved.Bounds) && doc.GetComments(0).Single(c => c.Type == AnnotationType.Square).Color == Red,
            "annotation edits are undone step by step");
        doc.Redo();
        doc.Redo();
        doc.Redo();

        var path = Path.Combine(output, "annotations.pdf");
        doc.Save(path);
        using var reopened = PdfDocument.Open(path);
        var saved = reopened.GetComments(0);
        Check(saved.Count == 6 && saved.Single(c => c.Type == AnnotationType.Square).Color == Green && Near(PixelAt(reopened, 0, LeftEdge(green.Bounds)), Green),
            "edited annotations and their appearance survive saving");
        SaveImage(reopened.Render(0, 794), Path.Combine(output, "annotations-page.png"));
        var last = saved[^1];
        reopened.DeleteComment(last);
        Check(reopened.GetComments(0).Count == 5, "delete edited annotation");
    }

    private static void TestRecovery(string output)
    {
        var directory = Path.Combine(output, "recovery-unit");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        var recovery = new RecoveryService(directory);
        var path = Path.Combine(output, "recover.pdf");
        File.WriteAllBytes(path, PdfFixture.Create());
        using (var doc = PdfDocument.Open(path))
        {
            doc.AddText(0, new(.1, .6), "Unsaved work", 12);
            recovery.Write("crashed", doc.GetRecoverySnapshot());
        }
        Check(recovery.FindOrphaned().Count == 0, "copies of a running process are not offered");
        // Simulate a process that has ended.
        var info = Path.Combine(directory, "crashed.json");
        var entry = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllBytes(info))!;
        File.WriteAllBytes(info, JsonSerializer.SerializeToUtf8Bytes(entry with { ProcessStartTicks = 1 }));
        var orphan = recovery.FindOrphaned().Single();
        using (var restored = PdfDocument.Restore(recovery.Read(orphan), null, orphan.OriginalPath, RecoveryService.ParseHash(orphan.DiskHash)))
        {
            Check(restored.IsDirty && restored.FilePath == path && restored.ExtractText(0).Contains("Unsaved work"), "restored copy is a modified document bound to its file");
            restored.Save(path);
            using var reopened = PdfDocument.Open(path);
            Check(reopened.ExtractText(0).Contains("Unsaved work"), "restored work saves over the original");
        }
        File.WriteAllBytes(path, PdfFixture.CreateForm());
        using (var stale = PdfDocument.Restore(recovery.Read(orphan), null, orphan.OriginalPath, RecoveryService.ParseHash(orphan.DiskHash)))
            Throws<IOException>(() => stale.Save(path), "restore does not overwrite a file changed meanwhile");
        recovery.Delete(orphan.Id);
        Check(recovery.FindOrphaned().Count == 0 && !File.Exists(Path.Combine(directory, "crashed.pdf")), "recovery copy removed");
    }

    private static void TestScrollBars()
    {
        foreach (var orientation in new[] { System.Windows.Controls.Orientation.Vertical, System.Windows.Controls.Orientation.Horizontal })
        {
            var vertical = orientation == System.Windows.Controls.Orientation.Vertical;
            var bar = new System.Windows.Controls.Primitives.ScrollBar
            {
                Orientation = orientation, Minimum = 0, Maximum = 1_000_000, ViewportSize = 500,
                Width = vertical ? 16 : 800, Height = vertical ? 800 : 16,
                Style = (Style)Application.Current.FindResource(typeof(System.Windows.Controls.Primitives.ScrollBar))
            };
            var host = new Window
            {
                Content = bar, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0
            };
            host.Show();
            bar.ApplyTemplate();
            var track = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
            foreach (var length in new[] { 800d, 160d, 400d })
            {
                var size = vertical ? new Size(16, length) : new Size(length, 16);
                bar.Width = size.Width;
                bar.Height = size.Height;
                foreach (var value in new[] { 0d, 500_000d, 1_000_000d })
                {
                    bar.Value = value;
                    bar.Measure(size);
                    bar.Arrange(new Rect(size));
                    bar.UpdateLayout();
                    var thumb = track.Thumb;
                    var thumbLength = vertical ? thumb.ActualHeight : thumb.ActualWidth;
                    Check(track.IsVisible && thumbLength >= 40 && thumbLength < length,
                        $"{orientation} thumb remains usable in a long document at track length {length} (visible={track.IsVisible}, thumb={thumbLength}, maximum={track.Maximum}, viewport={track.ViewportSize})");
                    var origin = thumb.TranslatePoint(new Point(), track);
                    var offset = vertical ? origin.Y : origin.X;
                    Check(Math.Abs(offset - (length - thumbLength) * value / bar.Maximum) < .01,
                        $"{orientation} thumb reaches start, middle and end after resizing");
                    var travel = length - thumbLength;
                    Check(Math.Abs(track.ValueFromDistance(vertical ? 0 : travel, vertical ? travel : 0) - bar.Maximum) < .01,
                        $"{orientation} dragging spans the full scroll range");
                    Check(thumb.InputHitTest(new Point(1, 1)) is not null,
                        $"{orientation} thumb margin can be grabbed");
                }
            }
            bar.Maximum = 1500;
            bar.Value = 0;
            bar.Measure(new Size(bar.Width, bar.Height));
            bar.Arrange(new Rect(0, 0, bar.Width, bar.Height));
            bar.UpdateLayout();
            Check(Math.Abs((vertical ? track.Thumb.ActualHeight : track.Thumb.ActualWidth) - 100) < .01,
                $"{orientation} thumb still reflects viewport proportion for shorter documents");
            host.Close();
        }
    }

    private static void TestUi(string output, string[] args)
    {
        Console.WriteLine("WPF: initialize application resources");
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // A plain Application keeps the product's OnStartup from opening a second window with test arguments.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PaperDesk;component/Views/Theme.xaml", UriKind.Relative) });
        TestScrollBars();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        application.DispatcherUnhandledException += (_, e) =>
        {
            _dispatcherFailure = e.Exception;
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
            Dispatcher.ExitAllFrames();
        };
        var recoveryDirectory = Path.Combine(output, "recovery-ui");
        if (Directory.Exists(recoveryDirectory)) Directory.Delete(recoveryDirectory, true);
        var window = new MainWindow(new SettingsService(Path.Combine(output, "settings.json")), new RecoveryService(recoveryDirectory))
        {
            ShowActivated = false,
            ShowInTaskbar = false
        };
        var workspace = window.Workspace;
        Console.WriteLine("WPF: create and render window");
        window.Show();
        Console.WriteLine("WPF: window shown, pumping layout");
        Pump(200);
        var graphicsDialog = new PDF_Reader.Views.GraphicsSettingsWindow(new SettingsService(Path.Combine(output, "3d-settings.json"))) { Owner = window, ShowActivated = false };
        graphicsDialog.Show(); Pump(800); SaveWindow(graphicsDialog, Path.Combine(output, "3d-graphics-settings.png"));
        Check(FindVisuals<System.Windows.Controls.ComboBox>(graphicsDialog).Any(c => c.Items.Count >= 2), "3D GPU settings exposes auto and WARP"); graphicsDialog.Close();
        var threeDArgument = Array.IndexOf(args,"--3d-samples");
        if (threeDArgument >= 0 && threeDArgument+1 < args.Length)
        {
            var sample = Directory.GetFiles(args[threeDArgument+1]).First(p => p.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase));
            using var sampleDocument = PdfDocument.Open(sample);var modelPage = sampleDocument.GetPages().First(p=>p.Has3D);
            var modelViewer = new PDF_Reader.Views.ThreeDWindow(new PageItem(sampleDocument, modelPage, 1), new SettingsService(Path.Combine(output,"3d-ui-settings.json"))) { Owner=window,ShowActivated=false };
            modelViewer.Show();
            var deadline=DateTime.UtcNow.AddSeconds(30);
            while (!FindVisuals<System.Windows.Controls.Image>(modelViewer).Any(i=>i.Source is not null) && DateTime.UtcNow<deadline) Pump(50);
            Check(FindVisuals<System.Windows.Controls.Image>(modelViewer).Any(i=>i.Source is not null),"interactive 3D window renders model");
            Check(FindVisuals<System.Windows.Controls.Image>(modelViewer).Any(i=>i.Source is BitmapSource b && b.PixelWidth>300),"3D window renders at viewport resolution");
            SaveWindow(modelViewer,Path.Combine(output,"3d-modelViewer.png"));
            var modes=FindVisuals<System.Windows.Controls.ComboBox>(modelViewer).First();modes.SelectedIndex=4;Pump(250);SaveWindow(modelViewer,Path.Combine(output,"3d-wireframe.png"));
            Check(!sampleDocument.IsDirty,"3D controls do not edit PDF");modelViewer.Close();Pump(100);
            var printPlan=PrintLayout.Create(sampleDocument.GetPages(),[modelPage.Index],new Size(794,1123),new Rect(20,20,754,1083),new PrintOptions());
            var printTask=PrintService.RenderPreviewAsync(sampleDocument,printPlan,0,"3D sample",CancellationToken.None);UntilComplete(printTask);
            var printed=printTask.GetAwaiter().GetResult();SaveImage(printed,Path.Combine(output,"3d-print-preview.png"));
            Check(Pixels(printed).Distinct().Count()>20,"3D print preview contains rendered model");
        }
        Console.WriteLine("WPF: layout ready");
        TestThemePanels(window, output);
        SaveWindow(window, Path.Combine(output, "welcome.png"));
        window.Themes.SetTheme(AppTheme.Dark);
        Pump(100);
        SaveWindow(window, Path.Combine(output, "welcome-dark.png"));
        Check(new SettingsService(Path.Combine(output, "settings.json")).Value.Theme == AppTheme.Dark, "theme selection is persisted");
        var textDialog = new PDF_Reader.Views.TextInputWindow("Добавить текст", "Текст и размер шрифта", "Пример текста", multiline: true, fontSize: 12) { Owner = window, ShowActivated = false };
        textDialog.Show();
        Pump(100);
        SaveWindow(textDialog, Path.Combine(output, "text-dialog-dark.png"));
        Check(((SolidColorBrush)textDialog.Background).Color.R < 128, "text dialog follows dark theme");
        window.Themes.SetTheme(AppTheme.Light);
        Pump(100);
        Check(((SolidColorBrush)textDialog.Background).Color.R > 200, "open dialog follows live theme switch");
        textDialog.Close();
        TestPrintWindow(window, output);
        var messageDialog = new PDF_Reader.Views.MessageWindow("Несохранённые изменения", "Сохранить изменения в документе?", MessageBoxButton.YesNoCancel) { Owner = window, ShowActivated = false };
        window.Themes.SetTheme(AppTheme.Dark);
        messageDialog.Show();
        Pump(100);
        SaveWindow(messageDialog, Path.Combine(output, "message-dialog-dark.png"));
        messageDialog.Close();

        UntilComplete(workspace.OpenPathAsync(Path.Combine(output, "sample.pdf")));
        Pump(900);
        Check(workspace.HasDocument && workspace.Pages.Count == 2, "WPF document workspace");
        workspace.FitWidthCommand.Execute(null);
        Pump(900);
        SaveWindow(window, Path.Combine(output, "workspace.png"));
        var darkPage = workspace.Document!.Render(0, 400);
        window.Themes.SetTheme(AppTheme.Light);
        Pump(100);
        SaveWindow(window, Path.Combine(output, "workspace-light.png"));
        Check(SameImage(darkPage, workspace.Document.Render(0, 400)), "PDF colours stay identical across themes");
        Check(!workspace.Document.IsDirty, "theme switch does not modify PDF");
        window.Themes.SetTheme(AppTheme.System);
        Check(window.Themes.Theme == AppTheme.System, "system theme can be restored");
        window.Themes.SetTheme(AppTheme.Dark);

        workspace.Query = "Hello";
        Pump(900);
        Check(workspace.Matches.Count == 3 && workspace.GetPageMatches(0).Count == 2 && workspace.GetPageMatches(1).Count == 1, "WPF asynchronous search with per-page highlights");
        workspace.NavigationTab = 2;
        Pump(300);
        SaveWindow(window, Path.Combine(output, "search.png"));
        workspace.CurrentPage = 2;
        Pump(250);
        Check(workspace.CurrentPage == 2, "page navigation remains synchronized");

        // A page-local edit keeps page views and refreshes only that page's annotations.
        var firstPage = workspace.Pages[0];
        var version = firstPage.Version;
        UntilComplete(workspace.ChangeAsync(d => d.AddNote(0, new(.7, .1), "UI note"), 0));
        Check(ReferenceEquals(workspace.Pages[0], firstPage) && firstPage.Version > version && workspace.Comments.Count == 1, "page-local edit reuses page items");
        UntilComplete(workspace.AutosaveAsync());
        Check(Directory.GetFiles(recoveryDirectory, "*.pdf").Length == 1, "autosave writes a recovery copy of the modified document");
        UntilComplete(workspace.ChangeAsync(d => d.Undo()));
        Check(!workspace.Document!.IsDirty && workspace.Comments.Count == 0, "undo restores the clean document");
        UntilComplete(workspace.AutosaveAsync());
        Check(Directory.GetFiles(recoveryDirectory, "*.pdf").Length == 0, "recovery copy removed once the document is clean");

        // Annotation workflow through the view model: draw, select, move, recolour, delete.
        workspace.CurrentPage = 1;
        Pump(300);
        workspace.Tool = DocumentTool.Rectangle;
        workspace.StrokeWidth = 4;
        UntilComplete(workspace.AddShapeAsync(0, new Rect(.15, .3, .3, .12)));
        var drawn = workspace.Comments.Single(c => c.Type == AnnotationType.Square);
        Check(drawn.Color == workspace.CurrentColor, "new shape uses the tool colour");
        workspace.Tool = DocumentTool.Select;
        Check(workspace.HitAnnotation(0, new Point(.3, .36), new Size(.005, .005), movableOnly: true) == drawn, "click finds the annotation under the pointer");
        workspace.SelectedAnnotation = drawn;
        UntilComplete(workspace.MoveAnnotationAsync(drawn, new Vector(.2, .1)));
        Check(workspace.SelectedAnnotation is { Type: AnnotationType.Square } kept && Near(kept.Bounds, new Rect(.35, .4, .3, .12)), "selection follows the moved annotation");
        workspace.CurrentColor = WorkspaceViewModel.Palette[2];
        UntilComplete(Task.Run(async () => { while (workspace.IsBusy) await Task.Delay(20); }));
        Pump(200);
        Check(workspace.SelectedAnnotation?.Color == WorkspaceViewModel.Palette[2] && workspace.CurrentColor == WorkspaceViewModel.Palette[2], "palette recolours the selected annotation");
        Pump(600);
        SaveWindow(window, Path.Combine(output, "annotation-selected.png"));
        workspace.DeleteAnnotationCommand.Execute(null);
        UntilComplete(Task.Run(async () => { while (workspace.IsBusy) await Task.Delay(20); }));
        Pump(200);
        Check(workspace.SelectedAnnotation is null && workspace.Comments.All(c => c.Type != AnnotationType.Square), "delete the selected annotation");
        while (workspace.Document!.IsDirty) UntilComplete(workspace.ChangeAsync(d => d.Undo()));
        Check(workspace.Comments.Count == 0, "annotation workflow is fully undoable");

        // High zoom renders a sharp region of the visible part instead of a huge bitmap.
        workspace.ZoomPercent = 400;
        workspace.CurrentPage = 1;
        Pump(1000);
        var control = FindVisuals<PdfPageControl>(window).FirstOrDefault(c => !c.Thumbnail && c.Item?.Info.Index == 0);
        var viewer = FindVisuals<System.Windows.Controls.ScrollViewer>(window.PageList).First();
        if (control is not null)
        {
            // Bring the heading and the first matches into view; the sharp region must follow the scroll.
            var origin = control.TransformToAncestor(viewer).Transform(new Point());
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + origin.X + 160);
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + origin.Y + 220);
        }
        Pump(1500);
        var detail = typeof(PdfPageControl).GetField("_detail", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(control) as BitmapSource;
        var baseImage = typeof(PdfPageControl).GetField("_image", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(control) as BitmapSource;
        Check(detail is not null && baseImage is not null && (long)baseImage.PixelWidth * baseImage.PixelHeight <= 2_000_000, "zoomed page uses a bounded base and a sharp visible region");
        SaveWindow(window, Path.Combine(output, "zoom-400.png"));
        workspace.ZoomPercent = 100;
        Pump(300);
        workspace.CurrentPage = 2;
        Pump(250);

        UntilComplete(workspace.OpenPathAsync(Path.Combine(output, "form.pdf")));
        Check(workspace.Documents.Count == 2 && workspace.Info?.FormType == 1, "multiple document tabs");
        UntilComplete(workspace.OpenPathAsync(Path.Combine(output, "sample.pdf")));
        Check(workspace.Documents.Count == 2 && workspace.CurrentPage == 2, "existing tab restores its page");
        UntilComplete(workspace.CloseTabAsync(workspace.ActiveTab!));
        Check(workspace.HasDocument && workspace.Info?.FormType == 1, "close active tab switches to remaining document");
        UntilComplete(workspace.CloseTabAsync(workspace.ActiveTab!));
        Check(!workspace.HasDocument, "close tab returns welcome");
        window.Close();
        Pump(100);

        TestRestoreAfterCrash(output);
    }

    private static void TestThemePanels(MainWindow owner, string output)
    {
        owner.Themes.SetTheme(AppTheme.Dark);
        var surface = Application.Current.FindResource("Surface");
        var root = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        var menu = new System.Windows.Controls.Menu();
        var file = new System.Windows.Controls.MenuItem { Header = "Файл" };
        file.Items.Add(new System.Windows.Controls.MenuItem { Header = "Открыть", InputGestureText = "Ctrl+O" });
        file.Items.Add(new System.Windows.Controls.MenuItem { Header = "Сохранить", IsEnabled = false });
        menu.Items.Add(file);
        root.Children.Add(menu);
        var combo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 10, 0, 10), SelectedIndex = 0 };
        combo.Items.Add("Первая настройка"); combo.Items.Add("Вторая настройка");
        root.Children.Add(combo);
        var tree = new System.Windows.Controls.TreeView { Height = 160 };
        var chapter = new System.Windows.Controls.TreeViewItem { Header = "Глава документа", IsExpanded = true, IsSelected = true };
        chapter.Items.Add(new System.Windows.Controls.TreeViewItem { Header = "Вложенная закладка" });
        tree.Items.Add(chapter);
        var tabs = new System.Windows.Controls.TabControl { Height = 210 };
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "Закладки", Content = tree });
        var list = new System.Windows.Controls.ListBox { SelectedIndex = 0 };
        for (var i = 0; i < 40; i++) list.Items.Add("Элемент списка " + i);
        tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "Список", Content = list });
        root.Children.Add(tabs);
        var progress = new System.Windows.Controls.ProgressBar { Height = 6, IsIndeterminate = true, Margin = new Thickness(0, 16, 0, 10) };
        root.Children.Add(progress);
        root.Children.Add(new System.Windows.Controls.CheckBox { Content = "Активная настройка", IsChecked = true });
        var host = new Window { Width = 640, Height = 440, Content = root, ShowActivated = false, ShowInTaskbar = false, Owner = owner };
        host.Show();
        Pump(100);
        void DarkFrame(FrameworkElement element, string label)
        {
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var pixels = Pixels(bitmap);
            var bright = 0;
            for (var i = 0; i < pixels.Length; i += 4) if (pixels[i] > 190 && pixels[i + 1] > 190 && pixels[i + 2] > 190) bright++;
            Check(bright / (double)(bitmap.PixelWidth * bitmap.PixelHeight) < .15, label);
        }
        DarkFrame(host, "dark control surfaces have no light native panel backgrounds");
        SaveWindow(host, Path.Combine(output, "theme-controls-dark.png"));
        tabs.SelectedIndex = 1;
        Pump(50);
        list.ScrollIntoView(list.Items[^1]);
        Pump(50);
        DarkFrame(host, "dark tab switch and scrolling keep panel backgrounds dark");
        combo.IsDropDownOpen = true;
        Pump(25);
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        Check(popup.IsOpen && popup.PopupAnimation == System.Windows.Controls.Primitives.PopupAnimation.None, "dropdown avoids native light fade animation");
        foreach (var delay in new[] { 16, 50, 200 }) { Pump(delay); DarkFrame((FrameworkElement)popup.Child, "dropdown background remains dark across rendered frames"); }
        owner.Themes.SetTheme(AppTheme.Light);
        Pump(50);
        Check(((SolidColorBrush)((System.Windows.Controls.Border)popup.Child).Background).Color.R > 200, "open dropdown follows light theme");
        owner.Themes.SetTheme(AppTheme.Dark);
        Pump(50);
        Check(ReferenceEquals(surface, Application.Current.FindResource("Surface")), "theme changes preserve brush identity for open visuals");
        DarkFrame((FrameworkElement)popup.Child, "open dropdown follows dark theme without recreating controls");
        combo.IsDropDownOpen = false;
        var tooltip = new System.Windows.Controls.ToolTip { Content = "Подсказка в выбранной теме", PlacementTarget = combo, IsOpen = true };
        Pump(100);
        DarkFrame(tooltip, "tooltip background uses dark theme");
        tooltip.IsOpen = false;
        var context = new System.Windows.Controls.ContextMenu { PlacementTarget = combo };
        context.Items.Add(new System.Windows.Controls.MenuItem { Header = "Копировать" });
        context.IsOpen = true;
        Pump(100);
        DarkFrame(context, "context menu background uses dark theme");
        context.IsOpen = false;
        file.IsSubmenuOpen = true;
        Pump(100);
        var menuPopup = (System.Windows.Controls.Primitives.Popup)file.Template.FindName("PART_Popup", file);
        DarkFrame((FrameworkElement)menuPopup.Child, "main menu popup uses dark theme");
        Check(menuPopup.PopupAnimation == System.Windows.Controls.Primitives.PopupAnimation.None, "main menu popup does not fade from light native chrome");
        file.IsSubmenuOpen = false;
        host.Close();
        owner.Themes.SetTheme(AppTheme.Light);
    }

    private static void TestPrintWindow(MainWindow owner, string output)
    {
        using var doc = PdfDocument.FromBytes(PdfFixture.CreatePoster());
        File.WriteAllBytes(Path.Combine(output, "poster.pdf"), PdfFixture.CreatePoster());
        var dialog = new PDF_Reader.Views.PrintWindow(doc, doc.GetPages(), 1, PrintScaling.Fit, "Постер 500 × 700 мм.pdf", discoverPrinters: false)
        { Owner = owner, ShowActivated = false, ShowInTaskbar = false };
        dialog.Show();
        void WaitPreview()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!dialog.PreviewReady && DateTime.UtcNow < deadline) Pump(50);
            Check(dialog.PreviewReady, "own print preview renders without printer driver preview");
        }
        WaitPreview();
        SaveWindow(dialog, Path.Combine(output, "print-size-light.png"));
        ((System.Windows.Controls.TabControl)dialog.FindName("ModeTabs")).SelectedIndex = 1;
        WaitPreview();
        Check(dialog.Plan!.Sheets.Count == 9, "poster settings drive live preview geometry");
        SaveWindow(dialog, Path.Combine(output, "print-poster-light.png"));
        var image = (System.Windows.Controls.Image)dialog.FindName("PreviewImage");
        SaveImage((BitmapSource)image.Source, Path.Combine(output, "poster-tile-1-1.png"));
        ((System.Windows.Controls.Button)dialog.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        WaitPreview();
        Check(((System.Windows.Controls.TextBlock)dialog.FindName("SheetLabel")).Text.Contains("1-2"), "preview sheet navigation updates coordinates");
        ((System.Windows.Controls.TextBox)dialog.FindName("GlueLeftBox")).Text = "12";
        WaitPreview();
        Check(Math.Abs(dialog.Plan!.ContentArea.Left / PrintLayout.Mm - 17) < .01, "glue margin edits update preview");
        owner.Themes.SetTheme(AppTheme.Dark);
        Pump(100);
        SaveWindow(dialog, Path.Combine(output, "print-poster-dark.png"));
        ((System.Windows.Controls.TextBox)dialog.FindName("OverlapBox")).Text = "bad";
        Pump(450);
        Check(dialog.Plan is null && image.Source is null && !((System.Windows.Controls.Button)dialog.FindName("PrintButton")).IsEnabled, "invalid settings clear stale preview and block printing");
        ((System.Windows.Controls.TextBox)dialog.FindName("OverlapBox")).Text = "5";
        ((System.Windows.Controls.TabControl)dialog.FindName("ModeTabs")).SelectedIndex = 2;
        WaitPreview();
        SaveWindow(dialog, Path.Combine(output, "print-multiple-dark.png"));
        ((System.Windows.Controls.TabControl)dialog.FindName("ModeTabs")).SelectedIndex = 3;
        WaitPreview();
        Check(dialog.Plan!.Sheets.Count == 2, "booklet preview includes blank padded back");
        SaveWindow(dialog, Path.Combine(output, "print-booklet-dark.png"));
        ((System.Windows.Controls.TextBox)dialog.FindName("ScaleBox")).Text = "150";
        ((System.Windows.Controls.ComboBox)dialog.FindName("ScalingBox")).SelectedIndex = 3;
        ((System.Windows.Controls.TabControl)dialog.FindName("ModeTabs")).SelectedIndex = 0;
        dialog.Close();
        UntilComplete(dialog.DrainAsync());
        Check(!doc.IsDirty, "closing print preview drains rendering without changing document");
        owner.Themes.SetTheme(AppTheme.Light);
    }

    private static void TestRestoreAfterCrash(string output)
    {
        var directory = Path.Combine(output, "recovery-restore");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        var recovery = new RecoveryService(directory);
        var path = Path.Combine(output, "crash.pdf");
        File.WriteAllBytes(path, PdfFixture.Create());
        using (var doc = PdfDocument.Open(path))
        {
            doc.AddText(0, new(.1, .6), "Work before crash", 12);
            recovery.Write("lost", doc.GetRecoverySnapshot());
        }
        var info = Path.Combine(directory, "lost.json");
        var entry = JsonSerializer.Deserialize<RecoveryEntry>(File.ReadAllBytes(info))!;
        File.WriteAllBytes(info, JsonSerializer.SerializeToUtf8Bytes(entry with { ProcessStartTicks = 1 }));

        var dialogs = new FakeDialogs();
        using var workspace = new ViewModels.WorkspaceViewModel(dialogs, new SettingsService(Path.Combine(output, "settings-restore.json")), recovery);
        UntilComplete(workspace.RestoreRecoveredAsync());
        var restored = workspace.Document!;
        Check(dialogs.Confirmations == 1 && workspace.Documents.Count == 1 && restored.IsDirty && restored.FilePath == path,
            "startup offers and restores the crashed document as modified");
        Check(restored.ExtractText(0).Contains("Work before crash") && !File.Exists(Path.Combine(directory, "lost.pdf")), "restored copy replaces the orphaned file");
        UntilComplete(workspace.SaveAsync(workspace.ActiveTab!, false));
        using var saved = PdfDocument.Open(path);
        Check(saved.ExtractText(0).Contains("Work before crash"), "restored work is saved to the original file");
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public int Confirmations { get; private set; }
        public string[] ChooseOpenFiles() => [];
        public string? ChooseSaveFile(string suggestedName) => null;
        public string? AskText(string title, string description, string value = "", bool multiline = false, bool password = false) => null;
        public (string Text, float Size)? EditText(string title, string description, string value, float fontSize, bool multiline) => null;
        public MessageBoxResult AskSave(string name) => MessageBoxResult.No;
        public bool Confirm(string title, string message) => ++Confirmations > 0;
        public void Error(Exception exception) => throw new InvalidOperationException("Unexpected error dialog", exception);
        public void Info(string title, string message) { }
    }

    private static IEnumerable<T> FindVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var nested in FindVisuals<T>(child)) yield return nested;
        }
    }

    private static bool SameImage(BitmapSource expected, BitmapSource actual)
    {
        if (expected.PixelWidth != actual.PixelWidth || expected.PixelHeight != actual.PixelHeight) return false;
        var a = Pixels(expected);
        var b = Pixels(actual);
        long difference = 0;
        for (var i = 0; i < a.Length; i++) difference += Math.Abs(a[i] - b[i]);
        return difference / (double)a.Length < 1.0;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void UntilComplete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI command did not complete in 30 seconds.");
            Pump(25);
        }
        task.GetAwaiter().GetResult();
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Delay(milliseconds).ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.Send));
        Dispatcher.PushFrame(frame);
        if (_dispatcherFailure is not null) throw new InvalidOperationException("WPF dispatcher failed", _dispatcherFailure);
    }

    private static void SaveWindow(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        SaveImage(bitmap, path);
    }

    private static void SaveImage(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
