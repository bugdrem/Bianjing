using System.Collections.Generic;
using Godot;

namespace Bianjing;

/// <summary>
/// 村民关节骨骼（纯代码正向运动学，零外部资产）：把人体拆成 7 个关节——
/// 骨盆 pelvis → 脊柱 spine → 颈 neck，以及 脊柱 → 肩 shoulderL/R → 肘 elbowL/R。
/// 每帧按动画状态生成姿态角，做 FK 推出各关节的局部位置与旋转，再写入所绑部件的
/// Position / Rotation / Scale。
///
/// **为什么不走 Skeleton3D + BoneAttachment3D**（本方案要解决的问题）：
/// Godot 4.7 C# 里代码构造的 Skeleton3D 无法让 BA3D 稳定跟骨（BoneIdx 显式绑定、
/// ForceUpdateAllBoneTransforms、ResetBonePoses 均试过失败，见 CitizenAnim.cs 阶段 C 注释），
/// 且 MultiMesh 不能承载蒙皮网格。本类改用**最直白的等价物**——关节变换在 C# 侧算好，
/// 直接写部件的 Node3D 变换。不需要骨架节点、不需要骨骼网格、不需要 AnimationPlayer，
/// 三百个村民与三千个村民的开销曲线一致（每村民 7 次四元数运算）。
///
/// **服装与可动性的取舍（宋人形象）**：村民着及踝长袍，袍摆覆盖双腿——这既是宋画形制，
/// 也意味着腿部摆动不可见。因此可见的关节集中在**躯干（前倾/扭转）、双臂（摆动/屈肘）、
/// 头部（起伏/张望）与袍摆（随躯干微倾）**四组，由步态相位驱动。若日后改短打装束，
/// 在 Pelvis 下挂 hipL/R → kneeL/R → footL 三节即可，FK 框架不用动。
///
/// **坐标系约定（改姿态角前必读）**：
///   · Godot 中 -Z 为「前」（默认相机朝 -Z 看），故「前倾/前摆」在代码里取**负**的 X 旋转；
///   · `Basis.FromEuler` 默认 YXZ 序，即 `Ry(yaw) * Rx(pitch) * Rz(roll)`——**Rz 先作用**；
///   · 手臂朝向 = `Rx(摆臂) * Rz(外张)`：先外张把手臂从体侧微微撑开，再绕 X 前后摆。
///     摆臂为正时两端同向（臂端 z 分量为负）→ 前摆；左右臂取相反摆臂角即成对侧步态。
/// </summary>
public sealed class CitizenRig
{
    /// <summary>四段循环动作：idle 呼吸闲立 / walk 行走 / carry 负重前行 / working 俯身劳作。</summary>
    public enum AnimState { Idle, Walk, Carry, Working }

    // ---- 关节索引（FK 计算顺序即层级顺序：父关节先算，子关节用父的结果）----
    // 公开：上层（CitizenAgent.ApplyLook）按索引把部件绑到关节。
    public const int JPelvis = 0;    // 骨盆（身体根，承担前倾与整体起伏）
    public const int JSpine = 1;     // 脊柱（躯干扭转）
    public const int JNeck = 2;      // 颈（头部起伏与张望）
    public const int JShoulderL = 3; // 左肩（肩关节，手臂摆动）
    public const int JElbowL = 4;    // 左肘（前臂屈伸）
    public const int JShoulderR = 5; // 右肩
    public const int JElbowR = 6;    // 右肘
    private const int JJointCount = 7;

    /// <summary>部件绑定：部件挂在哪个关节、相对关节的偏移/自身朝向/尺寸。
    /// 偏移与朝向都是**关节局部空间**的量，随关节变换一起进世界。</summary>
    private readonly struct Bind
    {
        public readonly int Joint;
        public readonly Vector3 Offset;   // 部件中心相对关节锚点（如袖中心 = 肘沿臂向上半个袖长处）
        public readonly Quaternion Rot;   // 部件自身朝向修正（网格默认轴向 ≠ 关节轴向时用）
        public readonly Vector3 Scale;    // 部件尺寸

        public Bind(int joint, Vector3 offset, Quaternion rot, Vector3 scale)
        {
            Joint = joint;
            Offset = offset;
            Rot = rot;
            Scale = scale;
        }
    }

    private readonly Node3D _root;                 // 身体根（= CitizenAgent._body），关节都在它的局部空间
    private readonly List<Node3D> _parts = new();
    private readonly List<Bind> _binds = new();

    // 关节局部变换缓存（FK 每帧重算后写入）
    private readonly Vector3[] _jpos = new Vector3[JJointCount];
    private readonly Quaternion[] _jrot = new Quaternion[JJointCount];

