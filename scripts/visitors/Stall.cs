using Godot;

namespace Bianjing;

/// <summary>
/// 路边摆摊（货郎支起的自带库存临时摊位）：简易木台 + 彩色遮阳棚。日结对照需求账本售货，
/// 城市向外城货郎付钱（类比外来货币流出）；存活天数归零后由 VisitorSystem 收摊、带人离城。
/// </summary>
public partial class Stall : Node3D
{
    public Inventory Inv = new();
    public ForeignVisitor OwnerVisitor;
    public int Category;
    public int DaysLeft;

    private static readonly BoxMesh SharedBox = new() { Size = Vector3.One };

    /// <summary>在 cell 处支摊；cargo 直接接管为摊位库存。</summary>
    public void Init(GameState gs, ForeignVisitor owner, Vector2I cell, Inventory cargo, int days)
    {
        OwnerVisitor = owner;
        Inv = cargo;
        DaysLeft = days;
        Category = cargo.Stacks.Count > 0 ? Goods.CategoryOf(cargo.Stacks[0].GoodsId) : -1;
        Position = MapGrid.CellToWorld(cell) + Vector3.Up * (gs.Map.GroundY(cell) + 0.0f);
        BuildStall();
    }

    private void BuildStall()
    {
        // 木台
        var table = new MeshInstance3D
        {
            Mesh = SharedBox,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.42f, 0.28f) },
            Scale = new Vector3(1.1f, 0.5f, 0.7f),
            Position = new Vector3(0f, 0.25f, 0f),
        };
        AddChild(table);

        // 遮阳棚（用所属邻城配色，和货郎呼应）
        var canopy = new MeshInstance3D
        {
            Mesh = SharedBox,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.30f, 0.26f) },
            Scale = new Vector3(1.3f, 0.08f, 0.9f),
            Position = new Vector3(0f, 1.05f, 0f),
        };
        AddChild(canopy);

        // 四角棚柱
        var postMat = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.27f, 0.20f) };
        float px = 0.6f, pz = 0.4f;
        foreach (var (ox, oz) in new[] { (-px, -pz), (px, -pz), (px, pz), (-px, pz) })
        {
            var post = new MeshInstance3D
            {
                Mesh = SharedBox,
                MaterialOverride = postMat,
                Scale = new Vector3(0.06f, 1.0f, 0.06f),
                Position = new Vector3(ox, 0.5f, oz),
            };
            AddChild(post);
        }
    }

    /// <summary>每日结算：把短缺货逐步卖给城里（进商铺库存，城市付钱给外城货郎）。</summary>
    public void TickTrade(GameState gs)
    {
        if (Inv.Total <= 0)
            return;
        double budget = VisitorConfig.StallDailySaleCap;
        foreach (var s in new System.Collections.Generic.List<GoodsStack>(Inv.Stacks))
        {
            if (budget <= 0)
                break;
            if (!gs.Demand.IsShort(s.GoodsId))
                continue;
            double sell = System.Math.Min(s.Amount, budget);
            double taken = Inv.Take(s.GoodsId, sell);
            if (taken <= 0)
                continue;
            // 货进本地商铺/驿站库存（无则直接被城消费）
            var shop = FirstShopOrInn(gs);
            if (shop != null)
                shop.StoreGoodsForce(s.GoodsId, taken);
            long cost = (long)(taken * Goods.PriceOf(s.GoodsId));
            gs.Money -= cost; // 城市付钱给外城货郎
            gs.Ledger?.Add("外来摆摊", -cost);
            budget -= taken;
        }
    }

    private static BuildingInstance FirstShopOrInn(GameState gs)
    {
        foreach (var b in gs.BuildingsOfType("shop"))
            return b;
        foreach (var b in gs.BuildingsOfType("inn"))
            return b;
        return null;
    }
}
