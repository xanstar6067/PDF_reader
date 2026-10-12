using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PDF_Reader.Native;

/// <summary>Driver settings UI. Only managed DEVMODE bytes cross this boundary.</summary>
internal static class PrinterProperties
{
    private sealed class PrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PrinterHandle() : base(true) { }
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string name, out PrinterHandle printer, nint defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(nint printer);
    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(nint window, PrinterHandle printer, string name, nint output, nint input, uint mode);

    internal static byte[]? Show(nint window, string printer, byte[] devMode)
    {
        if (!OpenPrinter(printer, out var handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var held = handle;
        var length = DocumentProperties(window, handle, printer, 0, 0, 0);
        if (length <= 0 || length > 16 * 1024 * 1024) throw new Win32Exception("Не удалось открыть свойства принтера.");
        var input = Marshal.AllocHGlobal(devMode.Length);
        var output = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(devMode, 0, input, devMode.Length);
            var result = DocumentProperties(window, handle, printer, output, input, 4 | 8 | 2); // OUT_BUFFER | IN_BUFFER | IN_PROMPT
            if (result == 2) return null; // IDCANCEL
            if (result != 1) throw new Win32Exception("Не удалось применить свойства принтера.");
            var bytes = new byte[length];
            Marshal.Copy(output, bytes, 0, length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(input); Marshal.FreeHGlobal(output); }
    }
}
