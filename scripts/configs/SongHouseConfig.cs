using System;

namespace Bianjing;

/// <summary>
/// 宋式建筑尺度配置（批次一百零一）：把「房子该多高」从各建筑各自为政的
/// <c>BuildingDef.Height</c> 魔法值，改成**按占地推导的统一规则**。
///
/// **为什么要改**：原 <see cref="BuildingModelFactory.Compose"/> 用
/// <c>height = Def.Height × (1 + 0.35×(Level−1))</c> 当房体高，而 Height 是每个建筑
/// 手调的魔法值——民居 0.55（总高 1.05m）、宅邸 1.6、宫殿 3、王爷府 4。
/// 村民实际高 <see cref="VillagerConfig.ModelScale"/> × 1.35 ≈ **0.57m**，
/// 于是 2×2 民居只有 1.85 倍人高（正常小宅应 2.5~3 倍），而 12×12 王爷府 5.4m 又已经对了——
/// **小建筑整体矮了约四成**，这是"房屋建模太差"在尺度上的主因。
///
/// **新规则**：墙高按占地面积开方推导（大宅墙高是形制要求，不是随手调），
/// 屋顶高按宋代举折比例随墙高走，等级只做微调（不推翻形制）。
/// </summary>
public static class SongHouseConfig
{
    // ---- 墙高：按占地开方推导（米）----

    /// <summary>墙高基准系数：墙高 = 此值 × √(占地格数)。2×2 → 1.1m、4×4 → 2.2m、12×12 → 6.6m（封顶）。</summary>
    public const float WallPerRootArea = 0.55f;

    /// <summary>墙高下限（米）：再小的宅子也要高过人头一倍半，否则读作"窝棚"。</summary>
    public const float WallMin = 0.95f;

    /// <summary>墙高上限（米）：宋代民居多为单层，殿阁方高；封顶避免巨宅墙高失控。</summary>
    public const float WallMax = 3.6f;

    /// <summary>等级对墙高的微调幅度（每级 ±）：升级长高但不改形制（±8%/级）。</summary>
    public const float LevelWallStep = 0.08f;

    // ---- 屋顶：宋代举折（屋面坡度与出檐）----

    /// <summary>屋顶高 / 墙高 比。宋《营造法式》举屋之法，殿阁举高约墙高之半，民居略陡。
    /// 取 0.5 使 2×2 民居总高 ≈ 1.65m = 2.9 倍人高。</summary>
    public const float RoofToWall = 0.5f;

    /// <summary>出檐倍数（檐口相对房体每边外扩）：宋式出檐深远，民居约 1.15，殿阁 1.25。</summary>
    public const float EaveOverhangCommon = 1.15f;
    public const float EaveOverhangGrand = 1.25f;

    /// <summary>檐口厚度（米）。</summary>
    public const float EaveThickness = 0.12f;

    /// <summary>屋脊粗细（米）。</summary>
    public const float RidgeThickness = 0.14f;

    // ---- 台基 ----

    /// <summary>台基露明高度（米）：房体坐在台基上，露一道压阑石，避免"贴地薄板"。</summary>
    public const float PlinthReveal = 0.12f;

    /// <summary>台基相对房体的外扩（米）。</summary>
    public const float PlinthRing = 0.22f;

    // ---- 院落（三种布局共用的度量）----

    /// <summary>院墙高（米）：宋院墙低于房体，"参考宋院图比例 totalH/wallH ≈ 2.2"。</summary>
    public const float WallHeightCourtyard = 1.1f;

    /// <summary>院墙厚（米）：参考图视觉是细线，0.12 读作"线"而非"砖墩"。</summary>
    public const float CourtyardWallThick = 0.12f;

    /// <summary>院墙压顶瓦条高/宽（米）：参考图最显眼的"深色顶冠线"。</summary>
    public const float CourtyardCapH = 0.06f;
    public const float CourtyardCapW = 0.18f;

