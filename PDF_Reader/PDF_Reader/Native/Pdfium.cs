using System.Runtime.InteropServices;

namespace PDF_Reader.Native;

// Windows ABI: unsigned long / FPDF_BOOL are 32-bit, size_t is pointer-sized.
// Every call is serialized by PdfDocument.Gate, including calls from different documents.
internal static class Pdfium
{
    private const string Dll = "pdfium";
    [StructLayout(LayoutKind.Sequential)] internal struct SizeF { public float Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct RectF { public float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Matrix { public float A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] internal struct Quad { public float X1, Y1, X2, Y2, X3, Y3, X4, Y4; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointF { public float X, Y; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int WriteBlock(nint self, nint data, uint size);
    [StructLayout(LayoutKind.Sequential)] internal struct FileWrite { public int Version; public nint Callback; }

    [DllImport(Dll)] internal static extern void FPDF_InitLibrary();
    [DllImport(Dll)] internal static extern nint FPDF_LoadMemDocument64(nint data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport(Dll)] internal static extern uint FPDF_GetLastError();
    [DllImport(Dll)] internal static extern void FPDF_CloseDocument(nint document);
    [DllImport(Dll)] internal static extern nint FPDF_CreateNewDocument();
    [DllImport(Dll)] internal static extern int FPDF_GetPageCount(nint document);
    [DllImport(Dll)] internal static extern uint FPDF_GetDocPermissions(nint document);
    [DllImport(Dll)] internal static extern int FPDF_GetSecurityHandlerRevision(nint document);
    [DllImport(Dll)] internal static extern int FPDF_GetSignatureCount(nint document);
    [DllImport(Dll)] internal static extern int FPDF_GetFormType(nint document);
    [DllImport(Dll)] internal static extern nint FPDF_LoadPage(nint document, int index);
    [DllImport(Dll)] internal static extern void FPDF_ClosePage(nint page);
    [DllImport(Dll)] internal static extern int FPDF_GetPageSizeByIndexF(nint document, int index, out SizeF size);
    [DllImport(Dll)] internal static extern float FPDF_GetPageWidthF(nint page);
    [DllImport(Dll)] internal static extern float FPDF_GetPageHeightF(nint page);
    [DllImport(Dll)] internal static extern int FPDF_DeviceToPage(nint page, int x, int y, int width, int height, int rotate, int dx, int dy, out double px, out double py);
    [DllImport(Dll)] internal static extern int FPDF_PageToDevice(nint page, int x, int y, int width, int height, int rotate, double px, double py, out int dx, out int dy);
    [DllImport(Dll)] internal static extern nint FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Dll)] internal static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] internal static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int x, int y, int width, int height, int rotate, int flags);
    [DllImport(Dll)] internal static extern nint FPDFBitmap_GetBuffer(nint bitmap);
    [DllImport(Dll)] internal static extern int FPDFBitmap_GetStride(nint bitmap);
    [DllImport(Dll)] internal static extern void FPDFBitmap_Destroy(nint bitmap);
    [DllImport(Dll)] internal static extern int FPDF_SaveAsCopy(nint document, ref FileWrite writer, uint flags);
    [DllImport(Dll)] internal static extern uint FPDF_GetMetaText(nint document, [MarshalAs(UnmanagedType.LPUTF8Str)] string tag, byte[]? buffer, uint length);
    [DllImport(Dll)] internal static extern uint FPDF_GetPageLabel(nint document, int index, byte[]? buffer, uint length);

    [DllImport(Dll)] internal static extern nint FPDFText_LoadPage(nint page);
    [DllImport(Dll)] internal static extern void FPDFText_ClosePage(nint textPage);
    [DllImport(Dll)] internal static extern int FPDFText_CountChars(nint textPage);
    [DllImport(Dll)] internal static extern int FPDFText_GetText(nint textPage, int start, int count, byte[] text);
    [DllImport(Dll)] internal static extern int FPDFText_GetCharIndexAtPos(nint textPage, double x, double y, double tx, double ty);
    [DllImport(Dll)] internal static extern nint FPDFText_GetTextObject(nint textPage, int index);
    [DllImport(Dll)] internal static extern int FPDFText_CountRects(nint textPage, int start, int count);
    [DllImport(Dll)] internal static extern int FPDFText_GetRect(nint textPage, int index, out double left, out double top, out double right, out double bottom);
    [DllImport(Dll, CharSet = CharSet.Unicode)] internal static extern nint FPDFText_FindStart(nint textPage, string text, uint flags, int start);
    [DllImport(Dll)] internal static extern int FPDFText_FindNext(nint search);
    [DllImport(Dll)] internal static extern int FPDFText_GetSchResultIndex(nint search);
    [DllImport(Dll)] internal static extern int FPDFText_GetSchCount(nint search);
    [DllImport(Dll)] internal static extern void FPDFText_FindClose(nint search);

