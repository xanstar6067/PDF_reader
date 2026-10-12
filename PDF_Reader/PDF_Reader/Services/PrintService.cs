using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF_Reader.Models;
using PDF_Reader.Views;

namespace PDF_Reader.Services;

public static class PrintService
{
    private const double PrintDpi = 200;

    public static async Task<bool> PrintAsync(Window owner, PdfDocument document, int currentPage, PrintScaling scaling, Action<string> progress)
    {
        using var copy = await Task.Run(document.CreateCopy);
        var info = await Task.Run(copy.GetInfo);
        if (!info.CanPrint) throw new UnauthorizedAccessException("Печать запрещена владельцем PDF.");
        var pageInfo = await Task.Run(copy.GetPages);
        var dialog = new PrintWindow(copy, pageInfo, currentPage, scaling, Path.GetFileName(document.FilePath) ?? "Документ") { Owner = owner };
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { await dialog.DrainAsync(); }
        if (!accepted || dialog.Plan is null || dialog.PrinterName is null || dialog.Ticket is null) return false;

        using var server = new LocalPrintServer();
        using var queue = PrinterSettingsService.OpenQueue(server, dialog.PrinterName);
        queue.CurrentJobSettings.Description = Path.GetFileName(document.FilePath) ?? "PaperDesk PDF";
        var paginator = new PdfPaginator(copy, dialog.Plan, dialog.DocumentName);
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        paginator.Failed += () => writer.CancelAsync();
        writer.WritingProgressChanged += (_, e) => progress($"Печать: лист {Math.Min(e.Number, paginator.PageCount)} из {paginator.PageCount}");
        writer.WritingCompleted += (_, e) =>
        {
            if (e.Error is not null) completion.TrySetException(e.Error);
            else if (e.Cancelled) completion.TrySetCanceled();
            else completion.TrySetResult();
        };
        progress("Подготовка печати…");
        try
        {
            writer.WriteAsync(paginator, dialog.Ticket);
            await completion.Task;
            if (paginator.Error is not null) throw new InvalidOperationException("Не удалось подготовить лист к печати.", paginator.Error);
        }
        catch (OperationCanceledException) when (paginator.Error is not null)
        {
            throw new InvalidOperationException("Не удалось подготовить лист к печати: " + paginator.Error.Message, paginator.Error);
        }
        finally { await paginator.DrainAsync(); }
        return true;
    }

    public static DocumentPaginator CreatePaginator(PdfDocument document, int[] pages, Size size, Rect area, PrintScaling scaling = PrintScaling.Fit)
    {
        if (!document.GetInfo().CanPrint) throw new UnauthorizedAccessException("Печать запрещена владельцем PDF.");
        return new PdfPaginator(document, PrintLayout.Create(document.GetPages(), pages, size, area, new PrintOptions { Scaling = scaling }), "Документ");
    }

    internal static DocumentPaginator CreatePaginator(PdfDocument document, PrintPlan plan, string name = "Документ")
    {
        if (!document.GetInfo().CanPrint) throw new UnauthorizedAccessException("Печать запрещена владельцем PDF.");
        return new PdfPaginator(document, plan, name);
    }

    internal readonly record struct Placement(int Index, double Width, double Height, bool Rotated, Point Center, int PixelWidth);
    internal static Placement Place(PdfPageInfo page, Rect area, PrintScaling scaling)
    {
        var slot = PrintLayout.Fit(page, area, scaling, 100, true);
        return new(slot.PageIndex, slot.Size.Width, slot.Size.Height, slot.Rotated, slot.Center, (int)Math.Ceiling(slot.Size.Width * PrintDpi / 96));
    }

    private sealed record SlotImage(BitmapSource Image, Rect Bounds, Matrix Transform, Rect Clip);

    private static Matrix Transform(PrintSlot slot)
    {
        var matrix = Matrix.Identity;
        matrix.Translate(slot.Center.X - slot.Size.Width / 2, slot.Center.Y - slot.Size.Height / 2);
        if (slot.Rotated) matrix.RotateAt(-90, slot.Center.X, slot.Center.Y);
        return matrix;
    }

