using System.IO;
using PDF_Reader.Services;

namespace PDF_Reader.ViewModels;

public sealed class DocumentTab(PdfDocument document) : ObservableObject, IDisposable
{
    public PdfDocument Document { get; } = document;
    public int LastPage { get; set; } = 1;
    public string Title => (Document.FilePath is null ? "Новый документ" : System.IO.Path.GetFileName(Document.FilePath)) + (Document.IsDirty ? " •" : "");
    public string? Path => Document.FilePath;
    public void Refresh() { Notify(nameof(Title)); Notify(nameof(Path)); }
    public void Dispose() => Document.Dispose();
}
