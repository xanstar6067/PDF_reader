using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDF_Reader.Models;
using PDF_Reader.Native;

namespace PDF_Reader.Services;

/// <summary>Owns an explicitly selected D3D11 device. Calls are serialized independently of PDFium.</summary>
public sealed class ThreeDRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly ThreeDNative.RendererHandle _handle;
    public long AdapterId { get; }
    public static IReadOnlyList<GpuAdapter> GetAdapters()
    {
        var native = new ThreeDNative.Gpu[32];
        var count = ThreeDNative.pd_gpu_list(native, native.Length);
        return native.Take(count).Select(g => new GpuAdapter(g.Id, g.Name, g.Memory, g.Integrated != 0)).ToArray();
    }
    public ThreeDRenderer(ThreeDScene scene, long preferred = 0, string? preferredName = null)
    {
        // DXGI LUIDs can change after a Windows restart; match the saved device name again.
        if (preferred is not (0 or -1) && !string.IsNullOrEmpty(preferredName))
        {
            var adapters = GetAdapters();
            var selected = adapters.FirstOrDefault(a => a.Id == preferred && a.Name == preferredName) ?? adapters.FirstOrDefault(a => a.Name == preferredName);
            if (selected is null) throw new InvalidOperationException("Выбранный ГП больше не доступен. Измените настройку 3D-графики.");
            preferred = selected.Id;
        }
        var pointer = ThreeDNative.pd_renderer_create(preferred, out var actual);
        if (pointer == 0) throw new InvalidOperationException("Не удалось создать D3D11-просмотрщик на выбранном адаптере. Выберите другой ГП или программный режим.");
        _handle = new(pointer);
        AdapterId = actual;
        try
        {
            var total = scene.Parts.Sum(p => p.Vertices.Length);
            var vertices = new float[total];
            var offset = 0;
            foreach (var part in scene.Parts) { part.Vertices.CopyTo(vertices, offset); offset += part.Vertices.Length; }
            if (ThreeDNative.pd_renderer_upload(_handle, vertices, (uint)(total / 10)) == 0)
                throw new InvalidOperationException("Недостаточно ресурсов ГП для модели. Попробуйте программный режим.");
        }
        catch { _handle.Dispose(); throw; }
    }
    public BitmapSource Render(int width, int height, ThreeDViewState state)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            var reduction = Math.Min(1d, 2048d / Math.Max(width, height));
            width = Math.Max(16, (int)(width * reduction)); height = Math.Max(16, (int)(height * reduction));
            var world = Matrix4x4.CreateRotationX(state.Pitch) * Matrix4x4.CreateRotationY(state.Yaw);
            var view = Matrix4x4.CreateLookAt(new Vector3(state.PanX, state.PanY, state.Distance), new Vector3(state.PanX, state.PanY, 0), Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(.65f, (float)width / height, .01f, 100);
            var m = world * view * projection;
            Matrix4x4.Invert(world, out var inverse);
            var light = Vector3.TransformNormal(Vector3.Normalize(new Vector3(MathF.Sin(state.LightAngle), .65f, MathF.Cos(state.LightAngle))), inverse);
            var b = state.DarkBackground ? .09f : .94f;
            float[] options = [m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44,
                light.X,light.Y,light.Z,state.Ambient,(float)state.Style,state.Shininess,state.Section ? 1 : 0,state.SectionPosition,b,b,b,1];
            var pixels = new byte[width * height * 4];
            if (ThreeDNative.pd_renderer_render(_handle, (uint)width, (uint)height, options, state.Style == ThreeDStyle.Wireframe ? 1 : 0, pixels, (uint)pixels.Length) == 0)
                throw new InvalidOperationException("ГП не смог отобразить модель. Возможно, устройство отключено или драйвер перезапущен.");
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze(); return bitmap;
        }
    }
    public void Dispose() { lock (_gate) _handle.Dispose(); }
}
