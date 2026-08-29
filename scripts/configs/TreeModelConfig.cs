namespace Bianjing;

/// <summary>
/// 树木外部 3D 模型配置（外部资产接入）。
///
/// 每种树对应一个「完整树」模型（含树干 + 树冠），由 TreeModelFactory 合并为单一 Mesh 后
/// 交给 GridRenderer 的 MultiMesh 层批量绘制——MultiMesh 一次只能承载一个 Mesh，
/// 而树木模型通常是「树干 + 树冠」多个子网格，故必须先合并。
///
/// ModelPath 填 res:// 相对路径（.glb / .gltf）；留空则该树种继续用代码原始体造型
/// （圆柱树干 + 圆锥/椭球树冠），即接入前的既有表现，便于逐树种灰度替换。
/// TargetHeight 为模型归一化后的落地高度（世界单位），用于抹平不同来源模型的尺度差异。
/// </summary>
public static class TreeModelConfig
{
    /// <summary>树种数量：与 GridRenderer 的树模型层一一对应。</summary>
    public const int SpeciesCount = 3;

    /// <summary>阔叶树（最常见，默认造型）。</summary>
    public const int Broadleaf = 0;
    /// <summary>针叶 / 松柏。</summary>
    public const int Conifer = 1;
    /// <summary>果树（挂果树专用，造型与阔叶区分）。</summary>
    public const int Fruit = 2;

    // ---- 模型路径（留空 = 用代码原始体造型）----
    // 资产来源：Kenney「Nature Kit」（CC0 1.0 公共领域，可商用、无需署名），授权文件见 assets/kenney_License.txt。
    // 导入由 Godot 编辑器首次打开项目时自动完成（生成 .import）。
    private const string BroadleafPath = "res://assets/trees/tree_default.glb";
    private const string ConiferPath = "res://assets/trees/tree_pineDefaultA.glb";
    private const string FruitPath = "res://assets/trees/tree_plateau.glb";

    // ---- 落地高度（世界单位；树高 = 该值 × 逐株生长/大小系数）----
    // [PLACEHOLDER] 未实跑校调：按宋代宅院尺度估的初值，需实跑比对建筑层高后再定。
    private const float BroadleafHeight = 4.2f;
    private const float ConiferHeight = 5.0f;
    private const float FruitHeight = 3.6f;

    // ---- 配色调和：Kenney 原色是卡通高饱和（树冠青绿 0.16/0.79/0.67），与本作宣纸-墨-青的淡雅调不搭 ----
    // 在烘焙顶点色阶段统一"往灰度拉"再压亮：比用实例色通道乘算更稳妥——
    // 后者无法同时把树冠拉向橄榄绿又不把树干推成红棕，会破坏两者的固有色关系。
    // [PLACEHOLDER] 未实跑校调：按"去饱和 45%、亮度 ×0.95"估的初值，实跑后按观感微调。
    /// <summary>去饱和强度 0–1：0 保留原色，1 完全灰度。</summary>
    public const float Desaturate = 0.45f;
    /// <summary>去饱和后的整体亮度倍率。</summary>
    public const float Brightness = 0.95f;

    /// <summary>某树种的模型路径；未配置返回空串。</summary>
    public static string PathOf(int species) => species switch
    {
        Conifer => ConiferPath,
        Fruit => FruitPath,
        _ => BroadleafPath,
    };

    /// <summary>某树种的落地高度（模型已归一化到高 1，故此处即实际树高）。</summary>
    public static float HeightOf(int species) => species switch
    {
        Conifer => ConiferHeight,
        Fruit => FruitHeight,
        _ => BroadleafHeight,
    };
}