    // ---- 骨架尺寸（米，与 CitizenAgent.ApplyLook 的分层完全一致，改模型尺寸只需改这里）----
    /// <summary>袍摆高（= 骨盆高度）：下摆从 y=0 升到 y=LowerH。</summary>
    public float LowerH = 0.85f;
    /// <summary>上身高：从腰带接续上长。</summary>
    public float UpperH = 0.50f;
    /// <summary>头球直径。</summary>
    public float HeadSize = 0.24f;
    /// <summary>肩宽半值（左肩 = -X 此值，右肩 = +X 此值）。
    /// 取上身边宽 + 2cm：肩必须落在上身外缘，否则袖子会陷进躯干里看不见。</summary>
    public float ShoulderHalf = 0.20f;
    /// <summary>肩下垂量：脊柱关节（= 颈底）到肩锚点的下落距离。
    /// 颈底在 y=1.35，而宋人形象没有长颈——肩应落在 y≈1.27（上身外缘偏上）。</summary>
    public float ShoulderDrop = 0.075f;
    /// <summary>上臂长（肩→肘）。袖长按它折算，故袖会同时覆盖上臂与前臂。</summary>
    public float UpperArm = 0.225f;

    // ---- 步态状态 ----
    private AnimState _state = AnimState.Idle;
    private float _phase;          // 步态相位（弧度，一个周期 = 完整两步）
    private float _blend = 1f;     // 姿态权重平滑（换动作时不跳变）
    private float _idleClock;      // 闲立时钟（呼吸/张望用，非周期）

    /// <summary>步频（弧度/秒）：与 CitizenAgent 旧 BobFreq 同量级，肉眼约 1.6Hz 两步。</summary>
    private const float StrideFreq = 10f;
    /// <summary>步伐幅度：步幅越大，摆臂/扭转/前倾越大。0=站定，1=满步。</summary>
    private float _stride;
    private const float StrideAttack = 6f;   // 起步/停步的步伐包络跟踪速率

    /// <summary>手臂外张角（弧度）：把手臂从体侧微微撑开，避免袖子贴身看不出摆动。</summary>
    private const float ArmSplay = 0.22f;

    public CitizenRig(Node3D root)
    {
        _root = root;
        ResetJoints();
    }

    /// <summary>把节点绑到关节。part 可为 MeshInstance3D（部件）或 Node3D（挂点，如搬运货堆的父节点）；
    /// offset 为节点原点相对关节锚点的位移（关节局部空间）。</summary>
    public void Attach(int joint, Node3D part, Vector3 offset, Vector3 scale, Quaternion? rot = null)
    {
        _parts.Add(part);
        _binds.Add(new Bind(joint, offset, rot ?? Quaternion.Identity, scale));
    }

    /// <summary>清空全部绑定（体型重算时先清再绑，避免重复累积）。</summary>
    public void ClearBinds()
    {
        _parts.Clear();
        _binds.Clear();
    }

    /// <summary>已绑定部件数（诊断/测试用）。</summary>
    public int BoundCount => _parts.Count;

    /// <summary>换动作：同状态不打断相位；不同状态时由 _blend 平滑过渡，避免动作切换跳帧。</summary>
    public void SetState(AnimState state)
    {
        if (_state == state)
            return;
        _state = state;
        _blend = 0f; // 重新淡入到新姿态
    }

    /// <summary>
    /// 每帧推进：按状态生成姿态角 → FK → 写部件变换。
    /// stride01 为「移动强度」0..1（上层传是否在走），决定动作幅度包络；
    /// speedScale 为游戏速度倍率，让 4x 快进时动作同步加快而不是抽帧。
    /// </summary>
    public void Tick(AnimState state, float dt, float stride01, float speedScale)
    {
        SetState(state);

        // 步伐包络：起步渐强、停步渐弱，避免瞬间切到满幅摆动
        float target = Mathf.Clamp(stride01, 0f, 1f);
        _stride = Mathf.MoveToward(_stride, target, StrideAttack * dt);
        _blend = Mathf.Min(1f, _blend + dt * 4f);
        _idleClock += dt;

        // 相位只在真正迈步时推进；停步时冻结并平滑回落（由 _stride 归零实现）
        _phase += StrideFreq * _stride * speedScale * dt;
        if (_phase > Mathf.Tau)
            _phase -= Mathf.Tau;

        BuildPose();
        ForwardKinematics();
        WriteParts();
    }

    // ---- 姿态生成：把动画状态翻译成各关节的局部旋转角 ----
    // 约定：_lean/_headTilt 正 = 前俯；_armL/_armR 正 = 前摆；_fore 恒正（手臂只朝一个方向弯）。

