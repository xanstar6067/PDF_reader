using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PDF_Reader.Native;

internal static class ThreeDNative
{
    private const string Dll = "PaperDesk3D.dll";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Gpu
    {
        public long Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public ulong Memory;
        public int Integrated;
    }
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int pd_gpu_list([Out] Gpu[] output, int capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern nint pd_renderer_create(long preferred, out long actual);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void pd_renderer_destroy(nint handle);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int pd_renderer_upload(RendererHandle handle, float[] vertices, uint count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int pd_renderer_render(RendererHandle handle, uint width, uint height, float[] options, int wire, [Out] byte[] pixels, uint bytes);
    internal sealed class RendererHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RendererHandle(nint handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() { pd_renderer_destroy(handle); return true; }
    }
}
