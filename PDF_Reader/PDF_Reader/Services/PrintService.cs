using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

public static class PrintService
{
    private const double PrintDpi = 200;

    /// <summary>
    /// Shows the printer dialog and spools asynchronously. Pages are rendered in the background from a copy
    /// of the document, so the window stays responsive and later edits do not leak into the print job.
    /// </summary>
    public static async Task PrintAsync(PdfDocument document, int currentPage, PrintScaling scaling, Action<string> progress)
    {
        var info = await Task.Run(document.GetInfo);
        if (!info.CanPrint) throw new UnauthorizedAccessException("Печать запрещена владельцем PDF.");
        var count = info.Pages;
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)count,
            PageRange = new PageRange(1, count)
        };
        if (dialog.ShowDialog() != true) return;
        var pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [Math.Clamp(currentPage, 1, count) - 1],
            PageRangeSelection.UserPages => GetPageRange(dialog.PageRange, count),
            _ => Enumerable.Range(0, count).ToArray()
        };
        var capabilities = dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket);
        var imageable = capabilities.PageImageableArea;
        var pageSize = new Size(capabilities.OrientedPageMediaWidth ?? dialog.PrintableAreaWidth,
            capabilities.OrientedPageMediaHeight ?? dialog.PrintableAreaHeight);
        var area = new Rect(imageable?.OriginWidth ?? 0, imageable?.OriginHeight ?? 0,
            imageable?.ExtentWidth ?? dialog.PrintableAreaWidth, imageable?.ExtentHeight ?? dialog.PrintableAreaHeight);

        using var copy = await Task.Run(document.CreateCopy);
        var paginator = await Task.Run(() => new PdfPaginator(copy, Validate(copy, pages, pageSize, area), pageSize, area, scaling));
        dialog.PrintQueue.CurrentJobSettings.Description = Path.GetFileName(document.FilePath) ?? "PaperDesk PDF";
        var writer = PrintQueue.CreateXpsDocumentWriter(dialog.PrintQueue);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        paginator.Failed += () => writer.CancelAsync();
        writer.WritingProgressChanged += (_, e) => progress($"Печать: страница {Math.Min(e.Number, pages.Length)} из {pages.Length}");
        writer.WritingCompleted += (_, e) =>
        {
            if (e.Error is not null) completion.TrySetException(e.Error);
            else if (e.Cancelled) completion.TrySetCanceled();
            else completion.TrySetResult();
        };
        progress("Подготовка печати…");
        try
        {
            writer.WriteAsync(paginator, dialog.PrintTicket);
            await completion.Task;
        }
        catch (OperationCanceledException) when (paginator.Error is not null)
        {
            throw new InvalidOperationException("Не удалось подготовить страницу к печати: " + paginator.Error.Message, paginator.Error);
        }
        finally
        {
            await paginator.DrainAsync();
        }
    }

    private static int[] GetPageRange(PageRange range, int count)
    {
        var from = Math.Clamp(range.PageFrom, 1, count);
        var to = Math.Clamp(range.PageTo, from, count);
        return Enumerable.Range(from - 1, to - from + 1).ToArray();
    }

    private static int[] Validate(PdfDocument document, int[] pages, Size size, Rect area)
    {
        if (!document.GetInfo().CanPrint) throw new UnauthorizedAccessException("Печать запрещена владельцем PDF.");
        if (pages.Length == 0 || pages.Any(p => p < 0 || p >= document.PageCount)) throw new ArgumentException("Некорректные страницы для печати.");
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || size.Width <= 0 || size.Height <= 0)
            throw new ArgumentException("Принтер сообщил некорректную область печати.");
        return pages.ToArray();
    }

    public static DocumentPaginator CreatePaginator(PdfDocument document, int[] pages, Size size, Rect area, PrintScaling scaling = PrintScaling.Fit) =>
        new PdfPaginator(document, Validate(document, pages, size, area), size, area, scaling);

    /// <summary>Placement of one PDF page on paper, in 1/96 inch.</summary>
    internal readonly record struct Placement(int Index, double Width, double Height, bool Rotated, Point Center, int PixelWidth);

    internal static Placement Place(PdfPageInfo page, Rect area, PrintScaling scaling)
    {
        var width = page.Width * 96 / 72;
        var height = page.Height * 96 / 72;
        // Auto-rotate: a landscape page on portrait paper (or vice versa) is turned to use the sheet fully.
        var rotated = Math.Abs(width - height) > 1 && width > height != area.Width > area.Height;
        var footprintWidth = rotated ? height : width;
        var footprintHeight = rotated ? width : height;
        var fit = Math.Min(area.Width / footprintWidth, area.Height / footprintHeight);
        var scale = scaling switch
        {
            PrintScaling.ActualSize => 1,
            PrintScaling.ShrinkOversized => Math.Min(1, fit),
            _ => fit
        };
        width *= scale;
        height *= scale;
        var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        return new(page.Index, width, height, rotated, center, (int)Math.Ceiling(width * PrintDpi / 96));
    }

    private sealed class PdfPaginator : DocumentPaginator
    {
        private const int Lookahead = 2;
        private readonly PdfDocument _document;
        private readonly int[] _pages;
        private readonly Rect _area;
        private readonly PrintScaling _scaling;
        private readonly IReadOnlyList<PdfPageInfo> _pageInfo;
        private readonly Dictionary<int, Task<BitmapSource>> _renders = [];

        public PdfPaginator(PdfDocument document, int[] pages, Size size, Rect area, PrintScaling scaling)
        {
            _document = document;
            _pages = pages;
            _area = area;
            _scaling = scaling;
            _pageInfo = document.GetPages();
            PageSize = size;
        }

        public event Action? Failed;
        public Exception? Error { get; private set; }
        public override bool IsPageCountValid => true;
        public override int PageCount => _pages.Length;
        public override Size PageSize { get; set; }
        public override IDocumentPaginatorSource? Source => null;

        private Placement Layout(int pageNumber) => Place(_pageInfo[_pages[pageNumber]], _area, _scaling);

        private Task<BitmapSource> Request(int pageNumber)
        {
            if (_renders.TryGetValue(pageNumber, out var task)) return task;
            var placement = Layout(pageNumber);
            task = Task.Run(() => _document.Render(placement.Index, placement.PixelWidth, printing: true));
            _renders[pageNumber] = task;
            return task;
        }

        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= _pages.Length) return DocumentPage.Missing;
            // Rendering runs on worker threads; the next pages are prepared while the spooler writes this one.
            for (var next = pageNumber; next < Math.Min(_pages.Length, pageNumber + 1 + Lookahead); next++) Request(next);
            var task = _renders[pageNumber];
            _renders.Remove(pageNumber);
            var placement = Layout(pageNumber);
            var visual = new DrawingVisual();
            try
            {
                var image = task.GetAwaiter().GetResult();
                using var dc = visual.RenderOpen();
                dc.PushClip(new RectangleGeometry(_area));
                if (placement.Rotated) dc.PushTransform(new RotateTransform(-90, placement.Center.X, placement.Center.Y));
                dc.DrawImage(image, new Rect(placement.Center.X - placement.Width / 2, placement.Center.Y - placement.Height / 2, placement.Width, placement.Height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Error ??= ex;
                Failed?.Invoke();
            }
            return new DocumentPage(visual, PageSize, new Rect(PageSize), _area);
        }

        public async Task DrainAsync()
        {
            var pending = _renders.Values.ToArray();
            _renders.Clear();
            try
            {
                await Task.WhenAll(pending);
            }
            catch (Exception)
            {
                // Pages that were prepared but not printed (cancelled job) are discarded.
            }
        }
    }
}