    private float _lean;    // 躯干前倾
    private float _twist;   // 躯干扭转（绕竖直轴，与对侧摆臂配合）
    private float _armL, _armR;       // 肩摆动
    private float _foreL, _foreR;     // 肘屈
    private float _bob;     // 整体起伏（米）
    private float _headTilt, _headYaw;

    private void BuildPose()
    {
        // 默认站姿：全部归零，由各状态覆写
        _lean = 0f; _twist = 0f; _armL = 0f; _armR = 0f;
        _foreL = 0.06f; _foreR = 0.06f; _bob = 0f; _headTilt = 0f; _headYaw = 0f;

        switch (_state)
        {
            case AnimState.Idle:
                BuildIdle();
                break;
            case AnimState.Walk:
                BuildWalk();
                break;
            case AnimState.Carry:
                BuildCarry();
                break;
            case AnimState.Working:
                BuildWorking();
                break;
        }
    }

    /// <summary>闲立：呼吸（躯干微起伏）+ 偶发张望 + 双臂自然微张。
    /// 用两个互质频率叠加，避免读出机械周期感。</summary>
    private void BuildIdle()
    {
        float t = _idleClock;
        float breath = Mathf.Sin(t * 1.7f) * 0.5f + Mathf.Sin(t * 0.6f + 1.3f) * 0.5f;
        _bob = breath * 0.012f * _blend;
        _lean = breath * 0.02f * _blend;
        // 张望：平滑立方包络让转头慢起慢停，不像方波跳变
        float look = Mathf.Sin(t * 0.35f);
        _headYaw = Mathf.Sign(look) * Mathf.Pow(Mathf.Abs(look), 3f) * 0.5f * _blend;
        _headTilt = Mathf.Sin(t * 0.9f + 0.7f) * 0.06f * _blend;
        // 站立时双臂微微撑开，袖口随呼吸轻颤
        _armL = 0.04f + breath * 0.03f;
        _armR = -0.04f - breath * 0.03f;
        _foreL = 0.14f; _foreR = 0.14f;
    }

    /// <summary>行走：核心四件套——躯干前倾、躯干扭转、对侧摆臂、上下起伏。
    /// 扭转与左臂取同一相位（左臂前摆时左肩相对骨盆后收），故只需一个 sin/cos。</summary>
    private void BuildWalk()
    {
        float s = _stride * _blend;
        float p = _phase;
        float swing = Mathf.Sin(p);        // 主摆动（左臂/扭转）
        float bounce = Mathf.Sin(p * 2f);  // 起伏（一步两峰）

        _lean = 0.09f * s;                        // 行走前俯约 5°，停步回正
        _twist = -0.13f * s * swing;              // 左肩相对骨盆反向 → 大步流星感
        _armL = 0.42f * s * swing;                // 左臂前摆 = swing 正向
        _armR = -0.42f * s * swing;               // 右臂反相（对侧步态）
        _foreL = 0.30f + 0.18f * s * Mathf.Cos(p); // 屈肘随步态开合
        _foreR = 0.30f - 0.18f * s * Mathf.Cos(p);
        _bob = 0.055f * s * bounce;               // 起伏约 6cm（与旧 BobAmp 同量级）
        _headTilt = -0.04f * s;                   // 走路微低头前望
        _headYaw = 0.05f * s * swing;
    }

    /// <summary>负重：双臂前抱（抱货/捧物），躯干微后仰配平，起伏减半——抱东西走得稳。</summary>
    private void BuildCarry()
    {
        float s = _stride * _blend;
        float p = _phase;
        float swing = Mathf.Sin(p);
        float bounce = Mathf.Sin(p * 2f);

        _lean = -0.06f * s;                  // 后仰配平前臂负载
        _twist = -0.05f * s * swing;         // 扭转大幅减小：抱稳优先
        _armL = 1.35f + 0.10f * s * swing;   // 双臂前抱约 77°（手与胸齐高）
        _armR = 1.35f - 0.10f * s * swing;
        _foreL = 0.95f + 0.08f * s * Mathf.Cos(p); // 屈肘约 54°（抱持姿态）
        _foreR = 0.95f - 0.08f * s * Mathf.Cos(p);
        _bob = 0.03f * s * bounce;           // 起伏减半
        _headTilt = 0.10f * s;               // 低头看怀中货
        _headYaw = 0f;
    }

