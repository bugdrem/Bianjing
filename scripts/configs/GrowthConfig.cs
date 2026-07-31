using System;

namespace Bianjing;

/// <summary>
/// 坊区生长配置：村民自建住宅的选址打分/地价、升级与转业分布、扩建与布门
/// （业务归属：ZoneGrowthSystem 全流程、GameState 小路环与布门）。
/// 转业模型：住宅升级掷中时按临路档位（主路/辅路/仅小路）取一组去向分布，余量 = 维持住宅照常升级。
/// </summary>
public static class GrowthConfig
{
    /// <summary>住宅四周自动生成的小路环宽度（格）。</summary>
    public const int LaneRing = 1;

    /// <summary>建房基价（地价下限，不论地段的营造成本）。</summary>
    public const double HouseBaseCost = 20;

    // ---- 选址偏好打分（可叠加：河边十字路口 = 主路+辅路+河道 分最高）----

    /// <summary>选址扫描半径（米）：占地外扩此距内找偏好要素（主/辅路、河道、邻居）。</summary>
    public const int SiteScanDist = 4;

    /// <summary>选址分项：主路 / 辅路 / 河道各计一次，可叠加；邻居改按密度计分（见下）。</summary>
    public const double SiteMainRoadScore = 3;
    public const double SiteSideRoadScore = 2;
    public const double SiteRiverScore = 1.5;

    /// <summary>邻居密度计分：扫描范围内每栋建筑（按实例去重）加此分，计分栋数封顶——
    /// 3 栋即满 3.6 分与主路同档，使民居明显倾向贴着已有建筑成片聚居（可脱离主辅路向外扩片）。</summary>
    public const double SiteNeighborScorePerBuilding = 1.2;
    public const int SiteNeighborCountCap = 3;

    /// <summary>选址“足够好”阈值：达标候选按分数加权抽签（见 SiteWeightOf）；
    /// 无达标者退而选可负担候选中分最高处。</summary>
    public const double SiteThreshold = 3;

    /// <summary>加权抽签的分数幂次：权重 = 分数^此幂——越大越向高分地段（邻居多/地段好）集中，
    /// 但达标的冷清地段仍保留小概率中签（如两个十字路口一热闹一空旷：大部分人挨着热闹处建，
    /// 少量人仍去空旷路口落户）。</summary>
    public const double SitePickPower = 2;

    /// <summary>公式：选址分 → 抽签权重（幂次放大分差；下限 0.1 防零分候选权重归零）。</summary>
    public static double SiteWeightOf(double score) =>
        Math.Pow(Math.Max(0.1, score), SitePickPower);

    /// <summary>每点选址分的地价系数：贴主路/临河等好地段越贵。</summary>
    public const double LandPricePerScore = 5;

    /// <summary>公式：选址分 → 地价（贯）= 基价 + 系数 × 分。</summary>
    public static double LandPriceOf(double siteScore) =>
        HouseBaseCost + LandPricePerScore * Math.Max(0, siteScore);

    // ---- 升级 ----

    /// <summary>建筑每日升级概率 / 失修不升门槛（完好度）/ 每级所需吸引力系数。</summary>
    public const float LevelUpChancePerDay = 0.02f;
    public const float LevelUpMinCondition = 60f;
    public const float LevelUpDesirPerLevel = 1.2f;

    /// <summary>全城自发工商户占比封顶（约十间住宅出两三家）。</summary>
    public const float BizRatioCap = 0.3f;

    /// <summary>住宅扩建边长上限（米）。</summary>
    public const int ExpandMaxSide = 8;

    // ---- 吸引力（原 DesirabilityConfig 并入）：道路临街加成，业务归属 DesirabilitySystem；
    // 建筑自身的加成/污染在 buildings.json 的 desirabilityBonus/pollution 字段，数据驱动不在此 ----

    /// <summary>主路 / 辅路每格的吸引力幅度（除以密度归一系数后泼溅；桥面不加成）。</summary>
    public const float DesirMainRoadBonus = 1.0f;
    public const float DesirSideRoadBonus = 0.4f;

    /// <summary>幅度归一系数（1m 格密度是旧版 4m 格的 16 倍，不除会把吸引力场吹胀十几倍）。</summary>
    public const float DesirRoadScale = 16f;

    /// <summary>道路吸引力泼溅半径（米，线性衰减）。</summary>
    public const float DesirRoadRadius = 12f;

    // ---- 转业 ----

    /// <summary>住宅转业（商铺/工坊）的最小占地（平米）：起步 2×2=4，扩建一次（2×3=6）即够格开店。</summary>
    public const int ConvertMinArea = 6;

    /// <summary>符合条件的路边住宅每日转业概率（独立于升级链；全城工商占比封顶在 TryConvertHouse 内约束）。</summary>
    public const float ConvertChancePerDay = 0.03f;

    /// <summary>转业临路判定半径（米）：占地边缘到主/辅路在此距内才算“贴近”该级道路。</summary>
    public const int ConvertRoadDist = 6;

    /// <summary>贴近主路：商铺大概率 / 工坊中概率 /（余 0.2）更高级住宅小概率。</summary>
    public const double MainShopChance = 0.5;
    public const double MainWorkshopChance = 0.3;

    /// <summary>贴近辅路（不贴主路）：工坊与住宅（余 0.5）都高，商铺小概率。</summary>
    public const double SideShopChance = 0.1;
    public const double SideWorkshopChance = 0.4;

    /// <summary>只靠自带小路：高概率（余 0.85）维持住宅升级，小概率转工坊，不出商铺。</summary>
    public const double LaneShopChance = 0;
    public const double LaneWorkshopChance = 0.15;

    // ---- 布门 ----

    /// <summary>每多少格占地增设 1 个后门（大门恒 1 个，后门数 = max(1, 占地格数 / 本值)）。</summary>
    public const int CellsPerBackDoor = 64;

    /// <summary>相邻门之间的最小间距（格，切比雪夫）：先按此间距分散布门，凑不足再放宽。</summary>
    public const int MinDoorGap = 2;
}
