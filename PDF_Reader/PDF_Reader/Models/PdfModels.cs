using System.Windows;

namespace PDF_Reader.Models;

public enum DocumentTool { Select, EditText, AddText, Highlight, Note }
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
public record PdfComment(int PageIndex, int AnnotationIndex, string Text, string Author, int Type)
{
    public int PageNumber => PageIndex + 1;
    public string Kind => Type == 9 ? "Маркер" : "Заметка";
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
public sealed class PdfPasswordException : Exception
{
    public PdfPasswordException() : base("Документ защищён паролем. Введите пароль для открытия.") { }
}
