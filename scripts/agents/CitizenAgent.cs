using System;
using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 居民 3D 代理：按身份/家庭角色驱动日常作息——
/// 受雇者到工作地驻留直至下班；山民伐木/采摘/打猎后去市集交易；
/// 修缮匠巡修公共设施；主妇采购、孩童玩耍、老人闲逛。
/// 出行优先走道路（路网 AStar，带随机车道偏移避免排成一线），可脱离道路直线行走但有大幅减速惩罚；
/// 每帧受邻居分离推力，避免人群叠在一起。
/// 只读写 Citizen 的状态值，不做任何生死/雇佣决策（那是数据层系统的职责）。
/// </summary>
public partial class CitizenAgent : Node3D
{
    // 调参均集中在 configs 目录（MovementConfig/VillagerConfig），此处只留短名转发便于阅读
    private const float BaseSpeed = MovementConfig.BaseSpeed;
    private const float OffRoadFactor = MovementConfig.OffRoadFactor; // 脱离道路的减速惩罚
    private const float TiredThreshold = VillagerConfig.TiredThreshold;
    private const float BoredThreshold = VillagerConfig.BoredThreshold;
    private const float StayUntilDone = 9999f; // 驻留到条件触发（如下班）而非计时（程序哨兵值非调参）
    private const float LaneJitterRange = VillagerConfig.LaneJitterRange; // 车道偏移幅度
    private const float ChopDamage = VillagerConfig.ChopDamage; // 每斧砍伐伤害
    private const double WoodPerHp = VillagerConfig.WoodPerHp; // 血量→柴薪折算：一斧恰好一担

    // 家庭储备目标（份/人）：低于目标一半触发补货/打水，补到目标为止
    private const double FoodPerResident = VillagerConfig.FoodPerResident;
    private const double WoodPerResident = VillagerConfig.WoodPerResident;
    private const double WaterPerResident = VillagerConfig.WaterPerResident;

    /// <summary>就近采集半径（米，见 VillagerConfig）：打水不受此限。</summary>
    private const int ForageRadius = VillagerConfig.ForageRadius;

    public Citizen C { get; }

    /// <summary>剩余路径只读视图（选中居民目标路线绘制用）：null 即当前无路线。</summary>
    public IReadOnlyList<Vector3> PathPoints => _path;
    public int PathIndex => _pathIndex;

    private readonly GameClock _clock;
    private readonly AgentManager _manager;
    private readonly Random _rng = new();

    private MeshInstance3D _lower, _upper, _belt, _sleeveL, _sleeveR, _head, _hair, _hat;
    private StandardMaterial3D _lowerMat, _upperMat, _beltMat, _headMat, _hatMat;

    // 宋人占位模型：头/发盖/上身/袍摆 + 腰带 + 双垂袖 + 冠发，共享网格资源省内存，材质各自持有（袖同上衣、发盖同冠发共用）
    private static readonly BoxMesh SharedBox = new() { Size = Vector3.One };
    private static readonly SphereMesh SharedSphere = new() { Radius = 0.5f, Height = 1f };

    private Node3D _body;
    private bool _spawnPending;

    // ---- 背货可视化：胸前叠货块（驮/挑等后期搬运形态的挂点接口预留） ----
    private Node3D _carryRig;
    private readonly List<MeshInstance3D> _carryBlocks = new();
    private double _carryShownTotal = -1;
    private int _carryShownKinds = -1;

    private List<Vector3> _path;
    private int _pathIndex;
    private float _dwell;
    private Vector2I? _activityCell; // 当前活动目标格（伐木/采摘/拾堆结算用）
    private int _activityAnimalId = -1; // 打猎目标动物 Id
    private int _haulBuildingId = -1; // 挑担目的地建筑（自家或田仓）
    private int _supplyBuildingId = -1; // 为该工坊/商铺采集或采买原料，取到后送入其库
    private string _buyGoodsId = ""; // 前往来源建筑要买的原料
    private int _buySourceId = -1; // 买原料的来源建筑 Id
    private string _consignGoodsId = ""; // 工坊成品外销寄卖中的货品（送达时由铺面付货款）
    private Vector2I? _chopAgain; // 上一斧未砍倒的树：下轮决策继续砍同一棵
    private bool _fieldHarvest; // 背上的货是自家田里拾的收成（优先挑入田仓而非回家）
    private bool _sellFailed; // 上次售卖有卖不掉的尾货：下轮改背回家囤，防反复跑铺子死循环
    private Vector3 _laneJitter; // 个人固定车道偏移，避免全员踩路心叠成一条线
    private int _lookAgeYears = -1;

    public CitizenAgent(Citizen citizen, GameClock clock, AgentManager manager)
    {
        C = citizen;
        _clock = clock;
        _manager = manager;
    }

    public override void _Ready()
    {
        _body = new Node3D();
        AddChild(_body);
        _lower = AddPart(SharedBox, _lowerMat = new StandardMaterial3D());
        _upper = AddPart(SharedBox, _upperMat = new StandardMaterial3D());
        _sleeveL = AddPart(SharedBox, _upperMat); // 垂袖与上衣同料同色，共用材质
        _sleeveR = AddPart(SharedBox, _upperMat);
        _belt = AddPart(SharedBox, _beltMat = new StandardMaterial3D());
        _head = AddPart(SharedSphere, _headMat = new StandardMaterial3D());
        _hair = AddPart(SharedSphere, _hatMat = new StandardMaterial3D()); // 发盖与冠发同色，共用材质
        _hat = AddPart(SharedBox, _hatMat);
        ApplyLook();

        _laneJitter = new Vector3(
            ((float)_rng.NextDouble() * 2f - 1f) * LaneJitterRange,
            0f,
            ((float)_rng.NextDouble() * 2f - 1f) * LaneJitterRange);

        // 搬运挂点：货块挂在身体根节点下随体型缩放，后期驮/挑只需换挂点与排布
        _carryRig = new Node3D { Position = CarryMountOffset(CarryMount.Chest) };
        _body.AddChild(_carryRig);

        if (C.PosValid)
        {
            // 从存档恢复位置，稍作停留后按当前活动重新决策衔接
            Position = new Vector3(C.PosX, 0.2f, C.PosZ);
            _dwell = 0.5f;
        }
        else
        {
            // 迁入者从住所/工商建筑门前出现；HomeId 在同次月结算稍后才指定，首帧再校正一次
            Position = HomePosition();
            _spawnPending = true;
            _dwell = (float)_rng.NextDouble() * 3f;
        }
    }

