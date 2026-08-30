namespace Bianjing;

/// <summary>
/// 野外动物外部模型配置。资产来源：Quaternius「Farm Animal Pack」（CC0 1.0 公共领域，
/// 可商用无需署名），7 种动物均为无贴图纯材质色、约 560–800 面，MultiMesh 批渲零压力。
///
/// 每种动物一个 .glb（由 GltfMeshMerger 归一化到"底面 y=0、高 1、水平居中"），
/// HeightOf 即归一化后的实际站立高度（世界单位）。路径留空的物种不建渲染层（回退程序化方块猪）。
/// </summary>
public static class AnimalModelConfig
{
    /// <summary>物种数量：与 AnimalObj.Kind / 动物渲染层一一对应。</summary>
    public const int SpeciesCount = 7;

    public const int Cow = 0;    // 牛
    public const int Horse = 1;  // 马
    public const int Llama = 2;  // 羊驼
    public const int Pig = 3;    // 猪
    public const int Pug = 4;    // 巴哥犬
    public const int Sheep = 5;  // 绵羊
    public const int Zebra = 6;  // 斑马

    // ---- 模型路径（assets/animals/；由 Godot 编辑器导入或 GltfDocument 运行时解析均可）----
    private const string CowPath = "res://assets/animals/Cow.glb";
    private const string HorsePath = "res://assets/animals/Horse.glb";
    private const string LlamaPath = "res://assets/animals/Llama.glb";
    private const string PigPath = "res://assets/animals/Pig.glb";
    private const string PugPath = "res://assets/animals/Pug.glb";
    private const string SheepPath = "res://assets/animals/Sheep.glb";
    private const string ZebraPath = "res://assets/animals/Zebra.glb";

    // ---- 站立高度（世界单位）。[PLACEHOLDER] 未实跑校调：按与村民体量相当的比例估的初值
    // （村民 ≈0.43 单位高，原方块猪渲染后 ≈0.46–0.57）。用户反馈"体积偏大"，首版整体下调约 45%。 ----
    private const float CowHeight = 0.42f;
    private const float HorseHeight = 0.50f;
    private const float LlamaHeight = 0.44f;
    private const float PigHeight = 0.28f;
    private const float PugHeight = 0.20f;
    private const float SheepHeight = 0.30f;
    private const float ZebraHeight = 0.47f;

    /// <summary>野外会出现的物种：不含 Pug（狗是家畜/宠物，不该出现在野外）。
    /// Pug 仍保留在 SpeciesCount 与其模型路径中，供后期村民宠物系统复用。</summary>
    public static readonly int[] WildlifeKinds = { Cow, Horse, Llama, Pig, Sheep, Zebra };

    // ---- 配色调和：Quaternius 纯色比 Kenney 柔和，轻调一档即可。同上 [PLACEHOLDER]。----
    public const float Desaturate = 0.25f;
    public const float Brightness = 0.98f;

    // ---- 视野分级（LOD）：骨骼动画是逐只 CPU 开销，远景/画外没必要白白耗 ----
    // 相机距离超过此值（广角/拉远俯瞰）或不在视锥内 → 只按 FarUpdateSeconds 间隔更新后台位置，
    // 并暂停动画；在视锥内且够近 → 每帧更新并播放 Idle/Walk。均为 [PLACEHOLDER]。
    /// <summary>播放动画的最大相机距离（世界单位）。</summary>
    public const float AnimateMaxDistance = 90f;
    /// <summary>非动画档（远景/画外）的位置更新间隔（秒）。</summary>
    public const float FarUpdateSeconds = 1.0f;

    /// <summary>某物种的模型路径。</summary>
    public static string PathOf(int kind) => kind switch
    {
        Horse => HorsePath,
        Llama => LlamaPath,
        Pig => PigPath,
        Pug => PugPath,
        Sheep => SheepPath,
        Zebra => ZebraPath,
        _ => CowPath,
    };

    /// <summary>某物种的站立高度（模型已归一化到高 1，故此处即实际高度）。</summary>
    public static float HeightOf(int kind) => kind switch
    {
        Horse => HorseHeight,
        Llama => LlamaHeight,
        Pig => PigHeight,
        Pug => PugHeight,
        Sheep => SheepHeight,
        Zebra => ZebraHeight,
        _ => CowHeight,
    };
}
