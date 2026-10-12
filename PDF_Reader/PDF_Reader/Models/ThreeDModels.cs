using System.Numerics;
using System.Windows;

namespace PDF_Reader.Models;

public record GpuAdapter(long Id, string Name, ulong DedicatedBytes, bool Integrated)
{
    public string Description => Id switch { 0 => Name, -1 => Name, _ => $"{Name} · {(Integrated ? "встроенная" : "дискретная")} · {DedicatedBytes / 1048576} МБ" };
}
public enum ThreeDStyle { Solid, Glossy, Metallic, Illustration, Wireframe }
public sealed record ThreeDPart(string Name, float[] Vertices)
{
    public int VertexCount => Vertices.Length / 10;
}
public sealed record ThreeDScene(IReadOnlyList<ThreeDPart> Parts, string Format, Rect Bounds, bool ApproximatePlacement)
{
    public int TriangleCount => Parts.Sum(p => p.VertexCount) / 3;
    public string? Notice => ApproximatePlacement ? "Для некоторых деталей PRC не удалось определить положение в сборке. Эти детали показаны в собственных координатах." : null;
}
public sealed class ThreeDViewState
{
    public float Yaw { get; set; } = -.35f;
    public float Pitch { get; set; } = -1.1f;
    public float Distance { get; set; } = 2.1f;
    public float PanX { get; set; }
    public float PanY { get; set; }
    public float Ambient { get; set; } = .3f;
    public float LightAngle { get; set; } = .6f;
    public float Shininess { get; set; } = 32;
    public ThreeDStyle Style { get; set; } = ThreeDStyle.Glossy;
    public bool Section { get; set; }
    public float SectionPosition { get; set; } = .2f;
    public bool DarkBackground { get; set; }
    public ThreeDViewState Copy() => (ThreeDViewState)MemberwiseClone();
    public void Reset() { Yaw = -.35f; Pitch = -1.1f; Distance = 2.1f; PanX = PanY = 0; }
}
