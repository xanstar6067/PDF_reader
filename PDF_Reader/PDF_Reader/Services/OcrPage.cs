using System.IO;
using System.Windows;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

/// <summary>Immutable, memory-only recognition result in normalized display coordinates.</summary>
internal sealed class OcrPage
{
    private readonly PdfTextMap _map;
    public string Text => _map.Text;
    public int ByteCount => _map.ByteCount;

    internal OcrPage(byte[] bytes)
    {
        if (bytes.Length == 0) { _map = new("", []); return; }
        using var source = PdfDocument.FromBytes(bytes);
        if (source.PageCount != 1) throw new InvalidDataException("Некорректный результат OCR.");
        _map = source.GetSelectionMap(0);
    }

    public TextSelection Select(Point from, Point to)
    {
        return _map.Select(from, to);
    }
}
