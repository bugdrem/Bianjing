using Godot;

namespace Bianjing;

/// <summary>
/// 水面层：每分块一张半透三角网格——四角顶点取邻水格水位均值，坡河上水面连续倾斜不再逐格阶梯，
/// 河床透水可见；与地形层同模式（顶点插值）但独立成层，调水色/透明度不会牵连地形重建。
/// </summary>
public partial class WaterLayer : MapLayer
{
    private StandardMaterial3D _mat;
    private MeshInstance3D[] _meshes;

    public override void Build()
    {
        // 顶点色半透（透见河床），双面免低角度穿帮漏面；不投影——水面阴影在浅河上会糊成一片
        _mat = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _meshes = new MeshInstance3D[ChunksPerSide * ChunksPerSide];
        for (int i = 0; i < _meshes.Length; i++)
        {
            _meshes[i] = new MeshInstance3D
            {
                Name = $"Water{i}",
                MaterialOverride = _mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_meshes[i]);
        }
    }

    /// <summary>把分块水面缓冲刷成网格：空缓冲挂 null（节点不绘制）。</summary>
    public void ApplyChunk(int index, ChunkGeometry geo) =>
        _meshes[index].Mesh = LayerKit.MeshFrom(geo.Water);
}