    private MeshInstance3D AddPart(Mesh mesh, StandardMaterial3D mat)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
        _body.AddChild(mi);
        return mi;
    }

    public override void _Process(double delta)
    {
        if (_spawnPending)
        {
            // 出生/迁入时住所在代理创建之后才分配，首帧从住所建筑重新定位
            _spawnPending = false;
            Position = HomePosition();
        }

        if (_clock.Speed <= 0)
            return;
        float dt = (float)delta * _clock.Speed;

        if (_path != null)
        {
            MoveAlongPath(dt);
        }
        else
        {
            ApplyActivityNeeds(dt);
            _dwell -= dt;
            if (_dwell <= 0f)
            {
                CompleteActivity();
                DecideNext();
            }
        }

        // 碰撞分离：被邻居推开，避免人群重叠
        var push = _manager.SeparationPush(this);
        if (push != Vector3.Zero)
            Position += push * Mathf.Min(dt, 0.1f);

        // 站面贴合：脚下是桥板时抬到桥面顶，下桥回落地面基准（平滑过渡免上下瞬跳）
        float surfaceY = SurfaceYAt(Position);
        if (Mathf.Abs(Position.Y - surfaceY) > 0.001f)
            Position = Position with { Y = Mathf.MoveToward(Position.Y, surfaceY, 1.5f * dt) };

        // 位置回写数据层，随存档保存
        C.PosX = Position.X;
        C.PosZ = Position.Z;
        C.PosValid = true;

        // 背包货物可视化：份数/种类变化才重建，平时零开销
        UpdateCarryDisplay();
    }

    /// <summary>搬运挂点：目前只有胸前抱持；后期肩挑（扁担两端）/畜驮（鞍侧）在此枚举与偏移表扩展。</summary>
    private enum CarryMount
    {
        Chest,
    }

    /// <summary>挂点局部偏移（相对身体根节点，随体型缩放）。</summary>
    private static Vector3 CarryMountOffset(CarryMount mount) => mount switch
    {
        _ => new Vector3(0f, 0.85f, 0.28f), // 胸前抱持：上身前方胸口高处
    };

    /// <summary>背货块重建：每种货一块（公用色表，与地堆/屋内堆同色同货），块高随份数，
    /// 自挂点向上叠放留缝不重合；血量=剩余份数，卖掉/入库/消耗即变矮乃至消失。</summary>
    private void UpdateCarryDisplay()
    {
        double total = C.Pack.Total;
        int kinds = C.Pack.Stacks.Count;
        if (total == _carryShownTotal && kinds == _carryShownKinds)
            return;
        _carryShownTotal = total;
        _carryShownKinds = kinds;

        foreach (var block in _carryBlocks)
            block.QueueFree();
        _carryBlocks.Clear();

        float y = 0f;
        foreach (var s in C.Pack.Stacks)
        {
            float h = 0.1f + 0.15f * (float)Math.Min(1.0, s.Amount / Goods.LoadUnits);
            var mi = new MeshInstance3D
            {
                Mesh = SharedBox,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = GoodsColors.ColorOf(s.GoodsId) },
                Position = new Vector3(0f, y + h / 2f, 0f),
                Scale = new Vector3(0.4f, h, 0.24f),
            };
            _carryRig.AddChild(mi);
            _carryBlocks.Add(mi);
            y += h + 0.02f; // 块间留缝，叠而不重合
        }
    }

    // ---- 决策 ----

    private void DecideNext()
    {
        var gs = GameState.I;

        if (C.AgeYears != _lookAgeYears)
            ApplyLook();

        // 为自家产业采得的原料（背不为空）：优先挑回该工坊/商铺入库，而非回家
        if (!C.Pack.IsEmpty && _supplyBuildingId >= 0)
        {
            if (gs.Buildings.TryGetValue(_supplyBuildingId, out var sup))
            {
                _haulBuildingId = sup.Id;
                StartActivity(ActivityType.Hauling, BuildingAnchor(sup) ?? sup.Origin, 2f);
                return;
            }
            _supplyBuildingId = -1;
        }

        // 背包有货：田里拾的收成先挑入田仓；家里缺这类储备且放得下才搬回家，否则挑去专营铺子卖掉
        if (!C.Pack.IsEmpty)
        {
            if (_fieldHarvest && C.JobKind == JobKind.Employed
                && gs.Buildings.TryGetValue(C.WorkplaceId, out var barn) && !barn.StorageAtCap)
            {
                _haulBuildingId = barn.Id;
                StartActivity(ActivityType.Hauling, BuildingAnchor(barn) ?? barn.Origin, 2f);
            }
            else if (gs.Buildings.TryGetValue(C.HomeId, out var home) && !home.StorageAtCap
                && (HomeWantsPack(gs, home) || _sellFailed))
            {
                // 家里缺这类储备，或市面饱和卖不掉（尾货改囤家，免得反复跑铺子）：背回家入库
                _sellFailed = false;
                _fieldHarvest = false;
                _haulBuildingId = home.Id;
                StartActivity(ActivityType.Hauling, HomeAnchor(), 2f);
            }
            else if (!_sellFailed)
            {
                _fieldHarvest = false;
                StartActivity(ActivityType.Trading, TradeAnchor(gs), 2.5f);
            }
            else
            {
                // 卖不掉且家里也装不下：就近净地卸堆（谁都能拾），腾空背包回到日常循环
                _sellFailed = false;
                _fieldHarvest = false;
                foreach (var s in C.Pack.Stacks.ToArray())
                {
                    C.Pack.Take(s.GoodsId, s.Amount);
                    if (s.GoodsId != Goods.Water)
                        gs.DropNearby(MapGrid.WorldToCell(Position), s.GoodsId, s.Amount);
                }
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            }
            return;
        }

        // 背包已空：上一轮的补料目标与供货认领一并释放（本轮按最新需求重新认领，
        // 也防采集落空后残留的 _supplyBuildingId 把下次采获误送去铺子）
        _supplyBuildingId = -1;
        ClearClaim();

        // 上一斧没砍倒：原地继续砍同一棵树（直到砍倒或树已不在）；放在孩童分支前，半大孩子帮工砍柴同样适用
        if (_chopAgain != null)
        {
            var stump = _chopAgain.Value;
            _chopAgain = null;
            if (gs.Map.CellAt(stump).HasTree)
            {
                StartActivity(ActivityType.Logging, stump, 4f);
                return;
            }
        }

        if (C.IsChild)
        {
            // 孩童分龄：三岁前不出门；六岁前只在家门口玩；六岁起满村玩耍；
            // 十岁起家中缺储备时帮忙跑腿采集；十六岁成年才可受雇（JobSystem 已限）
            if (C.AgeYears < 3)
            {
                StartRestHome(gs, 5f); // 幼儿在屋里由家人照看，不出门
                return;
            }
            if (C.Fatigue >= TiredThreshold)
            {
                StartRestHome(gs, 4f); // 玩累/帮工累了回屋歇着
                return;
            }
            if (C.AgeYears < 6)
            {
                StartActivity(ActivityType.Playing, NearbyRoadCell(HomeCell(), 6), 3f); // 只在家门口玩耍
                return;
            }
            if (C.AgeYears >= 10 && TryRestockHome(gs))
                return; // 帮家里补储备：采摘/伐木/打水（孩子没钱，采买分支自然走不到）
            StartActivity(ActivityType.Playing, NearbyRoadCell(HomeCell(), 32), 3f); // 满村撒欢
            return;
        }

        // 有职者按固定作息（早晨上班、下午下班，每 RestCycleDays 天轮休一天）：作息优先于疲劳——
        // 上班时段照常上工干活，非班点回家歇息，休息日按面板状态自行安排。
        if (C.JobKind == JobKind.Employed && gs.Buildings.TryGetValue(C.WorkplaceId, out var wp))
        {
            if (IsRestDayToday())
            {
                SpendRestDay(gs); // 休息日：按面板属性 在家/闲逛/采集/砍柴
                return;
            }
            if (IsWorkHourNow())
            {
                // 修缮房雇工：外出巡修最破旧的公共建筑（而非坐班）
                if (wp.Def.Id == "repairhouse")
                {
                    StartRepairing(gs, wp);
                    return;
                }
                // 农夫：田面有收成堆先去拾担（拾完下一轮决策挑入田仓），否则照常驻留耕作
                var fieldPile = FindFieldPile(gs, wp);
                if (fieldPile != null)
                {
                    StartActivity(ActivityType.PickingUp, new Vector2I(fieldPile.X, fieldPile.Y), 2f);
                    return;
                }
                // 工坊/商铺：先处理补料/成品外销物流，无事可做才站堂加工
                if (Goods.IsCraftable(wp.Specialty) && StartCraftLogistics(gs, wp))
                    return;
                // 受雇者/店主：进工作地驻留，到点（下午）自然下班
                StartWorkAt(wp);
                return;
            }
            // 清晨未到点 / 傍晚收工后 / 夜间：兴趣低则出门散心，否则回家歇息
            if (C.Fun < BoredThreshold)
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            else
                StartRestHome(gs, 5f);
            return;
        }

        // 退休者（过退休年龄且已离岗）：富户闲逛、寒门采薪（行为见 SpendRetirement）
        if (IsRetiredNow())
        {
            SpendRetirement(gs);
            return;
        }

        if (C.Fatigue >= TiredThreshold)
        {
            // 累了：兴趣太低先闲逛散心，否则回家歇息（进屋歇着，而非站在门口路边）
            if (C.Fun < BoredThreshold)
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            else
                StartRestHome(gs, 5f);
            return;
        }

        // 家中储备告急（食/柴/水低于目标一半）：不当班的成年人先补家再谋生
        if (TryRestockHome(gs))
            return;

        // 本楼铺面缺料：商铺/工坊的活优先派给没有正职的居住者（认领后外部散户自动让位）
        if (TryServeHomeBusiness(gs))
            return;

        if (C.JobKind == JobKind.Logger)
        {
            StartDemandForaging(gs);
            return;
        }

        if (C.Gender == Gender.Female && C.IsMarried && C.Activity != ActivityType.Shopping
            && _rng.NextDouble() < VillagerConfig.HousewifeShopChance)
        {
            // 主妇：外出采购，回程自然衔接下次决策
            StartActivity(ActivityType.Shopping, ShoppingAnchor(gs), 2.5f);
            return;
        }

        if (C.IsElder)
        {
            if (_rng.NextDouble() < VillagerConfig.ElderStrollChance)
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            else
                StartRestHome(gs, 4f);
            return;
        }

        // 无业成年人：市面缺货才上山采集卖钱，否则闲度
        StartDemandForaging(gs);
    }

    /// <summary>背上的货要不要往家搬：水必回家；食物/柴仅在家中低于储备目标时才囤（达标即挑去卖钱，不再无限囤家）；
    /// 其余货品（矿/盐/成品等）沿旧例放得下就搬回家。</summary>
    private bool HomeWantsPack(GameState gs, BuildingInstance home)
    {
        string goodsId = C.PackGoodsId;
        if (goodsId == Goods.Water)
            return true;
        int residents = gs.HomeResidents(home.Id);
        if (Goods.IsFood(goodsId))
        {
            double stock = 0;
            foreach (var id in Goods.FoodKinds)
                stock += home.Inv.AmountOf(id);
            return stock < residents * FoodPerResident;
        }
        if (goodsId == Goods.Wood)
            return home.Inv.AmountOf(Goods.Wood) < residents * WoodPerResident;
        return true;
    }

    private void StartActivity(ActivityType activity, Vector2I? targetCell, float dwellSeconds)
    {
        C.Activity = activity;
        _dwell = dwellSeconds;
        _activityCell = targetCell;
        _activityAnimalId = -1;

        if (targetCell == null)
        {
            _path = null; // 无处可去：原地待着
            return;
        }
        BuildPathTo(MapGrid.CellToWorld(targetCell.Value));
    }

    /// <summary>回家歇息：目标是屋内站位（而非邻路锚点），无家则原地待着。</summary>
    private void StartRestHome(GameState gs, float dwellSeconds)
    {
        C.Activity = ActivityType.RestHome;
        _dwell = dwellSeconds;
        _activityCell = null;
        _activityAnimalId = -1;

        if (gs.Buildings.TryGetValue(C.HomeId, out var home))
            BuildPathTo(IndoorStand(home));
        else
            _path = null; // 无家可归：原地歇脚
    }

    /// <summary>屋内站位：以就近门的门内格为起点向建筑中心推进 80%，保证落在建筑占地内（半透明建筑可透视屋内人）；
    /// 无门时退回邻路锚点。</summary>
    private Vector3 IndoorStand(BuildingInstance b)
    {
        var door = NearestDoor(b);
        Vector3 anchorWorld;
        if (door.HasValue)
            anchorWorld = MapGrid.CellToWorld(door.Value.Inside);
        else
        {
            var road = GameState.I.Map.FindAdjacentRoad(b.Origin, b.FootX, b.FootY);
            anchorWorld = road != null ? MapGrid.CellToWorld(road.Value) : BuildingCenter(b);
        }
        return anchorWorld.Lerp(BuildingCenter(b), 0.8f);
    }

    /// <summary>受雇者上班/修缮匠巡修：站进目标建筑内部，驻留至疲劳攻顶。</summary>
    private void StartWorkAt(BuildingInstance wp, ActivityType activity = ActivityType.Working)
    {
        C.Activity = activity;
        _dwell = StayUntilDone;
        _activityCell = null;
        _activityAnimalId = -1;
        BuildPathTo(IndoorStand(wp));
    }

    // ---- 作息：固定上下班 + 轮休 ----

    /// <summary>今日是否为本人的休息日：按绝对天数每 RestCycleDays 天休一天，叠加个体 Id 错峰（不全城同日停工）。</summary>
    private bool IsRestDayToday()
        => (_clock.AbsoluteDay + C.Id) % TimeConfig.RestCycleDays == 0;

    /// <summary>当前是否处于上班时段（早晨上工、下午收工，不含收工时）。</summary>
    private bool IsWorkHourNow()
        => _clock.Hour >= TimeConfig.WorkStartHour && _clock.Hour < TimeConfig.WorkEndHour;

    /// <summary>当前是否该上工（非休息日且处于上班时段）：驻工下班判定用。</summary>
    private bool IsWorkTimeNow() => !IsRestDayToday() && IsWorkHourNow();

    /// <summary>休息日安排（有职者轮休当天）：按面板状态择一——
    /// 太累在家歇，无聊出门闲逛，否则做点轻活（家中缺柴去砍柴，否则采摘果子）补贴家用。</summary>
    private void SpendRestDay(GameState gs)
    {
        if (C.Fatigue >= TiredThreshold)
        {
            StartRestHome(gs, 5f);
            return;
        }
        if (C.Fun < BoredThreshold)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        // 轻活：家中柴薪不足优先砍柴，否则采摘（两者均内含“附近无目标则闲逛”的兑底）
        if (HomeLowOnWood(gs))
            StartLogging();
        else
            StartGathering();
    }

    /// <summary>家中柴薪是否低于住户储备目标（休息日砍柴/采摘的取舍依据）。</summary>
    private bool HomeLowOnWood(GameState gs)
    {
        if (!gs.Buildings.TryGetValue(C.HomeId, out var home))
            return false;
        int residents = gs.HomeResidents(home.Id);
        return home.Inv.AmountOf(Goods.Wood) < residents * WoodPerResident;
    }

    // ---- 退休生活 ----

    /// <summary>是否已退休：过退休年龄且已离岗（无业）的成年人（致仕判定在数据层 JobSystem）。</summary>
    private bool IsRetiredNow()
        => C.JobKind == JobKind.None && !C.IsChild && C.AgeYears >= LifeConfig.RetireAge;

    /// <summary>退休生活安排：家中告急仍去补（打水/采购/自采，属“采集等”范畴）；
    /// 否则：富裕家庭闲逛消遣，寒门则上山采薪采果补贴家用。</summary>
    private void SpendRetirement(GameState gs)
    {
        if (C.Fatigue >= TiredThreshold)
        {
            StartRestHome(gs, 5f);
            return;
        }
        if (TryRestockHome(gs)) // 家中食/柴/水告急：退休者仍会去打水/采购/自采
            return;
        if (FamilyIsWealthy(gs))
        {
            StartLeisure(gs); // 富户：闲逛（预留后期消费娱乐/游山玩水）
            return;
        }
        if (C.Fun < BoredThreshold)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        StartGathering(); // 寒门：采薪采果（附近无目标则自动闲逛）
    }

    /// <summary>家庭是否富裕（人均资产高于阀值）：退休后闲逛与采集的分流依据。</summary>
    private bool FamilyIsWealthy(GameState gs)
    {
        double perCapita;
        if (gs.Families.TryGetValue(C.FamilyId, out var fam) && fam.MemberIds.Count > 0)
            perCapita = fam.TotalAssets(gs) / fam.MemberIds.Count;
        else
            perCapita = C.Money;
        return perCapita >= LifeConfig.WealthyPerCapitaAssets;
    }

    /// <summary>闲逛消遣（退休富户/闲人）：当前仅四处闲逛。
    /// 预留后期接口——去街市消费娱乐（酒肆/勾栏等）或出城探索游山玩水（选目标后 BuildPathTo）。</summary>
    private void StartLeisure(GameState gs)
    {
        // TODO(后期)：根据兴趣/财力选择街市消费娱乐点或城外景致，再导航前往
        StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
    }

    /// <summary>家中储备检查（食物/柴/水任一低于目标一半即触发补货）：
    /// 附近有货源且自己有钱先采买背回家，否则上山自采/去井河打水；家中充足返回 false。</summary>
    private bool TryRestockHome(GameState gs)
    {
        if (!gs.Buildings.TryGetValue(C.HomeId, out var home))
            return false;
        int residents = gs.HomeResidents(home.Id);
        if (residents <= 0)
            return false;

        // 食物：粮果野味合计，低于半月存量就补
        double foodStock = 0;
        foreach (var id in Goods.FoodKinds)
            foodStock += home.Inv.AmountOf(id);
        if (foodStock < residents * FoodPerResident / 2)
        {
            foreach (var id in Goods.FoodKinds)
            {
                var src = FindGoodsSource(gs, id);
                if (src != null)
                {
                    StartBuyForHome(id, src);
                    return true;
                }
            }
            // 无处可买：采摘或打猎自给（采回因家中未达标自动搬回家）
            if (_rng.NextDouble() < 0.6)
                StartGathering();
            else
                StartHunting();
            return true;
        }

        // 柴薪：买柴或伐木
        if (home.Inv.AmountOf(Goods.Wood) < residents * WoodPerResident / 2)
        {
            var src = FindGoodsSource(gs, Goods.Wood);
            if (src != null)
                StartBuyForHome(Goods.Wood, src);
            else
                StartLogging();
            return true;
        }

        // 饮水：无处可买，只能去井/河岸打水
        if (home.Inv.AmountOf(Goods.Water) < residents * WaterPerResident / 2)
        {
            StartFetchWater(gs);
            return true;
        }
        return false;
    }

    /// <summary>为自家采买：买入背包后因家中未达标自动走搬回家入库分支（非工坊补料）。</summary>
    private void StartBuyForHome(string goodsId, BuildingInstance src)
    {
        _supplyBuildingId = -1;
        _buyGoodsId = goodsId;
        _buySourceId = src.Id;
        StartActivity(ActivityType.Shopping, BuildingAnchor(src) ?? src.Origin, 2f);
    }

    /// <summary>家庭采买货源：自己有钱且 BuySearchRadius 内有存货的市集/商铺/官营产业（市集加权优先）。</summary>
    private BuildingInstance FindGoodsSource(GameState gs, string goodsId)
    {
        if (C.Money <= 0)
            return null;
        var pos = MapGrid.WorldToCell(Position);
        BuildingInstance best = null;
        float bestDist = float.MaxValue;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Inv.AmountOf(goodsId) < 1)
                continue;
            bool sells = b.Def.Id == "market" || b.Def.Id == "shop" || b.Def.Category == "official";
            if (!sells)
                continue;
            float dist = new Vector2(b.X - pos.X, b.Y - pos.Y).Length();
            if (dist > BuySearchRadius)
                continue;
            if (b.Def.Id == "market")
                dist *= 0.5f; // 同距下先选市集
            if (dist < bestDist)
            {
                bestDist = dist;
                best = b;
            }
        }
        return best;
    }

    /// <summary>打水：最近水井与最近河岸取近者（河岸环扫半径以井距封顶，扫到即更近）；两者皆无则闲逛等待。</summary>
    private void StartFetchWater(GameState gs)
    {
        var pos = MapGrid.WorldToCell(Position);
        Vector2I? target = null;
        int wellDist = int.MaxValue;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Id != "well")
                continue;
            int d = Math.Max(Math.Abs(b.X - pos.X), Math.Abs(b.Y - pos.Y));
            if (d < wellDist)
            {
                wellDist = d;
                target = BuildingAnchor(b) ?? b.Origin;
            }
        }
        var shore = gs.FindNearestWaterShore(pos, Math.Min(wellDist, 192));
        if (shore != null)
            target = shore;
        if (target == null)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        StartActivity(ActivityType.FetchingWater, target, 3f);
    }

    /// <summary>需求驱动的采集谋生（家中储备已达标时）：市集缺备货/工坊商铺缺料才上山采集，
    /// 出发前认领需求方（在途量计入判定，后来者不再扎堆响应同一缺口）；
    /// 采回因家中达标自动走售卖路径换钱；全无需求或城中尚无铺面则闲逛歇息，不再无休止砍树。</summary>
    private void StartDemandForaging(GameState gs)
    {
        string[] kinds = { Goods.Wood, Goods.Fruit, Goods.Game };
        int start = _rng.Next(kinds.Length); // 随机起点：避免全员扎堆砍柴
        for (int i = 0; i < kinds.Length; i++)
        {
            string g = kinds[(start + i) % kinds.Length];
            var target = FindDemandTarget(gs, g);
            if (target == null)
                continue;
            SetClaim(target.Id, g); // 认领缺口：需求判定扣除在途份额
            if (g == Goods.Wood)
                StartLogging();
            else if (g == Goods.Fruit)
                StartGathering();
            else
                StartHunting();
            return;
        }
        // 市面不缺货：闲逛散心或回家歇息
        if (_rng.NextDouble() < 0.5)
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
        else
            StartRestHome(gs, 4f);
    }

    /// <summary>市集每货备货线（份）：市集存量低于此线即构成收购需求。</summary>
    private const double MarketStockLine = EconomyConfig.MarketStockLine;

    /// <summary>找对某原始货品有收购需求的建筑（null=全城无需求）：市集备货线以下 /
    /// 专营该货铺面半仓以下 / 工坊商铺配方原料不足两担；各条款均叠加在途认领量再比较，
    /// 已被足额认领的缺口不再重复派人；城中没有任何铺面时自然无需求（早期山民不为卖而采）。</summary>
    private static BuildingInstance FindDemandTarget(GameState gs, string goodsId)
    {
        var inbound = BuildInboundIndex(gs);
        foreach (var b in gs.Buildings.Values)
        {
            if (b.StorageAtCap)
                continue; // 已达/超仓储上限的铺面不再构成需求（超限存入机制的闸门）
            double inb = inbound.GetValueOrDefault((b.Id, goodsId));
            if (b.Def.Id == "market" && b.Inv.AmountOf(goodsId) + inb < MarketStockLine)
                return b;
            if (b.Specialty == goodsId && b.Inv.AmountOf(goodsId) + inb < b.StorageCap / 2.0)
                return b; // 专营该货的铺面半仓以下要进货
            if (Goods.IsCraftable(b.Specialty))
                foreach (var raw in Goods.InputsOf(b.Specialty))
                    if (raw == goodsId && b.Inv.AmountOf(raw) + inb < Goods.LoadUnits * 2)
                        return b; // 工坊/商铺加工原料告急
        }
        return null;
    }

    // ---- 供货认领：出发即登记、背包腾空即释放，防多人扎堆响应同一缺口 ----

    /// <summary>登记供货认领（目标建筑 + 货品）。</summary>
    private void SetClaim(int buildingId, string goodsId)
    {
        C.ClaimBuildingId = buildingId;
        C.ClaimGoodsId = goodsId;
    }

    /// <summary>释放供货认领。</summary>
    private void ClearClaim()
    {
        C.ClaimBuildingId = -1;
        C.ClaimGoodsId = "";
    }

    /// <summary>某建筑某货的在途认领量（每个认领人按一担计）：单点查询版。</summary>
    private static double InboundOf(GameState gs, int buildingId, string goodsId)
    {
        double sum = 0;
        foreach (var c in gs.Citizens.Values)
            if (c.ClaimBuildingId == buildingId && c.ClaimGoodsId == goodsId)
                sum += Goods.LoadUnits;
        return sum;
    }

    /// <summary>汇总全城在途认领（建筑Id×货品 → 份额）：遍历型需求判定用，一次构建避免逐建筑重扫居民。</summary>
    private static Dictionary<(int, string), double> BuildInboundIndex(GameState gs)
    {
        var map = new Dictionary<(int, string), double>();
        foreach (var c in gs.Citizens.Values)
            if (c.ClaimBuildingId >= 0 && c.ClaimGoodsId != "")
            {
                var key = (c.ClaimBuildingId, c.ClaimGoodsId);
                map[key] = map.GetValueOrDefault(key) + Goods.LoadUnits;
            }
        return map;
    }

    /// <summary>本楼铺面优先：无正职的居住者先为自己住的工坊/商铺跑腿补料——
    /// 铺面的活优先派给没有工作的居住者，认领后外部散户的需求判定自动让位。</summary>
    private bool TryServeHomeBusiness(GameState gs)
    {
        if (!gs.Buildings.TryGetValue(C.HomeId, out var home) || !Goods.IsCraftable(home.Specialty))
            return false;
        if (home.StorageAtCap)
            return false; // 总仓已达上限：先把存货消化掉再进货
        foreach (var raw in Goods.InputsOf(home.Specialty))
        {
            if (home.Inv.AmountOf(raw) + InboundOf(gs, home.Id, raw) >= Goods.LoadUnits * 2)
                continue; // 存量加在途量够两担：不缺
            if (DispatchAcquire(gs, home, raw))
                return true; // DispatchAcquire 内已登记认领
        }
        return false;
    }

    // ---- 工坊/商铺物流：补料与成品外销 ----

    /// <summary>采买判定半径（米）：此范围内有备货的市集/铺面就去买，否则自主采集。</summary>
    private const int BuySearchRadius = EconomyConfig.BuySearchRadius;

    /// <summary>工坊/商铺雇工的物流决策：
    /// 1) 工坊成品攒够一担 → 挑去商铺寄卖（商铺自产自销不外运）；
    /// 2) 任一配方原料不足一担 → 外出取料（市集近且有货则买，否则上山自采）；
    /// 都不需要则返回 false，站堂加工。</summary>
    private bool StartCraftLogistics(GameState gs, BuildingInstance wp)
    {
        string spec = wp.Specialty;

        // 成品外销：仅工坊需要（商铺兼具交易功能，成品就地上柜）
        if (wp.Def.Id == "workshop" && wp.Inv.AmountOf(spec) >= Goods.LoadUnits && C.Pack.Free > 0)
        {
            var shop = FindCraftShop(gs, spec);
            if (shop != null)
            {
                double got = wp.TakeGoods(spec, C.Pack.Free);
                if (got > 0)
                {
                    C.Pack.Store(spec, got);
                    _supplyBuildingId = -1; // 外销而非补料
                    _consignGoodsId = spec; // 送达时按基价向铺面收货款
                    _haulBuildingId = shop.Id;
                    StartActivity(ActivityType.Hauling, BuildingAnchor(shop) ?? shop.Origin, 2f);
                    return true;
                }
            }
        }

        // 补料：任一原料存量加在途量低于一担就去取（同坊另一雇工/住户已在路上则不重复出门）；
        // 总仓已达上限则不再进货（先把存货加工/外销消化掉）
        if (wp.StorageAtCap)
            return false;
        foreach (var raw in Goods.InputsOf(spec))
        {
            if (wp.Inv.AmountOf(raw) + InboundOf(gs, wp.Id, raw) >= Goods.LoadUnits)
                continue;
            if (DispatchAcquire(gs, wp, raw))
                return true;
        }
        return false;
    }

    /// <summary>取料：附近（BuySearchRadius 内）有备货充足的市集/铺面则前往采买，
    /// 否则可野外采集的原料（柴/果/野味）上山自采；粮/矿/盐无货源时只能等。
    /// 出发即登记供货认领，外部散户与同坊他人的需求判定自动扣除在途量。</summary>
    private bool DispatchAcquire(GameState gs, BuildingInstance wp, string raw)
    {
        var source = FindStockedSource(gs, raw, wp);
        if (source != null)
        {
            SetClaim(wp.Id, raw);
            _supplyBuildingId = wp.Id;
            _buyGoodsId = raw;
            _buySourceId = source.Id;
            StartActivity(ActivityType.Shopping, BuildingAnchor(source) ?? source.Origin, 2f);
            return true;
        }

        // 自主收集：只有山野里有的原料才能采
        if (raw == Goods.Wood)
        {
            SetClaim(wp.Id, raw);
            _supplyBuildingId = wp.Id;
            StartLogging();
            return true;
        }
        if (raw == Goods.Fruit)
        {
            SetClaim(wp.Id, raw);
            _supplyBuildingId = wp.Id;
            StartGathering();
            return true;
        }
        if (raw == Goods.Game)
        {
            SetClaim(wp.Id, raw);
            _supplyBuildingId = wp.Id;
            StartHunting();
            return true;
        }
        return false;
    }

    /// <summary>附近有该原料备货（≥一担）的合法货源：市集/商铺/官营产业（农田矿盐等），市集加权优先；
    /// 住宅家底与同行工坊的备料不是商品，不得买走。</summary>
    private static BuildingInstance FindStockedSource(GameState gs, string raw, BuildingInstance wp)
    {
        BuildingInstance best = null;
        float bestDist = float.MaxValue;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Id == wp.Id || b.Inv.AmountOf(raw) < Goods.LoadUnits)
                continue;
            // 只从在卖的地方买：市集、商铺、官营产业；住宅、其他工坊的存货排除
            bool sells = b.Def.Id == "market" || b.Def.Id == "shop" || b.Def.Category == "official";
            if (!sells)
                continue;
            float dist = (b.Origin - wp.Origin).Length();
            if (dist > BuySearchRadius)
                continue;
            // 市集加权优先（同距离下先选市集）
            if (b.Def.Id == "market")
                dist *= 0.5f;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = b;
            }
        }
        return best;
    }

    /// <summary>找成品外销目的地：专营同货的商铺优先，其次市集（通吃各货），都要有库容；
    /// 不往专营别的货的商铺送（居民只从专营铺/市集买，错配入库会成永久死库存）。</summary>
    private static BuildingInstance FindCraftShop(GameState gs, string goodsId)
    {
        BuildingInstance best = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Id != "shop" || b.Specialty != goodsId || b.StorageAtCap)
                continue;
            if (best == null || b.SpareCap > best.SpareCap)
                best = b;
        }
        return best ?? FindMarket(gs, needFree: true);
    }

    /// <summary>伐木：找最近的树走过去砍（线性扫植物实体，免大半径环扫）；无树可砍则闲逛等待。</summary>
    private void StartLogging()
    {
        var tree = GameState.I.FindNearestTreeCell(MapGrid.WorldToCell(Position), ForageRadius);
        if (tree == null)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        StartActivity(ActivityType.Logging, tree, 4f);
    }

    /// <summary>采摘：优先拾附近的地面果堆，其次去挂果成树采摘；都没有则闲逛等待。</summary>
    private void StartGathering()
    {
        var gs = GameState.I;
        var pos = MapGrid.WorldToCell(Position);

        // 落地的熟果不拾白不拾（典型案例三）
        var pile = gs.FindNearestPile(pos, Goods.Fruit, ForageRadius);
        if (pile != null)
        {
            StartActivity(ActivityType.PickingUp, new Vector2I(pile.X, pile.Y), 2f);
            return;
        }

        // 树上挂果才有得摘（典型案例四），不再凭空产果
        var tree = gs.FindNearestFruitTree(pos, ForageRadius);
        if (tree == null)
        {
            StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            return;
        }
        StartActivity(ActivityType.Gathering, new Vector2I(tree.X, tree.Y), 3f);
    }

    /// <summary>打猎：锁定最近的野物赶过去；没有猎物则改伐木。</summary>
    private void StartHunting()
    {
        var prey = GameState.I.FindNearestAnimal(MapGrid.WorldToCell(Position), ForageRadius);
        if (prey == null)
        {
            StartLogging();
            return;
        }
        StartActivity(ActivityType.Hunting, new Vector2I(prey.X, prey.Y), 4f);
        _activityAnimalId = prey.Id;
    }

    /// <summary>修缮匠：去最破旧的公共建筑驻留巡修（实际修复量由 MaintenanceSystem 日结）；无可修则回修缮房值守。</summary>
    private void StartRepairing(GameState gs, BuildingInstance repairhouse)
    {
        BuildingInstance target = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Category != "official" || b.Def.Natural)
                continue;
            if (target == null || b.Condition < target.Condition)
                target = b;
        }
        StartWorkAt(target ?? repairhouse, ActivityType.Repairing);
    }

    /// <summary>驻留结束时的活动结算（砍树/采摘/打猎/拾堆/卖货/挑担入库/下工工钱）：钱与货品一律按动作完成即时结算。</summary>
    private void CompleteActivity()
    {
        var gs = GameState.I;
        switch (C.Activity)
        {
            case ActivityType.Logging:
                // 一斧扣树血，血量对应木材产量：砍掉多少血就得多少柴（老树血厚出柴多，需多斧多趟）；
                // 每斧伤害受背包余量限制免得浪费，血尽树倒消失，未倒且背得下则下轮继续砍同一棵
                if (_activityCell != null && C.Pack.Free > 0)
                {
                    float want = (float)Math.Min(ChopDamage, C.Pack.Free / WoodPerHp);
                    float dealt = gs.DamageTree(_activityCell.Value, want, out bool felled);
                    if (dealt > 0f)
                        C.Pack.Store(Goods.Wood, dealt * WoodPerHp);
                    if (!felled && C.Pack.Free > 0 && gs.Map.CellAt(_activityCell.Value).HasTree)
                        _chopAgain = _activityCell;
                }
                break;
            case ActivityType.Gathering:
                // 从树上摘果：背包能装多少摘多少，树上存量相应减少
                if (_activityCell != null
                    && gs.Plants.TryGetValue(GameState.CellIndex(_activityCell.Value), out var plant)
                    && plant.FruitStock > 0)
                {
                    double got = Math.Min(plant.FruitStock, C.Pack.Free);
                    plant.FruitStock -= C.Pack.Store(Goods.Fruit, got);
                }
                break;
            case ActivityType.Hunting:
                // 猎物倒地化为野味堆，猎人当场拾入背包；动物已游远（超 8 米）则视为脱逃，不得隔空击杀
                if (_activityAnimalId >= 0 && gs.Animals.TryGetValue(_activityAnimalId, out var prey))
                {
                    var kill = new Vector2I(prey.X, prey.Y);
                    var self = MapGrid.WorldToCell(Position);
                    bool inReach = Math.Max(Math.Abs(kill.X - self.X), Math.Abs(kill.Y - self.Y)) <= 8;
                    if (inReach && gs.HarvestAnimal(prey.Id))
                        gs.PickupPile(kill, C.Pack);
                }
                break;
            case ActivityType.PickingUp:
                // 拾堆入背包；若拾的是自家田里的收成，标记优先挑入田仓
                if (_activityCell != null)
                {
                    gs.PickupPile(_activityCell.Value, C.Pack);
                    _fieldHarvest = !C.Pack.IsEmpty && IsOnWorkplaceField(gs, _activityCell.Value);
                }
                break;
            case ActivityType.Shopping:
                // 带采买单的购物（工坊补料或家庭补货）：按基价买一担背走（量力而行），货款付给货源方（雇工分账/官库入账）；主妇闲逛式采买无需结算
                if (_buyGoodsId != "" && gs.Buildings.TryGetValue(_buySourceId, out var src))
                {
                    double price = Goods.PriceOf(_buyGoodsId);
                    double afford = price > 0 ? Math.Max(0, C.Money) / price : C.Pack.Free;
                    double got = src.TakeGoods(_buyGoodsId, Math.Min(C.Pack.Free, afford));
                    if (got > 0)
                    {
                        C.Pack.Store(_buyGoodsId, got);
                        double pay = price * got;
                        C.Money -= pay;
                        gs.PayToBuilding(src, pay); // 钱货两让：卖方真收到钱
                    }
                }
                _buyGoodsId = "";
                _buySourceId = -1;
                break;
            case ActivityType.Trading:
                if (!C.Pack.IsEmpty)
                    SellPack(gs);
                break;
            case ActivityType.FetchingWater:
                // 打满缺口量（封顶背包容量）：井/河水无限源；回程走背包回家入库分支，水不入交易链
                if (gs.Buildings.TryGetValue(C.HomeId, out var wellHome))
                {
                    double target = gs.HomeResidents(wellHome.Id) * WaterPerResident;
                    double lack = Math.Max(0, target - wellHome.Inv.AmountOf(Goods.Water));
                    if (lack > 0)
                        C.Pack.Store(Goods.Water, Math.Min(C.Pack.Free, lack));
                }
                break;
            case ActivityType.Hauling:
                // 挑到目的地入库；寄卖单据入库量由铺面付货款（雇工凑钱/官库拨付）
                if (gs.Buildings.TryGetValue(_haulBuildingId, out var dest))
                {
                    double stored = 0;
                    foreach (var s in C.Pack.Stacks.ToArray())
                    {
                        // 超限入库：背来的货全收（上限只把门不拦货）
                        double put = dest.StoreGoodsForce(s.GoodsId, s.Amount);
                        if (put > 0 && s.GoodsId == _consignGoodsId)
                            gs.PayFromBuilding(dest, C, Goods.PriceOf(s.GoodsId) * put); // 成品卖给商铺：铺面付款
                        C.Pack.Take(s.GoodsId, put);
                        stored += put;
                    }
                    if (C.Pack.IsEmpty)
                        _fieldHarvest = false; // 收成已入仓，回到日常循环
                    if (stored <= 0 && _supplyBuildingId >= 0)
                        _supplyBuildingId = -1; // 本坊满仓一份也入不了：放弃补料，免得原地挑担死循环
                }
                if (C.Pack.IsEmpty)
                    _supplyBuildingId = -1; // 补料已送达
                _consignGoodsId = "";
                _haulBuildingId = -1;
                break;
            case ActivityType.Working:
                // 下工即结：官营岗位当班工钱（月俸/30）由官库实付记账；
                // 工商自营岗位不发固定工钱（收入来自售货分账与寄卖货款，钱不凭空生）；
                // 农夫田仓有存粮就挑一担带走（回家或上市，视作官仓实物俸的一部分）
                if (gs.Buildings.TryGetValue(C.WorkplaceId, out var work))
                {
                    if (work.Def.Category == "official")
                    {
                        double pay = work.Def.Salary / GameClock.DaysPerMonth;
                        C.Money += pay;
                        gs.Money -= pay;
                        gs.Ledger.Add("雇工俸禄", -pay);
                    }
                    if (work.Def.HarvestMonths > 0 && C.Pack.IsEmpty)
                    {
                        double got = work.TakeGoods(Goods.Grain, C.Pack.Free);
                        if (got > 0)
                            C.Pack.Store(Goods.Grain, got);
                    }
                }
                break;
            case ActivityType.Repairing:
                // 修缮匠下工即结：官库发当班俸禄并记账（俸禄随修缮房定义 Salary）
                if (gs.Buildings.TryGetValue(C.WorkplaceId, out var rh))
                {
                    double pay = rh.Def.Salary / GameClock.DaysPerMonth;
                    C.Money += pay;
                    gs.Money -= pay;
                    gs.Ledger.Add("修缮匠俸禄", -pay);
                }
                break;
        }
        _activityCell = null;
        _activityAnimalId = -1;
    }

    /// <summary>目标格是否落在自己工作的农田占地内（判断拾的是不是自家收成）。</summary>
    private bool IsOnWorkplaceField(GameState gs, Vector2I c)
    {
        if (C.JobKind != JobKind.Employed || !gs.Buildings.TryGetValue(C.WorkplaceId, out var wp))
            return false;
        return c.X >= wp.Origin.X && c.X < wp.Origin.X + wp.FootX
            && c.Y >= wp.Origin.Y && c.Y < wp.Origin.Y + wp.FootY;
    }

    /// <summary>找工作建筑占地格上的收成堆（农夫拾运用）。</summary>
    private static ItemPileObj FindFieldPile(GameState gs, BuildingInstance wp)
    {
        for (int x = wp.Origin.X; x < wp.Origin.X + wp.FootX; x++)
            for (int y = wp.Origin.Y; y < wp.Origin.Y + wp.FootY; y++)
                if (gs.Piles.TryGetValue(GameState.CellIndex(new Vector2I(x, y)), out var pile))
                    return pile;
        return null;
    }

    /// <summary>把背包里的货逐堆卖掉：优先专营铺、其次缺此原料的工坊/商铺（与 FindDemandTarget 同口径，
    /// 山民采的原料才卖得进需求方）、再次市集；只按铺面实收份额由买方付款（钱不凭空生）。
    /// 卖不掉的不再就地丢弃：置 _sellFailed 标记，下轮决策改背回家囤（家满才卸堆）；水不入交易链直接泼掉。</summary>
    private void SellPack(GameState gs)
    {
        foreach (var s in C.Pack.Stacks.ToArray())
        {
            if (s.GoodsId == Goods.Water)
            {
                C.Pack.Take(s.GoodsId, s.Amount); // 水不卖钱不落堆：家里装不下就泼掉
                continue;
            }
            var shop = FindTradeShop(gs, s.GoodsId, needFree: true)
                       ?? FindRawBuyer(gs, s.GoodsId)
                       ?? FindMarket(gs, needFree: true);
            double amount = s.Amount;
            if (shop != null)
            {
                // 超限收购：收货方已经过 AtCap 闸门筛选，背来的一担全收不截断
                double accepted = shop.StoreGoodsForce(s.GoodsId, amount);
                if (accepted > 0)
                {
                    gs.PayFromBuilding(shop, C, Goods.PriceOf(s.GoodsId) * accepted); // 铺面能付多少付多少
                    C.Pack.Take(s.GoodsId, accepted);
                    amount -= accepted;
                }
            }
            if (amount > 0)
                _sellFailed = true; // 尾货留在背上：下轮决策背回家囤或家满时就近卸堆
        }
    }

    /// <summary>找缺这种原料的工坊/商铺（配方含该货且存量不足两担、有库容）：
    /// 与 FindDemandTarget 的工坊缺料条款同口径——需求驱动采回的原料才有对应收购方，不致卖无可卖。</summary>
    private static BuildingInstance FindRawBuyer(GameState gs, string goodsId)
    {
        BuildingInstance best = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (!Goods.IsCraftable(b.Specialty) || b.StorageAtCap)
                continue;
            bool needs = false;
            foreach (var raw in Goods.InputsOf(b.Specialty))
                needs |= raw == goodsId && b.Inv.AmountOf(raw) < Goods.LoadUnits * 2;
            if (!needs)
                continue;
            if (best == null || b.SpareCap > best.SpareCap)
                best = b;
        }
        return best;
    }

    /// <summary>找一座市集（通收各货）：needFree 时要求还有库容，取余仓最大者。</summary>
    private static BuildingInstance FindMarket(GameState gs, bool needFree)
    {
        BuildingInstance best = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Id != "market")
                continue;
            if (needFree && b.StorageAtCap)
                continue;
            if (best == null || b.SpareCap > best.SpareCap)
                best = b;
        }
        return best;
    }

    /// <summary>找专营该货品的商铺/工坊（needFree 时要求未达仓储上限），取余仓最大者。</summary>
    private static BuildingInstance FindTradeShop(GameState gs, string goodsId, bool needFree)
    {
        BuildingInstance best = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Specialty != goodsId)
                continue;
            if (needFree && b.StorageAtCap)
                continue;
            if (best == null || b.SpareCap > best.SpareCap)
                best = b;
        }
        return best;
    }

    /// <summary>交易目的地：优先有库容的专营铺，其次缺此原料的工坊/商铺，再次市集（与 SellPack 同序）。</summary>
    private Vector2I? TradeAnchor(GameState gs)
    {
        var shop = FindTradeShop(gs, C.PackGoodsId, needFree: true)
                   ?? FindRawBuyer(gs, C.PackGoodsId)
                   ?? FindMarket(gs, needFree: true)
                   ?? FindMarket(gs, needFree: false);
        return shop != null ? BuildingAnchor(shop) ?? shop.Origin : ShoppingAnchor(gs);
    }

    // ---- 寻路与移动 ----

    /// <summary>
    /// 混合寻路：就近上路 → 路网 AStar → 末段脱路直线接近目标。
    /// 脱路段若直线会蹚水，先用网格 BFS 找旱路绕行（岸上/桥/路可走）；
    /// 只有真正无旱路可绕时才允许直线过河，不再瞬移。
    /// </summary>
    private void BuildPathTo(Vector3 targetWorld)
    {
        var gs = GameState.I;
        var startCell = MapGrid.WorldToCell(Position);
        var targetCell = MapGrid.WorldToCell(targetWorld);

        var points = new List<Vector3>();

        // 上路与下路都先找最近道路：前往树林等远处目标时尽量骑到最近的路再下路直行（减少脱路慢行）；
        // 全图无路时直接跳过环扫，免得早期每次决策白扫大半径
        var entry = gs.RoadCells.Count > 0 ? gs.Map.FindNearestRoad(startCell, 64) : null;
        var exit = entry != null ? gs.Map.FindNearestRoad(targetCell, 96) : null;
        if (entry != null && exit != null && entry.Value != exit.Value)
        {
            var cells = gs.Roads.FindPath(entry.Value, exit.Value);
            if (cells.Count > 0)
                AppendDrySegment(points, Position, MapGrid.CellToWorld(cells[0])); // 上路前的脱路段也不蹚水
            foreach (var c in cells)
                points.Add(MapGrid.CellToWorld(c) + Vector3.Up * 0.2f + _laneJitter); // 车道偏移：各走各道
        }

        // 末段：脱路走向目标（先绕开水面，无旱路才直线过河）
        var dest = new Vector3(targetWorld.X, 0.2f, targetWorld.Z);
        AppendDrySegment(points, points.Count > 0 ? points[^1] : Position, dest);
        points.Add(dest);

        _path = points;
        _pathIndex = 0;
    }

    /// <summary>脱路段避水：直线会蹚水时插入 BFS 旱路绕行点；找不到旱路则保持直线（无路可走才过河）。</summary>
    private static void AppendDrySegment(List<Vector3> points, Vector3 from, Vector3 to)
    {
        if (!LineCrossesWater(from, to))
            return;
        var detour = FindDryDetour(MapGrid.WorldToCell(from), MapGrid.WorldToCell(to));
        if (detour == null)
            return;
        for (int i = 1; i < detour.Count; i++) // 跳过脚下第一格，免得原地折返
            points.Add(MapGrid.CellToWorld(detour[i]) + Vector3.Up * 0.2f);
    }

    /// <summary>直线途经是否会蹚水（桥面/路面上的水不算），沿线每 2m 采样一次。</summary>
    private static bool LineCrossesWater(Vector3 from, Vector3 to)
    {
        float dist = new Vector2(to.X - from.X, to.Z - from.Z).Length();
        int steps = Mathf.CeilToInt(dist / 2f);
        for (int i = 1; i <= steps; i++)
        {
            var c = MapGrid.WorldToCell(from.Lerp(to, i / (float)steps));
            if (MapGrid.InBounds(c) && !IsDryCell(c))
                return true;
        }
        return false;
    }

    /// <summary>旱路格：岸上、桥面或路面。</summary>
    private static bool IsDryCell(Vector2I c)
    {
        var cell = GameState.I.Map.CellAt(c);
        return !cell.HasWater || cell.HasBridge || cell.HasRoad;
    }

    /// <summary>相邻两格能否步行相通：既是旱路，且高差/坡度在可翻越范围内（陡壁不可跨）。
    /// 涉水豁免：岸陆与水面/桥面的落差属水陆分界而非陡壁，上下桥不受坡度限制。</summary>
    private static bool StepTraversable(Vector2I from, Vector2I to)
    {
        if (!IsDryCell(to))
            return false;
        ref var cf = ref GameState.I.Map.CellAt(from);
        ref var ct = ref GameState.I.Map.CellAt(to);
        if (cf.HasWater || ct.HasWater)
            return true; // 上下桥/岸沿落差不作陡壁论
        return TerrainConfig.Traversable(GameState.I.Map.GroundY(from), GameState.I.Map.GroundY(to));
    }

    /// <summary>四向 BFS 找旱路（含起终格）；搜索量封顶防卡帧，找不到返回 null。
    /// 1m 格下上限需足够大：旧值 2000 格只覆盖二十米见方，绕到几十米外的桥就搜不到——
    /// 导致有桥也直接蹚水过河；现封顶 24000 格（约 150m 见方），仅直线穿水时才触发。</summary>
    private static List<Vector2I> FindDryDetour(Vector2I from, Vector2I to)
    {
        if (!MapGrid.InBounds(from) || !MapGrid.InBounds(to) || !IsDryCell(to))
            return null;

        var prev = new Dictionary<Vector2I, Vector2I> { [from] = from };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(from);
        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

        while (queue.Count > 0 && prev.Count < 24000)
        {
            var cur = queue.Dequeue();
            if (cur == to)
            {
                var path = new List<Vector2I>();
                for (var c = to; ; c = prev[c])
                {
                    path.Add(c);
                    if (c == from)
                        break;
                }
                path.Reverse();
                return path;
            }
            foreach (var d in dirs)
            {
                var n = cur + d;
                if (!prev.ContainsKey(n) && MapGrid.InBounds(n) && StepTraversable(cur, n))
                {
                    prev[n] = cur;
                    queue.Enqueue(n);
                }
            }
        }
        return null;
    }

    /// <summary>脚下站面高度：桥格站桥板顶（桥悬浮在水面 -0.5 之上，顶 0.34）；
    /// 其余处双线性采样高度场 +0.2（坡面行走平滑升降，路面抬升差无感不单独处理）。</summary>
    private static float SurfaceYAt(Vector3 pos)
    {
        var c = MapGrid.WorldToCell(pos);
        if (!MapGrid.InBounds(c))
            return 0.2f;
        ref var cell = ref GameState.I.Map.CellAt(c);
        if (cell.HasBridge)
            return 0.34f;
        return GameState.I.Map.Height.SampleWorld(pos.X, pos.Z) + 0.2f;
    }

    private void MoveAlongPath(float dt)
    {
        // 道路优先：脚下无路时大幅减速（脱路惩罚）；路面按种类快慢（主路快/辅路常速）
        var cell = MapGrid.WorldToCell(Position);
        bool inBounds = MapGrid.InBounds(cell);
        float speedFactor;
        if (inBounds && GameState.I.Map.CellAt(cell).HasRoad)
        {
            // 路面按种类快慢（主路快/辅路常速/小路慢/桥面常速），取自 MovementConfig
            speedFactor = MovementConfig.RoadSpeedFactor(GameState.I.Map.CellAt(cell).RoadKind);
        }
        else
        {
            speedFactor = OffRoadFactor;
        }
        float step = BaseSpeed * speedFactor * dt;
        var before = Position; // 记录本帧起点，移完按净位移转身（跨路点时取合方向，免拐角瞬间甩头）

        while (step > 0f && _path != null)
        {
            var target = _path[_pathIndex];
            target.Y = Position.Y; // 水平面内移动；垂直贴面（桥面/地面）由 _Process 统一校正
            float dist = Position.DistanceTo(target);
            if (dist > step)
            {
                Position += (target - Position).Normalized() * step;
                break;
            }
            Position = target;
            step -= dist;
            _pathIndex++;
            if (_pathIndex >= _path.Count)
                _path = null; // 到达，进入活动驻留
        }

        // 正面朝前：按本帧路径净位移平滑转身（分离推力不计入，免被挤得左右抖头）
        FaceMoveDirection(Position - before, dt);
    }

    /// <summary>行进转身：模型正面（局部 +Z，胸前抱货挂点同向）按固定角速度转向水平位移方向；
    /// 位移过小（贴点/驻留）保持原朝向不回正，驻留期自然停在最后的行进朝向。</summary>
    private void FaceMoveDirection(Vector3 moved, float dt)
    {
        moved.Y = 0f;
        if (moved.LengthSquared() < 1e-8f)
            return;
        float desired = Mathf.Atan2(moved.X, moved.Z); // +Z 为正面的偏航角
        float yaw = _body.Rotation.Y;
        float diff = Mathf.AngleDifference(yaw, desired);
        float maxStep = MovementConfig.TurnSpeedRadPerSec * dt;
        _body.Rotation = new Vector3(0f, yaw + Mathf.Clamp(diff, -maxStep, maxStep), 0f);
    }

    /// <summary>驻留期间按活动更新疲劳/兴趣；上班攒满疲劳即下班。</summary>
    private void ApplyActivityNeeds(float dt)
    {
        switch (C.Activity)
        {
            case ActivityType.Working:
            case ActivityType.Repairing:
                C.Fatigue += 3f * dt;
                C.Fun -= 0.5f * dt;
                if (!IsWorkTimeNow())
                    _dwell = 0f; // 到点下班（傍晚）或轮休日到来
                break;
            case ActivityType.RestHome:
                C.Fatigue -= 5f * dt;
                C.Fun += 0.5f * dt;
                break;
            case ActivityType.Shopping:
                C.Fatigue += 0.5f * dt;
                C.Fun += 1.5f * dt;
                break;
            case ActivityType.Playing:
                C.Fun += 4f * dt;
                break;
            case ActivityType.Strolling:
                C.Fatigue -= 2f * dt;
                C.Fun += 3f * dt;
                break;
            case ActivityType.Logging:
                C.Fatigue += 4f * dt;
                C.Fun -= 0.5f * dt;
                break;
            case ActivityType.Gathering:
                C.Fatigue += 2f * dt;
                C.Fun += 0.5f * dt;
                break;
            case ActivityType.Hunting:
                C.Fatigue += 4f * dt;
                C.Fun += 1f * dt;
                break;
            case ActivityType.Trading:
                C.Fatigue += 0.5f * dt;
                C.Fun += 1f * dt;
                break;
            case ActivityType.Hauling:
                C.Fatigue += 1f * dt; // 挑担赶路：略耗体力
                break;
            case ActivityType.PickingUp:
                C.Fatigue += 1.5f * dt; // 弯腰拾货：比挑担更耗体力
                break;
            case ActivityType.FetchingWater:
                C.Fatigue += 1.5f * dt; // 汲水提桶：与拾货同等耗力
                break;
        }
        C.Fatigue = Mathf.Clamp(C.Fatigue, 0f, 100f);
        C.Fun = Mathf.Clamp(C.Fun, 0f, 100f);
    }

    // ---- 位置工具 ----

    private Vector2I? HomeCell()
    {
        if (GameState.I.Buildings.TryGetValue(C.HomeId, out var home))
            return home.Origin;
        return null;
    }

    private Vector2I? HomeAnchor()
    {
        if (GameState.I.Buildings.TryGetValue(C.HomeId, out var home))
            return BuildingAnchor(home);
        return null;
    }

    /// <summary>建筑出入停靠格：就近门的门外格；无门退回邻路锚点。</summary>
    private Vector2I? BuildingAnchor(BuildingInstance b)
    {
        var door = NearestDoor(b);
        return door?.Outside ?? GameState.I.Map.FindAdjacentRoad(b.Origin, b.FootX, b.FootY);
    }

    /// <summary>就近选门：按村民当前位置到各门外格的切比雪夫距离取最近门；建筑无门返回 null。</summary>
    private Door? NearestDoor(BuildingInstance b)
    {
        var gs = GameState.I;
        gs.EnsureDoors(b);
        if (b.Doors == null || b.Doors.Count == 0)
            return null;
        var here = MapGrid.WorldToCell(Position);
        Door best = b.Doors[0];
        int bestD = int.MaxValue;
        foreach (var d in b.Doors)
        {
            int dist = Mathf.Max(Mathf.Abs(d.Outside.X - here.X), Mathf.Abs(d.Outside.Y - here.Y));
            if (dist < bestD) { bestD = dist; best = d; }
        }
        return best;
    }

    private static Vector3 BuildingCenter(BuildingInstance b)
    {
        var a = MapGrid.CellToWorld(b.Origin);
        var c = MapGrid.CellToWorld(new Vector2I(b.Origin.X + b.FootX - 1, b.Origin.Y + b.FootY - 1));
        return (a + c) * 0.5f;
    }

    private Vector3 HomePosition()
    {
        var gs = GameState.I;
        if (gs.Buildings.TryGetValue(C.HomeId, out var home))
            return MapGrid.CellToWorld(home.Origin) + Vector3.Up * 0.2f;
        // 无住所也从住宅/工商建筑出现，不在地图上凭空刷新
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Category == "grown")
                return MapGrid.CellToWorld(b.Origin) + Vector3.Up * 0.2f;
        var road = _manager.RandomRoadCell(_rng);
        return road != null ? MapGrid.CellToWorld(road.Value) + Vector3.Up * 0.2f : Vector3.Up * 0.2f;
    }

    private Vector2I? NearbyRoadCell(Vector2I? center, int radius)
    {
        if (center == null)
            return _manager.RandomRoadCell(_rng);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var c = new Vector2I(
                center.Value.X + _rng.Next(-radius, radius + 1),
                center.Value.Y + _rng.Next(-radius, radius + 1));
            if (MapGrid.InBounds(c) && GameState.I.Map.CellAt(c).HasRoad)
                return c;
        }
        return _manager.RandomRoadCell(_rng);
    }

    private Vector2I? ShoppingAnchor(GameState gs)
    {
        var shops = new List<BuildingInstance>();
        foreach (var b in gs.Buildings.Values)
            if (b.Def.Id == "shop")
                shops.Add(b);

        var target = shops.Count > 0 ? shops[_rng.Next(shops.Count)] : null;
        return target != null ? BuildingAnchor(target) : _manager.RandomRoadCell(_rng);
    }

    /// <summary>外观：宋人市井装束（参考宋画风人物）——男女皆着及踝长袍（A 字下摆），
    /// 束深色腰带配宽垂袖；男戴幞头、女盘圆发髻；体型随年龄，配色按人稳定取自色板，老人白发灰袍。</summary>
    private void ApplyLook()
    {
        _lookAgeYears = C.AgeYears;
        // 体型随年龄线性生长：新生儿 ChildMinScale → 成年门槛达满值 1.0，再乘全局村民模型缩放
        float grow = Mathf.Min(1f, C.AgeMonths / (LifeConfig.AdultAgeYears * 12f));
        float bodyScale = (VillagerConfig.ChildMinScale + (1f - VillagerConfig.ChildMinScale) * grow)
            * VillagerConfig.ModelScale;
        _body.Scale = Vector3.One * bodyScale;

        bool female = C.Gender == Gender.Female;

        // 袍摆：及踝长袍下半，比上身宽出一圈成 A 字轮廓（女裙袍更宽）；
        // 含袖总宽控在旧版体量内（≤约 0.56），不放大人物
        _lower.Scale = female ? new Vector3(0.54f, 0.85f, 0.4f) : new Vector3(0.5f, 0.82f, 0.36f);
        _lower.Position = Vector3.Up * (_lower.Scale.Y / 2f);
        float waist = _lower.Scale.Y;
        
        // 袍身上段：收窄于袍摆，男肩略宽（两侧留出垂袖位）
        _upper.Scale = new Vector3(female ? 0.34f : 0.36f, 0.5f, female ? 0.26f : 0.28f);
        _upper.Position = Vector3.Up * (waist + 0.25f);
        float neck = waist + 0.5f;
        
        // 双垂袖：自肩部垂至腰际，前后宽于臂形成宽袖剪影
        var sleeve = new Vector3(0.1f, 0.38f, 0.2f);
        _sleeveL.Scale = sleeve;
        _sleeveR.Scale = sleeve;
        float armX = _upper.Scale.X / 2f + sleeve.X / 2f;
        float armY = neck - sleeve.Y / 2f;
        _sleeveL.Position = new Vector3(-armX, armY, 0f);
        _sleeveR.Position = new Vector3(armX, armY, 0f);
        
        // 腰带：深色细带束在袍身交界处上沿，贴上身凸出可见（位置略高于袍摆顶免被盖住）
        _belt.Scale = new Vector3(_upper.Scale.X + 0.06f, 0.08f, _upper.Scale.Z + 0.06f);
        _belt.Position = Vector3.Up * (waist + 0.05f);
        
        // 头 + 发：发盖是套在头顶上半的扁球（贴头皮包住头颅，不再是悬浮黑团），
        // 其上再戴冠发：男幞头（扁盒）、女与孩童小圆髻（小球），老人白发
        _head.Scale = Vector3.One * 0.34f;
        _head.Position = Vector3.Up * (neck + 0.19f);
        _hair.Scale = new Vector3(0.365f, 0.2f, 0.365f); // 略宽于头、压扁，只罩头顶半球
        _hair.Position = Vector3.Up * (neck + 0.27f);
        if (female || C.IsChild)
        {
            _hat.Mesh = SharedSphere;
            _hat.Scale = Vector3.One * (female ? 0.13f : 0.1f);
            _hat.Position = Vector3.Up * (neck + 0.41f);
        }
        else
        {
            _hat.Mesh = SharedBox;
            _hat.Scale = new Vector3(0.2f, 0.09f, 0.2f);
            _hat.Position = Vector3.Up * (neck + 0.4f);
        }

        _headMat.AlbedoColor = new Color(0.91f, 0.76f, 0.62f); // 肤色
        _hatMat.AlbedoColor = C.IsElder ? new Color(0.92f, 0.92f, 0.9f) : new Color(0.09f, 0.08f, 0.08f);

        Color upperCol, lowerCol, beltCol = new(0.24f, 0.19f, 0.14f); // 默认深褐腰带
        if (C.IsChild)
        {
            upperCol = new Color(0.95f, 0.85f, 0.45f);
            lowerCol = new Color(0.75f, 0.55f, 0.35f);
        }
        else if (C.IsElder)
        {
            upperCol = new Color(0.64f, 0.63f, 0.58f);
            lowerCol = new Color(0.48f, 0.47f, 0.43f);
            beltCol = new Color(0.3f, 0.28f, 0.25f);
        }
        else if (female)
        {
            // 按人稳定取色（Id 取模，重看不变色）：襦衫 + 罗裙两色一组
            var (u, l) = FemaleRobes[C.Id % FemaleRobes.Length];
            upperCol = u; lowerCol = l;
            beltCol = new Color(0.45f, 0.26f, 0.22f); // 女束红褐带
        }
        else
        {
            var (u, l) = MaleRobes[C.Id % MaleRobes.Length];
            upperCol = u; lowerCol = l;
        }
        _upperMat.AlbedoColor = upperCol;
        _lowerMat.AlbedoColor = lowerCol;
        _beltMat.AlbedoColor = beltCol;
    }

    // 成年袍服色板（上衣/下摆）：取自宋画市井色调——男灰蓝/青绿/米褐/藏青/茶棕，
    // 女米白襦朱红裙/青襦米裙/藕荷襦灰蓝裙；下摆略深于上衣显层次
    private static readonly (Color Upper, Color Lower)[] MaleRobes =
    {
        (new Color(0.42f, 0.50f, 0.58f), new Color(0.36f, 0.43f, 0.50f)), // 灰蓝
        (new Color(0.45f, 0.55f, 0.47f), new Color(0.38f, 0.47f, 0.40f)), // 青绿
        (new Color(0.72f, 0.63f, 0.50f), new Color(0.62f, 0.54f, 0.42f)), // 米褐
        (new Color(0.30f, 0.36f, 0.46f), new Color(0.25f, 0.30f, 0.39f)), // 藏青
        (new Color(0.55f, 0.44f, 0.34f), new Color(0.47f, 0.37f, 0.28f)), // 茶棕
    };

    private static readonly (Color Upper, Color Lower)[] FemaleRobes =
    {
        (new Color(0.88f, 0.84f, 0.76f), new Color(0.70f, 0.30f, 0.30f)), // 米白襦 + 朱红裙
        (new Color(0.55f, 0.65f, 0.60f), new Color(0.80f, 0.74f, 0.62f)), // 青襦 + 米裙
        (new Color(0.76f, 0.55f, 0.55f), new Color(0.45f, 0.52f, 0.60f)), // 藕荷襦 + 灰蓝裙
    };
}
