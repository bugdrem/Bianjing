using Godot;

namespace Bianjing;

/// <summary>
/// 货品渲染配色（渲染层公用）：地面物资堆 / 村民背货块 / 建筑内库存堆共用同一张色表，
/// 玩家在任何场合看到同色即同货；未知货品（mod 新货）用灰色兜底也能显示。
/// </summary>
public static class GoodsColors
{
    private static readonly Color Fallback = new(0.6f, 0.6f, 0.6f);

    /// <summary>货品 id → 配色（粮金黄/柴棕/果红/野味深褐/矿青灰/盐白/成品各具其色/水蓝）。</summary>
    public static Color ColorOf(string goodsId) => goodsId switch
    {
        Goods.Grain => new Color(0.85f, 0.72f, 0.3f),
        Goods.Wood => new Color(0.5f, 0.36f, 0.2f),
        Goods.Fruit => new Color(0.78f, 0.3f, 0.28f),
        Goods.Game => new Color(0.42f, 0.26f, 0.22f),
        Goods.IronOre => new Color(0.45f, 0.5f, 0.55f),
        Goods.RawSalt => new Color(0.9f, 0.9f, 0.88f),
        Goods.Timber => new Color(0.66f, 0.5f, 0.3f),
        Goods.Wine => new Color(0.6f, 0.32f, 0.5f),
        Goods.Ironware => new Color(0.3f, 0.32f, 0.38f),
        Goods.Cured => new Color(0.55f, 0.33f, 0.18f),
        Goods.Water => new Color(0.3f, 0.5f, 0.75f),
        Goods.Weapon => new Color(0.52f, 0.52f, 0.58f),
        Goods.Book => new Color(0.42f, 0.35f, 0.25f),
        _ => Fallback,
    };
}
