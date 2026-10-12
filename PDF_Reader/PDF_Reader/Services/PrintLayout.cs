using System.Globalization;
using System.Windows;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

internal sealed record PrintSlot(int PageIndex, Size Size, Point Center, bool Rotated, Rect Clip);
internal sealed record PrintSheet(IReadOnlyList<PrintSlot> Slots, string Label, int PageIndex = -1,
    int Row = 0, int Column = 0, int Rows = 0, int Columns = 0);
internal sealed record PrintPlan(Size Paper, Rect ImageableArea, Rect ContentArea, PrintOptions Options, IReadOnlyList<PrintSheet> Sheets);

/// <summary>Shared physical geometry for preview and spooler, in 1/96 inch.</summary>
internal static class PrintLayout
{
    internal const double Mm = 96 / 25.4;
    private const int MaxSheets = 10000;

    internal static int[] SelectPages(string range, int count, PrintPageFilter filter = PrintPageFilter.All, bool reverse = false)
    {
        if (count < 1) throw new ArgumentException("Документ не содержит страниц.");
        var pages = new List<int>();
        var seen = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(range)) range = $"1-{count}";
        foreach (var part in range.Replace(';', ',').Split(','))
        {
            var ends = part.Trim().Split('-');
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var from) ||
                from < 1 || from > count) throw new ArgumentException($"Введите номера страниц от 1 до {count}, например: 1, 3-5.");
            var to = from;
            if (ends.Length == 2 && (!int.TryParse(ends[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < from || to > count))
                throw new ArgumentException($"Некорректный диапазон «{part}».");
            for (var p = from; p <= to; p++)
                if (seen.Add(p) && (filter == PrintPageFilter.All || (p % 2 == 1) == (filter == PrintPageFilter.Odd))) pages.Add(p - 1);
        }
        if (pages.Count == 0) throw new ArgumentException("После отбора не осталось страниц для печати.");
        if (reverse) pages.Reverse();
        return pages.ToArray();
    }

    internal static PrintPlan Create(IReadOnlyList<PdfPageInfo> info, int[] pages, Size paper, Rect area, PrintOptions options)
    {
        if (!Finite(paper.Width) || !Finite(paper.Height) || !Finite(area.Width) || !Finite(area.Height) ||
            !double.IsFinite(area.X) || !double.IsFinite(area.Y) || !new Rect(paper).Contains(area))
            throw new ArgumentException("Некорректная область печати.");
        if (pages.Length == 0 || pages.Any(p => p < 0 || p >= info.Count)) throw new ArgumentException("Некорректные страницы для печати.");
        if (!double.IsFinite(options.ScalePercent) || options.ScalePercent is < 10 or > 1000) throw new ArgumentException("Масштаб должен быть от 10 до 1000 %.");
        if (options.Columns is < 1 or > 8 || options.Rows is < 1 or > 8) throw new ArgumentException("Сетка должна быть от 1 до 8 строк и столбцов.");
        var content = area;
        if (options.Mode == PrintMode.Poster)
        {
            var m = options.GlueMarginsMm;
            if (new[] { m.Left, m.Top, m.Right, m.Bottom }.Any(v => !double.IsFinite(v) || v < 0 || v > 100))
                throw new ArgumentException("Поля под склейку должны быть от 0 до 100 мм.");
            // Labels remain in a white strip even when the requested glue margin is zero.
            var top = Math.Max(m.Top * Mm, options.Labels ? 20 : 0);
            var bottom = Math.Max(m.Bottom * Mm, options.Labels ? 20 : 0);
            content = new Rect(area.Left + m.Left * Mm, area.Top + top,
                Math.Max(0, area.Width - (m.Left + m.Right) * Mm), Math.Max(0, area.Height - top - bottom));
            if (content.Width < 10 * Mm || content.Height < 10 * Mm) throw new ArgumentException("Поля оставляют слишком мало места для изображения.");
            if (!double.IsFinite(options.OverlapMm) || options.OverlapMm < 0 || options.OverlapMm > 100 ||
                options.OverlapMm * Mm >= Math.Min(content.Width, content.Height)) throw new ArgumentException("Нахлёст должен быть меньше области изображения (0–100 мм).");
        }
        var sheets = new List<PrintSheet>();
        void Add(PrintSheet sheet)
        {
            if (sheets.Count >= MaxSheets) throw new ArgumentException("Получается больше 10 000 листов. Уменьшите масштаб или выберите меньше страниц.");
            sheets.Add(sheet);
        }
        if (options.Mode == PrintMode.Booklet)
        {
            var padded = checked((pages.Length + 3) / 4 * 4);
            int Page(int p) => p < pages.Length ? pages[p] : -1;
            for (var s = 0; s < padded / 4; s++)
            {
                if (options.BookletSide != BookletSide.Back) Side(Page(padded - 1 - 2 * s), Page(2 * s), s, "лицо");
                if (options.BookletSide != BookletSide.Front) Side(Page(2 * s + 1), Page(padded - 2 - 2 * s), s, "оборот");
            }
            void Side(int left, int right, int sheet, string side)
            {
                if (options.RightBinding) (left, right) = (right, left);
                var slots = new List<PrintSlot>();
                for (var c = 0; c < 2; c++)
                {
                    var index = c == 0 ? left : right;
                    if (index >= 0) slots.Add(Fit(info[index], new Rect(area.X + c * area.Width / 2, area.Y, area.Width / 2, area.Height), PrintScaling.Fit, 100, options.AutoRotate));
                }
                Add(new(slots, $"Брошюра · лист {sheet + 1} · {side}"));
            }
        }
        else if (options.Mode == PrintMode.Multiple)
        {
            var perSheet = options.Rows * options.Columns;
            for (var start = 0; start < pages.Length; start += perSheet)
            {
                var slots = new List<PrintSlot>();
                for (var i = 0; i < perSheet && start + i < pages.Length; i++)
                {
                    var vertical = options.PageOrder is PrintPageOrder.Vertical or PrintPageOrder.VerticalReverse;
                    var c = vertical ? i / options.Rows : i % options.Columns;
                    var r = vertical ? i % options.Rows : i / options.Columns;
                    if (options.PageOrder is PrintPageOrder.HorizontalReverse or PrintPageOrder.VerticalReverse) c = options.Columns - 1 - c;
                    var cell = new Rect(area.X + c * area.Width / options.Columns, area.Y + r * area.Height / options.Rows,
                        area.Width / options.Columns, area.Height / options.Rows);
                    var inset = Math.Min(4, Math.Min(cell.Width, cell.Height) / 10);
                    cell.Inflate(-inset, -inset);
                    slots.Add(Fit(info[pages[start + i]], cell, PrintScaling.Fit, 100, options.AutoRotate));
                }
                Add(new(slots, "Страницы " + string.Join(", ", pages.Skip(start).Take(perSheet).Select(p => p + 1))));
            }
        }
        else foreach (var index in pages)
        {
            var page = info[index];
            if (options.Mode == PrintMode.Size)
            {
                Add(new([Fit(page, area, options.Scaling, options.ScalePercent, options.AutoRotate)], $"Страница {index + 1}"));
                continue;
            }
            var width = page.Width * 96 / 72 * options.ScalePercent / 100;
            var height = page.Height * 96 / 72 * options.ScalePercent / 100;
            // Poster axes follow the PDF: the printed row-column coordinates match the assembled canvas.
            var strideX = content.Width - options.OverlapMm * Mm;
            var strideY = content.Height - options.OverlapMm * Mm;
            var colsValue = Math.Max(1, Math.Ceiling((width - content.Width - .001) / strideX) + 1);
            var rowsValue = Math.Max(1, Math.Ceiling((height - content.Height - .001) / strideY) + 1);
            if (!double.IsFinite(colsValue * rowsValue) || colsValue * rowsValue > MaxSheets)
                throw new ArgumentException("Постер превышает 10 000 листов. Уменьшите масштаб.");
            var cols = (int)colsValue;
            var rows = (int)rowsValue;
            if (cols == 1 && rows == 1 && options.TileOnlyLargePages)
            {
                Add(new([Fit(page, content, PrintScaling.Custom, options.ScalePercent, false)], $"Страница {index + 1} · 1-1", index, 1, 1, 1, 1));
                continue;
            }
            for (var r = 0; r < rows; r++) for (var c = 0; c < cols; c++)
            {
                var slot = new PrintSlot(index, new Size(width, height), new Point(content.X - c * strideX + width / 2, content.Y - r * strideY + height / 2), false, content);
                Add(new([slot], $"Страница {index + 1} · {r + 1}-{c + 1} · сетка {rows}×{cols}", index, r + 1, c + 1, rows, cols));
            }
        }
        return new(paper, area, content, options, sheets);
    }

    internal static PrintSlot Fit(PdfPageInfo page, Rect area, PrintScaling scaling, double percent, bool autoRotate)
    {
        var width = page.Width * 96 / 72;
        var height = page.Height * 96 / 72;
        var rotated = autoRotate && Math.Abs(width - height) > 1 && (width > height) != (area.Width > area.Height);
        var fit = Math.Min(area.Width / (rotated ? height : width), area.Height / (rotated ? width : height));
        var scale = scaling switch { PrintScaling.ActualSize => 1, PrintScaling.Custom => percent / 100, PrintScaling.ShrinkOversized => Math.Min(1, fit), _ => fit };
        return new(page.Index, new Size(width * scale, height * scale), new Point(area.X + area.Width / 2, area.Y + area.Height / 2), rotated, area);
    }

    private static bool Finite(double value) => double.IsFinite(value) && value > 0;
}