    /// <summary>劳作：俯身 + 快速交替挥臂（砍伐/锤击），起伏更深、躯干扭转更大。</summary>
    private void BuildWorking()
    {
        float s = _blend;
        float p = _phase * 1.6f;              // 劳作节奏比走路快
        float swing = Mathf.Sin(p);

        _lean = 0.42f * s;                    // 俯身约 24°
        _twist = -0.30f * s * swing;          // 挥臂带腰
        _armL = 0.85f * s * swing + 0.15f;    // 大幅挥臂，落点不低于体侧
        _armR = -0.85f * s * swing - 0.15f;
        _foreL = 0.55f + 0.35f * s * Mathf.Cos(p);
        _foreR = 0.55f - 0.35f * s * Mathf.Cos(p);
        _bob = 0.035f * s * Mathf.Sin(p * 2f);
        _headTilt = 0.22f * s;                // 盯着手里的活
        _headYaw = 0.08f * s * swing;
    }

    // ---- 正向运动学：由根沿层级推各关节局部变换 ----

    private void ResetJoints()
    {
        for (int i = 0; i < JJointCount; i++)
        {
            _jpos[i] = Vector3.Zero;
            _jrot[i] = Quaternion.Identity;
        }
    }

    private void ForwardKinematics()
    {
        // 骨盆：身体根正上方 LowerH 处（袍摆顶），承担前倾与起伏。
        // 前倾为负 X 旋转（-Z 为前），故此处取 -_lean。
        Quaternion pelvisRot = RotX(-_lean);
        _jpos[JPelvis] = new Vector3(0f, LowerH + _bob, 0f);
        _jrot[JPelvis] = pelvisRot;

        // 脊柱：沿骨盆的「上」方向长 UpperH——前倾时躯干跟着倾，而不是竖直戳在天上。
        Quaternion spineLocal = RotY(_twist);
        _jpos[JSpine] = _jpos[JPelvis] + pelvisRot * (Vector3.Up * UpperH);
        _jrot[JSpine] = pelvisRot * spineLocal;

        // 颈：脊柱正上方（头顶下方），转头/点头
        Quaternion neckLocal = RotX(-_headTilt) * RotY(_headYaw);
        _jpos[JNeck] = _jpos[JSpine];
        _jrot[JNeck] = _jrot[JSpine] * neckLocal;

        // 肩/肘：肩锚点在脊柱两侧（随躯干扭转），左右镜像
        BuildArm(JShoulderL, JElbowL, -1f, _armL, _foreL);
        BuildArm(JShoulderR, JElbowR, +1f, _armR, _foreR);
    }

    private void BuildArm(int jShoulder, int jElbow, float side, float swing, float fore)
    {
        // 肩锚点：脊柱关节处横开 ShoulderHalf、下落 ShoulderDrop（落在上身外缘偏上）
        _jpos[jShoulder] = _jpos[JSpine] + _jrot[JSpine] * new Vector3(ShoulderHalf * side, -ShoulderDrop, 0f);

        // 肩朝向 = 躯干旋转 × [前后摆动 × 体侧外张]。
        // 先外张（Rz，把手臂从体侧撑开 ArmSplay），再绕 X 前后摆（Rx）。
        // side 为 ±1：外张方向随手性翻转，左右臂对称。
        Quaternion shoulderLocal = RotX(swing) * RotZ(ArmSplay * side);
        _jrot[jShoulder] = _jrot[JSpine] * shoulderLocal;

        // 肘锚点：沿肩朝向的「下」方向长 UpperArm
        _jpos[jElbow] = _jpos[jShoulder] + _jrot[jShoulder] * (Vector3.Down * UpperArm);

        // 肘朝向 = 肩朝向 × 前臂屈曲（绕 X，恒朝一个方向弯）
        _jrot[jElbow] = _jrot[jShoulder] * RotX(fore);
    }

    private static Quaternion RotX(float a) => Basis.FromEuler(new Vector3(a, 0f, 0f)).GetRotationQuaternion();
    private static Quaternion RotY(float a) => Basis.FromEuler(new Vector3(0f, a, 0f)).GetRotationQuaternion();
    private static Quaternion RotZ(float a) => Basis.FromEuler(new Vector3(0f, 0f, a)).GetRotationQuaternion();

    // ---- 写部件：部件变换 = 关节变换 ∘ 绑定偏移 ----

    private void WriteParts()
    {
        for (int i = 0; i < _parts.Count; i++)
        {
            var part = _parts[i];
            if (part == null)
                continue;
            var b = _binds[i];
            // 部件中心 = 关节位置 + 关节旋转 × 绑定偏移；朝向 = 关节旋转 × 部件自身朝向
            part.Position = _jpos[b.Joint] + _jrot[b.Joint] * b.Offset;
            part.Rotation = (_jrot[b.Joint] * b.Rot).GetEuler();
            part.Scale = b.Scale;
        }
    }

    /// <summary>当前步伐强度（0..1），供上层判断是否在走。</summary>
    public float Stride => _stride;
}