    [DllImport(Dll)] internal static extern nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);
    [DllImport(Dll)] internal static extern nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);
    [DllImport(Dll)] internal static extern uint FPDFBookmark_GetTitle(nint bookmark, byte[]? buffer, uint length);
    [DllImport(Dll)] internal static extern nint FPDFBookmark_GetDest(nint document, nint bookmark);
    [DllImport(Dll)] internal static extern nint FPDFBookmark_GetAction(nint bookmark);
    [DllImport(Dll)] internal static extern nint FPDFAction_GetDest(nint document, nint action);
    [DllImport(Dll)] internal static extern int FPDFDest_GetDestPageIndex(nint document, nint dest);
    [DllImport(Dll)] internal static extern nint FPDFLink_GetLinkAtPoint(nint page, double x, double y);
    [DllImport(Dll)] internal static extern nint FPDFLink_GetDest(nint document, nint link);
    [DllImport(Dll)] internal static extern nint FPDFLink_GetAction(nint link);
    [DllImport(Dll)] internal static extern uint FPDFAction_GetURIPath(nint document, nint action, byte[]? buffer, uint length);

    [DllImport(Dll)] internal static extern nint FPDFPage_New(nint document, int index, double width, double height);
    [DllImport(Dll)] internal static extern void FPDFPage_Delete(nint document, int index);
    [DllImport(Dll)] internal static extern int FPDF_MovePages(nint document, int[] pages, uint count, int destination);
    [DllImport(Dll)] internal static extern int FPDFPage_GetRotation(nint page);
    [DllImport(Dll)] internal static extern void FPDFPage_SetRotation(nint page, int rotation);
    [DllImport(Dll)] internal static extern int FPDFPage_GenerateContent(nint page);
    [DllImport(Dll)] internal static extern int FPDFPage_CountObjects(nint page);
    [DllImport(Dll)] internal static extern nint FPDFPage_GetObject(nint page, int index);
    [DllImport(Dll)] internal static extern int FPDFPageObj_GetType(nint obj);
    [DllImport(Dll)] internal static extern int FPDFPageObj_GetBounds(nint obj, out float left, out float bottom, out float right, out float top);
    [DllImport(Dll)] internal static extern int FPDFPageObj_GetMatrix(nint obj, out Matrix matrix);
    [DllImport(Dll)] internal static extern int FPDFPageObj_SetMatrix(nint obj, ref Matrix matrix);
    [DllImport(Dll)] internal static extern int FPDFPageObj_GetFillColor(nint obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] internal static extern int FPDFPageObj_SetFillColor(nint obj, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] internal static extern int FPDFPageObj_GetStrokeColor(nint obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] internal static extern int FPDFPageObj_SetStrokeColor(nint obj, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] internal static extern int FPDFTextObj_GetTextRenderMode(nint obj);
    [DllImport(Dll)] internal static extern int FPDFTextObj_SetTextRenderMode(nint obj, int mode);
    [DllImport(Dll)] internal static extern nint FPDFTextObj_GetFont(nint obj);
    [DllImport(Dll)] internal static extern int FPDFFont_GetIsEmbedded(nint font);
    [DllImport(Dll)] internal static extern nint FPDFFont_GetGlyphPath(nint font, uint glyph, float size);
    [DllImport(Dll)] internal static extern int FPDFPage_InsertObject(nint page, nint obj);
    [DllImport(Dll)] internal static extern int FPDFPage_InsertObjectAtIndex(nint page, nint obj, nuint index);
    [DllImport(Dll)] internal static extern int FPDFPage_RemoveObject(nint page, nint obj);
    [DllImport(Dll)] internal static extern void FPDFPageObj_Destroy(nint obj);
    [DllImport(Dll)] internal static extern nint FPDFPageObj_CreateTextObj(nint document, nint font, float size);
    [DllImport(Dll)] internal static extern nint FPDFPageObj_NewTextObj(nint document, [MarshalAs(UnmanagedType.LPUTF8Str)] string font, float size);
    [DllImport(Dll, CharSet = CharSet.Unicode)] internal static extern int FPDFText_SetText(nint obj, string text);
    [DllImport(Dll)] internal static extern uint FPDFTextObj_GetText(nint obj, nint textPage, byte[]? buffer, uint length);
    [DllImport(Dll)] internal static extern int FPDFTextObj_GetFontSize(nint obj, out float size);
    [DllImport(Dll)] internal static extern nint FPDFText_LoadFont(nint document, byte[] data, uint size, int type, int cid);
    [DllImport(Dll)] internal static extern void FPDFFont_Close(nint font);
    [DllImport(Dll)] internal static extern int FPDF_ImportPages(nint document, nint source, [MarshalAs(UnmanagedType.LPUTF8Str)] string? range, int index);

    [DllImport(Dll)] internal static extern nint FPDFPage_CreateAnnot(nint page, int type);
    [DllImport(Dll)] internal static extern int FPDFPage_GetAnnotCount(nint page);
    [DllImport(Dll)] internal static extern nint FPDFPage_GetAnnot(nint page, int index);
    [DllImport(Dll)] internal static extern void FPDFPage_CloseAnnot(nint annot);
    [DllImport(Dll)] internal static extern int FPDFPage_RemoveAnnot(nint page, int index);
    [DllImport(Dll)] internal static extern int FPDFAnnot_GetSubtype(nint annot);
    [DllImport(Dll)] internal static extern int FPDFPage_GetAnnotIndex(nint page, nint annot);
    [DllImport(Dll)] internal static extern nint FPDFAnnot_GetLinkedAnnot(nint annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Dll)] internal static extern int FPDFAnnot_SetRect(nint annot, ref RectF rect);
    [DllImport(Dll)] internal static extern int FPDFAnnot_GetRect(nint annot, out RectF rect);
    [DllImport(Dll)] internal static extern int FPDFAnnot_SetColor(nint annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Dll)] internal static extern int FPDFAnnot_AppendAttachmentPoints(nint annot, ref Quad quad);
    [DllImport(Dll)] internal static extern int FPDFAnnot_GetColor(nint annot, int type, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Dll)] internal static extern int FPDFAnnot_SetBorder(nint annot, float horizontal, float vertical, float width);
    [DllImport(Dll)] internal static extern int FPDFAnnot_GetBorder(nint annot, out float horizontal, out float vertical, out float width);
    [DllImport(Dll, CharSet = CharSet.Unicode)] internal static extern int FPDFAnnot_SetAP(nint annot, int mode, string? value);
    [DllImport(Dll)] internal static extern int FPDFAnnot_AddInkStroke(nint annot, PointF[] points, nuint count);
    [DllImport(Dll)] internal static extern int FPDFAnnot_RemoveInkList(nint annot);
    [DllImport(Dll)] internal static extern int FPDFAnnot_GetObjectCount(nint annot);
    [DllImport(Dll)] internal static extern nint FPDFAnnot_GetObject(nint annot, int index);
    [DllImport(Dll)] internal static extern int FPDFPath_GetDrawMode(nint path, out int fillMode, out int stroke);
    [DllImport(Dll)] internal static extern uint FPDFAnnot_GetInkListCount(nint annot);
    [DllImport(Dll)] internal static extern uint FPDFAnnot_GetInkListPath(nint annot, uint index, [Out] PointF[]? buffer, uint length);
    [DllImport(Dll)] internal static extern int FPDFAnnot_SetFlags(nint annot, int flags);
    [DllImport(Dll, CharSet = CharSet.Unicode)] internal static extern int FPDFAnnot_SetStringValue(nint annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, string value);
    [DllImport(Dll)] internal static extern uint FPDFAnnot_GetStringValue(nint annot, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, byte[]? buffer, uint length);
    [DllImport(Dll)] internal static extern nint FPDFDOC_InitFormFillEnvironment(nint document, nint info);
    [DllImport(Dll)] internal static extern void FPDFDOC_ExitFormFillEnvironment(nint form);
    [DllImport(Dll)] internal static extern void FORM_OnAfterLoadPage(nint page, nint form);
    [DllImport(Dll)] internal static extern void FORM_OnBeforeClosePage(nint page, nint form);
    [DllImport(Dll)] internal static extern void FPDF_FFLDraw(nint form, nint bitmap, nint page, int x, int y, int width, int height, int rotation, int flags);
    [DllImport(Dll)] internal static extern int FORM_OnFocus(nint form, nint page, int modifiers, double x, double y);
    [DllImport(Dll)] internal static extern int FORM_OnLButtonDown(nint form, nint page, int modifiers, double x, double y);
    [DllImport(Dll)] internal static extern int FORM_OnLButtonUp(nint form, nint page, int modifiers, double x, double y);
    [DllImport(Dll)] internal static extern int FORM_SelectAllText(nint form, nint page);
    [DllImport(Dll, CharSet = CharSet.Unicode)] internal static extern void FORM_ReplaceSelection(nint form, nint page, string text);
    [DllImport(Dll)] internal static extern int FORM_ForceToKillFocus(nint form);
    [DllImport(Dll)] internal static extern int FPDFAnnot_GetFormFieldType(nint form, nint annotation);
    [DllImport(Dll)] internal static extern int FPDFAnnot_GetFormFieldFlags(nint form, nint annotation);
    [DllImport(Dll)] internal static extern uint FPDFAnnot_GetFormFieldName(nint form, nint annotation, byte[]? buffer, uint size);
    [DllImport(Dll)] internal static extern uint FPDFAnnot_GetFormFieldValue(nint form, nint annotation, byte[]? buffer, uint size);
    [DllImport(Dll)] internal static extern int FPDFAnnot_IsChecked(nint form, nint annotation);
}
