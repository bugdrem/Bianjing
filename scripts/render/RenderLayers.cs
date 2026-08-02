namespace Bianjing;

/// <summary>
/// 渲染图层约定（VisualInstance3D.Layers / Camera3D.CullMask 位掩码）：
/// 游戏世界（地图内）与卷轴装裱（地图外）分置两层，主相机同摄两层但可独立开关/施加特效。
/// Map = 1 即引擎默认层，故地图内全部既有渲染节点（地形/水/路/桥/树/建筑/坊区/居民代理/物资堆）
/// 无需逐一指定即落在地图层；卷轴装裱（白底/纸面/卷轴圆柱/图缘裙板/祥云/诗词印章）专用 Scroll 层。
/// </summary>
public static class RenderLayers
{
    /// <summary>地图内（游戏世界）：地形地貌、水系、道路桥梁、树木、建筑、坊区、居民代理、物资堆等。</summary>
    public const uint Map = 1;

    /// <summary>地图外（卷轴装裱）：白底、绢帛纸面、卷轴圆柱、图缘裙板与祥云/诗词/印章等装饰。</summary>
    public const uint Scroll = 2;

    /// <summary>主相机遮罩：同摄地图与卷轴两层。</summary>
    public const uint All = Map | Scroll;
}
