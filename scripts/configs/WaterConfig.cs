namespace Bianjing;

/// <summary>
/// 水系生成配置（业务归属：RiverGenerator 生成一次，随存档保存）：
/// 一条贯穿全图的干流 + 递归分叉的支流/小溪（树状水系，带水流方向）+ 若干扭曲大湖（含湖中岛、坐落河上形成入/出水口）。
/// 顶点高度场下水系不只标水面格，还按深度把河床顶点下压（中心深、边缘浅），
/// 岸形由地势自然涌现：平原岸缓入水成浅滩，山体被河切穿处成峡谷陡岸。
/// 集中调参遵循 configs/*Config.cs 约定。
/// </summary>
public static class WaterConfig
{
    // ---- 水位与河床深度（顶点高度场）----

    /// <summary>全图统一水面高度（米，低于平原基准 0）。后期水源模块/分段水位接入时，
    /// 换掉 WaterLevelAt 的实现即可，调用方无需改动。</summary>
    public const float WaterLevel = -0.5f;

    /// <summary>某格的水面高度：本版全图统一返回 WaterLevel（分段水位预留收口）。</summary>
    public static float WaterLevelAt(Godot.Vector2I c) => WaterLevel;

    /// <summary>河床下压深度（米，水面以下）：水体边缘 / 深水中心，按离岸距离插值——
    /// 边缘浅压出浅滩带，中心深压出河槽/湖盆。</summary>
    public const float BedDepthEdge = 0.3f;
    public const float BedDepthCenter = 1.6f;

    /// <summary>达到满深度的离岸距离（米）：离岸超过此距的水格一律按中心深度下压。</summary>
    public const int BedFalloffDist = 6;

    // ---- 主干河（一条完整干流，自西源蜿蜒东流入海口）----

    /// <summary>河口（下游）最大河宽（米）：干流越往下游越宽。</summary>
    public const int MainWidthMax = 16;

    /// <summary>源头（上游）最小河宽（米）。</summary>
    public const int MainWidthMin = 7;

    /// <summary>干流蜿蜒摆幅（米）：中心线相对直线的最大纵向偏移。</summary>
    public const int MainMeanderAmp = 96;

    /// <summary>干流蜿蜒波长（米）：越大越舒缓。</summary>
    public const int MainMeanderWave = 260;

    // ---- 支流树（二叉树式递归分叉，水流指向汇入的母河）----

    /// <summary>一级支流数量范围（条）：直接从干流分叉。</summary>
    public const int PrimaryBranchMin = 3;
    public const int PrimaryBranchMax = 5;

    /// <summary>每条支流再分叉的子支（小溪）数量范围。</summary>
    public const int ChildBranchMin = 1;
    public const int ChildBranchMax = 2;

    /// <summary>支流递归层数（干流之下）：2 = 支流 + 小溪。</summary>
    public const int TributaryDepth = 2;

    /// <summary>一级支流初始河宽（米）；逐级 ×ChildWidthFactor 变细。</summary>
    public const float PrimaryWidth = 6f;
    public const float ChildWidthFactor = 0.55f;

    /// <summary>一级支流行进长度范围（米）；逐级 ×ChildLenFactor 变短。</summary>
    public const int PrimaryLenMin = 140;
    public const int PrimaryLenMax = 320;
    public const float ChildLenFactor = 0.6f;

    /// <summary>支流分叉张角（弧度）：子支相对母流方向偏转的角度基准。</summary>
    public const double BranchAngle = 0.6;

    /// <summary>支流每步方向抖动幅度（弧度）：越大越蜿蜒。</summary>
    public const double TributaryWaver = 0.34;

    // ---- 湖泊（扭曲边缘的大湖，含湖中岛，坐落河上自然连通）----

    /// <summary>坐落于河网之上的大湖数量范围（座）：天然带入水口/出水口。</summary>
    public const int RiverLakeMin = 2;
    public const int RiverLakeMax = 3;

    /// <summary>离河独立的小湖数量范围（座）：另凿一条出水渠连向最近水体。</summary>
    public const int SoloLakeMin = 1;
    public const int SoloLakeMax = 2;

    /// <summary>大湖基础半径范围（米）。</summary>
    public const int BigLakeRadiusMin = 30;
    public const int BigLakeRadiusMax = 52;

    /// <summary>小湖基础半径范围（米）。</summary>
    public const int SoloLakeRadiusMin = 16;
    public const int SoloLakeRadiusMax = 28;

    /// <summary>湖岸扭曲幅度（0-1）：多正弦谐波叠加使湖缘呈不规则湾汊。</summary>
    public const double LakeEdgeWaviness = 0.42;

    /// <summary>大湖生成湖中岛的概率，以及每湖最多岛数。</summary>
    public const float IslandChance = 0.7f;
    public const int IslandMaxPerLake = 2;

    /// <summary>湖中岛半径相对湖半径的比例范围。</summary>
    public const float IslandRadiusMin = 0.12f;
    public const float IslandRadiusMax = 0.24f;

    /// <summary>独立小湖出水渠宽度（米）与最大探测连通距离（米）。</summary>
    public const int OutletChannelWidth = 3;
    public const int OutletMaxDist = 120;
}
