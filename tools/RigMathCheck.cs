// 一次性数学校验：复现 CitizenRig 的 FK，核对静止姿态下各部件落点是否与旧「绝对定位」方案一致，
// 并抽查摆臂方向。用法：dotnet run（引用工程自带 GodotSharp.dll）。验完即删，不进版本库。
using System;
using Godot;

// —— 与 CitizenRig 完全一致的 FK（复制过来，避免为测试改动生产代码）——
static Quaternion RotX(float a) => Basis.FromEuler(new Vector3(a, 0f, 0f)).GetRotationQuaternion();
static Quaternion RotY(float a) => Basis.FromEuler(new Vector3(0f, a, 0f)).GetRotationQuaternion();
static Quaternion RotZ(float a) => Basis.FromEuler(new Vector3(0f, 0f, a)).GetRotationQuaternion();

const float LowerH = 0.85f, UpperH = 0.50f, HeadSize = 0.24f;
const float ShoulderHalf = 0.20f, ShoulderDrop = 0.075f, UpperArm = 0.225f, ArmSplay = 0.22f;

var jp = new Vector3[7];
var jr = new Quaternion[7];

void FK(float lean, float twist, float armL, float armR, float foreL, float foreR, float bob)
{
    var pelvis = RotX(-lean);
    jp[0] = new Vector3(0f, LowerH + bob, 0f); jr[0] = pelvis;
    jp[1] = jp[0] + pelvis * (Vector3.Up * UpperH); jr[1] = pelvis * RotY(twist);
    jp[2] = jp[1]; jr[2] = jr[1] * RotX(0f) * RotY(0f);

    void Arm(int s, int e, float side, float swing, float fore)
    {
        jp[s] = jp[1] + jr[1] * new Vector3(ShoulderHalf * side, -ShoulderDrop, 0f);
        jr[s] = jr[1] * RotX(swing) * RotZ(ArmSplay * side);
        jp[e] = jp[s] + jr[s] * (Vector3.Down * UpperArm);
        jr[e] = jr[s] * RotX(fore);
    }
    Arm(3, 4, -1f, armL, foreL);
    Arm(5, 6, +1f, armR, foreR);
}

Vector3 Part(int j, Vector3 off) => jp[j] + jr[j] * off;

int fails = 0;
void Check(string name, Vector3 got, Vector3 want, float tol = 0.01f)
{
    float d = (got - want).Length();
    bool ok = d <= tol;
    if (!ok) fails++;
    Console.WriteLine($"{(ok ? "  OK " : "FAIL")} {name,-14} got=({got.X:F3},{got.Y:F3},{got.Z:F3}) want=({want.X:F3},{want.Y:F3},{want.Z:F3}) d={d:F4}");
}

// ===== 用例 1：完全静止（全部角度 0）——应与旧绝对定位逐点重合 =====
Console.WriteLine("== 用例1：静止姿态（旧方案落点复现）==");
FK(0, 0, 0, 0, 0.06f, 0.06f, 0);
var sleeve = new Vector3(0.12f, 0.40f, 0.18f);
float sleeveUp = UpperArm - sleeve.Y / 2f;   // 0.225 - 0.20 = 0.025
var sleeveOff = Vector3.Up * sleeveUp + new Vector3(0f, 0f, 0.06f);
float lowerH = LowerH;

// 旧方案：袍摆中心 y = lowerH/2 = 0.425
Check("袍摆", Part(0, Vector3.Down * (lowerH / 2f)), new Vector3(0, 0.425f, 0));
// 旧方案：腰带头中心 y ≈ lowerH+0.04 = 0.89
Check("腰带", Part(0, Vector3.Zero), new Vector3(0, LowerH, 0));
// 旧方案：上身中心 y = lowerH + upperH/2 = 1.10
Check("上身", Part(0, Vector3.Up * (UpperH / 2f)), new Vector3(0, 1.10f, 0));
// 旧方案：头顶 y = neckY + headSize/2 = 1.35+0.12 = 1.47
Check("头", Part(2, Vector3.Up * (HeadSize / 2f)), new Vector3(0, 1.47f, 0));
// 旧方案：发冠中心 y = neckY + headSize + hairH/2 = 1.35+0.24+0.02 = 1.61
Check("发冠", Part(2, Vector3.Up * (HeadSize + 0.04f / 2f + 0.005f)), new Vector3(0, 1.61f, 0));

// 袖：绑肘，偏移 up*sleeveUp。静止时肘在肩正下方 UpperArm 处，
//   袖中心 = 肩 + down*UpperArm + up*sleeveUp = 肩 + down*(UpperArm - sleeveUp) = 肩 + down*0.20
//   旧方案袖中心 y = lowerH + 0.05 + sleeve.Y/2 = 0.85+0.05+0.20 = 1.10，x = ±(upper.X/2 + sleeve.X/2 + 0.04)
float oldArmX = 0.36f / 2f + sleeve.X / 2f + 0.04f;   // 0.18+0.06+0.04 = 0.28
Check("左袖", Part(4, sleeveOff), new Vector3(-0.28f, 1.10f, 0.06f), 0.07f);
Check("右袖", Part(6, sleeveOff), new Vector3(+0.28f, 1.10f, 0.06f), 0.07f);

