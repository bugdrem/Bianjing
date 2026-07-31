namespace Bianjing;

/// <summary>
/// 相机配置：RTS 轨道相机的距离/俯仰范围与屏缘推移（业务归属：RtsCameraRig）。
/// 世界为 1024m 见方（MapGrid.Size × CellSize），最远距离以能纵览全城为准。
/// </summary>
public static class CameraConfig
{
    /// <summary>缩放距离下限（米）：可凑到单个村民/建筑跟前看细节。</summary>
    public const float MinDist = 2.5f;

    /// <summary>缩放距离上限（米）：地图边长 1024m，此距足以纵览全城。</summary>
    public const float MaxDist = 700f;

    /// <summary>俯仰角范围（弧度，负值向下看）：近乎垂直俯视 ~ 低角度平视。</summary>
    public const float MinPitch = -1.45f;
    public const float MaxPitch = -0.35f;

    /// <summary>屏幕边缘推移触发带宽度（像素）：光标贴边即平移视野。</summary>
    public const float EdgeMargin = 8f;
}
