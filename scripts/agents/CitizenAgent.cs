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
    private const float BaseSpeed = 5f;
    private const float OffRoadFactor = 0.35f; // 脱离道路的减速惩罚
    private const float TiredThreshold = 80f;
    private const float BoredThreshold = 25f;
    private const float StayUntilDone = 9999f; // 驻留到条件触发（如下班）而非计时
    private const float LaneJitterRange = 1.2f; // 车道偏移幅度（路格宽 4m，偏移后仍在路面内）
    private const float ChopDamage = 25f; // 每斧砍伐伤害（幼树一斧倒，老树需多斧）
    private const double WoodPerHp = Goods.LoadUnits / (double)ChopDamage; // 血量→柴薪折算：一斧 25 血恰好一担

    public Citizen C { get; }

    /// <summary>剩余路径只读视图（选中居民目标路线绘制用）：null 即当前无路线。</summary>
    public IReadOnlyList<Vector3> PathPoints => _path;
    public int PathIndex => _pathIndex;

    private readonly GameClock _clock;
    private readonly AgentManager _manager;
    private readonly Random _rng = new();

    private MeshInstance3D _lower, _upper, _head, _hat;
    private StandardMaterial3D _lowerMat, _upperMat, _headMat, _hatMat;

    // 宋人占位模型：头/上身/下身 + 冠发，共享网格资源省内存，材质各自持有
    private static readonly BoxMesh SharedBox = new() { Size = Vector3.One };
    private static readonly SphereMesh SharedSphere = new() { Radius = 0.5f, Height = 1f };

    private Node3D _body;
    private bool _spawnPending;

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
        _head = AddPart(SharedSphere, _headMat = new StandardMaterial3D());
        _hat = AddPart(SharedBox, _hatMat = new StandardMaterial3D());
        ApplyLook();

        _laneJitter = new Vector3(
            ((float)_rng.NextDouble() * 2f - 1f) * LaneJitterRange,
            0f,
            ((float)_rng.NextDouble() * 2f - 1f) * LaneJitterRange);

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

        // 位置回写数据层，随存档保存
        C.PosX = Position.X;
        C.PosZ = Position.Z;
        C.PosValid = true;
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

        // 背包有货：田里拾的收成先挑入田仓；否则家里放得下先搬回家，再不行挑去专营铺子卖掉
        if (!C.Pack.IsEmpty)
        {
            if (_fieldHarvest && C.JobKind == JobKind.Employed
                && gs.Buildings.TryGetValue(C.WorkplaceId, out var barn) && barn.StorageFree >= 1)
            {
                _haulBuildingId = barn.Id;
                StartActivity(ActivityType.Hauling, BuildingAnchor(barn) ?? barn.Origin, 2f);
            }
            else if (gs.Buildings.TryGetValue(C.HomeId, out var home) && home.StorageFree >= 1)
            {
                _fieldHarvest = false;
                _haulBuildingId = home.Id;
                StartActivity(ActivityType.Hauling, HomeAnchor(), 2f);
            }
            else
            {
                _fieldHarvest = false;
                StartActivity(ActivityType.Trading, TradeAnchor(gs), 2.5f);
            }
            return;
        }

        if (C.IsChild)
        {
            // 孩童：在家附近玩耍（后续接入学堂后改为上学）
            StartActivity(ActivityType.Playing, NearbyRoadCell(HomeCell(), 4), 3f);
            return;
        }

        // 上一斧没砍倒：原地继续砍同一棵树（直到砍倒或树已不在）
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

        if (C.Fatigue >= TiredThreshold)
        {
            // 累了：兴趣太低先闲逛散心，否则回家歇息（进屋歇着，而非站在门口路边）
            if (C.Fun < BoredThreshold)
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            else
                StartRestHome(gs, 5f);
            return;
        }

        if (C.JobKind == JobKind.Employed && gs.Buildings.TryGetValue(C.WorkplaceId, out var wp))
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
            // 受雇者/店主：进工作地驻留，疲劳攒满才下班
            StartWorkAt(wp);
            return;
        }

        if (C.JobKind == JobKind.Logger)
        {
            StartForaging();
            return;
        }

        if (C.Gender == Gender.Female && C.IsMarried && C.Activity != ActivityType.Shopping
            && _rng.NextDouble() < 0.6)
        {
            // 主妇：外出采购，回程自然衔接下次决策
            StartActivity(ActivityType.Shopping, ShoppingAnchor(gs), 2.5f);
            return;
        }

        if (C.IsElder)
        {
            if (_rng.NextDouble() < 0.5)
                StartActivity(ActivityType.Strolling, _manager.RandomRoadCell(_rng), 3f);
            else
                StartRestHome(gs, 4f);
            return;
        }

        // 无业成年人：上山谋生换钱，再去市集交易
        StartForaging();
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

    /// <summary>屋内站位：从门口锚点向建筑中心推进 80%，保证落在建筑占地内（半透明建筑可透视屋内人）。</summary>
    private static Vector3 IndoorStand(BuildingInstance b)
    {
        var anchor = GameState.I.Map.FindAdjacentRoad(b.Origin, b.FootX, b.FootY);
        var anchorWorld = anchor != null ? MapGrid.CellToWorld(anchor.Value) : BuildingCenter(b);
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

    /// <summary>山民谋生三选一：伐木 / 林中采摘 / 打猎。</summary>
    private void StartForaging()
    {
        double roll = _rng.NextDouble();
        if (roll < 0.4)
            StartLogging();
        else if (roll < 0.7)
            StartGathering();
        else
            StartHunting();
    }

    // ---- 工坊/商铺物流：补料与成品外销 ----

    /// <summary>采买判定半径：此范围内有备货的市集/铺面就去买，否则自主采集。</summary>
    private const int BuySearchRadius = 40;

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

        // 补料：任一原料低于一担就去取
        foreach (var raw in Goods.InputsOf(spec))
        {
            if (wp.Inv.AmountOf(raw) >= Goods.LoadUnits)
                continue;
            if (DispatchAcquire(gs, wp, raw))
                return true;
        }
        return false;
    }

    /// <summary>取料：附近（BuySearchRadius 内）有备货充足的市集/铺面则前往采买，
    /// 否则可野外采集的原料（柴/果/野味）上山自采；粮/矿/盐无货源时只能等。</summary>
    private bool DispatchAcquire(GameState gs, BuildingInstance wp, string raw)
    {
        var source = FindStockedSource(gs, raw, wp);
        if (source != null)
        {
            _supplyBuildingId = wp.Id;
            _buyGoodsId = raw;
            _buySourceId = source.Id;
            StartActivity(ActivityType.Shopping, BuildingAnchor(source) ?? source.Origin, 2f);
            return true;
        }

        // 自主收集：只有山野里有的原料才能采
        if (raw == Goods.Wood)
        {
            _supplyBuildingId = wp.Id;
            StartLogging();
            return true;
        }
        if (raw == Goods.Fruit)
        {
            _supplyBuildingId = wp.Id;
            StartGathering();
            return true;
        }
        if (raw == Goods.Game)
        {
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
            if (b.Def.Id != "shop" || b.Specialty != goodsId || b.StorageFree < 1)
                continue;
            if (best == null || b.StorageFree > best.StorageFree)
                best = b;
        }
        return best ?? FindMarket(gs, needFree: true);
    }

    /// <summary>伐木：找最近的树走过去砍；无树可砍则闲逛等待。</summary>
    private void StartLogging()
    {
        var tree = GameState.I.Map.FindNearestTree(MapGrid.WorldToCell(Position), 48);
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
        var pile = gs.FindNearestPile(pos, Goods.Fruit, 48);
        if (pile != null)
        {
            StartActivity(ActivityType.PickingUp, new Vector2I(pile.X, pile.Y), 2f);
            return;
        }

        // 树上挂果才有得摘（典型案例四），不再凭空产果
        var tree = gs.FindNearestFruitTree(pos, 48);
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
        var prey = GameState.I.FindNearestAnimal(MapGrid.WorldToCell(Position), 48);
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
                // 猎物倒地化为野味堆，猎人当场拾入背包；动物已游远（超 2 格）则视为脱逃，不得隔空击杀
                if (_activityAnimalId >= 0 && gs.Animals.TryGetValue(_activityAnimalId, out var prey))
                {
                    var kill = new Vector2I(prey.X, prey.Y);
                    var self = MapGrid.WorldToCell(Position);
                    bool inReach = Math.Max(Math.Abs(kill.X - self.X), Math.Abs(kill.Y - self.Y)) <= 2;
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
                // 工坊/商铺采买原料：按基价买一担背走（雇工垫付、量力而行），货款付给货源方（雇工分账/官库入账）；普通采买无需结算
                if (_supplyBuildingId >= 0 && _buyGoodsId != ""
                    && gs.Buildings.TryGetValue(_buySourceId, out var src))
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
            case ActivityType.Hauling:
                // 挑到目的地入库；寄卖单据入库量由铺面付货款（雇工凑钱/官库拨付）
                if (gs.Buildings.TryGetValue(_haulBuildingId, out var dest))
                {
                    double stored = 0;
                    foreach (var s in C.Pack.Stacks.ToArray())
                    {
                        double put = dest.StoreGoods(s.GoodsId, s.Amount);
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

    /// <summary>把背包里的货逐堆卖掉：优先专营铺、其次市集；只按铺面实际收下的份额由买方付款（钱不凭空生），
    /// 卖不掉的就地卸成物资堆（堆满则烂掉），免得背着死循环。</summary>
    private void SellPack(GameState gs)
    {
        foreach (var s in C.Pack.Stacks.ToArray())
        {
            var shop = FindTradeShop(gs, s.GoodsId, needFree: true) ?? FindMarket(gs, needFree: true);
            double amount = s.Amount;
            if (shop != null)
            {
                double accepted = shop.StoreGoods(s.GoodsId, amount);
                if (accepted > 0)
                {
                    gs.PayFromBuilding(shop, C, Goods.PriceOf(s.GoodsId) * accepted); // 铺面能付多少付多少
                    C.Pack.Take(s.GoodsId, accepted);
                    amount -= accepted;
                }
            }
            if (amount > 0)
            {
                // 无铺可收：就地卸货成堆（谁都能拾），背包腾空回到日常循环
                C.Pack.Take(s.GoodsId, amount);
                gs.DropOnGround(MapGrid.WorldToCell(Position), s.GoodsId, amount);
            }
        }
    }

    /// <summary>找一座市集（通收各货）：needFree 时要求还有库容，取余仓最大者。</summary>
    private static BuildingInstance FindMarket(GameState gs, bool needFree)
    {
        BuildingInstance best = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Def.Id != "market")
                continue;
            if (needFree && b.StorageFree < 1)
                continue;
            if (best == null || b.StorageFree > best.StorageFree)
                best = b;
        }
        return best;
    }

    /// <summary>找专营该货品的商铺/工坊（needFree 时要求还有库容），取余仓最大者。</summary>
    private static BuildingInstance FindTradeShop(GameState gs, string goodsId, bool needFree)
    {
        BuildingInstance best = null;
        foreach (var b in gs.Buildings.Values)
        {
            if (b.Specialty != goodsId)
                continue;
            if (needFree && b.StorageFree < 1)
                continue;
            if (best == null || b.StorageFree > best.StorageFree)
                best = b;
        }
        return best;
    }

    /// <summary>交易目的地：优先有库容的专营铺，其次任意专营铺，再次市集，最后退化到随机商铺。</summary>
    private Vector2I? TradeAnchor(GameState gs)
    {
        var shop = FindTradeShop(gs, C.PackGoodsId, needFree: true)
                   ?? FindTradeShop(gs, C.PackGoodsId, needFree: false)
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

        // 上路与下路都先找最近道路：前往树林等远处目标时尽量骑到最近的路再下路直行（减少脱路慢行）
        var entry = gs.Map.FindNearestRoad(startCell, 16);
        var exit = gs.Map.FindNearestRoad(targetCell, 24);
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

    /// <summary>四向 BFS 找旱路（含起终格）；搜索量封顶防卡帧，找不到返回 null。</summary>
    private static List<Vector2I> FindDryDetour(Vector2I from, Vector2I to)
    {
        if (!MapGrid.InBounds(from) || !MapGrid.InBounds(to) || !IsDryCell(to))
            return null;

        var prev = new Dictionary<Vector2I, Vector2I> { [from] = from };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(from);
        Vector2I[] dirs = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

        while (queue.Count > 0 && prev.Count < 2000)
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
                if (!prev.ContainsKey(n) && MapGrid.InBounds(n) && IsDryCell(n))
                {
                    prev[n] = cur;
                    queue.Enqueue(n);
                }
            }
        }
        return null;
    }

    private void MoveAlongPath(float dt)
    {
        // 道路优先：脚下无路时大幅减速（脱路惩罚）；路面按种类快慢（主路快/辅路常速/小路略慢）
        var cell = MapGrid.WorldToCell(Position);
        bool inBounds = MapGrid.InBounds(cell);
        float speedFactor;
        if (inBounds && GameState.I.Map.CellAt(cell).HasRoad)
        {
            speedFactor = GameState.I.Map.CellAt(cell).RoadKind switch
            {
                RoadKind.Main => 1.2f,
                RoadKind.Small => 0.9f,
                _ => 1f, // 辅路与桥面（RoadKind.None 但 HasRoad）常速
            };
        }
        else
        {
            speedFactor = OffRoadFactor;
        }
        float step = BaseSpeed * speedFactor * dt;

        while (step > 0f && _path != null)
        {
            var target = _path[_pathIndex];
            float dist = Position.DistanceTo(target);
            if (dist > step)
            {
                Position += (target - Position).Normalized() * step;
                return;
            }
            Position = target;
            step -= dist;
            _pathIndex++;
            if (_pathIndex >= _path.Count)
                _path = null; // 到达，进入活动驻留
        }
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
                if (C.Fatigue >= TiredThreshold)
                    _dwell = 0f; // 下班
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

    private static Vector2I? BuildingAnchor(BuildingInstance b) =>
        GameState.I.Map.FindAdjacentRoad(b.Origin, b.FootX, b.FootY);

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

    /// <summary>外观：宋人三段式（头/上身/下身）+ 冠发。体型随年龄，配色分男女，老人白发。</summary>
    private void ApplyLook()
    {
        _lookAgeYears = C.AgeYears;
        float bodyScale = C.IsChild ? 0.45f + 0.35f * (C.AgeYears / 16f) : 1f;
        _body.Scale = Vector3.One * bodyScale;

        bool female = C.Gender == Gender.Female;

        // 下身：男着裤褌收窄，女着长裙放宽
        _lower.Scale = female ? new Vector3(0.6f, 0.8f, 0.42f) : new Vector3(0.48f, 0.75f, 0.32f);
        _lower.Position = Vector3.Up * (_lower.Scale.Y / 2f);
        float waist = _lower.Scale.Y;

        // 上身：男肩宽、女肩窄
        _upper.Scale = new Vector3(female ? 0.5f : 0.56f, 0.55f, 0.34f);
        _upper.Position = Vector3.Up * (waist + 0.275f);
        float neck = waist + 0.55f;

        // 头 + 冠发（男戴幞头、女绾发髻，老人白发）
        _head.Scale = Vector3.One * 0.34f;
        _head.Position = Vector3.Up * (neck + 0.19f);
        _hat.Scale = female ? new Vector3(0.18f, 0.14f, 0.18f) : new Vector3(0.3f, 0.1f, 0.3f);
        _hat.Position = Vector3.Up * (neck + 0.38f);

        _headMat.AlbedoColor = new Color(0.91f, 0.76f, 0.62f); // 肤色
        _hatMat.AlbedoColor = C.IsElder ? new Color(0.92f, 0.92f, 0.9f) : new Color(0.09f, 0.08f, 0.08f);

        Color upperCol, lowerCol;
        if (C.IsChild)
        {
            upperCol = new Color(0.95f, 0.85f, 0.45f);
            lowerCol = new Color(0.75f, 0.55f, 0.35f);
        }
        else if (C.IsElder)
        {
            upperCol = new Color(0.62f, 0.62f, 0.58f);
            lowerCol = new Color(0.46f, 0.46f, 0.43f);
        }
        else if (female)
        {
            upperCol = new Color(0.76f, 0.42f, 0.47f); // 粉色襦衫
            lowerCol = new Color(0.5f, 0.62f, 0.5f);   // 青绿罗裙
        }
        else
        {
            upperCol = new Color(0.34f, 0.4f, 0.5f);   // 青灰襕衫
            lowerCol = new Color(0.26f, 0.3f, 0.36f);
        }
        _upperMat.AlbedoColor = upperCol;
        _lowerMat.AlbedoColor = lowerCol;
    }
}