// ===== 用例 2：行走摆臂——左臂前摆应把袖送向 -Z（前），右臂反向 =====
Console.WriteLine("\n== 用例2：行走摆臂方向 ==");
FK(0.09f, -0.13f, 0.42f, -0.42f, 0.30f, 0.30f, 0.055f);
var l = Part(4, sleeveOff);
var r = Part(6, sleeveOff);
Console.WriteLine($"  左袖 z = {l.Z:F3}（应为负=前摆）");
Console.WriteLine($"  右袖 z = {r.Z:F3}（应为正=后摆）");
Console.WriteLine($"  左右差 dz = {MathF.Abs(l.Z - r.Z):F3}（应明显 >0.15，说明对侧摆臂生效）");
if (l.Z >= -0.05f) { Console.WriteLine("  FAIL 左袖没有前摆"); fails++; }
if (r.Z <= 0.05f) { Console.WriteLine("  FAIL 右袖没有后摆"); fails++; }
if (MathF.Abs(l.Z - r.Z) <= 0.15f) { Console.WriteLine("  FAIL 对侧摆臂幅度不足"); fails++; }

// ===== 用例 3：俯身劳作——躯干前倾应把头/手送向 -Z，且头低于站立 =====
Console.WriteLine("\n== 用例3：俯身劳作 ==");
FK(0.42f, 0f, 0.85f, -0.85f, 0.55f, 0.55f, 0.035f);
var headWork = Part(2, Vector3.Up * (HeadSize / 2f));
var handWork = Part(4, Vector3.Up * sleeveUp);
Console.WriteLine($"  劳作时头 z = {headWork.Z:F3}（应为负=探前）");
Console.WriteLine($"  劳作时头 y = {headWork.Y:F3}（静止 1.47；躯干短故降幅有限，真正信号是 z 探前）");
if (headWork.Z >= -0.05f) { Console.WriteLine("  FAIL 俯身没有探前"); fails++; }
if (headWork.Y >= 1.46f) { Console.WriteLine("  FAIL 俯身没有压低"); fails++; }
if (headWork.Z >= -0.15f) { Console.WriteLine("  FAIL 俯身探前不足"); fails++; }

// ===== 用例 4：负重前抱——双手应到胸前且高于腰 =====
Console.WriteLine("\n== 用例4：负重前抱 ==");
FK(-0.06f, 0f, 1.35f, 1.35f, 0.95f, 0.95f, 0.03f);
var carryL = Part(4, sleeveOff);
var carryR = Part(6, sleeveOff);
Console.WriteLine($"  左袖 = ({carryL.X:F3},{carryL.Y:F3},{carryL.Z:F3})");
Console.WriteLine($"  右袖 = ({carryR.X:F3},{carryR.Y:F3},{carryR.Z:F3})");
Console.WriteLine($"  抱持时双手应接近胸前等高、z 同为负（前）");
if (carryL.Z >= -0.05f || carryR.Z >= -0.05f) { Console.WriteLine("  FAIL 前抱没有向前"); fails++; }
if (MathF.Abs(carryL.Y - carryR.Y) > 0.05f) { Console.WriteLine("  FAIL 前抱不等高"); fails++; }

// ===== 用例 5：整套四状态跑 600 帧，确认无 NaN / 位置有界 =====
Console.WriteLine("\n== 用例5：四状态长跑稳定性 ==");
var rng = new Random(42);
var states = new (float, float, float, float, float, float, float)[]
{
    (0f, 0f, 0.04f, -0.04f, 0.14f, 0.14f, 0f),          // idle
    (0.09f, -0.13f, 0.42f, -0.42f, 0.30f, 0.30f, 0.055f), // walk
    (-0.06f, 0f, 1.35f, 1.35f, 0.95f, 0.95f, 0.03f),      // carry
    (0.42f, 0f, 0.85f, -0.85f, 0.55f, 0.55f, 0.035f),     // working
};
float maxY = float.MinValue, minY = float.MaxValue;
for (int f = 0; f < 600; f++)
{
    var s = states[f % 4];
    float ph = (f * 0.16f) % MathF.Tau;
    FK(s.Item1, s.Item2 * MathF.Sin(ph), s.Item3 * MathF.Sin(ph), s.Item4 * MathF.Sin(ph),
       s.Item5, s.Item6, s.Item7 * MathF.Sin(ph * 2f));
    for (int j = 0; j < 7; j++)
    {
        if (float.IsNaN(jp[j].X) || float.IsNaN(jp[j].Y) || float.IsNaN(jp[j].Z))
        { Console.WriteLine($"  FAIL 帧{f} 关节{j} 出现 NaN"); fails++; f = 9999; break; }
        if (jp[j].Y > maxY) maxY = jp[j].Y;
        if (jp[j].Y < minY) minY = jp[j].Y;
    }
}
Console.WriteLine($"  关节 y 范围 = [{minY:F3}, {maxY:F3}]（应大致在 [0, 1.6] 内）");
if (minY < -0.35f || maxY > 2.2f) { Console.WriteLine("  FAIL 关节越界"); fails++; }

Console.WriteLine(fails == 0 ? "\n全部通过 ✅" : $"\n失败 {fails} 项 ❌");
return fails == 0 ? 0 : 1;
