using PDF_Reader.Models;
using PDF_Reader.Services;

namespace PDF_Reader.ViewModels;

public sealed class PageItem(PdfDocument document, PdfPageInfo info, double zoom) : ObservableObject
{
    private double _zoom = zoom;
    private int _version;

    public PdfDocument Document { get; } = document;
    public PdfPageInfo Info { get; } = info;
    public int Number => Info.Number;
    public string Label => Info.Label;
    public double Zoom { get => _zoom; set { if (Set(ref _zoom, value)) { Notify(nameof(Width)); Notify(nameof(Height)); } } }
    public double Width => Info.Width * 96 / 72 * Zoom;
    public double Height => Info.Height * 96 / 72 * Zoom;
    public double ThumbnailHeight => 158 * Info.Height / Info.Width;

    /// <summary>Changes whenever the page content may differ; visible page views re-render, others render on demand.</summary>
    public int Version => _version;

    public void Invalidate()
    {
        _version++;
        Notify(nameof(Version));
    }
}
