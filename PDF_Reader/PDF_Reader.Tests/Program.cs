using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDF_Reader.Models;
using PDF_Reader.Services;

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
            var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/tests"); Directory.CreateDirectory(output);
            TestDocument(output); Console.WriteLine("PASS: editing, search, outlines, annotations, pages");
            TestHistory(output); TestProtection(output); TestAtomicSave(output); TestParallelAccess(); TestForms(output); Console.WriteLine("PASS: undo/redo, encryption, permissions, atomic save, concurrency, AcroForm");
            TestUi(output);
            Console.WriteLine($"PASS: {_assertions} assertions, native PDFium and WPF integration."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); _assertions++; }
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { _assertions++; return; } throw new InvalidOperationException("FAIL: " + name); }
    private static Point Center(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    private static void TestDocument(string output)
    {
        var sample = Path.Combine(output, "sample.pdf"); File.WriteAllBytes(sample, PdfFixture.Create());
        using var doc = PdfDocument.Open(sample);
        Check(doc.PageCount == 2 && !doc.IsDirty, "open two pages, clean state");
        var pages = doc.GetPages(); Check(pages[1].Width > pages[1].Height, "landscape geometry");
        var outline = doc.GetOutline(); Check(outline.Count == 2 && outline[0].Children.Count == 1 && outline[1].PageIndex == 1, "nested outlines");
        var matches = doc.Search("hello", false, false, default); Check(matches.Count == 3, "case-insensitive document search");
        Check(doc.Search("hello", true, false, default).Count == 0, "case-sensitive search");
        Check(doc.Search("Hell", false, true, default).Count == 0, "whole word search");
        Check(matches.All(m => m.Rectangles.Count > 0 && m.Rectangles.All(r => r.X >= 0 && r.Y >= 0)), "search display rectangles");
        var hit = doc.HitText(0, Center(matches[0].Rectangles[0])); Check(hit is not null && hit.Text.Contains("Hello"), "hit text object");
        doc.ReplaceText(hit!, "Привет, PDF. Новый текст.", 13);
        Check(doc.ExtractText(0).Contains("Привет, PDF") && !doc.ExtractText(0).Contains("Hello PDF"), "real replacement with Cyrillic");
        Throws<InvalidOperationException>(() => doc.ReplaceText(hit!, "stale", 12), "reject stale object token");
        Check(doc.ExtractText(0).Contains("Привет"), "rollback preserves text after failure");
        doc.AddText(0, new(.12, .4), "Добавленная строка\nВторая строка", 14);
        Check(doc.ExtractText(0).Contains("Вторая строка"), "multiline Unicode addition");
        doc.AddNote(0, new(.8, .2), "Проверка заметки");
        var search = doc.Search("Привет", false, false, default).Single(); doc.Highlight(0, search.Rectangles);
        Check(doc.GetComments().Count == 2, "standard note and highlight annotations");
        var saved = Path.Combine(output, "edited.pdf"); doc.Save(saved);
        using (var reopen = PdfDocument.Open(saved))
        {
            Check(reopen.GetOutline().Count == 2 && reopen.GetComments().Count == 2, "outlines and annotations survive save");
            Check(reopen.ExtractText(0).Contains("Привет") && !reopen.ExtractText(0).Contains("Hello PDF"), "text survives save without original");
            SaveImage(reopen.Render(0, 794), Path.Combine(output, "edited-page.png"));
        }
        Check(!doc.IsDirty, "save marks clean");
        doc.RotatePage(0, 1); Check(doc.GetPages()[0].Width > doc.GetPages()[0].Height, "page rotation updates geometry");
        var rotatedMatch = doc.Search("Привет", false, false, default).Single();
        Check(doc.HitText(0, Center(rotatedMatch.Rectangles[0])) is not null, "hit test on rotated page");
        doc.AddText(0, new(.2, .5), "Поворот", 12); Check(doc.ExtractText(0).Contains("Поворот"), "addition to rotated page");
        doc.MovePage(0, 1); Check(doc.ExtractText(0).Contains("Second chapter"), "move pages");
        doc.InsertBlankPage(1); Check(doc.PageCount == 3, "insert blank page");
        doc.DeletePage(1); Check(doc.PageCount == 2, "delete page");
        using var imported = PdfDocument.FromBytes(PdfFixture.Create()); doc.ImportPages(imported, 1); Check(doc.PageCount == 4, "merge PDF pages");
        using var extracted = PdfDocument.FromBytes(doc.ExportPages("2-3")); Check(extracted.PageCount == 2 && extracted.ExtractText(0).Contains("PaperDesk"), "extract range to new PDF");
        var note = doc.GetComments().First(c => c.Type == 1); doc.DeleteComment(note); Check(!doc.GetComments().Any(c => c.Type == 1), "delete annotation");
    }
    private static void TestHistory(string output)
    {
        using var doc = PdfDocument.FromBytes(PdfFixture.Create()); doc.RotatePage(0, 1);
        Check(doc.IsDirty && doc.CanUndo, "dirty history"); doc.Undo(); Check(!doc.IsDirty && doc.CanRedo, "undo to initial clean state");
        doc.Redo(); Check(doc.IsDirty && doc.GetPages()[0].Width > doc.GetPages()[0].Height, "redo restores modification");
        doc.Save(Path.Combine(output, "history.pdf")); doc.Undo(); Check(doc.IsDirty, "undo away from saved state"); doc.Redo(); Check(!doc.IsDirty, "redo to saved state");
        doc.Undo(); doc.InsertBlankPage(1); Check(!doc.CanRedo, "new edit discards redo branch");
        var count = doc.PageCount; Throws<ArgumentOutOfRangeException>(() => doc.MovePage(999, 0), "invalid edit fails transaction"); Check(doc.PageCount == count, "failed edit rollback");
        using var blank = PdfDocument.Create(); Check(blank.PageCount == 1 && blank.IsDirty, "new unsaved PDF");
        Throws<InvalidOperationException>(() => blank.DeletePage(0), "cannot delete final page");
        Throws<ArgumentException>(() => blank.AddText(0, new(.1, .1), "test", float.NaN), "reject invalid font size");
    }
    private static void TestProtection(string output)
    {
        var bytes = PdfFixture.Create(encrypted: true);
        Throws<PdfPasswordException>(() => PdfDocument.FromBytes(bytes), "password required");
        Throws<PdfPasswordException>(() => PdfDocument.FromBytes(bytes, "wrong"), "wrong password");
        using var doc = PdfDocument.FromBytes(bytes, "reader-secret"); Check(doc.GetInfo().IsEncrypted && doc.ExtractText(0).Contains("Hello"), "open encrypted PDF");
        doc.AddText(0, new(.1, .4), "Protected edit", 12); var saved = Path.Combine(output, "protected.pdf"); doc.Save(saved);
        Throws<PdfPasswordException>(() => PdfDocument.Open(saved), "save retains encryption");
        using var reopened = PdfDocument.Open(saved, "reader-secret"); Check(reopened.ExtractText(0).Contains("Protected edit"), "encrypted edits survive save");
        using var restricted = PdfDocument.FromBytes(PdfFixture.Create(true, -64), "reader-secret");
        Check(!restricted.GetInfo().CanEdit && !restricted.GetInfo().CanPrint && !restricted.GetInfo().CanCopy && !restricted.GetInfo().CanAssemble, "permission flags including legacy security");
        Throws<UnauthorizedAccessException>(() => restricted.AddText(0, new(.1, .1), "blocked", 12), "enforce edit restriction");
        Throws<UnauthorizedAccessException>(() => restricted.ExtractText(0), "enforce copy restriction");
        Throws<UnauthorizedAccessException>(() => restricted.RotatePage(0, 1), "enforce legacy page assembly restriction");
        Throws<UnauthorizedAccessException>(() => PrintService.CreatePaginator(restricted, [0], new Size(816, 1056), new Rect(24, 24, 768, 1008)), "enforce print restriction");
        using var owner = PdfDocument.FromBytes(PdfFixture.Create(true, -64), "owner-secret"); Check(owner.GetInfo().CanEdit, "owner unlocks permissions");
    }
    private static void TestAtomicSave(string output)
    {
        var path = Path.Combine(output, "atomic.pdf"); File.WriteAllBytes(path, PdfFixture.Create()); using var doc = PdfDocument.Open(path);
        doc.AddText(0, new(.1, .5), "saved", 12); doc.Save(path); using (var again = PdfDocument.Open(path)) Check(again.ExtractText(0).Contains("saved"), "replace original while open");
        var external = PdfFixture.Create(); File.WriteAllBytes(path, external);
        Throws<IOException>(() => doc.Save(path), "protect external file changes"); Check(File.ReadAllBytes(path).SequenceEqual(external), "conflict leaves external file intact");
        var blocked = Path.Combine(output, "missing-folder", "file.pdf"); Throws<DirectoryNotFoundException>(() => doc.Save(blocked), "save failure is reported");
        Check(File.ReadAllBytes(path).SequenceEqual(external), "failed save preserves original");
    }
    private static void TestParallelAccess()
    {
        using var first = PdfDocument.FromBytes(PdfFixture.Create()); using var second = PdfDocument.FromBytes(PdfFixture.Create());
        Task.WaitAll(Task.Run(() => first.Render(0, 400)), Task.Run(() => second.Search("Hello", false, false, default)), Task.Run(() => first.GetBytes()));
        Check(true, "serialize simultaneous native operations");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => first.Search("Hello", false, false, cancellation.Token), "cancel search");
    }
    private static void TestForms(string output)
    {
        var path = Path.Combine(output, "form.pdf"); File.WriteAllBytes(path, PdfFixture.CreateForm());
        using var doc = PdfDocument.Open(path); Check(doc.GetInfo().FormType == 1, "AcroForm detection");
        var point = new Point(.2, .271); var field = doc.HitFormField(0, point);
        Check(field is { Name: "Name", Value: "Initial", Type: 6 }, "AcroForm text field discovery");
        doc.FillFormField(field!, "Filled PDF form"); Check(doc.HitFormField(0, point)?.Value == "Filled PDF form", "fill text form field");
        var checkPoint = new Point(.1, .335); var checkbox = doc.HitFormField(0, checkPoint);
        Check(checkbox is { Type: 2, Checked: false }, "checkbox discovery");
        doc.FillFormField(checkbox!, ""); Check(doc.HitFormField(0, checkPoint)?.Checked == true, "toggle checkbox");
        doc.Save(path); using var reopened = PdfDocument.Open(path);
        Check(reopened.HitFormField(0, point)?.Value == "Filled PDF form" && reopened.HitFormField(0, checkPoint)?.Checked == true, "form values persist after save");
        SaveImage(reopened.Render(0, 794), Path.Combine(output, "form-page.png"));
        doc.Undo(); Check(doc.HitFormField(0, checkPoint)?.Checked == false, "form undo");
    }
    private static void TestUi(string output)
    {
        Console.WriteLine("WPF: initialize application resources");
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // A plain Application keeps the product's OnStartup from opening a second window with test arguments.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PaperDesk;component/Views/Theme.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        application.DispatcherUnhandledException += (_, e) => { _dispatcherFailure = e.Exception; Console.Error.WriteLine(e.Exception); e.Handled = true; Dispatcher.ExitAllFrames(); };
        var window = new MainWindow(new SettingsService(Path.Combine(output, "settings.json"))) { ShowActivated = false, ShowInTaskbar = false };
        Console.WriteLine("WPF: create and render window");
        window.Show(); Console.WriteLine("WPF: window shown, pumping layout"); Pump(200); Console.WriteLine("WPF: layout ready");
        SaveWindow(window, Path.Combine(output, "welcome.png"));
        var opening = window.Workspace.OpenPathAsync(Path.Combine(output, "sample.pdf")); UntilComplete(opening); Pump(900);
        Check(window.Workspace.HasDocument && window.Workspace.Pages.Count == 2, "WPF document workspace");
        var paginator = PrintService.CreatePaginator(window.Workspace.Document!, [0, 1], new Size(816, 1056), new Rect(24, 24, 768, 1008));
        Check(paginator.PageCount == 2 && paginator.GetPage(0).Size == new Size(816, 1056), "printer pagination with imageable area");
        Check(paginator.GetPage(1).Visual is DrawingVisual, "landscape page rendering for print");
        window.Workspace.FitWidthCommand.Execute(null); Pump(900); SaveWindow(window, Path.Combine(output, "workspace.png"));
        window.Workspace.Query = "Hello"; Pump(900); Check(window.Workspace.Matches.Count == 3, "WPF asynchronous search");
        window.Workspace.NavigationTab = 2; Pump(300); SaveWindow(window, Path.Combine(output, "search.png"));
        window.Workspace.CurrentPage = 2; Pump(250);
        Check(window.Workspace.CurrentPage == 2, "page navigation remains synchronized");
        UntilComplete(window.Workspace.OpenPathAsync(Path.Combine(output, "form.pdf")));
        Check(window.Workspace.Documents.Count == 2 && window.Workspace.Info?.FormType == 1, "multiple document tabs");
        UntilComplete(window.Workspace.OpenPathAsync(Path.Combine(output, "sample.pdf")));
        Check(window.Workspace.Documents.Count == 2 && window.Workspace.CurrentPage == 2, "existing tab restores its page");
        UntilComplete(window.Workspace.CloseTabAsync(window.Workspace.ActiveTab!));
        Check(window.Workspace.HasDocument && window.Workspace.Info?.FormType == 1, "close active tab switches to remaining document");
        UntilComplete(window.Workspace.CloseTabAsync(window.Workspace.ActiveTab!)); Check(!window.Workspace.HasDocument, "close tab returns welcome");
        window.Close(); Pump(100);
    }
    private static void UntilComplete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted) { if (DateTime.UtcNow > deadline) throw new TimeoutException("UI command did not complete in 30 seconds."); Pump(25); }
        task.GetAwaiter().GetResult();
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        _ = Task.Delay(milliseconds).ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.Send));
        Dispatcher.PushFrame(frame);
        if (_dispatcherFailure is not null) throw new InvalidOperationException("WPF dispatcher failed", _dispatcherFailure);
    }
    private static void SaveWindow(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); SaveImage(bitmap, path);
    }
    private static void SaveImage(BitmapSource image, string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
