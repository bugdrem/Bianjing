using Godot;

namespace Bianjing;

/// <summary>
/// 地图渲染图层基类：地表渲染按内容切成地形/水面/道路/植被/建筑/叠加六个独立 Node3D，
/// 图层自持子节点与材质，只向协调器（GridRenderer）暴露极简接口。
/// 关键约束：**脏标记与每帧预算仲裁集中在协调器**——图层不订阅 EventBus、不实现 _Process，
/// 否则六个图层各跑一份预算，同帧重建量会翻六倍，分帧摊峰就失效了。
/// </summary>
public abstract partial class MapLayer : Node3D
{
    /// <summary>分块边长（格）：1024 图 16×16 块，单块重建量恒定（与协调器共用，保证分块划分一致）。</summary>
    public const int ChunkCells = 64;

    /// <summary>分块阵列边长（块数）：128 图 2×2 块，1024 图 16×16 块。</summary>
    public static int ChunksPerSide => (MapGrid.Size + ChunkCells - 1) / ChunkCells;

    /// <summary>初始化图层：建共享网格/材质与各分块子节点。协调器在 _Ready 中调用，早于首次 ApplyChunk。</summary>
    public abstract void Build();

    /// <summary>分块覆盖的格区间（半开 [x0,x1) × [y0,y1)）：末尾块按图幅裁剪。</summary>
    public static (int x0, int y0, int x1, int y1) ChunkRect(int index)
    {
        int cx = index % ChunksPerSide, cy = index / ChunksPerSide;
        int x0 = cx * ChunkCells, y0 = cy * ChunkCells;
        return (x0, y0, Mathf.Min(x0 + ChunkCells, MapGrid.Size), Mathf.Min(y0 + ChunkCells, MapGrid.Size));
    }
}
