using Godot;

namespace Bianjing;

/// <summary>
/// 地面物资堆占位渲染：小扁方块 MultiMesh，按堆内主要货品配色（公用 GoodsColors 色表），
/// 高度随堆存量微调（一眼看出堆大小）。堆数量少，每帧全量重建即可。
/// </summary>
public partial class PileRenderer : Node3D
{
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

    /// <summary>货品 id → 堆的颜色：改用全局统一色表（与背货块/屋内库存堆同色同货）。</summary>
    private static Color ColorOf(string goodsId) => GoodsColors.ColorOf(goodsId);

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

            // 堆越大越高（0.15~0.5m），底面固定 0.5m 见方（小方块掉落物）
            float h = 0.15f + 0.35f * Mathf.Min(1f, (float)(p.Inv.Total / ItemPileObj.PileCapacity));
            var pos = MapGrid.CellToWorld(new Vector2I(p.X, p.Y)) + Vector3.Up * (h / 2f);
            var basis = Basis.Identity.Scaled(new Vector3(0.5f, h, 0.5f));

            mm.SetInstanceTransform(i, new Transform3D(basis, pos));
            mm.SetInstanceColor(i, ColorOf(domId));
            i++;
        }
    }
}
