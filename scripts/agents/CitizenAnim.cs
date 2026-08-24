namespace Bianjing;

/// <summary>
/// 村民骨骼姿态库（纯函数，无状态）：四段循环动作——idle（呼吸微晃）/ walk（上下浮肩摆袖）/
/// carry（双臂前抱）/ working（俯身挥臂）。每帧由 CitizenAgent 用相位 φ 调 ApplyPose，经骨架
/// 自有的 SetBonePose 通道写入，避免 300 个 agent 各挂 AnimationPlayer 的开销与节点路径脆弱性。
///
/// **阶段 C 暂未启用**——当前 CitizenAgent 取消 Skeleton3D + BoneAttachment3D 方案
/// （Godot 4.7 代码构造 BA3D 反复不跟骨：试过 BoneIdx 显式绑定 + ForceUpdateAllBoneTransforms
/// 都失败），部件直接挂在 _body 下，Position = 视觉绝对位置。先保看到分层人形，姿态动画
/// 留待骨架方案重做时再启用此模块。
/// </summary>
public static class CitizenAnim
{
    public enum AnimState { Idle, Walk, Carry, Working }

    /// <summary>占位：阶段 C 未启用——调用方已不再传骨架过来。保留签名仅作存档。</summary>
    public static void ApplyPose(AnimState state, float phase,
        Godot.Skeleton3D skel, int iRoot, int iSpine, int iHead, int iArmL, int iArmR)
    {
        // 留空——阶段 C 无骨架可写。恢复时把旧版四段姿态还原即可。
    }
}
