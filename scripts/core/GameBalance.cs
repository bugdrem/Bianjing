namespace Bianjing;

/// <summary>
/// 全局平衡调参中枢：时间换算、移动速度、模型缩放、道路/生长等比例集中于此，
/// 后期只需改动本文件的常量即可整体调节（引用处会随之变化）。
/// 数值用 const 便于在其它 const 上下文（如 MaintenanceSystem.Days）中直接引用。
/// </summary>
public static class GameBalance
{
    /// <summary>时间换算：一天 24 小时（= 12 时辰）、一月 12 天、一年 12 月。</summary>
    public static class Time
    {
        public const int HoursPerDay = 24;
        public const int DaysPerMonth = 12;
        public const int MonthsPerYear = 12;

        /// <summary>1x 速度下一个游戏小时对应的真实秒数（速度主控旋钮）。
        /// 值取自旧版基准 7200 秒/年 ÷ (24×30×12) ≈ 0.833 秒/时；
        /// 日/年长度 = 日历天数 × 本值，故减少每月天数即缩短一年时长。</summary>
        public const float SecondsPerGameHour = 7200f / (24 * 30 * 12);
    }

    /// <summary>移动速度（米/秒制，1m 格）：基础速度与各路面/脱路系数。</summary>
    public static class Movement
    {
        public const float BaseSpeed = 5f;

        /// <summary>脱离道路的减速惩罚系数。</summary>
        public const float OffRoadFactor = 0.35f;

        /// <summary>各道路种类的移速系数：主路最快、小路最慢；桥面（RoadKind.None 但 HasRoad）同辅路。</summary>
        public const float SpeedMain = 1.2f;
        public const float SpeedSide = 1.0f;
        public const float SpeedLane = 0.7f;
        public const float SpeedBridge = 1.0f;

        /// <summary>脚下道路种类的移速系数（仅路面调用；脱路由调用方按 OffRoadFactor 处理）。</summary>
        public static float RoadSpeedFactor(RoadKind kind) => kind switch
        {
            RoadKind.Main => SpeedMain,
            RoadKind.Side => SpeedSide,
            RoadKind.Lane => SpeedLane,
            _ => SpeedBridge, // None 且 HasRoad（桥面）
        };

        /// <summary>寻路权重（以主路为 1.0 基准，越慢代价越高，使寻路最小化实际旅行时间）。</summary>
        public static float RoadWeight(RoadKind kind) => SpeedMain / RoadSpeedFactor(kind);
    }

    /// <summary>村民表现层参数。</summary>
    public static class Villager
    {
        /// <summary>成年人模型整体缩放（1.0 为原始大小；儿童在此基础上再按年龄折算）。</summary>
        public const float ModelScale = 0.25f;

        /// <summary>新生儿体型占成人的比例（体型从此值线性生长到成年门槛处的 1.0）。</summary>
        public const float ChildMinScale = 0.4f;
    }

    /// <summary>村民寿命与死亡：成年门槛、最大寿数、死亡率随龄上升的曲线参数（后期可接入健康值）。</summary>
    public static class Life
    {
        /// <summary>成年门槛（岁）：达到即为成年，可打工/婚嫁/繁育/立户。</summary>
        public const int AdultAgeYears = 16;

        /// <summary>最大寿数（岁）：达到必亡，任何个体不超过此龄。</summary>
        public const int MaxAgeYears = 120;

        /// <summary>婚配年龄上限（岁）。</summary>
        public const int MarriageMaxAgeYears = 50;

        /// <summary>生育年龄区间（岁）：下限同成年门槛，上限见此。</summary>
        public const int FertileMinAgeYears = AdultAgeYears;
        public const int FertileMaxAgeYears = 45;

        /// <summary>任何年龄的基础年死亡率（意外/疾病等与龄无关的底噪）。</summary>
        public const float BaseAnnualMortality = 0.005f;

        /// <summary>死亡率陡增起点（岁）：主要死亡区间由此展开（约 55-65）。</summary>
        public const int MortalityRampAgeYears = 55;

        /// <summary>陡增起点处的年死亡率系数（Gompertz 幅值 A）。</summary>
        public const float MortalityAtRamp = 0.03f;

        /// <summary>Gompertz 尺度（岁）：越小死亡率随龄上升越陡。</summary>
        public const float MortalityScaleYears = 8f;
    }

    /// <summary>村民作息：有职者固定早晚上下班、轮休周期。</summary>
    public static class Schedule
    {
        /// <summary>上班时段起止时（含起不含止）：早晨上工、下午收工。</summary>
        public const int WorkStartHour = 6;
        public const int WorkEndHour = 18;

        /// <summary>轮休周期（天）：每满此天数休息一天（按个体错峰，不全城同日停工）。</summary>
        public const int RestCycleDays = 5;
    }

    /// <summary>退休制度：致仕年龄与退休后的行为分流（富户闲逛/寒门采薪）。</summary>
    public static class Retire
    {
        /// <summary>普通雇工退休年龄（岁）：达此退出当前岗位。</summary>
        public const int Age = 50;

        /// <summary>店主/家族产业内的人延迟退休年龄（岁）。</summary>
        public const int FamilyBusinessAge = 60;

        /// <summary>家庭人均资产高于此视为富裕（退休后闲逛而非采集）。</summary>
        public const double WealthyPerCapitaAssets = 200;
    }

    /// <summary>村民自建住宅相关。</summary>
    public static class Growth
    {
        /// <summary>住宅四周自动生成的小路环宽度（格）。</summary>
        public const int LaneRing = 1;

        /// <summary>每多少格占地增设 1 个后门（大门恒 1 个，后门数 = max(1, 占地格数 / 本值)）。</summary>
        public const int CellsPerBackDoor = 64;

        /// <summary>相邻门之间的最小间距（格，切比雪夫）：先按此间距分散布门，凑不足再放宽。</summary>
        public const int MinDoorGap = 2;
    }
}
