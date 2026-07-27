using Godot;

namespace Bianjing;

/// <summary>动物占位渲染：小棕色方块 MultiMesh，随 WildlifeChanged 事件重建。</summary>
public partial class AnimalRenderer : Node3D
{
    private static readonly Color AnimalColor = new(0.45f, 0.3f, 0.18f);

    private MultiMeshInstance3D _mm;
    private bool _dirty = true;

    public override void _Ready()
    {
        var mesh = new BoxMesh { Size = new Vector3(1.1f, 0.7f, 1.6f) };
        mesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _mm = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = mesh,
            },
        };
        AddChild(_mm);

        EventBus.WildlifeChanged += MarkDirty;
        EventBus.GameLoaded += MarkDirty;
    }

    public override void _ExitTree()
    {
        EventBus.WildlifeChanged -= MarkDirty;
        EventBus.GameLoaded -= MarkDirty;
    }

    private void MarkDirty() => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty)
            return;
        _dirty = false;
        Rebuild();
    }

    private void Rebuild()
    {
        var animals = GameState.I.Animals;
        var mm = _mm.Multimesh;
        mm.InstanceCount = animals.Count;

        int i = 0;
        foreach (var a in animals.Values)
        {
            // 位置/朝向按 Id 伪随机扰动，避免整齐划一
            float jx = (a.Id * 37 % 11 - 5) * 0.12f;
            float jz = (a.Id * 53 % 11 - 5) * 0.12f;
            var world = MapGrid.CellToWorld(new Vector2I(a.X, a.Y)) + new Vector3(jx, 0.35f, jz);
            var basis = Basis.FromEuler(new Vector3(0f, a.Id * 2.399f % Mathf.Tau, 0f));
            mm.SetInstanceTransform(i, new Transform3D(basis, world));
            mm.SetInstanceColor(i, AnimalColor);
            i++;
        }
    }
}
