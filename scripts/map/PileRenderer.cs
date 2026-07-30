using Godot;

namespace Bianjing;

/// <summary>
/// 地面物资堆占位渲染：小扁方块 MultiMesh，按堆内主要货品配色（公用 GoodsColors 色表），
/// 高度随堆存量微调（一眼看出堆大小）。果品堆特例：落在树格的果堆缩小成果串，挂在树冠下方
/// （而非地面），位置/尺寸与树渲染同源哈希对准树身。堆数量少，每帧全量重建即可。
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
        var gs = GameState.I;
        var piles = gs.Piles;
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

            var c = new Vector2I(p.X, p.Y);
            float groundY = gs.Map.GroundY(c);
            Transform3D xf;
            if (domId == Goods.Fruit && gs.Map.CellAt(c).HasTree
                && gs.Plants.TryGetValue(GameState.CellIndex(c), out var plant))
            {
                // 果品挂树：缩小成果串块，吊在树冠下沿（与 GridRenderer 同源哈希算位置扰动与株大小，
                // 果串对准树身；冠底≈树干顶 1.1s 下探 0.35s），而非坠在地面
                float jx = ((p.X * 73 + p.Y * 31) % 7 - 3) * 0.15f;
                float jz = ((p.X * 41 + p.Y * 57) % 7 - 3) * 0.15f;
                float s = (0.8f + ((p.X * 13 + p.Y * 17) % 5) * 0.1f) * (0.35f + 0.65f * plant.GrowthRatio);
                float size = 0.16f + 0.1f * Mathf.Min(1f, (float)(p.Inv.Total / ItemPileObj.PileCapacity));
                var hang = MapGrid.CellToWorld(c) + new Vector3(jx, groundY + 0.75f * s, jz);
                xf = new Transform3D(Basis.Identity.Scaled(new Vector3(size, size, size)), hang);
            }
            else
            {
                // 其余货堆：堆越大越高（0.15~0.5m），底面固定 0.5m 见方；叠加地形海拔免埋进山体
                float h = 0.15f + 0.35f * Mathf.Min(1f, (float)(p.Inv.Total / ItemPileObj.PileCapacity));
                var pos = MapGrid.CellToWorld(c) + Vector3.Up * (groundY + h / 2f);
                xf = new Transform3D(Basis.Identity.Scaled(new Vector3(0.5f, h, 0.5f)), pos);
            }

            mm.SetInstanceTransform(i, xf);
            mm.SetInstanceColor(i, ColorOf(domId));
            i++;
        }
    }
}
