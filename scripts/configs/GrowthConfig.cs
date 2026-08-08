using System;

namespace Bianjing;

/// <summary>
/// 坊区生长配置：村民自建住宅的选址打分/地价、升级、扩建与布门
/// （业务归属：ZoneGrowthSystem 全流程、GameState 小路环与布门）。
/// 创业模型：住宅转商铺/工坊的条件与门槛见 EconomyConfig 创业常量段（技能+家庭资金+市场缺口三条件）。
/// </summary>
public static class GrowthConfig
{
    /// <summary>住宅四周自动生成的小路环宽度（格）。</summary>
    public const int LaneRing = 1;

    // ---- 选址偏好打分（可叠加：河边十字路口 = 主路+辅路+河道 分最高）----

    /// <summary>选址扫描半径（米）：占地外扩此距内找偏好要素（主/辅路、河道、邻居）。</summary>
    public const int SiteScanDist = 4;

    /// <summary>选址分项：主路 / 辅路 / 河道各计一次，可叠加；邻居改按密度计分（见下）。
    /// 批次六十八：河流降权（1.5→0.5）——河道只是基础加分项，村民建房优先贴主路辅路。</summary>
    public const double SiteMainRoadScore = 3;
    public const double SiteSideRoadScore = 2;
    public const double SiteRiverScore = 0.5;

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

    // ---- 地价（需求 §4.1 四级：资源点近旁 2,000 / 普通 2,500 / 临街 3,750 / 城中心 6,250 文；
    // 批次七十：在上一轮减半基础上再减半，寄居者更快攒够自建）----

    /// <summary>资源点近旁地价（文）：近树/近水的宅基地最贱，鼓励定居者近资源谋生。</summary>
    public const long LandPriceResource = 2_000;

    /// <summary>普通宅基地地价（文）。</summary>
    public const long LandPricePlain = 2_500;

    /// <summary>临街地价（文）：选址分达“临街档”（贴主/辅路或成片聚居）即按此计价。</summary>
    public const long LandPriceStreet = 3_750;

    /// <summary>城中心地价（文）：选址高分（主路十字路口/密集聚居）按此计价。</summary>
    public const long LandPriceCenter = 6_250;

    /// <summary>选址分分档：≥ LandPriceCenterScore 按城中心计价；≥ LandPriceStreetScore 按临街计价；否则普通地价。</summary>
    public const double LandPriceCenterScore = 6;
    public const double LandPriceStreetScore = 3;

    /// <summary>公式：选址分 + 是否近资源点（树/水） → 地价（文）。</summary>
    public static long LandPriceOf(double siteScore, bool nearResource) =>
        nearResource ? LandPriceResource
        : siteScore >= LandPriceCenterScore ? LandPriceCenter
        : siteScore >= LandPriceStreetScore ? LandPriceStreet
        : LandPricePlain;

    // ---- 升级 ----

    /// <summary>建筑每日升级概率 / 失修不升门槛（完好度）/ 每级所需吸引力系数。</summary>
    public const float LevelUpChancePerDay = 0.02f;
    public const float LevelUpMinCondition = 60f;
    public const float LevelUpDesirPerLevel = 1.2f;

    /// <summary>全城自发工商户占比封顶（约十间住宅出两三家）。</summary>
    public const float BizRatioCap = 0.3f;

    /// <summary>住宅扩建边长上限（米）：初始建房尺寸与拥挤扩建均不超此限（批次六十六 8→6）。</summary>
    public const int ExpandMaxSide = 6;

    // ---- 初始建房尺寸（批次六十六：默认 2×2，按资产阶梯放大，人口多者再 +1，上限 6×6）----

    /// <summary>初始建房边长按预算（文）阶梯：索引 0 起对应边长 2..6（预算达阈值即起更大宅；
    /// 目标边长无合法落位时由 TryBuildHouse 逐档退小）。
    /// 批次七十：门槛随地价再减半（6,000/15,000/35,000/75,000）——平民起步更容易盖大宅。</summary>
    public static readonly long[] HouseSideByAssets = { 0, 6_000, 15_000, 35_000, 75_000 };

    /// <summary>家庭人口达此数时初始边长再 +1（人多家业大，起手就盖大宅；上限仍受 ExpandMaxSide 约束）。</summary>
    public const int HouseSidePeopleBonus = 5;

    // ---- 吸引力（原 DesirabilityConfig 并入）：道路临街加成，业务归属 DesirabilitySystem；
    // 建筑自身的加成/污染在 buildings.json 的 desirabilityBonus/pollution 字段，数据驱动不在此 ----

    /// <summary>主路 / 辅路每格的吸引力幅度（除以密度归一系数后泼溅；桥面不加成）。</summary>
    public const float DesirMainRoadBonus = 1.0f;
    public const float DesirSideRoadBonus = 0.4f;

    /// <summary>幅度归一系数（1m 格密度是旧版 4m 格的 16 倍，不除会把吸引力场吹胀十几倍）。</summary>
    public const float DesirRoadScale = 16f;

    /// <summary>道路吸引力泼溅半径（米，线性衰减）。</summary>
    public const float DesirRoadRadius = 12f;

    // ---- 创业 ----

    /// <summary>住宅转业（商铺/工坊）的最小占地（平米）：起步 2×2=4 即够格开店（批次八十三放宽）。</summary>
    public const int ConvertMinArea = 4;

    // ---- 布门 ----

    /// <summary>每多少格占地增设 1 个后门（大门恒 1 个，后门数 = max(1, 占地格数 / 本值)）。</summary>
    public const int CellsPerBackDoor = 64;

    /// <summary>相邻门之间的最小间距（格，切比雪夫）：先按此间距分散布门，凑不足再放宽。</summary>
    public const int MinDoorGap = 2;
}
