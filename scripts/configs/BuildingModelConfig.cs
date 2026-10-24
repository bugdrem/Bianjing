using System.Collections.Generic;

namespace Bianjing;

/// <summary>
/// 建筑零件模型配置（模块化套件的资产绑定层）。
///
/// **设计动机**：旧方案是「整栋建筑一个 .glb」的二选一——要么整栋走资产（缺失则整栋退回原始体），
/// 要么整栋走原始体。这带来三个问题：① 风格难统一（不同来源的整栋模型拼在一起必然割裂）；
/// ② 无渐进接入（必须一次性备齐所有建筑模型才能看到效果）；③ 等级/占地变化时无法复用零件。
///
/// **本方案**：把建筑拆成 13 个**零件槽位**（地基/台基/房体/屋顶/端坡/檐口/屋脊/立柱/招幌/灯笼/
/// 门窗/院墙/门）。几何变换仍由 <see cref="BuildingModelFactory"/> 统一计算（那 531 行是可信的），
/// 本配置只决定「哪个槽位用哪个 .glb 网格来渲染」。于是：
///   · 只做屋顶模型 → 只有屋顶变资产，其余照旧原始体，立即可见；
///   · 同槽位全城共用一个 MultiMesh，批渲开销与原始体方案完全相同；
///   · 想换风格只改一个路径，不用碰任何 C#。
///
/// **约定**：路径为空 = 该槽位走原始体（默认全部为空，即纯代码造型，零资产依赖）。
/// 路径统一挂在 <see cref="AssetRoot"/> 下，换目录只改这一个字符串。
/// </summary>
public static class BuildingModelConfig
{
    /// <summary>资产根目录（res:// 相对路径）。整个套件的零件都从这里找，换目录只改这一处。</summary>
    public const string AssetRoot = "res://assets/buildings/";

    /// <summary>零件槽位：与 <see cref="BuildingModelFactory.BuildRoleLists"/> 的角色一一对应，
    /// 另加 Door（门，由 BuildingLayer 按占地边界单独计算）。</summary>
    public enum Slot
    {
        Foundation, // 地基：房体下不透明基座
        Step,       // 台基：房体正下方石阶基座
        Body,       // 房体：半透主体（民居可透视屋内）
        Roof,       // 主坡屋顶：三棱柱
        RoofEnd,    // 庑殿端坡：威仪建筑的第二重顶
        Eave,       // 檐口：房体顶一周薄环
        Ridge,      // 屋脊：脊轴细条
        Pillar,     // 立柱：四角/边中/前廊
        Banner,     // 招幌：商铺前脸竖条
        Lantern,    // 灯笼：檐下点缀
        Window,     // 门窗：大门 + 窗
        Wall,       // 院墙：民居大院夯土围墙
        Door,       // 门：沿占地边界贴墙（大门金/后门暗木）
    }

    /// <summary>槽位数量（建 MultiMesh 层数组用）。</summary>
    public const int SlotCount = 13;

    /// <summary>槽位 → 零件文件名（相对 <see cref="AssetRoot"/>）。空 = 走原始体。</summary>
    private static readonly Dictionary<Slot, string> Files = new()
    {
        // —— 默认全部留空：纯代码宋代造型，零外部资产依赖 ——
        // 接入资产时按下面格式填（以 Quaternius Medieval Village MegaKit CC0 为例，
        // 把解压出来的对应模型重命名后丢进 assets/buildings/ 即可）：
        //   { Slot.Roof,   "kit/roof_gable.glb"   },  // 主坡屋顶
        //   { Slot.Body,   "kit/wall_plaster.glb" },  // 房体墙段
        //   { Slot.Door,   "kit/door_wood.glb"    },  // 门
        //   { Slot.Window, "kit/window_lattice.glb" }, // 窗
        //   { Slot.Pillar, "kit/pillar_round.glb" },  // 立柱
        //   { Slot.Wall,   "kit/wall_courtyard.glb" }, // 院墙
    };

    /// <summary>槽位 → 零件全路径；未配置返回空串。</summary>
    public static string PathOf(Slot slot)
    {
        if (!Files.TryGetValue(slot, out var f) || string.IsNullOrEmpty(f))
            return "";
        return AssetRoot + f;
    }

    /// <summary>槽位 → 去饱和强度（0=原色，1=全灰）。用于把不同来源的资产调和进同一套宋式灰调。</summary>
    public static float DesaturateOf(Slot slot) => slot switch
    {
        Slot.Roof or Slot.RoofEnd or Slot.Eave or Slot.Ridge => 0.35f, // 瓦作压灰，统一成深灰瓦
        Slot.Body or Slot.Wall or Slot.Step or Slot.Foundation => 0.15f, // 夯土/墙面微降饱和
        _ => 0f,
    };

    /// <summary>槽位 → 亮度增益（>1 提亮）。屋顶略压暗、木作略提亮，拉开层次。</summary>
    public static float BrightnessOf(Slot slot) => slot switch
    {
        Slot.Roof or Slot.RoofEnd => 0.9f,
        Slot.Body or Slot.Wall => 1.05f,
        _ => 1f,
    };

    /// <summary>是否配置了任何 GLB 零件（false 时 BuildingLayer 不建套件层，零开销）。</summary>
    public static bool HasKitPiece
    {
        get
        {
            foreach (var kv in Files)
                if (!string.IsNullOrEmpty(kv.Value))
                    return true;
            return false;
        }
    }
}
