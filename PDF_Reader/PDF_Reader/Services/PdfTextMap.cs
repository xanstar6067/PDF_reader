using System.Windows;
using PDF_Reader.Models;

namespace PDF_Reader.Services;

/// <summary>Managed glyph geometry for responsive caret-based selection, including whitespace endpoints.</summary>
internal sealed class PdfTextMap(string text, PdfTextMap.Glyph[] glyphs, bool vertical = false, bool forward = true)
{
    internal readonly record struct Glyph(int Start, int Length, Rect Bounds);
    public string Text { get; } = text;
    public int ByteCount => Text.Length * 2 + glyphs.Length * 48;
    private sealed record Line(int First, int Last, Rect Bounds);
    private readonly Line[] _lines = BuildLines(glyphs, vertical);

    public TextSelection Select(Point from, Point to)
    {
        if (glyphs.Length == 0) return new("", []);
        var first = Nearest(from);
        var last = Nearest(to);
        // Don't borrow distant native text when the user starts on an image in a mixed page.
        // Either endpoint may be the anchor: reversing the same gesture must select the same range.
        if (!NearText(from, glyphs[first].Bounds) && !NearText(to, glyphs[last].Bounds)) return new("", []);
        var a = Caret(first, from); var b = Caret(last, to);
        var start = Math.Min(a, b); var end = Math.Max(a, b);
        if (start == end) return new("", []);
        var rectangles = new List<Rect>();
        foreach (var glyph in glyphs)
        {
            if (glyph.Start < start || glyph.Start >= end) continue;
            var rect = glyph.Bounds;
            if (rectangles.Count > 0 && SameLine(rectangles[^1], rect)) rectangles[^1] = Rect.Union(rectangles[^1], rect);
            else rectangles.Add(rect);
        }
        return new(Text[start..end], rectangles);
    }

    private int Nearest(Point point)
    {
        var line = _lines[0]; var lineScore = double.PositiveInfinity;
        foreach (var candidateLine in _lines)
        {
            var r = candidateLine.Bounds;
            var dx = Distance(point.X, r.Left, r.Right); var dy = Distance(point.Y, r.Top, r.Bottom);
            var candidate = vertical ? dx * 1000 + dy : dy * 1000 + dx;
            if (candidate < lineScore) { lineScore = candidate; line = candidateLine; }
        }
        var best = line.First; var score = double.PositiveInfinity;
        for (var i = line.First; i <= line.Last; i++)
        {
            var r = glyphs[i].Bounds;
            var dx = Distance(point.X, r.Left, r.Right); var dy = Distance(point.Y, r.Top, r.Bottom);
            var candidate = vertical ? dy : dx;
            if (candidate < score) { score = candidate; best = i; }
        }
        return best;
    }

    private static Line[] BuildLines(Glyph[] glyphs, bool vertical)
    {
        var result = new List<Line>();
        for (var i = 0; i < glyphs.Length; i++)
        {
            var r = glyphs[i].Bounds;
            if (result.Count > 0)
            {
                var line = result[^1]; var a = line.Bounds;
                var overlap = vertical ? Math.Min(a.Right, r.Right) - Math.Max(a.Left, r.Left) : Math.Min(a.Bottom, r.Bottom) - Math.Max(a.Top, r.Top);
                var height = vertical ? Math.Min(a.Width, r.Width) : Math.Min(a.Height, r.Height);
                var gap = vertical ? Math.Max(a.Top - r.Bottom, r.Top - a.Bottom) : Math.Max(a.Left - r.Right, r.Left - a.Right);
                if (overlap > height * .2 && gap < Math.Max(.02, height * 4))
                { result[^1] = new(line.First, i, Rect.Union(a, r)); continue; }
            }
            result.Add(new(i, i, r));
        }
        return result.ToArray();
    }

    private int Caret(int index, Point point)
    {
        var glyph = glyphs[index]; var r = glyph.Bounds;
        var after = vertical ? point.Y > (r.Top + r.Bottom) / 2 : point.X > (r.Left + r.Right) / 2;
        return after == forward ? glyph.Start + glyph.Length : glyph.Start;
    }

    private bool NearText(Point point, Rect bounds)
    {
        var block = vertical ? Distance(point.X, bounds.Left, bounds.Right) : Distance(point.Y, bounds.Top, bounds.Bottom);
        var inline = vertical ? Distance(point.Y, bounds.Top, bounds.Bottom) : Distance(point.X, bounds.Left, bounds.Right);
        return block <= Math.Max(.008, (vertical ? bounds.Width : bounds.Height) * 1.2) && inline <= .2;
    }

    private bool SameLine(Rect a, Rect b)
    {
        var overlap = vertical ? Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) : Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var height = vertical ? Math.Min(a.Width, b.Width) : Math.Min(a.Height, b.Height);
        var gap = vertical ? Math.Max(a.Top - b.Bottom, b.Top - a.Bottom) : Math.Max(a.Left - b.Right, b.Left - a.Right);
        return overlap >= height * .5 && gap <= (vertical ? Math.Max(a.Width, b.Width) : Math.Max(a.Height, b.Height)) * 1.5;
    }

    private static double Distance(double value, double min, double max) => Math.Max(0, Math.Max(min - value, value - max));
}
