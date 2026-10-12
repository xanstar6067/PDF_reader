using System.Windows;

namespace PDF_Reader.Models;

public enum PrintMode { Size, Poster, Multiple, Booklet }
public enum PrintOrientation { Auto, Portrait, Landscape }
public enum PrintPageFilter { All, Odd, Even }
public enum PrintPageOrder { Horizontal, Vertical, HorizontalReverse, VerticalReverse }
public enum BookletSide { Both, Front, Back }

/// <summary>Application layout settings; distances are millimetres, scales are percentages.</summary>
public sealed record PrintOptions
{
    public PrintMode Mode { get; init; }
    public PrintScaling Scaling { get; init; } = PrintScaling.Fit;
    public double ScalePercent { get; init; } = 100;
    public bool AutoRotate { get; init; } = true;
    public double OverlapMm { get; init; } = 5;
    public Thickness GlueMarginsMm { get; init; } = new(8);
    public bool CutMarks { get; init; } = true;
    public bool Labels { get; init; } = true;
    public bool TileOnlyLargePages { get; init; } = true;
    public int Columns { get; init; } = 2;
    public int Rows { get; init; } = 2;
    public PrintPageOrder PageOrder { get; init; }
    public bool PageBorder { get; init; }
    public BookletSide BookletSide { get; init; }
    public bool RightBinding { get; init; }
    public bool Grayscale { get; init; }
}
