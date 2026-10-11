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

/// <summary>Owns a PDFium document and its input buffer. All native access is serialized.</summary>
public sealed class PdfDocument : IDisposable
{
    internal static readonly object Gate = new();
    private static bool _initialized;
    private nint _handle;
    private GCHandle _input;
    private nint _font;
    private FormFillSession? _forms;
    private readonly string? _password;
    private readonly LinkedList<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private long _state, _savedState, _nextState;
    private byte[]? _diskHash;
    private const long HistoryBudget = 128L * 1024 * 1024;
    private record Snapshot(byte[] Bytes, long State);

    public string? FilePath { get; private set; }
    public long Revision { get; private set; }
    public bool IsDirty { get { lock (Gate) return _state != _savedState; } }
    public bool CanUndo { get { lock (Gate) return _undo.Count > 0; } }
    public bool CanRedo { get { lock (Gate) return _redo.Count > 0; } }
    public int PageCount { get { lock (Gate) { EnsureOpen(); return Pdfium.FPDF_GetPageCount(_handle); } } }

    private PdfDocument(byte[] bytes, string? password)
    {
        _password = password;
        lock (Gate)
        {
            if (!_initialized) { Pdfium.FPDF_InitLibrary(); _initialized = true; }
            LoadBuffer(bytes);
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

    public static PdfDocument Create()
    {
        lock (Gate)
        {
            if (!_initialized) { Pdfium.FPDF_InitLibrary(); _initialized = true; }
            var handle = Pdfium.FPDF_CreateNewDocument();
            if (handle == 0) throw new InvalidOperationException("Не удалось создать PDF.");
            try
            {
                var page = Pdfium.FPDFPage_New(handle, 0, 595.28, 841.89);
                if (page == 0) throw new InvalidOperationException("Не удалось создать страницу.");
                Pdfium.FPDF_ClosePage(page);
                var document = new PdfDocument(Serialize(handle), null);
                document._state = ++document._nextState;
                return document;
            }
            finally { Pdfium.FPDF_CloseDocument(handle); }
        }
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_handle == 0, this);
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
        try { forms = Pdfium.FPDF_GetFormType(handle) == 1 ? new FormFillSession(handle) : null; }
        catch { Pdfium.FPDF_CloseDocument(handle); input.Free(); throw; }
        CloseHandle();
        _handle = handle; _input = input; _forms = forms;
    }

    private void CloseHandle()
    {
        _forms?.Dispose(); _forms = null;
        if (_font != 0) { Pdfium.FPDFFont_Close(_font); _font = 0; }
        if (_handle != 0) { Pdfium.FPDF_CloseDocument(_handle); _handle = 0; }
        if (_input.IsAllocated) _input.Free();
    }

    public void Dispose() { lock (Gate) { CloseHandle(); _undo.Clear(); _redo.Clear(); } }
    private T OnPage<T>(int index, Func<nint, T> action)
    {
        EnsureOpen();
        if (index < 0 || index >= Pdfium.FPDF_GetPageCount(_handle)) throw new ArgumentOutOfRangeException(nameof(index));
        var page = Pdfium.FPDF_LoadPage(_handle, index);
        if (page == 0) throw new InvalidDataException($"Не удалось загрузить страницу {index + 1}.");
        try { _forms?.AfterLoad(index, page); return action(page); }
        finally { _forms?.BeforeClose(index, page); Pdfium.FPDF_ClosePage(page); }
    }
    private T OnText<T>(nint page, Func<nint, T> action)
    {
        var text = Pdfium.FPDFText_LoadPage(page);
        if (text == 0) throw new InvalidDataException("Не удалось извлечь текст страницы.");
        try { return action(text); }
        finally { Pdfium.FPDFText_ClosePage(text); }
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
            return new(Meta("Title"), Meta("Author"), Meta("Subject"), Meta("Creator"), PageCount,
                Pdfium.FPDF_GetSecurityHandlerRevision(_handle) >= 0, Pdfium.FPDF_GetSignatureCount(_handle),
                Pdfium.FPDF_GetFormType(_handle), GetPermissions());
        }
    }

