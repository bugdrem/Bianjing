using Godot;

namespace Bianjing;

/// <summary>
/// 植被层：树木按分块 MultiMesh 批渲——原始体三件套（圆柱树干 / 圆锥冠 / 椭球冠）
/// + 每种外部树木模型一层。树木有独立脏标（月度生长只刷本层不重建地形），
/// 拆出来后这条轻量路径才真正独立：刷新时只填 MultiMesh，连水面/道路缓冲都不碰。
/// </summary>
public partial class VegetationLayer : MapLayer
{
    private CylinderMesh _trunkMesh;
    private CylinderMesh _coneCrownMesh;
    private SphereMesh _ballCrownMesh;
    /// <summary>外部树木模型（合并后的单一网格）；未配置模型的树种为 null，继续走原始体三件套。</summary>
    private ArrayMesh[] _treeModelMeshes;

    private MultiMeshInstance3D[] _trunks;
    private MultiMeshInstance3D[] _coneCrowns;
    private MultiMeshInstance3D[] _ballCrowns;
    /// <summary>外部树木模型层：每树种一层，每块一组；未配置模型的树种层为 null（不产生绘制开销）。</summary>
    private MultiMeshInstance3D[][] _treeModels;

    public override void Build()
    {
        // 树木三件套：单位尺寸网格，实例变换里再按株缩放——
        // 树干圆柱（上细下粗）；树冠分圆锥（针叶）与椭球（阔叶）两形，逐株伪随机选型
        _trunkMesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.16f, Height = 1f };
        _trunkMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _coneCrownMesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 1.1f, Height = 3f };
        _coneCrownMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };
        _ballCrownMesh = new SphereMesh { Radius = 0.5f, Height = 1f };
        _ballCrownMesh.Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true };

        _treeModelMeshes = new ArrayMesh[TreeModelConfig.SpeciesCount];
        for (int si = 0; si < TreeModelConfig.SpeciesCount; si++)
            _treeModelMeshes[si] = TreeModelFactory.MeshOf(si);

        int n = ChunksPerSide * ChunksPerSide;
        _trunks = new MultiMeshInstance3D[n];
        _coneCrowns = new MultiMeshInstance3D[n];
        _ballCrowns = new MultiMeshInstance3D[n];
        _treeModels = new MultiMeshInstance3D[n][];
        for (int i = 0; i < n; i++)
        {
            _trunks[i] = LayerKit.MakeMulti(_trunkMesh, useColors: true);
            _coneCrowns[i] = LayerKit.MakeMulti(_coneCrownMesh, useColors: true);
            _ballCrowns[i] = LayerKit.MakeMulti(_ballCrownMesh, useColors: true);
            AddChild(_trunks[i]);
            AddChild(_coneCrowns[i]);
            AddChild(_ballCrowns[i]);

            _treeModels[i] = new MultiMeshInstance3D[TreeModelConfig.SpeciesCount];
            for (int si = 0; si < TreeModelConfig.SpeciesCount; si++)
            {
                if (_treeModelMeshes[si] == null)
                    continue; // 未配置的树种不建层
                _treeModels[i][si] = LayerKit.MakeMulti(_treeModelMeshes[si], useColors: true);
                AddChild(_treeModels[i][si]);
            }
        }
    }

    /// <summary>外部树木模型网格表：交给 ChunkGeometryBuilder 决定整株走模型还是原始体。</summary>
    public ArrayMesh[] TreeModelMeshes => _treeModelMeshes;

    /// <summary>把分块树木缓冲刷进各 MultiMesh 层（整块重建与「只刷树」两条路径共用）。</summary>
    public void ApplyChunk(int index, ChunkGeometry geo)
    {
        LayerKit.FillMultiMesh(_trunks[index].Multimesh, geo.Trunks);
        LayerKit.FillMultiMesh(_coneCrowns[index].Multimesh, geo.ConeCrowns);
        LayerKit.FillMultiMesh(_ballCrowns[index].Multimesh, geo.BallCrowns);
        for (int s = 0; s < _treeModels[index].Length; s++)
            if (_treeModels[index][s] != null)
                LayerKit.FillMultiMesh(_treeModels[index][s].Multimesh, geo.TreeModels[s]);
    }
}
