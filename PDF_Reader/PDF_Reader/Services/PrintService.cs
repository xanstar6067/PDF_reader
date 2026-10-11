using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PDF_Reader.Services;

public static class PrintService
{
    public static void Print(PdfDocument document, int currentPage, Window owner)
    {
        if (!document.GetInfo().CanPrint) throw new UnauthorizedAccessException("Печать запрещена владельцем PDF.");
        var dialog = new PrintDialog { UserPageRangeEnabled = true, CurrentPageEnabled = true,
            MinPage = 1, MaxPage = (uint)document.PageCount, PageRange = new PageRange(1, document.PageCount) };
        if (dialog.ShowDialog() != true) return;
        var pages = dialog.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => new[] { currentPage - 1 },
            PageRangeSelection.UserPages => GetPageRange(dialog.PageRange, document.PageCount),
            _ => Enumerable.Range(0, document.PageCount).ToArray()
        };
        var capabilities = dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket);
        var area = capabilities.PageImageableArea;
        var pageSize = new Size(capabilities.OrientedPageMediaWidth ?? dialog.PrintableAreaWidth,
            capabilities.OrientedPageMediaHeight ?? dialog.PrintableAreaHeight);
        var content = new Rect(area?.OriginWidth ?? 0, area?.OriginHeight ?? 0,
            area?.ExtentWidth ?? dialog.PrintableAreaWidth, area?.ExtentHeight ?? dialog.PrintableAreaHeight);
        dialog.PrintDocument(CreatePaginator(document, pages, pageSize, content), System.IO.Path.GetFileName(document.FilePath) ?? "PaperDesk PDF");
    }
    private static int[] GetPageRange(PageRange range, int count)
    {
        var from = Math.Clamp(range.PageFrom, 1, count); var to = Math.Clamp(range.PageTo, from, count);
        return Enumerable.Range(from - 1, to - from + 1).ToArray();
    }
    public static DocumentPaginator CreatePaginator(PdfDocument document, int[] pages, Size size, Rect area)
    {
        if (!document.GetInfo().CanPrint) throw new UnauthorizedAccessException("Печать запрещена владельцем PDF.");
        if (pages.Length == 0 || pages.Any(p => p < 0 || p >= document.PageCount)) throw new ArgumentException("Некорректные страницы для печати.");
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || size.Width <= 0 || size.Height <= 0) throw new ArgumentException("Принтер сообщил некорректную область печати.");
        return new PdfPaginator(document, pages.ToArray(), size, area);
    }
    private sealed class PdfPaginator(PdfDocument document, int[] pages, Size size, Rect area) : DocumentPaginator
    {
        private readonly IReadOnlyList<Models.PdfPageInfo> _pageInfo = document.GetPages();
        public override bool IsPageCountValid => true;
        public override int PageCount => pages.Length;
        public override Size PageSize { get; set; } = size;
        public override IDocumentPaginatorSource? Source => null;
        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= pages.Length) return DocumentPage.Missing;
            var info = _pageInfo[pages[pageNumber]];
            var scale = Math.Min(area.Width / info.Width, area.Height / info.Height);
            var width = info.Width * scale; var height = info.Height * scale;
            var image = document.Render(info.Index, (int)Math.Ceiling(width * 200 / 96), printing: true);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawImage(image, new Rect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height));
            return new DocumentPage(visual, PageSize, new Rect(PageSize), area);
        }
    }
}