    /// <summary>影壁（照壁）宽与高（米）：进门迎面障门的立壁，宋宅院标配。</summary>
    public const float ScreenWallW = 1.2f;
    public const float ScreenWallH = 1.5f;

    /// <summary>大门高（米）：临街大门 1.2、后门 1.0（沿用 BuildingModelFactory 旧值）。</summary>
    public const float MainDoorH = 1.2f;
    public const float BackDoorH = 1.0f;

    // ---- 宋式屋顶制式（《营造法式》"厦两头/不厦两头"）----

    /// <summary>屋顶制式。</summary>
    public enum RoofForm
    {
        /// <summary>不厦两头 = 悬山（两坡挑出山墙）：宋代民居基本结构，"一般的民宅多以不厦两头
        /// 即被后世称为悬山式的为基本结构的合院制式"。</summary>
        Gable,

        /// <summary>厦两头 = 庑殿（四面坡 + 正脊）：官僚贵胄与殿阁，
        /// 《营造法式》规定六品以上官员宅第外部可建乌头门或门屋。</summary>
        Hip,

        /// <summary>歇山（九脊殿）：等级居庑殿之下、悬山之上，官署/富家用。</summary>
        GableHip,

        /// <summary>攒尖（斗尖顶）：亭榭楼阁，公共设施点缀用。</summary>
        Pyramid,
    }

    /// <summary>某等级建筑是否够格用庑殿/歇山：民居一律悬山，官署与贵胄方可用高等屋顶。</summary>
    public static RoofForm RoofFormOf(BuildingDef def, int level)
    {
        if (def == null)
            return RoofForm.Gable;
        // 王府/宫殿：庑殿顶（最高等级）
        if (def.Id is "prince_mansion" or "palace")
            return RoofForm.Hip;
        // 官署/官营/朝廷：歇山
        if (def.Category is "official" or "court")
            return RoofForm.GableHip;
        // 大宅邸满级：歇山（富户可僭越一档，宋人实际也这么干）
        if (def.Id == "mansion" && level >= 3)
            return RoofForm.GableHip;
        return RoofForm.Gable;
    }

    /// <summary>屋顶制式 → 出檐倍数。</summary>
    public static float EaveOverhangOf(RoofForm form) => form switch
    {
        RoofForm.Hip => EaveOverhangGrand,
        RoofForm.GableHip => EaveOverhangGrand,
        _ => EaveOverhangCommon,
    };

    // ---- 高度计算入口 ----

    /// <summary>墙高（米）：按占地开方推导，等级微调，夹在 [WallMin, WallMax]。</summary>
    public static float WallHeightOf(int footX, int footY, int level = 1)
    {
        float area = Math.Max(1, footX * footY);
        float h = WallPerRootArea * MathF.Sqrt(area);
        h *= 1f + LevelWallStep * (level - 1);
        return Math.Clamp(h, WallMin, WallMax);
    }

    /// <summary>屋顶高（米）= 墙高 × 举折比。</summary>
    public static float RoofHeightOf(float wallH) => wallH * RoofToWall;

    /// <summary>建筑总高（米）= 墙高 + 屋顶高。</summary>
    public static float TotalHeightOf(int footX, int footY, int level = 1)
    {
        float w = WallHeightOf(footX, footY, level);
        return w + RoofHeightOf(w);
    }

    /// <summary>厢房墙高 = 正房墙高 × 此比（"正房较厢房高大壮丽"，宋院图主从分明）。</summary>
    public const float WingWallRatio = 0.72f;

    /// <summary>倒座（南房）墙高 = 正房墙高 × 此比。</summary>
    public const float RearHallWallRatio = 0.78f;

    /// <summary>门楼墙高 = 正房墙高 × 此比（门楼自有小双坡瓦顶，高出围墙）。</summary>
    public const float GateTowerWallRatio = 0.85f;

    /// <summary>挟屋（正房两侧接建的小房）墙高 = 正房墙高 × 此比。</summary>
    public const float AttachedWallRatio = 0.68f;
}
