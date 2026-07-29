using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 建筑内库存堆渲染：把每座建筑仓房里的货品以小方块堆呈现在屋内地面
/// （建筑主体半透明，可透视屋内存货）——货品只有被消耗/取走才消失，堆高随存量涨落（血量=剩余份数）。
/// 每种货一堆，按建筑内部格逐格排开互不重合。库存变化频繁但无需逐帧精确，降频重建。
/// </summary>
public partial class BuildingStockRenderer : Node3D
{
    /// <summary>重建间隔（秒）：库存堆只需要跟得上肉眼节奏。</summary>
    private const float RefreshInterval = 0.25f;

    private MultiMeshInstance3D _mm;
    private float _cooldown;

    public override void _Ready()
    {
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

    public override void _Process(double delta)
    {
        _cooldown -= (float)delta;
        if (_cooldown > 0f)
            return;
        _cooldown = RefreshInterval;
        Rebuild();
    }

    /// <summary>全量重建：建筑数百量级 × 每座一至数种货，实例总量小，直接全刷。</summary>
    private void Rebuild()
    {
        var gs = GameState.I;
        var xforms = new List<Transform3D>();
        var colors = new List<Color>();

        foreach (var b in gs.Buildings.Values)
        {
            if (b.Inv.IsEmpty)
                continue;

            // 屋内可堆区：占地去掉一圈外檐（与住宅檐隙观感一致）；小屋退化为整个占地
            int innerW = Mathf.Max(1, b.FootX - 2);
            int innerH = Mathf.Max(1, b.FootY - 2);
            int ox = b.FootX > 2 ? 1 : 0;
            int oy = b.FootY > 2 ? 1 : 0;

            int slot = 0;
            foreach (var s in b.Inv.Stacks)
            {
                if (s.Amount <= 0.0001)
                    continue;
                // 堆位按屋内格先横后纵逐格排开（货种多于屋内格时回绕叠印，极端情况可接受）
                var c = new Vector2I(
                    b.Origin.X + ox + slot % innerW,
                    b.Origin.Y + oy + slot / innerW % innerH);
                slot++;

                // 与地面掉落堆同规格：0.5m 见方小方块，高随份数（耗尽即消，不凭空常驻）
                float h = 0.15f + 0.35f * Mathf.Min(1f, (float)(s.Amount / ItemPileObj.PileCapacity));
                var pos = MapGrid.CellToWorld(c) + Vector3.Up * (h / 2f);
                xforms.Add(new Transform3D(Basis.Identity.Scaled(new Vector3(0.5f, h, 0.5f)), pos));
                colors.Add(GoodsColors.ColorOf(s.GoodsId));
            }
        }

        var mm = _mm.Multimesh;
        mm.InstanceCount = xforms.Count;
        for (int i = 0; i < xforms.Count; i++)
        {
            mm.SetInstanceTransform(i, xforms[i]);
            mm.SetInstanceColor(i, colors[i]);
        }
    }
}
