namespace Bianjing;

/// <summary>
/// 相机配置：RTS 轨道相机的距离/俯仰范围与屏缘推移（业务归属：RtsCameraRig）。
/// 世界为 1024m 见方（MapGrid.Size × CellSize），最远距离按约览半城收紧，省渲染资源。
/// </summary>
public static class CameraConfig
{
    /// <summary>缩放距离下限（米）：可凑到单个村民/建筑跟前看细节。</summary>
    public const float MinDist = 2.5f;

    /// <summary>缩放距离上限（米）：约览半城为限——不再开到纵览全图，
    /// 同屏进入视锥的地形三角/建筑量明显减少，省渲染资源（全图总览交给小地图方向）。</summary>
    public const float MaxDist = 450f;

    /// <summary>相机远裁剪面（米）：最远拉距 + 地图对角线（≈1450m）留余，
    /// 低角度斜望对岸不穿帮；比旧值 4000 更早剪掉远景，深度精度也更好。</summary>
    public const float FarClip = 2000f;

    /// <summary>俯仰角范围（弧度，负值向下看）：近乎垂直俯视 ~ 低角度平视。</summary>
    public const float MinPitch = -1.45f;
    public const float MaxPitch = -0.35f;

    /// <summary>屏幕边缘推移触发带宽度（像素）：光标贴边即平移视野。</summary>
    public const float EdgeMargin = 8f;

    /// <summary>镜头离地最小净空（米）：镜头低于脚下地形+此值时抬升云台，防平移/低角度时镜头钻进山体透视。</summary>
    public const float MinAboveTerrain = 1.5f;

    /// <summary>雾化启用的相机拉距阈值（米）：拉距超过此值（视野扩到地图外卷轴/桌面）才渲染深度雾化，
    /// 凑近地图内时关闭雾化省一次雾 pass（性能优先）。</summary>
    public const float FogEnableDistance = 220f;

    // ---- 默认视角与进场动画 ----

    /// <summary>默认拉距/俯仰（进场动画终点，也是滚轮缩放/中键拖转后的常态视角）。</summary>
    public const float DefaultDist = 90f;
    public const float DefaultPitch = -0.95f;

    /// <summary>进场动画起点拉距（米）：高空俯瞰整张画卷（垂直视场覆盖全图 + 卷轴余量）。</summary>
    public const float IntroStartDist = 1050f;

    /// <summary>进场动画时长（秒）：俯瞰画卷 → 落向地图中心，减速缓入如人逐步靠近。</summary>
    public const float IntroDuration = 2.8f;
}