    public IReadOnlyList<PdfPageInfo> GetPages()
    {
        lock (Gate)
        {
            EnsureOpen();
            var result = new List<PdfPageInfo>();
            for (var i = 0; i < PageCount; i++)
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

    public BitmapSource Render(int index, int width, bool printing = false, CancellationToken cancellation = default)
    {
        lock (Gate)
        {
            cancellation.ThrowIfCancellationRequested();
            return OnPage(index, page =>
            {
                var ratio = Pdfium.FPDF_GetPageHeightF(page) / Pdfium.FPDF_GetPageWidthF(page);
                width = Math.Clamp(width, 16, 4096);
                var height = Math.Max(1, (int)Math.Min(8192, Math.Ceiling(width * ratio)));
                if ((long)width * height > 24_000_000) width = 24_000_000 / height;
                var bitmap = Pdfium.FPDFBitmap_Create(width, height, 1);
                if (bitmap == 0) throw new InvalidOperationException("Не удалось выделить память для страницы.");
                try
                {
                    Pdfium.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xffffffff);
                    Pdfium.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, printing ? 0x801 : 0x3);
                    if (_forms is not null) Pdfium.FPDF_FFLDraw(_forms.Handle, bitmap, page, 0, 0, width, height, 0, printing ? 0x801 : 0x3);
                    var stride = Pdfium.FPDFBitmap_GetStride(bitmap);
                    var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        Pdfium.FPDFBitmap_GetBuffer(bitmap), checked(stride * height), stride);
                    image.Freeze();
                    cancellation.ThrowIfCancellationRequested();
                    return image;
                }
                finally { Pdfium.FPDFBitmap_Destroy(bitmap); }
            });
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
                var a = ToPdf(p, from); var b = ToPdf(p, to);
                var first = Pdfium.FPDFText_GetCharIndexAtPos(t, a.X, a.Y, 12, 12);
                var last = Pdfium.FPDFText_GetCharIndexAtPos(t, b.X, b.Y, 12, 12);
                if (first < 0 || last < 0) return new TextSelection("", []);
                var start = Math.Min(first, last); var count = Math.Abs(last - first) + 1;
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
                            var start = Pdfium.FPDFText_GetSchResultIndex(handle); var count = Pdfium.FPDFText_GetSchCount(handle);
                            var previewStart = Math.Max(0, start - 35);
                            var previewLength = Math.Min(Pdfium.FPDFText_CountChars(t) - previewStart, count + 85);
                            result.Add(new(pageIndex, start, count, GetText(t, previewStart, previewLength).Replace("\r", " ").Replace("\n", " "), GetRects(p, t, start, count)));
                        }
                    }
                    finally { Pdfium.FPDFText_FindClose(handle); }
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
                    nodes.Add(new(ReadString((b, n) => Pdfium.FPDFBookmark_GetTitle(node, b, n)),
                        dest == 0 ? -1 : Pdfium.FPDFDest_GetDestPageIndex(_handle, dest), Read(node, depth + 1)));
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
                var pdf = ToPdf(p, point); var link = Pdfium.FPDFLink_GetLinkAtPoint(p, pdf.X, pdf.Y);
                if (link == 0) return null;
                var dest = Pdfium.FPDFLink_GetDest(_handle, link);
                var action = Pdfium.FPDFLink_GetAction(link);
                if (dest == 0) dest = Pdfium.FPDFAction_GetDest(_handle, action);
                if (dest != 0) return new PdfLink(Pdfium.FPDFDest_GetDestPageIndex(_handle, dest), null);
                var size = Pdfium.FPDFAction_GetURIPath(_handle, action, null, 0);
                if (size == 0 || size > 65536) return null;
                var buffer = new byte[size]; Pdfium.FPDFAction_GetURIPath(_handle, action, buffer, size);
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
                    var annot = Pdfium.FPDFPage_GetAnnot(page, i); if (annot == 0) continue;
                    try
                    {
                        if (Pdfium.FPDFAnnot_GetSubtype(annot) != 20 || Pdfium.FPDFAnnot_GetRect(annot, out var box) == 0) continue;
                        var bounds = new Rect(ToDisplay(page, box.Left, box.Top), ToDisplay(page, box.Right, box.Bottom));
                        if (!bounds.Contains(point)) continue;
                        var type = Pdfium.FPDFAnnot_GetFormFieldType(_forms.Handle, annot);
                        return new PdfFormField(index, i, ReadString((b, n) => Pdfium.FPDFAnnot_GetFormFieldName(_forms.Handle, annot, b, n)),
                            ReadString((b, n) => Pdfium.FPDFAnnot_GetFormFieldValue(_forms.Handle, annot, b, n)), type,
                            (Pdfium.FPDFAnnot_GetFormFieldFlags(_forms.Handle, annot) & 1) != 0, Pdfium.FPDFAnnot_IsChecked(_forms.Handle, annot) != 0, bounds, Revision);
                    }
                    finally { Pdfium.FPDFPage_CloseAnnot(annot); }
                }
                return null;
            });
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
        }), 256);
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
    private void Edit(Action action, uint permission = 8)
    {
        lock (Gate)
        {
            RequirePermission(permission, "Эта операция запрещена настройками защиты PDF.");
            var before = new Snapshot(Serialize(_handle), _state);
            try { action(); }
            catch { LoadBuffer(before.Bytes); Revision++; throw; }
            _undo.AddLast(before); _redo.Clear(); _state = ++_nextState; Revision++;
            TrimHistory();
        }
    }
    private void TrimHistory()
    {
        long size = _undo.Sum(s => (long)s.Bytes.Length) + _redo.Sum(s => (long)s.Bytes.Length);
        while (_undo.Count > 1 && (_undo.Count > 20 || size > HistoryBudget))
        {
            size -= _undo.First!.Value.Bytes.Length; _undo.RemoveFirst();
        }
    }
    public void Undo()
    {
        lock (Gate)
        {
            EnsureOpen(); if (_undo.Last is null) return;
            var target = _undo.Last.Value; var current = new Snapshot(Serialize(_handle), _state);
            LoadBuffer(target.Bytes); _undo.RemoveLast(); _redo.Push(current); _state = target.State; Revision++;
        }
    }
    public void Redo()
    {
        lock (Gate)
        {
            EnsureOpen(); if (_redo.Count == 0) return;
            var target = _redo.Peek(); var current = new Snapshot(Serialize(_handle), _state);
            LoadBuffer(target.Bytes); _redo.Pop(); _undo.AddLast(current); _state = target.State; Revision++; TrimHistory();
        }
    }
    private nint GetUnicodeFont()
    {
        if (_font != 0) return _font;
        var fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        var bytes = File.ReadAllBytes(fontPath);
        _font = Pdfium.FPDFText_LoadFont(_handle, bytes, (uint)bytes.Length, 2, 1);
        if (_font == 0) throw new InvalidOperationException("Не удалось загрузить шрифт Segoe UI.");
        return _font;
    }
    private nint CreateText(string text, float size)
    {
        var obj = Pdfium.FPDFPageObj_CreateTextObj(_handle, GetUnicodeFont(), size);
        if (obj == 0) throw new InvalidOperationException("Не удалось создать текстовый объект.");
        if (Pdfium.FPDFText_SetText(obj, text) != 0) return obj;
        Pdfium.FPDFPageObj_Destroy(obj);
        throw new InvalidOperationException("Не удалось записать текст в PDF.");
    }
    private static void Generate(nint page)
    {
        if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new InvalidOperationException("Не удалось обновить содержимое страницы.");
    }
    public void ReplaceText(EditableText selected, string text, float size)
    {
        ValidateText(text, size, allowEmpty: true);
        Edit(() => OnPage(selected.PageIndex, page =>
        {
            if (selected.Revision != Revision) throw new InvalidOperationException("Документ изменился. Выберите текст заново.");
            var obj = Pdfium.FPDFPage_GetObject(page, selected.ObjectIndex);
            if (obj == 0 || Pdfium.FPDFPageObj_GetType(obj) != 1) throw new InvalidOperationException("Текстовый объект больше не существует.");
            Pdfium.FPDFPageObj_GetMatrix(obj, out var matrix);
            Pdfium.FPDFPageObj_GetFillColor(obj, out var r, out var g, out var b, out var a);
            if (Pdfium.FPDFPage_RemoveObject(page, obj) == 0) throw new InvalidOperationException("Не удалось удалить исходный текст.");
            Pdfium.FPDFPageObj_Destroy(obj);
            if (!string.IsNullOrEmpty(text))
            {
                var replacement = CreateText(text, size);
                if (Pdfium.FPDFPageObj_SetMatrix(replacement, ref matrix) == 0 || Pdfium.FPDFPageObj_SetFillColor(replacement, r, g, b, a) == 0)
                { Pdfium.FPDFPageObj_Destroy(replacement); throw new InvalidOperationException("Не удалось сохранить стиль текста."); }
                if (Pdfium.FPDFPage_InsertObjectAtIndex(page, replacement, (nuint)selected.ObjectIndex) == 0)
                { Pdfium.FPDFPageObj_Destroy(replacement); throw new InvalidOperationException("Не удалось вставить замену текста."); }
            }
            Generate(page); return 0;
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
        ValidateText(text, size);
        Edit(() => OnPage(index, page =>
        {
            var origin = ToPdf(page, point);
            // Follow display orientation even when the PDF page is rotated.
            var right = ToPdf(page, new Point(point.X + .001, point.Y));
            var angle = Math.Atan2(right.Y - origin.Y, right.X - origin.X);
            var line = 0;
            foreach (var content in text.Replace("\r", "").Split('\n'))
            {
                if (!string.IsNullOrEmpty(content))
                {
                    var obj = CreateText(content, size);
                    var matrix = new Pdfium.Matrix { A = (float)Math.Cos(angle), B = (float)Math.Sin(angle),
                        C = (float)-Math.Sin(angle), D = (float)Math.Cos(angle),
                        E = (float)(origin.X + Math.Sin(angle) * line * size * 1.35), F = (float)(origin.Y - Math.Cos(angle) * line * size * 1.35) };
                    if (Pdfium.FPDFPageObj_SetMatrix(obj, ref matrix) == 0)
                    { Pdfium.FPDFPageObj_Destroy(obj); throw new InvalidOperationException("Не удалось разместить текст."); }
                    if (Pdfium.FPDFPage_InsertObject(page, obj) == 0) throw new InvalidOperationException("Не удалось добавить текст.");
                }
                line++;
            }
            Generate(page); return 0;
        }));
    }
    public void RotatePage(int index, int direction) => Edit(() => OnPage(index, p =>
    { Pdfium.FPDFPage_SetRotation(p, (Pdfium.FPDFPage_GetRotation(p) + direction + 4) % 4); return 0; }), 1024);
    public void DeletePage(int index) => Edit(() =>
    {
        if (PageCount <= 1) throw new InvalidOperationException("В документе должна остаться хотя бы одна страница.");
        if (index < 0 || index >= PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        Pdfium.FPDFPage_Delete(_handle, index);
    }, 1024);
    public void MovePage(int from, int to) => Edit(() =>
    {
        if (from < 0 || from >= PageCount || to < 0 || to >= PageCount) throw new ArgumentOutOfRangeException(nameof(to));
        if (Pdfium.FPDF_MovePages(_handle, [from], 1, to) == 0) throw new InvalidOperationException("Не удалось переместить страницу.");
    }, 1024);
    public void InsertBlankPage(int index) => Edit(() =>
    {
        if (index < 0 || index > PageCount) throw new ArgumentOutOfRangeException(nameof(index));
        var page = Pdfium.FPDFPage_New(_handle, index, 595.28, 841.89);
        if (page == 0) throw new InvalidOperationException("Не удалось вставить страницу.");
        Pdfium.FPDF_ClosePage(page);
    }, 1024);
    public void ImportPages(PdfDocument source, int index) => Edit(() =>
    {
        source.EnsureOpen();
        if (ReferenceEquals(source, this)) throw new ArgumentException("Выберите другой документ.");
        if (index < 0 || index > PageCount) throw new ArgumentOutOfRangeException(nameof(index));
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
                return Serialize(export);
            }
            finally { Pdfium.FPDF_CloseDocument(export); }
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
                    var tl = ToPdf(page, rect.TopLeft); var tr = ToPdf(page, rect.TopRight);
                    var bl = ToPdf(page, rect.BottomLeft); var br = ToPdf(page, rect.BottomRight);
                    foreach (var pt in new[] { tl, tr, bl, br }) bounds.Union(pt);
                    if (type == 9)
                    {
                        var quad = new Pdfium.Quad { X1 = (float)tl.X, Y1 = (float)tl.Y, X2 = (float)tr.X, Y2 = (float)tr.Y,
                            X3 = (float)bl.X, Y3 = (float)bl.Y, X4 = (float)br.X, Y4 = (float)br.Y };
                        if (Pdfium.FPDFAnnot_AppendAttachmentPoints(annot, ref quad) == 0) throw new InvalidOperationException("Не удалось выделить текст.");
                    }
                }
                var native = new Pdfium.RectF { Left = (float)bounds.Left, Right = (float)bounds.Right, Top = (float)bounds.Bottom, Bottom = (float)bounds.Top };
                if (Pdfium.FPDFAnnot_SetRect(annot, ref native) == 0 || Pdfium.FPDFAnnot_SetColor(annot, 0, 255, 202, 40, type == 9 ? 110u : 255u) == 0)
                    throw new InvalidOperationException("Не удалось настроить аннотацию.");
                Pdfium.FPDFAnnot_SetFlags(annot, 4); // Print with document.
                Pdfium.FPDFAnnot_SetStringValue(annot, "Contents", content);
                Pdfium.FPDFAnnot_SetStringValue(annot, "T", Environment.UserName);
                Pdfium.FPDFAnnot_SetStringValue(annot, "M", "D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "Z");
            }
            finally { Pdfium.FPDFPage_CloseAnnot(annot); }
            return 0;
        }), 32);
    }
    public IReadOnlyList<PdfComment> GetComments()
    {
        lock (Gate)
        {
            var result = new List<PdfComment>();
            for (var i = 0; i < PageCount; i++)
            {
                var pageIndex = i;
                OnPage(i, page =>
                {
                    for (var a = 0; a < Pdfium.FPDFPage_GetAnnotCount(page); a++)
                    {
                        var annot = Pdfium.FPDFPage_GetAnnot(page, a);
                        if (annot == 0) continue;
                        try
                        {
                            var type = Pdfium.FPDFAnnot_GetSubtype(annot);
                            if (type is not (1 or 9)) continue;
                            result.Add(new(pageIndex, a, ReadString((b, n) => Pdfium.FPDFAnnot_GetStringValue(annot, "Contents", b, n)),
                                ReadString((b, n) => Pdfium.FPDFAnnot_GetStringValue(annot, "T", b, n)), type));
                        }
                        finally { Pdfium.FPDFPage_CloseAnnot(annot); }
                    }
                    return 0;
                });
            }
            return result;
        }
    }
    public void DeleteComment(PdfComment comment) => Edit(() => OnPage(comment.PageIndex, p =>
    {
        if (Pdfium.FPDFPage_RemoveAnnot(p, comment.AnnotationIndex) == 0) throw new InvalidOperationException("Не удалось удалить аннотацию.");
        return 0;
    }), 32);

    private static byte[] Serialize(nint document)
    {
        using var stream = new MemoryStream();
        Exception? failure = null;
        Pdfium.WriteBlock callback = (_, pointer, size) =>
        {
            try
            {
                var buffer = new byte[checked((int)size)]; Marshal.Copy(pointer, buffer, 0, buffer.Length); stream.Write(buffer); return 1;
            }
            catch (Exception ex) { failure = ex; return 0; } // Exceptions must never cross the native callback boundary.
        };
        var writer = new Pdfium.FileWrite { Version = 1, Callback = Marshal.GetFunctionPointerForDelegate(callback) };
        var success = Pdfium.FPDF_SaveAsCopy(document, ref writer, 2); // Preserve security; rewrite all contents (no old deleted text).
        GC.KeepAlive(callback);
        if (failure is not null) throw new IOException("Не удалось сериализовать PDF.", failure);
        if (success == 0) throw new IOException("PDFium не смог сохранить документ.");
        return stream.ToArray();
    }
    public byte[] GetBytes() { lock (Gate) { EnsureOpen(); return Serialize(_handle); } }
    public void Save(string path)
    {
        lock (Gate)
        {
            EnsureOpen(); path = Path.GetFullPath(path);
            if (string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase) && _diskHash is not null &&
                (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(_diskHash)))
                throw new IOException("Исходный файл изменён или удалён другой программой. Сохраните документ под другим именем.");
            var bytes = Serialize(_handle);
            AtomicFile.Write(path, bytes);
            FilePath = path; _diskHash = SHA256.HashData(bytes); _savedState = _state;
        }
    }
}
