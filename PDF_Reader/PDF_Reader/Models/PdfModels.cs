using System.Windows;
using System.Windows.Media;

namespace PDF_Reader.Models;

public enum DocumentTool { Select, EditText, AddText, Highlight, Underline, StrikeOut, Note, Rectangle, Ellipse, Ink }

/// <summary>PDF annotation subtypes created by PaperDesk.</summary>
public static class AnnotationType
{
    public const int Note = 1, Square = 5, Circle = 6, Highlight = 9, Underline = 10, Squiggly = 11, StrikeOut = 12, Ink = 15;
}

public enum PrintScaling { Fit, ActualSize, ShrinkOversized, Custom }

public record PdfPageInfo(int Index, double Width, double Height, string Label)
{
    public int Number => Index + 1;
}

public record OutlineNode(string Title, int PageIndex, IReadOnlyList<OutlineNode> Children);

// Rectangles and points exposed to the UI use normalized display coordinates, top-left origin.
public record SearchMatch(int PageIndex, int Start, int Length, string Preview, IReadOnlyList<Rect> Rectangles)
{
    public int PageNumber => PageIndex + 1;
}

public record TextSelection(string Text, IReadOnlyList<Rect> Rectangles);

public record EditableText(int PageIndex, int ObjectIndex, string Text, float FontSize, long Revision, Rect Bounds);

/// <summary>A review annotation. <see cref="Bounds"/> uses normalized display coordinates like other UI geometry.</summary>
public record PdfComment(int PageIndex, int AnnotationIndex, string Text, string Author, int Type, Rect Bounds, Color Color)
{
    // PDF annotation subtypes that represent review markup (links, popups and widgets are excluded).
    public static readonly IReadOnlySet<int> MarkupTypes = new HashSet<int> { 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 18, 28 };
    // PDFium regenerates the appearance of these subtypes from their colour (its note icon is always yellow).
    private static readonly HashSet<int> Regenerated = [5, 6, 9, 10, 11, 12, 15];
    // Their geometry is fully described by the rectangle (and ink strokes), so moving keeps the data consistent.
    private static readonly HashSet<int> Movable = [1, 3, 5, 6, 13, 14, 15, 17];

    public int PageNumber => PageIndex + 1;
    public bool CanMove => Movable.Contains(Type);
    public bool CanResize => Type is 5 or 6;
    public bool CanRecolor => Regenerated.Contains(Type);
    public bool CanEditText => Type != 3;

    public string Kind => Type switch
    {
        1 => "Заметка",
        3 => "Надпись",
        4 => "Линия",
        5 => "Прямоугольник",
        6 => "Овал",
        7 => "Многоугольник",
        8 => "Ломаная",
        9 => "Маркер",
        10 => "Подчёркивание",
        11 => "Волнистая линия",
        12 => "Зачёркивание",
        13 => "Штамп",
        14 => "Вставка",
        15 => "Рисунок",
        17 => "Вложение",
        18 => "Звук",
        28 => "Скрытие",
        _ => "Аннотация"
    };
}

public record PdfLink(int PageIndex, string? Uri);

public record PdfFormField(int PageIndex, int AnnotationIndex, string Name, string Value, int Type, bool ReadOnly, bool Checked, Rect Bounds, long Revision);

public record DocumentInfo(string Title, string Author, string Subject, string Creator, int Pages, bool IsEncrypted, int Signatures, int FormType, uint Permissions)
{
    public bool CanPrint => (Permissions & 4) != 0;
    public bool CanEdit => (Permissions & 8) != 0;
    public bool CanCopy => (Permissions & 16) != 0;
    public bool CanAnnotate => (Permissions & 32) != 0;
    public bool CanAssemble => (Permissions & 1024) != 0;
}

/// <summary>Bytes and identity of a modified document, written to disk for crash recovery.</summary>
public record RecoverySnapshot(byte[] Bytes, string? OriginalPath, byte[]? DiskHash, long Revision);

public sealed class PdfPasswordException : Exception
{
    public PdfPasswordException() : base("Документ защищён паролем. Введите пароль для открытия.") { }
}
