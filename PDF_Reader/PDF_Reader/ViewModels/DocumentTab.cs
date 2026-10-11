using PDF_Reader.Services;

namespace PDF_Reader.ViewModels;

public sealed class DocumentTab(PdfDocument document, string recoveryId) : ObservableObject, IDisposable
{
    public PdfDocument Document { get; } = document;
    public int LastPage { get; set; } = 1;

    /// <summary>Name of this tab's autosaved copy in the recovery folder.</summary>
    public string RecoveryId { get; } = recoveryId;

    /// <summary>Document revision stored in the recovery copy; -1 when there is no copy.</summary>
    public long AutosavedRevision { get; set; } = -1;

    public string Title => (Document.FilePath is null ? "Новый документ" : System.IO.Path.GetFileName(Document.FilePath)) + (Document.IsDirty ? " •" : "");
    public string? Path => Document.FilePath;

    public void Refresh()
    {
        Notify(nameof(Title));
        Notify(nameof(Path));
    }

    public void Dispose() => Document.Dispose();
}
