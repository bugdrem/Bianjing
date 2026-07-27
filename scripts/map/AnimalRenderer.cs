using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>动物占位渲染：小棕色方块 MultiMesh。数据层格位变化时不再瞬移，
/// 而是记录每只动物的平滑位置逐帧插值走过去；增减个体时重建实例。</summary>
public partial class AnimalRenderer : Node3D
{
    private static readonly Color AnimalColor = new(0.45f, 0.3f, 0.18f);

    /// <summary>插值速度（每秒趋近目标的比例系数）。</summary>
    private const float LerpSpeed = 2.5f;

    private MultiMeshInstance3D _mm;
    private bool _dirty = true;

    /// <summary>动物 Id → 当前平滑世界坐标（个体消失后清理）。</summary>
    private readonly Dictionary<int, Vector3> _smoothPos = new();

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
        EventBus.GameLoaded += OnGameLoaded;
    }

    public override void _ExitTree()
    {
        EventBus.WildlifeChanged -= MarkDirty;
        EventBus.GameLoaded -= OnGameLoaded;
    }

    private void MarkDirty() => _dirty = true;

    private void OnGameLoaded()
    {
        // 读档/新开局：平滑位置作废，直接落到新格位
        _smoothPos.Clear();
        _dirty = true;
    }

    public override void _Process(double delta)
    {
        var animals = GameState.I.Animals;
        var mm = _mm.Multimesh;

        if (_dirty)
        {
            _dirty = false;
            mm.InstanceCount = animals.Count;

            // 清理已消失个体的平滑位置
            if (_smoothPos.Count > animals.Count)
            {
                var gone = new List<int>();
                foreach (int id in _smoothPos.Keys)
                    if (!animals.ContainsKey(id))
                        gone.Add(id);
                foreach (int id in gone)
                    _smoothPos.Remove(id);
            }
        }

        if (mm.InstanceCount == 0)
            return;

        float t = Mathf.Min(1f, (float)delta * LerpSpeed);
        int i = 0;
        foreach (var a in animals.Values)
        {
            // 目标位置按 Id 伪随机扰动，避免整齐划一
            float jx = (a.Id * 37 % 11 - 5) * 0.12f;
            float jz = (a.Id * 53 % 11 - 5) * 0.12f;
            var target = MapGrid.CellToWorld(new Vector2I(a.X, a.Y)) + new Vector3(jx, 0.35f, jz);

            // 新个体直接落位，老个体缓步挪向新格
            var pos = _smoothPos.TryGetValue(a.Id, out var cur) ? cur.Lerp(target, t) : target;
            _smoothPos[a.Id] = pos;

            var basis = Basis.FromEuler(new Vector3(0f, a.Id * 2.399f % Mathf.Tau, 0f));
            mm.SetInstanceTransform(i, new Transform3D(basis, pos));
            mm.SetInstanceColor(i, AnimalColor);
            i++;
        }
    }
}