    private static SlotImage[] RenderSheet(PdfDocument document, PrintSheet sheet, PrintOptions options, double dpi, CancellationToken cancellation)
    {
        var images = new List<SlotImage>();
        foreach (var slot in sheet.Slots)
        {
            cancellation.ThrowIfCancellationRequested();
            var matrix = Transform(slot);
            var inverse = matrix;
            inverse.Invert();
            var visible = new MatrixTransform(inverse).TransformBounds(slot.Clip);
            visible.Intersect(new Rect(slot.Size));
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) continue;
            // Render only the visible PDF region, preserving poster detail without a full A1/A0 raster.
            var factor = Math.Min(dpi / 96, (PdfDocument.MaxRenderSide - 1) / Math.Max(slot.Size.Width, slot.Size.Height));
            factor = Math.Min(factor, Math.Sqrt((PdfDocument.MaxRenderPixels - 100000) / (visible.Width * visible.Height)));
            var fullWidth = Math.Max(1, (int)Math.Ceiling(slot.Size.Width * factor));
            var fullHeight = Math.Max(1, (int)Math.Ceiling(slot.Size.Height * factor));
            var sx = fullWidth / slot.Size.Width;
            var sy = fullHeight / slot.Size.Height;
            var x = Math.Clamp((int)Math.Floor(visible.Left * sx), 0, fullWidth - 1);
            var y = Math.Clamp((int)Math.Floor(visible.Top * sy), 0, fullHeight - 1);
            var right = Math.Clamp((int)Math.Ceiling(visible.Right * sx), x + 1, fullWidth);
            var bottom = Math.Clamp((int)Math.Ceiling(visible.Bottom * sy), y + 1, fullHeight);
            BitmapSource image;
            if (document.Contains3D(slot.PageIndex))
            {
                // The same default 3D view used on the page, cropped to the print slot.
                var full = ThreeDService.PreviewAsync(document, slot.PageIndex, Math.Min(fullWidth, 2048), cancellation).GetAwaiter().GetResult();
                var crop = new Int32Rect(Math.Clamp((int)(x * (double)full.PixelWidth / fullWidth),0,full.PixelWidth-1), Math.Clamp((int)(y * (double)full.PixelHeight / fullHeight),0,full.PixelHeight-1),
                    Math.Max(1,(int)Math.Ceiling((right-x)*(double)full.PixelWidth/fullWidth)),Math.Max(1,(int)Math.Ceiling((bottom-y)*(double)full.PixelHeight/fullHeight)));
                crop.Width = Math.Min(crop.Width,full.PixelWidth-crop.X);crop.Height = Math.Min(crop.Height,full.PixelHeight-crop.Y);
                var cropped = new CroppedBitmap(full,crop);cropped.Freeze();image=cropped;
            }
            else image = document.RenderRegion(slot.PageIndex, fullWidth, fullHeight, new Int32Rect(x, y, right - x, bottom - y), cancellation, printing: true);
            if (options.Grayscale)
            {
                var gray = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
                gray.Freeze();
                image = gray;
            }
            images.Add(new(image, new Rect(x / sx, y / sy, (right - x) / sx, (bottom - y) / sy), matrix, slot.Clip));
        }
        return images.ToArray();
    }

    private static DrawingVisual DrawSheet(PrintPlan plan, int index, SlotImage[] images, string name, bool preview = false)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();
        dc.DrawRectangle(Brushes.White, null, preview ? new Rect(plan.Paper) : plan.ImageableArea);
        dc.PushClip(new RectangleGeometry(plan.ImageableArea));
        foreach (var image in images)
        {
            dc.PushClip(new RectangleGeometry(image.Clip));
            dc.PushTransform(new MatrixTransform(image.Transform));
            dc.DrawImage(image.Image, image.Bounds);
            dc.Pop();
            dc.Pop();
            if (plan.Options.PageBorder) dc.DrawRectangle(null, new Pen(Brushes.Black, .5), image.Clip);
        }
        var sheet = plan.Sheets[index];
        if (plan.Options.Mode == PrintMode.Poster)
        {
            var area = plan.ContentArea;
            if (plan.Options.CutMarks)
            {
                var pen = new Pen(Brushes.Black, .5);
                foreach (var x in new[] { area.Left, area.Right }) foreach (var y in new[] { area.Top, area.Bottom })
                {
                    var dx = x == area.Left ? -1 : 1;
                    var dy = y == area.Top ? -1 : 1;
                    dc.DrawLine(pen, new Point(x + dx * 2, y), new Point(x + dx * 10, y));
                    dc.DrawLine(pen, new Point(x, y + dy * 2), new Point(x, y + dy * 10));
                }
            }
            if (plan.Options.Labels)
            {
                var coordinate = new FormattedText($"{sheet.Row}-{sheet.Column}", CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 14, Brushes.Black, 1);
                var offset = coordinate.Width + 12;
                var text = new FormattedText($"стр. {sheet.PageIndex + 1} · {sheet.Rows}×{sheet.Columns} · {name}", CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 9, Brushes.Black, 1) { MaxTextWidth = Math.Max(1, area.Width - offset), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                foreach (var y in new[] { plan.ImageableArea.Top + Math.Max(0, (area.Top - plan.ImageableArea.Top - coordinate.Height) / 2),
                    area.Bottom + Math.Max(0, (plan.ImageableArea.Bottom - area.Bottom - coordinate.Height) / 2) })
                {
                    dc.DrawText(coordinate, new Point(area.Left, y));
                    dc.DrawText(text, new Point(area.Left + offset, y + (coordinate.Height - text.Height) / 2));
                }
            }
        }
        dc.Pop();
        if (preview)
        {
            var pen = new Pen(Brushes.Gray, .6) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, plan.ImageableArea);
            if (plan.Options.Mode == PrintMode.Poster) dc.DrawRectangle(null, pen, plan.ContentArea);
        }
        return visual;
    }

    internal static async Task<BitmapSource> RenderPreviewAsync(PdfDocument document, PrintPlan plan, int index, string name, CancellationToken cancellation)
    {
        var dpi = Math.Min(96, 1100 * 96 / Math.Max(plan.Paper.Width, plan.Paper.Height));
        var images = await Task.Run(() => RenderSheet(document, plan.Sheets[index], plan.Options, dpi, cancellation), cancellation);
        cancellation.ThrowIfCancellationRequested();
        var visual = DrawSheet(plan, index, images, name, preview: true);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(plan.Paper.Width * dpi / 96)),
            Math.Max(1, (int)Math.Ceiling(plan.Paper.Height * dpi / 96)), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class PdfPaginator(PdfDocument document, PrintPlan plan, string name) : DocumentPaginator
    {
        private readonly Dictionary<int, Task<SlotImage[]>> _renders = [];
        public event Action? Failed;
        public Exception? Error { get; private set; }
        public override bool IsPageCountValid => true;
        public override int PageCount => plan.Sheets.Count;
        public override Size PageSize { get => plan.Paper; set { if (value != plan.Paper) throw new NotSupportedException("Размер бумаги задан планом печати."); } }
        public override IDocumentPaginatorSource? Source => null;
        private Task<SlotImage[]> Request(int index)
        {
            if (!_renders.TryGetValue(index, out var task))
                _renders[index] = task = Task.Run(() => RenderSheet(document, plan.Sheets[index], plan.Options, PrintDpi, CancellationToken.None));
            return task;
        }

        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= PageCount) return DocumentPage.Missing;
            for (var next = pageNumber; next < Math.Min(PageCount, pageNumber + 2); next++) Request(next);
            try
            {
                var images = _renders[pageNumber].GetAwaiter().GetResult();
                _renders.Remove(pageNumber);
                return new DocumentPage(DrawSheet(plan, pageNumber, images, name), plan.Paper, new Rect(plan.Paper), plan.ImageableArea);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Error ??= ex;
                Failed?.Invoke();
                return DocumentPage.Missing;
            }
        }

        public async Task DrainAsync()
        {
            var pending = _renders.Values.ToArray();
            _renders.Clear();
            try { await Task.WhenAll(pending); }
            catch (Exception) { /* Cancelled/failed jobs discard prefetched sheets. */ }
        }
    }
}
