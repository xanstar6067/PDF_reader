using System.Runtime.InteropServices;

namespace PDF_Reader.Native;

// Version 1 FPDF_FORMFILLINFO ABI. This session supports AcroForm widgets without JavaScript or XFA.
internal sealed class FormFillSession : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FormInfo
    {
        public int Version;
        public nint Release, Invalidate, OutputSelectedRect, SetCursor, SetTimer, KillTimer, GetLocalTime, OnChange;
        public nint GetPage, GetCurrentPage, GetRotation, ExecuteNamedAction, SetTextFieldFocus, DoUriAction, DoGoToAction, JsPlatform;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InvalidateCallback(nint self, nint page, double l, double t, double r, double b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CursorCallback(nint self, int cursor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TimerCallback(nint self, int milliseconds, nint callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void KillTimerCallback(nint self, int id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate SystemTime TimeCallback(nint self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint PageCallback(nint self, nint document, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RotationCallback(nint self, nint page);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ActionCallback(nint self, nint action);

    // Delegates are kept alive here until the form environment is closed.
    private readonly List<Delegate> _callbacks = [];
    private readonly Dictionary<int, nint> _pages = [];
    private nint _info;

    public nint Handle { get; private set; }

    public FormFillSession(nint document)
    {
        nint Pointer(Delegate callback)
        {
            _callbacks.Add(callback);
            return Marshal.GetFunctionPointerForDelegate(callback);
        }

        var info = new FormInfo
        {
            Version = 1,
            Invalidate = Pointer(new InvalidateCallback((_, _, _, _, _, _) => { })),
            SetCursor = Pointer(new CursorCallback((_, _) => { })),
            SetTimer = Pointer(new TimerCallback((_, _, _) => 0)), // No live caret/timers in the modal field editor.
            KillTimer = Pointer(new KillTimerCallback((_, _) => { })),
            GetLocalTime = Pointer(new TimeCallback(_ => LocalTime())),
            GetPage = Pointer(new PageCallback((_, _, index) => _pages.GetValueOrDefault(index))),
            GetRotation = Pointer(new RotationCallback((_, page) => Pdfium.FPDFPage_GetRotation(page))),
            ExecuteNamedAction = Pointer(new ActionCallback((_, _) => { }))
        };
        _info = Marshal.AllocHGlobal(Marshal.SizeOf<FormInfo>());
        Marshal.StructureToPtr(info, _info, false);
        Handle = Pdfium.FPDFDOC_InitFormFillEnvironment(document, _info);
        if (Handle != 0) return;
        Marshal.FreeHGlobal(_info);
        _info = 0;
        throw new InvalidOperationException("Не удалось загрузить PDF-формы.");
    }

    private static SystemTime LocalTime()
    {
        var t = DateTime.Now;
        return new SystemTime
        {
            Year = (ushort)t.Year, Month = (ushort)t.Month, Day = (ushort)t.Day, DayOfWeek = (ushort)t.DayOfWeek,
            Hour = (ushort)t.Hour, Minute = (ushort)t.Minute, Second = (ushort)t.Second, Milliseconds = (ushort)t.Millisecond
        };
    }

    public void AfterLoad(int index, nint page)
    {
        _pages[index] = page;
        Pdfium.FORM_OnAfterLoadPage(page, Handle);
    }

    public void BeforeClose(int index, nint page)
    {
        Pdfium.FORM_OnBeforeClosePage(page, Handle);
        _pages.Remove(index);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            Pdfium.FPDFDOC_ExitFormFillEnvironment(Handle);
            Handle = 0;
        }
        if (_info != 0)
        {
            Marshal.FreeHGlobal(_info);
            _info = 0;
        }
        _callbacks.Clear();
        _pages.Clear();
    }
}
