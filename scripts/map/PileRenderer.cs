using Godot;

namespace Bianjing;

/// <summary>
/// 地面物资堆占位渲染：小扁方块 MultiMesh，按堆内主要货品配色，
/// 高度随堆存量微调（一眼看出堆大小）。堆数量少，每帧全量重建即可。
/// </summary>
public partial class PileRenderer : Node3D
{
    /// <summary>货品配色（未知货品用灰色兜底，mod 货品也能显示）。</summary>
    private static readonly Color FallbackColor = new(0.6f, 0.6f, 0.6f);

    private MultiMeshInstance3D _mm;

    public override void _Ready()
    {
        // 单位立方，实例变换里再按堆大小缩放
        var mesh = new BoxMesh { Size = Vector3.One };
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
    }

    /// <summary>货品 id → 堆的颜色（粮金黄/柴棕/果红/野味深褐）。</summary>
    private static Color ColorOf(string goodsId) => goodsId switch
    {
        Goods.Grain => new Color(0.85f, 0.72f, 0.3f),
        Goods.Wood => new Color(0.5f, 0.36f, 0.2f),
        Goods.Fruit => new Color(0.78f, 0.3f, 0.28f),
        Goods.Game => new Color(0.42f, 0.26f, 0.22f),
        _ => FallbackColor,
    };

    public override void _Process(double delta)
    {
        var piles = GameState.I.Piles;
        var mm = _mm.Multimesh;
        mm.InstanceCount = piles.Count;
        if (piles.Count == 0)
            return;

        int i = 0;
        foreach (var p in piles.Values)
        {
            // 取份数最多的货品作为堆的主色
            string domId = "";
            double domAmt = 0;
            foreach (var s in p.Inv.Stacks)
            {
                if (s.Amount > domAmt)
                {
                    domAmt = s.Amount;
                    domId = s.GoodsId;
                }
            }

            // 堆越大越高（0.3~0.9m），底面固定 2m 见方
            float h = 0.3f + 0.6f * Mathf.Min(1f, (float)(p.Inv.Total / ItemPileObj.PileCapacity));
            var pos = MapGrid.CellToWorld(new Vector2I(p.X, p.Y)) + Vector3.Up * (h / 2f);
            var basis = Basis.Identity.Scaled(new Vector3(2f, h, 2f));

            mm.SetInstanceTransform(i, new Transform3D(basis, pos));
            mm.SetInstanceColor(i, ColorOf(domId));
            i++;
        }
    }
}
