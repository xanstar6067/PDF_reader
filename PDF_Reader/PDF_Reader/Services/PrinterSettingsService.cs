using System.Printing;
using System.Printing.Interop;
using System.Windows;
using System.Windows.Interop;
using PDF_Reader.Native;

namespace PDF_Reader.Services;

internal sealed record PrinterList(string[] Names, string? Default);
internal sealed record PaperChoice(string Label, PageMediaSize Media)
{
    public override string ToString() => Label;
}
internal sealed record PrinterProfile(PrintTicket Ticket, IReadOnlyList<PaperChoice> Papers, IReadOnlyList<Duplexing> Duplex, int MaxCopies);
internal sealed record PrinterGeometry(PrintTicket Ticket, Size Paper, Rect Area, string? Warning);

internal static class PrinterSettingsService
{
    internal static PrintQueue OpenQueue(LocalPrintServer server, string name) => server.GetPrintQueue(name);

    internal static PrinterList List()
    {
        using var server = new LocalPrintServer();
        using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
        var names = new List<string>();
        foreach (var queue in queues) { using (queue) names.Add(queue.FullName); }
        string? defaultName = null;
        try { using var queue = LocalPrintServer.GetDefaultPrintQueue(); defaultName = queue.FullName; }
        catch (PrintSystemException) { }
        return new(names.Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToArray(), defaultName);
    }

    internal static PrinterProfile Load(string name)
    {
        using var server = new LocalPrintServer();
        using var queue = OpenQueue(server, name);
        var ticket = (queue.UserPrintTicket ?? queue.DefaultPrintTicket ?? new PrintTicket()).Clone();
        var caps = queue.GetPrintCapabilities(ticket);
        var sizes = caps.PageMediaSizeCapability.Where(p => p.Width > 0 && p.Height > 0).ToList();
        if (sizes.Count == 0 && ticket.PageMediaSize is { Width: > 0, Height: > 0 } media) sizes.Add(media);
        if (sizes.Count == 0) sizes.Add(new PageMediaSize(PageMediaSizeName.ISOA4, 210 * PrintLayout.Mm, 297 * PrintLayout.Mm));
        return new(ticket, sizes.Select(p => new PaperChoice(PaperLabel(p), p)).ToArray(), caps.DuplexingCapability.ToArray(), caps.MaxCopyCount ?? 999);
    }

    internal static string PaperLabel(PageMediaSize media)
    {
        var name = media.PageMediaSizeName switch
        {
            PageMediaSizeName.ISOA0 => "A0", PageMediaSizeName.ISOA1 => "A1", PageMediaSizeName.ISOA2 => "A2",
            PageMediaSizeName.ISOA3 => "A3", PageMediaSizeName.ISOA4 => "A4", PageMediaSizeName.ISOA5 => "A5",
            PageMediaSizeName.ISOA6 => "A6", PageMediaSizeName.NorthAmericaLetter => "Letter", PageMediaSizeName.NorthAmericaLegal => "Legal",
            _ => media.PageMediaSizeName?.ToString() ?? "Бумага"
        };
        return $"{name} · {media.Width / PrintLayout.Mm:0.#} × {media.Height / PrintLayout.Mm:0.#} мм";
    }

    internal static PrinterGeometry Resolve(string name, PrintTicket requested)
    {
        using var server = new LocalPrintServer();
        using var queue = OpenQueue(server, name);
        var ticket = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket ?? new PrintTicket(), requested).ValidatedPrintTicket;
        var caps = queue.GetPrintCapabilities(ticket);
        var media = ticket.PageMediaSize ?? requested.PageMediaSize;
        var width = media?.Width ?? 210 * PrintLayout.Mm;
        var height = media?.Height ?? 297 * PrintLayout.Mm;
        if (ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape) (width, height) = (height, width);
        var paper = new Size(caps.OrientedPageMediaWidth ?? width, caps.OrientedPageMediaHeight ?? height);
        var imageable = caps.PageImageableArea;
        var area = imageable is null ? Rect.Empty : new Rect(imageable.OriginWidth, imageable.OriginHeight, imageable.ExtentWidth, imageable.ExtentHeight);
        string? warning = null;
        if (!Valid(paper)) throw new InvalidOperationException("Драйвер сообщил некорректный размер бумаги.");
        if (area.IsEmpty || !double.IsFinite(area.X) || !double.IsFinite(area.Y) || !double.IsFinite(area.Width) || !double.IsFinite(area.Height) || area.Width <= 0 || area.Height <= 0)
        {
            area = new Rect(5 * PrintLayout.Mm, 5 * PrintLayout.Mm, Math.Max(1, paper.Width - 10 * PrintLayout.Mm), Math.Max(1, paper.Height - 10 * PrintLayout.Mm));
            warning = "Драйвер не сообщил поля. Используются поля 5 мм; проверьте их в свойствах принтера.";
        }
        area.Intersect(new Rect(paper));
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) throw new InvalidOperationException("У принтера отсутствует доступная область печати.");
        if (requested.PageOrientation != ticket.PageOrientation || requested.PageMediaSize?.PageMediaSizeName != ticket.PageMediaSize?.PageMediaSizeName)
            warning = "Драйвер изменил формат или ориентацию. Предпросмотр показывает принятые принтером настройки.";
        return new(ticket, paper, area, warning);
    }

    internal static PrintTicket? ShowProperties(Window owner, string name, PrintTicket ticket)
    {
        using var converter = new PrintTicketConverter(name, 1);
        var input = converter.ConvertPrintTicketToDevMode(ticket, BaseDevModeType.UserDefault);
        var output = PrinterProperties.Show(new WindowInteropHelper(owner).Handle, name, input);
        return output is null ? null : converter.ConvertDevModeToPrintTicket(output);
    }

    private static bool Valid(Size size) => double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;
}
