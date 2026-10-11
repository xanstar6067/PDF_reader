using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF_Reader.Models;
using PDF_Reader.Native;

namespace PDF_Reader.Services;

/// <summary>
/// Owns a PDFium document and its input buffer. All native access is serialized by <see cref="Gate"/>.
/// State read by the UI (page count, history, dirty flag) is published to plain fields, so reading it
/// never waits for a long render or save running on another thread.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    internal static readonly object Gate = new();
    private const uint SaveIncremental = 1;
    private const uint SaveRewrite = 2;
    private const long HistoryBudget = 128L * 1024 * 1024;
    internal const int MaxRenderSide = 32_000;
    internal const long MaxRenderPixels = 24_000_000;
    private static readonly Lazy<TrueTypeSubsetter> UnicodeFont = new(() => new TrueTypeSubsetter(
        File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"))));
    private static bool _initialized;

    private nint _handle;
    private GCHandle _input;
    private byte[] _buffer = [];
    // True while the loaded buffer reproduces the current state, so it can be reused instead of serializing.
    private bool _bufferIsCurrent;
    private FormFillSession? _forms;
    private readonly string? _password;
    private readonly LinkedList<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private long _state, _savedState, _nextState;
    private byte[]? _diskHash;

    private volatile int _pageCount;
    private volatile bool _canUndo, _canRedo, _isDirty;
    private long _revision;

    private record Snapshot(byte[] Bytes, long State);

    public string? FilePath { get; private set; }
    public long Revision => Interlocked.Read(ref _revision);
    public bool IsDirty => _isDirty;
    public bool CanUndo => _canUndo;
    public bool CanRedo => _canRedo;
    public int PageCount => _pageCount;

    /// <summary>
    /// Signed documents are saved as incremental updates: the signed revision stays byte-identical,
    /// so existing signatures remain verifiable and later changes are reported by the validator.
    /// </summary>
    public bool SavesIncrementally { get; }

    private PdfDocument(byte[] bytes, string? password)
    {
        _password = password;
        lock (Gate)
        {
            EnsureLibrary();
            LoadBuffer(bytes);
            SavesIncrementally = Pdfium.FPDF_GetSignatureCount(_handle) > 0;
            Publish();
        }
    }

    public static PdfDocument Open(string path, string? password = null)
    {
        var bytes = File.ReadAllBytes(path);
        var document = new PdfDocument(bytes, password) { FilePath = Path.GetFullPath(path), _diskHash = SHA256.HashData(bytes) };
        if (document.PageCount > 0) return document;
        document.Dispose();
        throw new InvalidDataException("В PDF нет страниц для просмотра.");
    }

    public static PdfDocument FromBytes(byte[] bytes, string? password = null) => new(bytes, password);

    /// <summary>Reopens an autosaved copy as a modified document bound to its original file.</summary>
    public static PdfDocument Restore(byte[] bytes, string? password, string? originalPath, byte[]? diskHash)
    {
        var document = new PdfDocument(bytes, password) { FilePath = originalPath, _diskHash = diskHash };
        lock (Gate)
        {
            document._state = ++document._nextState;
            document.Publish();
        }
        return document;
    }

    public static PdfDocument Create()
    {
        lock (Gate)
        {
            EnsureLibrary();
            var handle = Pdfium.FPDF_CreateNewDocument();
            if (handle == 0) throw new InvalidOperationException("Не удалось создать PDF.");
            try
            {
                var page = Pdfium.FPDFPage_New(handle, 0, 595.28, 841.89);
                if (page == 0) throw new InvalidOperationException("Не удалось создать страницу.");
                Pdfium.FPDF_ClosePage(page);
                var document = new PdfDocument(Serialize(handle, SaveRewrite), null);
                document._state = ++document._nextState;
                document.Publish();
                return document;
            }
            finally
            {
                Pdfium.FPDF_CloseDocument(handle);
            }
        }
    }

    /// <summary>Independent copy of the current state, e.g. for printing while editing continues.</summary>
    public PdfDocument CreateCopy()
    {
        lock (Gate)
        {
            EnsureOpen();
            return new PdfDocument(CurrentBytes(), _password) { FilePath = FilePath };
        }
    }

    private static void EnsureLibrary()
    {
        if (_initialized) return;
        Pdfium.FPDF_InitLibrary();
        _initialized = true;
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_handle == 0, this);

    private int NativePageCount => Pdfium.FPDF_GetPageCount(_handle);

    private void Publish()
    {
        _pageCount = _handle == 0 ? 0 : NativePageCount;
        _canUndo = _undo.Count > 0;
        _canRedo = _redo.Count > 0;
        _isDirty = _state != _savedState;
    }

    private void LoadBuffer(byte[] bytes)
    {
        // Open the new handle before releasing the old one; a failed reload preserves the old document.
        var input = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var handle = Pdfium.FPDF_LoadMemDocument64(input.AddrOfPinnedObject(), (nuint)bytes.Length, _password);
        if (handle == 0)
        {
            var error = Pdfium.FPDF_GetLastError();
            input.Free();
            if (error == 4) throw new PdfPasswordException();
            throw new InvalidDataException($"Не удалось прочитать PDF (код {error}). Файл повреждён или имеет неподдерживаемый формат.");
        }
        FormFillSession? forms;
        try
        {
            forms = Pdfium.FPDF_GetFormType(handle) == 1 ? new FormFillSession(handle) : null;
        }
        catch
        {
            Pdfium.FPDF_CloseDocument(handle);
            input.Free();
            throw;
        }
        CloseHandle();
        _handle = handle;
        _input = input;
        _forms = forms;
        _buffer = bytes;
        _bufferIsCurrent = true;
    }

    private void CloseHandle()
    {
        _forms?.Dispose();
        _forms = null;
        if (_handle != 0)
        {
            Pdfium.FPDF_CloseDocument(_handle);
            _handle = 0;
        }
        if (_input.IsAllocated) _input.Free();
    }

    public void Dispose()
    {
        lock (Gate)
        {
            CloseHandle();
            _undo.Clear();
            _redo.Clear();
            _buffer = [];
            Publish();
        }
    }

    /// <summary>Loads a page for the duration of <paramref name="action"/>. Form widgets are attached only when needed.</summary>
    private T OnPage<T>(int index, Func<nint, T> action, bool forms = false)
    {
        EnsureOpen();
        if (index < 0 || index >= NativePageCount) throw new ArgumentOutOfRangeException(nameof(index));
        var page = Pdfium.FPDF_LoadPage(_handle, index);
        if (page == 0) throw new InvalidDataException($"Не удалось загрузить страницу {index + 1}.");
        var session = forms ? _forms : null;
        try
        {
            session?.AfterLoad(index, page);
            return action(page);
        }
        finally
        {
            session?.BeforeClose(index, page);
            Pdfium.FPDF_ClosePage(page);
        }
    }

    private static T OnText<T>(nint page, Func<nint, T> action)
    {
        var text = Pdfium.FPDFText_LoadPage(page);
        if (text == 0) throw new InvalidDataException("Не удалось извлечь текст страницы.");
        try
        {
            return action(text);
        }
        finally
        {
            Pdfium.FPDFText_ClosePage(text);
        }
    }

    private static string ReadString(Func<byte[]?, uint, uint> getter)
    {
        var size = getter(null, 0);
        if (size < 2 || size > 16 * 1024 * 1024) return "";
        var buffer = new byte[size];
        getter(buffer, size);
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public DocumentInfo GetInfo()
    {
        lock (Gate)
        {
            EnsureOpen();
            string Meta(string name) => ReadString((b, n) => Pdfium.FPDF_GetMetaText(_handle, name, b, n));
            return new(Meta("Title"), Meta("Author"), Meta("Subject"), Meta("Creator"), NativePageCount,
                Pdfium.FPDF_GetSecurityHandlerRevision(_handle) >= 0, Pdfium.FPDF_GetSignatureCount(_handle),
                Pdfium.FPDF_GetFormType(_handle), GetPermissions());
        }
    }

    public IReadOnlyList<PdfPageInfo> GetPages()
    {
        lock (Gate)
        {
            EnsureOpen();
            var count = NativePageCount;
            var result = new List<PdfPageInfo>(count);
            for (var i = 0; i < count; i++)
            {
                if (Pdfium.FPDF_GetPageSizeByIndexF(_handle, i, out var size) == 0 ||
                    !float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
                    throw new InvalidDataException($"Некорректный размер страницы {i + 1}.");
                var label = ReadString((b, n) => Pdfium.FPDF_GetPageLabel(_handle, i, b, n));
                result.Add(new(i, size.Width, size.Height, string.IsNullOrEmpty(label) ? (i + 1).ToString() : label));
            }
            return result;
        }
    }

    /// <summary>Pixel size for a whole-page bitmap: keeps the aspect ratio and scales both sides down at the limits.</summary>
    internal static (int Width, int Height) FitRenderSize(double pageWidth, double pageHeight, int requestedWidth)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
            throw new InvalidDataException("Некорректный размер страницы.");
        double width = Math.Clamp(requestedWidth, 16, MaxRenderSide);
        var height = width * pageHeight / pageWidth;
        var scale = Math.Min(1, Math.Min(MaxRenderSide / Math.Max(width, height), Math.Sqrt(MaxRenderPixels / (width * height))));
        return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }

    public BitmapSource Render(int index, int width, bool printing = false, CancellationToken cancellation = default)
    {
        lock (Gate)
        {
            cancellation.ThrowIfCancellationRequested();
            return OnPage(index, page =>
            {
                var (pixelWidth, pixelHeight) = FitRenderSize(Pdfium.FPDF_GetPageWidthF(page), Pdfium.FPDF_GetPageHeightF(page), width);
                return RenderBitmap(page, pixelWidth, pixelHeight, new Int32Rect(0, 0, pixelWidth, pixelHeight), printing, cancellation);
            }, forms: true);
        }
    }

    /// <summary>Renders only <paramref name="region"/> of a page drawn at <paramref name="pageWidth"/>×<paramref name="pageHeight"/> pixels.</summary>
    public BitmapSource RenderRegion(int index, int pageWidth, int pageHeight, Int32Rect region, CancellationToken cancellation = default)
    {
        if (pageWidth is < 1 or > MaxRenderSide || pageHeight is < 1 or > MaxRenderSide)
            throw new ArgumentOutOfRangeException(nameof(pageWidth), "Слишком большой масштаб страницы.");
        var left = Math.Clamp(region.X, 0, pageWidth);
        var top = Math.Clamp(region.Y, 0, pageHeight);
        var right = Math.Clamp(region.X + region.Width, left, pageWidth);
        var bottom = Math.Clamp(region.Y + region.Height, top, pageHeight);
        var clipped = new Int32Rect(left, top, right - left, bottom - top);
        if (clipped.Width == 0 || clipped.Height == 0) throw new ArgumentException("Область страницы пуста.", nameof(region));
        if ((long)clipped.Width * clipped.Height > MaxRenderPixels) throw new ArgumentOutOfRangeException(nameof(region), "Слишком большая область страницы.");
        lock (Gate)
        {
            cancellation.ThrowIfCancellationRequested();
            return OnPage(index, page => RenderBitmap(page, pageWidth, pageHeight, clipped, false, cancellation), forms: true);
        }
    }

    private BitmapSource RenderBitmap(nint page, int pageWidth, int pageHeight, Int32Rect region, bool printing, CancellationToken cancellation)
    {
        var bitmap = Pdfium.FPDFBitmap_Create(region.Width, region.Height, 1);
        if (bitmap == 0) throw new InvalidOperationException("Не удалось выделить память для страницы.");
        try
        {
            var flags = printing ? 0x801 : 0x3;
            Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, region.Width, region.Height, 0xffffffff);
            Pdfium.FPDF_RenderPageBitmap(bitmap, page, -region.X, -region.Y, pageWidth, pageHeight, 0, flags);
            if (_forms is not null) Pdfium.FPDF_FFLDraw(_forms.Handle, bitmap, page, -region.X, -region.Y, pageWidth, pageHeight, 0, flags);
            var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
            var image = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null,
                Pdfium.FPDFBitmap_GetBuffer(bitmap), checked(stride * region.Height), stride);
            image.Freeze();
            cancellation.ThrowIfCancellationRequested();
            return image;
        }
        finally
        {
            Pdfium.FPDFBitmap_Destroy(bitmap);
        }
    }

    private static Point ToPdf(nint page, Point point)
    {
        if (Pdfium.FPDF_DeviceToPage(page, 0, 0, 10000, 10000, 0, (int)Math.Round(point.X * 10000),
                (int)Math.Round(point.Y * 10000), out var x, out var y) == 0)
            throw new InvalidOperationException("Не удалось преобразовать координаты страницы.");
        return new(x, y);
    }

    private static Point ToDisplay(nint page, double x, double y)
    {
        if (Pdfium.FPDF_PageToDevice(page, 0, 0, 10000, 10000, 0, x, y, out var dx, out var dy) == 0)
            throw new InvalidOperationException("Не удалось преобразовать координаты страницы.");
        return new(dx / 10000.0, dy / 10000.0);
    }

    private static IReadOnlyList<Rect> GetRects(nint page, nint text, int start, int count)
    {
        var result = new List<Rect>();
        var total = Pdfium.FPDFText_CountRects(text, start, count);
        for (var i = 0; i < Math.Min(total, 10000); i++)
            if (Pdfium.FPDFText_GetRect(text, i, out var l, out var t, out var r, out var b) != 0)
                result.Add(new(ToDisplay(page, l, t), ToDisplay(page, r, b)));
        return result;
    }

    private static string GetText(nint text, int start, int count)
    {
        if (count <= 0) return "";
        var buffer = new byte[checked((count + 1) * 2)];
        var length = Pdfium.FPDFText_GetText(text, start, count, buffer);
        return length > 0 ? Encoding.Unicode.GetString(buffer, 0, Math.Min(buffer.Length, length * 2)).TrimEnd('\0') : "";
    }

    public string ExtractText(int index)
    {
        lock (Gate)
        {
            RequirePermission(16, "Копирование текста запрещено владельцем PDF.");
            return OnPage(index, p => OnText(p, t => GetText(t, 0, Pdfium.FPDFText_CountChars(t))));
        }
    }

    public TextSelection SelectText(int index, Point from, Point to)
    {
        lock (Gate)
        {
            RequirePermission(16, "Копирование текста запрещено владельцем PDF.");
            return OnPage(index, p => OnText(p, t =>
            {
                var a = ToPdf(p, from);
                var b = ToPdf(p, to);
                var first = Pdfium.FPDFText_GetCharIndexAtPos(t, a.X, a.Y, 12, 12);
                var last = Pdfium.FPDFText_GetCharIndexAtPos(t, b.X, b.Y, 12, 12);
                if (first < 0 || last < 0) return new TextSelection("", []);
                var start = Math.Min(first, last);
                var count = Math.Abs(last - first) + 1;
                return new TextSelection(GetText(t, start, count), GetRects(p, t, start, count));
            }));
        }
    }

    public IReadOnlyList<SearchMatch> Search(string query, bool matchCase, bool wholeWord, CancellationToken cancellation)
    {
        var result = new List<SearchMatch>();
        if (string.IsNullOrWhiteSpace(query)) return result;
        // Release the lock between pages so navigation and rendering remain responsive.
        var revision = Revision;
        for (var index = 0; index < PageCount; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (Revision != revision) throw new OperationCanceledException("Документ изменён во время поиска.");
                var pageIndex = index;
                OnPage(index, p => OnText(p, t =>
                {
                    var handle = Pdfium.FPDFText_FindStart(t, query, (uint)((matchCase ? 1 : 0) | (wholeWord ? 2 : 0)), 0);
                    if (handle == 0) return 0;
                    try
                    {
                        while (Pdfium.FPDFText_FindNext(handle) != 0 && result.Count < 10000)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            var start = Pdfium.FPDFText_GetSchResultIndex(handle);
                            var count = Pdfium.FPDFText_GetSchCount(handle);
                            var previewStart = Math.Max(0, start - 35);
                            var previewLength = Math.Min(Pdfium.FPDFText_CountChars(t) - previewStart, count + 85);
                            var preview = GetText(t, previewStart, previewLength).Replace("\r", " ").Replace("\n", " ");
                            result.Add(new(pageIndex, start, count, preview, GetRects(p, t, start, count)));
                        }
                    }
                    finally
                    {
                        Pdfium.FPDFText_FindClose(handle);
                    }
                    return 0;
                }));
            }
            if (result.Count >= 10000) break;
        }
        return result;
    }

    public IReadOnlyList<OutlineNode> GetOutline()
    {
        lock (Gate)
        {
            EnsureOpen();
            var visited = new HashSet<nint>();
            IReadOnlyList<OutlineNode> Read(nint parent, int depth)
            {
                var nodes = new List<OutlineNode>();
                if (depth > 64) return nodes;
                for (var node = Pdfium.FPDFBookmark_GetFirstChild(_handle, parent); node != 0; node = Pdfium.FPDFBookmark_GetNextSibling(_handle, node))
                {
                    if (visited.Count >= 10000 || !visited.Add(node)) break;
                    var dest = Pdfium.FPDFBookmark_GetDest(_handle, node);
                    if (dest == 0) dest = Pdfium.FPDFAction_GetDest(_handle, Pdfium.FPDFBookmark_GetAction(node));
                    var title = ReadString((b, n) => Pdfium.FPDFBookmark_GetTitle(node, b, n));
                    nodes.Add(new(title, dest == 0 ? -1 : Pdfium.FPDFDest_GetDestPageIndex(_handle, dest), Read(node, depth + 1)));
                }
                return nodes;
            }
            return Read(0, 0);
        }
    }

    public EditableText? HitText(int index, Point point)
    {
        lock (Gate)
        {
            return OnPage(index, p => OnText(p, t =>
            {
                var pdf = ToPdf(p, point);
                var character = Pdfium.FPDFText_GetCharIndexAtPos(t, pdf.X, pdf.Y, 4, 4);
                if (character < 0) return null;
                var obj = Pdfium.FPDFText_GetTextObject(t, character);
                for (var i = 0; i < Pdfium.FPDFPage_CountObjects(p); i++)
                {
                    if (Pdfium.FPDFPage_GetObject(p, i) != obj) continue;
                    var text = ReadString((b, n) => Pdfium.FPDFTextObj_GetText(obj, t, b, n));
                    Pdfium.FPDFTextObj_GetFontSize(obj, out var size);
                    Pdfium.FPDFPageObj_GetBounds(obj, out var l, out var btm, out var r, out var top);
                    return new EditableText(index, i, text, size, Revision, new(ToDisplay(p, l, top), ToDisplay(p, r, btm)));
                }
                throw new NotSupportedException("Этот текст находится внутри составного PDF-объекта. Его редактирование пока не поддерживается. Можно добавить новый текст инструментом «Добавить текст».");
            }));
        }
    }

    public PdfLink? HitLink(int index, Point point)
    {
        lock (Gate)
        {
            return OnPage(index, p =>
            {
                var pdf = ToPdf(p, point);
                var link = Pdfium.FPDFLink_GetLinkAtPoint(p, pdf.X, pdf.Y);
                if (link == 0) return null;
                var dest = Pdfium.FPDFLink_GetDest(_handle, link);
                var action = Pdfium.FPDFLink_GetAction(link);
                if (dest == 0) dest = Pdfium.FPDFAction_GetDest(_handle, action);
                if (dest != 0) return new PdfLink(Pdfium.FPDFDest_GetDestPageIndex(_handle, dest), null);
                var size = Pdfium.FPDFAction_GetURIPath(_handle, action, null, 0);
                if (size == 0 || size > 65536) return null;
                var buffer = new byte[size];
                Pdfium.FPDFAction_GetURIPath(_handle, action, buffer, size);
                return new PdfLink(-1, Encoding.UTF8.GetString(buffer).TrimEnd('\0'));
            });
        }
    }

    public PdfFormField? HitFormField(int index, Point point)
    {
        lock (Gate)
        {
            if (_forms is null) return null;
            return OnPage(index, page =>
            {
                for (var i = 0; i < Pdfium.FPDFPage_GetAnnotCount(page); i++)
                {
                    var annot = Pdfium.FPDFPage_GetAnnot(page, i);
                    if (annot == 0) continue;
                    try
                    {
                        if (Pdfium.FPDFAnnot_GetSubtype(annot) != 20 || Pdfium.FPDFAnnot_GetRect(annot, out var box) == 0) continue;
                        var bounds = new Rect(ToDisplay(page, box.Left, box.Top), ToDisplay(page, box.Right, box.Bottom));
                        if (!bounds.Contains(point)) continue;
                        var form = _forms.Handle;
                        return new PdfFormField(index, i,
                            ReadString((b, n) => Pdfium.FPDFAnnot_GetFormFieldName(form, annot, b, n)),
                            ReadString((b, n) => Pdfium.FPDFAnnot_GetFormFieldValue(form, annot, b, n)),
                            Pdfium.FPDFAnnot_GetFormFieldType(form, annot),
                            (Pdfium.FPDFAnnot_GetFormFieldFlags(form, annot) & 1) != 0,
                            Pdfium.FPDFAnnot_IsChecked(form, annot) != 0, bounds, Revision);
                    }
                    finally
                    {
                        Pdfium.FPDFPage_CloseAnnot(annot);
                    }
                }
                return null;
            }, forms: true);
        }
    }

    public void FillFormField(PdfFormField field, string value)
    {
        if (field.ReadOnly) throw new UnauthorizedAccessException("Поле формы доступно только для чтения.");
        if (field.Type is not (2 or 3 or 4 or 6)) throw new NotSupportedException("Поддерживаются текстовые поля, флажки и переключатели AcroForm.");
        if (value.Contains('\0') || value.Length > 20000) throw new ArgumentException("Недопустимое значение поля.");
        Edit(() => OnPage(field.PageIndex, page =>
        {
            if (_forms is null || field.Revision != Revision) throw new InvalidOperationException("Форма изменилась. Выберите поле заново.");
            var point = ToPdf(page, new Point(field.Bounds.X + field.Bounds.Width / 2, field.Bounds.Y + field.Bounds.Height / 2));
            if (field.Type is 2 or 3)
            {
                Pdfium.FORM_OnLButtonDown(_forms.Handle, page, 0, point.X, point.Y);
                Pdfium.FORM_OnLButtonUp(_forms.Handle, page, 0, point.X, point.Y);
            }
            else
            {
                if (Pdfium.FORM_OnFocus(_forms.Handle, page, 0, point.X, point.Y) == 0 || Pdfium.FORM_SelectAllText(_forms.Handle, page) == 0)
                    throw new InvalidOperationException("Не удалось выделить значение формы.");
                Pdfium.FORM_ReplaceSelection(_forms.Handle, page, value);
            }
            Pdfium.FORM_ForceToKillFocus(_forms.Handle);
            return 0;
        }, forms: true), 256);
    }

    private void RequirePermission(uint bit, string message)
    {
        EnsureOpen();
        if ((GetPermissions() & bit) == 0) throw new UnauthorizedAccessException(message);
    }

    private uint GetPermissions()
    {
        var permissions = Pdfium.FPDF_GetDocPermissions(_handle);
        var revision = Pdfium.FPDF_GetSecurityHandlerRevision(_handle);
        // Revision 2 has no separate fill/assembly bits; reserved bits must not grant operations.
        if (revision is >= 0 and <= 2 && permissions != uint.MaxValue)
        {
            permissions &= ~(256u | 1024u);
            if ((permissions & 8) != 0) permissions |= 1024;
            if ((permissions & 32) != 0) permissions |= 256;
        }
        return permissions;
    }

    private byte[] CurrentBytes() => _bufferIsCurrent ? _buffer : Serialize(_handle, SavesIncrementally ? SaveIncremental : SaveRewrite);

    private void Edit(Action action, uint permission = 8)
    {
        lock (Gate)
        {
            RequirePermission(permission, "Эта операция запрещена настройками защиты PDF.");
            var before = new Snapshot(CurrentBytes(), _state);
            try
            {
                action();
            }
            catch (Exception failure)
            {
                try
                {
                    LoadBuffer(before.Bytes);
                }
                catch (Exception restore)
                {
                    throw new AggregateException("Не удалось восстановить документ после ошибки изменения.", failure, restore);
                }
                finally
                {
                    Interlocked.Increment(ref _revision);
                    Publish();
                }
                throw;
            }
            _bufferIsCurrent = false;
            _undo.AddLast(before);
            _redo.Clear();
            _state = ++_nextState;
            Interlocked.Increment(ref _revision);
            TrimHistory();
            Publish();
        }
    }

    private void TrimHistory()
    {
        // Snapshots may share the loaded buffer; count each array once.
        long size = _undo.Select(s => s.Bytes).Concat(_redo.Select(s => s.Bytes)).Distinct().Sum(b => (long)b.Length);
        while (_undo.Count > 1 && (_undo.Count > 20 || size > HistoryBudget))
        {
            var removed = _undo.First!.Value.Bytes;
            _undo.RemoveFirst();
            if (!_undo.Any(s => ReferenceEquals(s.Bytes, removed)) && !_redo.Any(s => ReferenceEquals(s.Bytes, removed))) size -= removed.Length;
        }
    }

    public void Undo()
    {
        lock (Gate)
        {
            EnsureOpen();
            if (_undo.Last is null) return;
            var target = _undo.Last.Value;
            var current = new Snapshot(CurrentBytes(), _state);
            LoadBuffer(target.Bytes);
            _undo.RemoveLast();
            _redo.Push(current);
            _state = target.State;
            Interlocked.Increment(ref _revision);
            Publish();
        }
    }

    public void Redo()
    {
        lock (Gate)
        {
            EnsureOpen();
            if (_redo.Count == 0) return;
            var target = _redo.Peek();
            var current = new Snapshot(CurrentBytes(), _state);
            LoadBuffer(target.Bytes);
            _redo.Pop();
            _undo.AddLast(current);
            _state = target.State;
            Interlocked.Increment(ref _revision);
            TrimHistory();
            Publish();
        }
    }

    /// <summary>Embeds a Segoe UI subset that contains exactly the characters of <paramref name="text"/>.</summary>
    private nint LoadUnicodeFont(string text)
    {
        var codepoints = text.EnumerateRunes().Select(r => r.Value).Where(c => c is not ('\r' or '\n'));
        var subset = UnicodeFont.Value.Subset(codepoints);
        var font = Pdfium.FPDFText_LoadFont(_handle, subset, (uint)subset.Length, 2, 1);
        if (font == 0) throw new InvalidOperationException("Не удалось встроить шрифт Segoe UI.");
        return font;
    }

    private nint NewText(nint font, string text, float size)
    {
        var obj = Pdfium.FPDFPageObj_CreateTextObj(_handle, font, size);
        if (obj == 0) throw new InvalidOperationException("Не удалось создать текстовый объект.");
        if (Pdfium.FPDFText_SetText(obj, text) != 0) return obj;
        Pdfium.FPDFPageObj_Destroy(obj);
        throw new InvalidOperationException("Не удалось записать текст в PDF.");
    }

    private static void Generate(nint page)
    {
        if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new InvalidOperationException("Не удалось обновить содержимое страницы.");
    }

    private readonly record struct TextStyle(Pdfium.Matrix Matrix, uint[] Fill, uint[]? Stroke, int RenderMode)
    {
        public static TextStyle Read(nint obj)
        {
            if (Pdfium.FPDFPageObj_GetMatrix(obj, out var matrix) == 0) throw new InvalidOperationException("Не удалось прочитать положение текста.");
            // A missing colour must not turn into transparent black.
            uint[] fill = Pdfium.FPDFPageObj_GetFillColor(obj, out var r, out var g, out var b, out var a) != 0 ? [r, g, b, a] : [0, 0, 0, 255];
            uint[]? stroke = Pdfium.FPDFPageObj_GetStrokeColor(obj, out var sr, out var sg, out var sb, out var sa) != 0 ? [sr, sg, sb, sa] : null;
            return new(matrix, fill, stroke, Pdfium.FPDFTextObj_GetTextRenderMode(obj));
        }

        public void Apply(nint obj)
        {
            var matrix = Matrix;
            if (Pdfium.FPDFPageObj_SetMatrix(obj, ref matrix) == 0 || Pdfium.FPDFPageObj_SetFillColor(obj, Fill[0], Fill[1], Fill[2], Fill[3]) == 0)
                throw new InvalidOperationException("Не удалось сохранить стиль текста.");
            if (Stroke is { } s) Pdfium.FPDFPageObj_SetStrokeColor(obj, s[0], s[1], s[2], s[3]);
            if (RenderMode is >= 0 and <= 7) Pdfium.FPDFTextObj_SetTextRenderMode(obj, RenderMode);
        }
    }

    /// <summary>Characters already drawn on the page with <paramref name="font"/>; an embedded subset certainly has their glyphs.</summary>
    private static HashSet<int> CharactersInFont(nint page, nint font) => OnText(page, t =>
    {
        var result = new HashSet<int>();
        for (var i = 0; i < Pdfium.FPDFPage_CountObjects(page); i++)
        {
            var obj = Pdfium.FPDFPage_GetObject(page, i);
            if (Pdfium.FPDFPageObj_GetType(obj) != 1 || Pdfium.FPDFTextObj_GetFont(obj) != font) continue;
            foreach (var rune in ReadString((b, n) => Pdfium.FPDFTextObj_GetText(obj, t, b, n)).EnumerateRunes()) result.Add(rune.Value);
        }
        return result;
    });

    private static bool CanDrawWith(nint font, HashSet<int> known, string text, float size)
    {
        var embedded = Pdfium.FPDFFont_GetIsEmbedded(font) != 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            if (embedded && !known.Contains(rune.Value)) return false;
            if (rune.Value > 0xFFFF || Pdfium.FPDFFont_GetGlyphPath(font, (uint)rune.Value, size) == 0) return false;
        }
        return true;
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public void ReplaceText(EditableText selected, string text, float size)
    {
        text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        ValidateText(text, size, allowEmpty: true);
        Edit(() => OnPage(selected.PageIndex, page =>
        {
            if (selected.Revision != Revision) throw new InvalidOperationException("Документ изменился. Выберите текст заново.");
            var index = selected.ObjectIndex;
            var obj = Pdfium.FPDFPage_GetObject(page, index);
            if (obj == 0 || Pdfium.FPDFPageObj_GetType(obj) != 1) throw new InvalidOperationException("Текстовый объект больше не существует.");
            var style = TextStyle.Read(obj);
            nint replacement = 0;
            if (text.Length > 0)
            {
                // Prefer the original font: it keeps the look of the document. The new object is created
                // before the old one is destroyed, because the font may be owned by that object alone.
                var font = Pdfium.FPDFTextObj_GetFont(obj);
                if (font != 0 && CanDrawWith(font, CharactersInFont(page, font), text, size))
                {
                    replacement = NewText(font, text, size);
                    try
                    {
                        style.Apply(replacement);
                    }
                    catch
                    {
                        Pdfium.FPDFPageObj_Destroy(replacement);
                        throw;
                    }
                    if (Pdfium.FPDFPage_InsertObjectAtIndex(page, replacement, (nuint)index) == 0)
                    {
                        Pdfium.FPDFPageObj_Destroy(replacement);
                        throw new InvalidOperationException("Не удалось вставить замену текста.");
                    }
                    index++;
                }
            }
            if (Pdfium.FPDFPage_RemoveObject(page, obj) == 0) throw new InvalidOperationException("Не удалось удалить исходный текст.");
            Pdfium.FPDFPageObj_Destroy(obj);

            // The encoding round trip catches characters the original font cannot represent.
            if (replacement != 0)
            {
                var stored = OnText(page, t => ReadString((b, n) => Pdfium.FPDFTextObj_GetText(replacement, t, b, n)));
                if (CollapseWhitespace(stored) != CollapseWhitespace(text))
                {
                    if (Pdfium.FPDFPage_RemoveObject(page, replacement) == 0) throw new InvalidOperationException("Не удалось удалить исходный текст.");
                    Pdfium.FPDFPageObj_Destroy(replacement);
                    replacement = 0;
                }
            }
            if (text.Length > 0 && replacement == 0)
            {
                var font = LoadUnicodeFont(text);
                try
                {
                    replacement = NewText(font, text, size);
                    try
                    {
                        style.Apply(replacement);
                        if (Pdfium.FPDFPage_InsertObjectAtIndex(page, replacement, (nuint)selected.ObjectIndex) == 0)
                            throw new InvalidOperationException("Не удалось вставить замену текста.");
                    }
                    catch
                    {
                        Pdfium.FPDFPageObj_Destroy(replacement);
                        throw;
                    }
                }
                finally
                {
                    Pdfium.FPDFFont_Close(font);
                }
            }
            Generate(page);
            return 0;
        }));
    }

    private static void ValidateText(string text, float size, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0') || text.Length > 20000)
            throw new ArgumentException("Введите текст длиной до 20 000 символов.");
        if (!float.IsFinite(size) || size < 4 || size > 200) throw new ArgumentException("Размер шрифта должен быть от 4 до 200 пт.");
    }

    public void AddText(int index, Point point, string text, float size)
    {
        text = text.Replace("\r", "").Replace('\t', ' ');
        ValidateText(text, size);
        Edit(() => OnPage(index, page =>
        {
            var origin = ToPdf(page, point);
            // Follow display orientation even when the PDF page is rotated.
            var right = ToPdf(page, new Point(point.X + .001, point.Y));
            var angle = Math.Atan2(right.Y - origin.Y, right.X - origin.X);
            var font = LoadUnicodeFont(text);
            try
            {
                var line = 0;
                foreach (var content in text.Split('\n'))
                {
                    if (content.Length > 0)
                    {
                        var obj = NewText(font, content, size);
                        var offset = line * size * 1.35;
                        var matrix = new Pdfium.Matrix
                        {
                            A = (float)Math.Cos(angle), B = (float)Math.Sin(angle),
                            C = (float)-Math.Sin(angle), D = (float)Math.Cos(angle),
                            E = (float)(origin.X + Math.Sin(angle) * offset), F = (float)(origin.Y - Math.Cos(angle) * offset)
                        };
                        if (Pdfium.FPDFPageObj_SetMatrix(obj, ref matrix) == 0 || Pdfium.FPDFPage_InsertObject(page, obj) == 0)
                        {
                            Pdfium.FPDFPageObj_Destroy(obj);
                            throw new InvalidOperationException("Не удалось добавить текст.");
                        }
                    }
                    line++;
                }
            }
            finally
            {
                Pdfium.FPDFFont_Close(font);
            }
            Generate(page);
            return 0;
        }));
    }

    public void RotatePage(int index, int direction) => Edit(() => OnPage(index, p =>
    {
        Pdfium.FPDFPage_SetRotation(p, (Pdfium.FPDFPage_GetRotation(p) + direction + 4) % 4);
        return 0;
    }), 1024);

    public void DeletePage(int index) => Edit(() =>
    {
        if (NativePageCount <= 1) throw new InvalidOperationException("В документе должна остаться хотя бы одна страница.");
        if (index < 0 || index >= NativePageCount) throw new ArgumentOutOfRangeException(nameof(index));
        Pdfium.FPDFPage_Delete(_handle, index);
    }, 1024);

    public void MovePage(int from, int to) => Edit(() =>
    {
        var count = NativePageCount;
        if (from < 0 || from >= count || to < 0 || to >= count) throw new ArgumentOutOfRangeException(nameof(to));
        if (Pdfium.FPDF_MovePages(_handle, [from], 1, to) == 0) throw new InvalidOperationException("Не удалось переместить страницу.");
    }, 1024);

    public void InsertBlankPage(int index) => Edit(() =>
    {
        if (index < 0 || index > NativePageCount) throw new ArgumentOutOfRangeException(nameof(index));
        var page = Pdfium.FPDFPage_New(_handle, index, 595.28, 841.89);
        if (page == 0) throw new InvalidOperationException("Не удалось вставить страницу.");
        Pdfium.FPDF_ClosePage(page);
    }, 1024);

    public void ImportPages(PdfDocument source, int index) => Edit(() =>
    {
        source.EnsureOpen();
        if (ReferenceEquals(source, this)) throw new ArgumentException("Выберите другой документ.");
        if (index < 0 || index > NativePageCount) throw new ArgumentOutOfRangeException(nameof(index));
        source.RequirePermission(16, "Копирование страниц исходного PDF запрещено.");
        if (Pdfium.FPDF_ImportPages(_handle, source._handle, null, index) == 0) throw new InvalidOperationException("Не удалось импортировать страницы.");
    }, 1024);

    public byte[] ExportPages(string range)
    {
        lock (Gate)
        {
            RequirePermission(16, "Копирование страниц запрещено владельцем PDF.");
            var export = Pdfium.FPDF_CreateNewDocument();
            if (export == 0) throw new InvalidOperationException("Не удалось создать PDF.");
            try
            {
                if (Pdfium.FPDF_ImportPages(export, _handle, range, 0) == 0) throw new ArgumentException("Укажите страницы, например: 1-3,5.");
                return Serialize(export, SaveRewrite);
            }
            finally
            {
                Pdfium.FPDF_CloseDocument(export);
            }
        }
    }

    public void AddNote(int index, Point point, string content) => AddAnnotation(index, [new Rect(point, new Size(.025, .025))], content, 1);

    public void Highlight(int index, IReadOnlyList<Rect> rectangles) => AddAnnotation(index, rectangles, "", 9);

    private void AddAnnotation(int index, IReadOnlyList<Rect> rectangles, string content, int type)
    {
        if (rectangles.Count == 0) return;
        Edit(() => OnPage(index, page =>
        {
            var annot = Pdfium.FPDFPage_CreateAnnot(page, type);
            if (annot == 0) throw new InvalidOperationException("Не удалось создать аннотацию.");
            try
            {
                var bounds = Rect.Empty;
                foreach (var rect in rectangles)
                {
                    var tl = ToPdf(page, rect.TopLeft);
                    var tr = ToPdf(page, rect.TopRight);
                    var bl = ToPdf(page, rect.BottomLeft);
                    var br = ToPdf(page, rect.BottomRight);
                    foreach (var pt in new[] { tl, tr, bl, br }) bounds.Union(pt);
                    if (type != 9) continue;
                    var quad = new Pdfium.Quad
                    {
                        X1 = (float)tl.X, Y1 = (float)tl.Y, X2 = (float)tr.X, Y2 = (float)tr.Y,
                        X3 = (float)bl.X, Y3 = (float)bl.Y, X4 = (float)br.X, Y4 = (float)br.Y
                    };
                    if (Pdfium.FPDFAnnot_AppendAttachmentPoints(annot, ref quad) == 0) throw new InvalidOperationException("Не удалось выделить текст.");
                }
                var native = new Pdfium.RectF { Left = (float)bounds.Left, Right = (float)bounds.Right, Top = (float)bounds.Bottom, Bottom = (float)bounds.Top };
                if (Pdfium.FPDFAnnot_SetRect(annot, ref native) == 0 || Pdfium.FPDFAnnot_SetColor(annot, 0, 255, 202, 40, type == 9 ? 110u : 255u) == 0)
                    throw new InvalidOperationException("Не удалось настроить аннотацию.");
                Pdfium.FPDFAnnot_SetFlags(annot, 4); // Print with document.
                Pdfium.FPDFAnnot_SetStringValue(annot, "Contents", content);
                Pdfium.FPDFAnnot_SetStringValue(annot, "T", Environment.UserName);
                Pdfium.FPDFAnnot_SetStringValue(annot, "M", "D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "Z");
            }
            finally
            {
                Pdfium.FPDFPage_CloseAnnot(annot);
            }
            return 0;
        }), 32);
    }

    /// <summary>Review annotations of the whole document, or of one page after a page-local change.</summary>
    public IReadOnlyList<PdfComment> GetComments(int? pageIndex = null)
    {
        lock (Gate)
        {
            EnsureOpen();
            var result = new List<PdfComment>();
            var pages = pageIndex is { } single ? [single] : Enumerable.Range(0, NativePageCount);
            foreach (var index in pages)
            {
                OnPage(index, page =>
                {
                    for (var a = 0; a < Pdfium.FPDFPage_GetAnnotCount(page); a++)
                    {
                        var annot = Pdfium.FPDFPage_GetAnnot(page, a);
                        if (annot == 0) continue;
                        try
                        {
                            var type = Pdfium.FPDFAnnot_GetSubtype(annot);
                            if (!PdfComment.MarkupTypes.Contains(type)) continue;
                            result.Add(new(index, a, ReadString((b, n) => Pdfium.FPDFAnnot_GetStringValue(annot, "Contents", b, n)),
                                ReadString((b, n) => Pdfium.FPDFAnnot_GetStringValue(annot, "T", b, n)), type));
                        }
                        finally
                        {
                            Pdfium.FPDFPage_CloseAnnot(annot);
                        }
                    }
                    return 0;
                });
            }
            return result;
        }
    }

    public void DeleteComment(PdfComment comment) => Edit(() => OnPage(comment.PageIndex, page =>
    {
        var annot = Pdfium.FPDFPage_GetAnnot(page, comment.AnnotationIndex);
        if (annot == 0) throw new InvalidOperationException("Аннотация больше не существует.");
        var popupIndex = -1;
        try
        {
            var current = new PdfComment(comment.PageIndex, comment.AnnotationIndex,
                ReadString((b, n) => Pdfium.FPDFAnnot_GetStringValue(annot, "Contents", b, n)),
                ReadString((b, n) => Pdfium.FPDFAnnot_GetStringValue(annot, "T", b, n)), Pdfium.FPDFAnnot_GetSubtype(annot));
            if (current != comment) throw new InvalidOperationException("Аннотации изменились. Выберите аннотацию заново.");
            // The popup window of a markup annotation is a separate annotation; remove it as well.
            var popup = Pdfium.FPDFAnnot_GetLinkedAnnot(annot, "Popup");
            if (popup != 0)
            {
                popupIndex = Pdfium.FPDFPage_GetAnnotIndex(page, popup);
                Pdfium.FPDFPage_CloseAnnot(popup);
            }
        }
        finally
        {
            Pdfium.FPDFPage_CloseAnnot(annot);
        }
        foreach (var index in new[] { comment.AnnotationIndex, popupIndex }.Where(i => i >= 0).Distinct().OrderDescending())
            if (Pdfium.FPDFPage_RemoveAnnot(page, index) == 0) throw new InvalidOperationException("Не удалось удалить аннотацию.");
        return 0;
    }), 32);

    private static byte[] Serialize(nint document, uint flags)
    {
        using var stream = new MemoryStream();
        Exception? failure = null;
        Pdfium.WriteBlock callback = (_, pointer, size) =>
        {
            try
            {
                var buffer = new byte[checked((int)size)];
                Marshal.Copy(pointer, buffer, 0, buffer.Length);
                stream.Write(buffer);
                return 1;
            }
            catch (Exception ex)
            {
                failure = ex; // Exceptions must never cross the native callback boundary.
                return 0;
            }
        };
        var writer = new Pdfium.FileWrite { Version = 1, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        // Rewrite keeps security and drops unreferenced objects; incremental appends changes to the original bytes.
        var success = Pdfium.FPDF_SaveAsCopy(document, ref writer, flags);
        GC.KeepAlive(callback);
        if (failure is not null) throw new IOException("Не удалось сериализовать PDF.", failure);
        if (success == 0) throw new IOException("PDFium не смог сохранить документ.");
        return stream.ToArray();
    }

    /// <summary>Current PDF bytes. The array may be shared with the document and must not be modified.</summary>
    public byte[] GetBytes()
    {
        lock (Gate)
        {
            EnsureOpen();
            return CurrentBytes();
        }
    }

    public RecoverySnapshot GetRecoverySnapshot()
    {
        lock (Gate)
        {
            EnsureOpen();
            return new(CurrentBytes(), FilePath, _diskHash, Revision);
        }
    }

    /// <summary>Best effort for crash handlers: gives up instead of waiting forever for a thread that holds the lock.</summary>
    public RecoverySnapshot? TryGetRecoverySnapshot(TimeSpan timeout)
    {
        if (!Monitor.TryEnter(Gate, timeout)) return null;
        try
        {
            return _handle == 0 ? null : new(CurrentBytes(), FilePath, _diskHash, Revision);
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    public void Save(string path)
    {
        path = Path.GetFullPath(path);
        byte[] bytes;
        long state;
        byte[]? expectedHash;
        string? currentPath;
        lock (Gate)
        {
            EnsureOpen();
            bytes = CurrentBytes();
            state = _state;
            expectedHash = _diskHash;
            currentPath = FilePath;
        }
        // File I/O runs outside the lock so rendering of other pages and documents continues.
        if (string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase) && expectedHash is not null && !FileMatches(path, expectedHash))
            throw new IOException("Исходный файл изменён или удалён другой программой. Сохраните документ под другим именем.");
        AtomicFile.Write(path, bytes);
        var hash = SHA256.HashData(bytes);
        lock (Gate)
        {
            FilePath = path;
            _diskHash = hash;
            _savedState = state;
            Publish();
        }
    }

    private static bool FileMatches(string path, byte[] hash)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream).SequenceEqual(hash);
    }
}
