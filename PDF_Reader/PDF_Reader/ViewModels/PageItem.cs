using System.Windows;
using PDF_Reader.Models;
using PDF_Reader.Services;

namespace PDF_Reader.ViewModels;

public sealed class PageItem(PdfDocument document, PdfPageInfo info, double zoom) : ObservableObject
{
    private double _zoom = zoom;
    private int _version;
    private PageMeasurement? _measurement;
    internal string OcrStatus { get; set; } = "";
    internal long OcrStatusVersion { get; set; }

    public PdfDocument Document { get; } = document;
    public PdfPageInfo Info { get; } = info;
    public int Number => Info.Number;
    public string Label => Info.Label;
    public double Zoom { get => _zoom; set { if (Set(ref _zoom, value)) { Notify(nameof(Width)); Notify(nameof(Height)); } } }
    public double Width => Info.Width * 96 / 72 * Zoom;
    public double Height => Info.Height * 96 / 72 * Zoom;
    public double ThumbnailHeight => 158 * Info.Height / Info.Width;
    public PageMeasurement? Measurement { get => _measurement; private set => Set(ref _measurement, value); }
    internal double MeasurementScale { get; private set; } = 1;

    internal double MeasurePageDistance(Point start, Point end)
    {
        var distance = end - start;
        return new Vector(distance.X * Info.Width, distance.Y * Info.Height).Length * 25.4 / 72;
    }

    internal void Measure(Point start, Point end) =>
        Measurement = new(start, end, MeasurePageDistance(start, end) * MeasurementScale);

    internal void SetMeasurementScale(double scale)
    {
        MeasurementScale = scale;
        if (Measurement is { } measurement) Measure(measurement.Start, measurement.End);
    }

    /// <summary>Changes whenever the page content may differ; visible page views re-render, others render on demand.</summary>
    public int Version => _version;

    public void Invalidate()
    {
        Measurement = null;
        MeasurementScale = 1;
        _version++;
        Notify(nameof(Version));
    }
}
