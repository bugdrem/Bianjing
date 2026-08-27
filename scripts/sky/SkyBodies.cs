using Godot;

namespace Bianjing;

/// <summary>天空天体：太阳（带光晕，早/黄昏红黄、白昼白）与月亮（相位随月份、亮度与太阳反相）。
/// 太阳方向由 Main 每帧传入（已随一日六时平滑转动）；月亮方向 = -太阳方向，
/// 故太阳升起月亮落下、太阳落下月亮升起——天然满足“太阳出来时月亮变暗，太阳消失时月亮变亮”。
/// 月亮另持有一盏平行光，夜间按需投射阴影，阴影深浅与月亮亮度（相位·可见度）挂钩。</summary>
public partial class SkyBodies : Node3D
{
    private const float Dist = 700f;        // 天体距世界原点的距离（相机远平面 2000 内）
    private const float SunCoreSize = 0.5f;  // Sprite3D scale（PixelSize=1、纹理 128px → 64 世界单位直径）
    private const float SunGlowSize = 1.7f;  // 光晕 ~218 世界单位

    private Sprite3D _sunCore;
    private Sprite3D _sunGlow;
    private DirectionalLight3D _moonLight;
    private ShaderMaterial _sunCoreMat;
    private ShaderMaterial _sunGlowMat;
    private ImageTexture _glowTex;

    public override void _Ready()
    {
        Build();
    }

    private void Build()
    {
        _glowTex = MakeRadialTexture();

        // —— 太阳核心（不透明感的光盘）：自定义 unshaded 着色器、不含雾处理 → 不受地平雾影响，
        // 太阳贴地平（早/黄昏）时仍鲜亮红黄，不被雾化糊掉（月亮同机制豁免雾）。
        // 仍挂 _glowTex 仅用于撑出非 0 尺寸四边形（着色器按 UV 自算圆盘，不采样贴图像素）。——
        _sunCoreMat = MakeSunMaterial(false);
        _sunCore = new Sprite3D
        {
            Texture = _glowTex,
            PixelSize = 1f,
            Scale = new Vector3(SunCoreSize, SunCoreSize, 1f),
            MaterialOverride = _sunCoreMat,
        };
        AddChild(_sunCore);

        // —— 太阳光晕（叠加混合，柔和外晕；同样豁免雾）——
        _sunGlowMat = MakeSunMaterial(true);
        _sunGlow = new Sprite3D
        {
            Texture = _glowTex,
            PixelSize = 1f,
            Scale = new Vector3(SunGlowSize, SunGlowSize, 1f),
            MaterialOverride = _sunGlowMat,
        };
        AddChild(_sunGlow);

        // —— 月光平行光（夜间投影，深浅挂钩月亮亮度；可见月盘已移除）——
        _moonLight = new DirectionalLight3D
        {
            LightColor = WorldConfig.MoonLightColor,
            LightEnergy = 0f,
            ShadowEnabled = false,
        };
        AddChild(_moonLight);
    }

    /// <summary>每帧由 Main 驱动：sunDir 为已平滑的“指向太阳”单位向量；moonPhase 为 0..1 的朔望相位。</summary>
    public void UpdateSky(Vector3 sunDir, float moonPhase)
    {
        float sunH = sunDir.Y;
        float sunVis = Smooth01(sunH, 0.0f, 0.12f);       // 太阳升过地平线后淡入
        float moonVis = Smooth01(-sunH, 0.0f, 0.12f);     // = 太阳反相：太阳在地平线下才显

        // 太阳颜色：地平红黄 → 正午白
        float t = Smooth01(sunH, 0.0f, 0.5f);
        Color sunCol = WorldConfig.SunWarmColor.Lerp(WorldConfig.SunNoonColor, t);

        // 太阳位置 / 颜色 / 可见度：颜色（vec3）与淡入透明度（float）分别写入着色器，与月亮同机制（Godot 着色器 vec4 不接受 Color，故拆 vec3+float）。
        // 自定义 unshaded 着色器不含雾处理 → 不受地平雾影响，太阳贴地平（早/黄昏）仍鲜亮红黄。
        _sunCore.GlobalPosition = sunDir * Dist;
        _sunGlow.GlobalPosition = sunDir * Dist;
        _sunCoreMat.SetShaderParameter("tint", new Color(sunCol.R, sunCol.G, sunCol.B));
        _sunCoreMat.SetShaderParameter("alpha", sunVis);
        _sunGlowMat.SetShaderParameter("tint", new Color(sunCol.R, sunCol.G, sunCol.B));
        _sunGlowMat.SetShaderParameter("alpha", sunVis * 0.55f);

        // 月亮方向 = -太阳方向（月光照方向来源；可见月盘已移除）
        Vector3 moonDir = -sunDir;

        // 月光照亮：亮度 = 可见度 × 相位受光比例（满月 1 / 新月 0）
        float phaseLit = 0.5f + 0.5f * Mathf.Cos((moonPhase - 0.5f) * Mathf.Tau);
        float moonEnergy = WorldConfig.MoonBaseEnergy * moonVis * (0.2f + 0.8f * phaseLit);
        _moonLight.GlobalPosition = moonDir * 100f;
        _moonLight.LookAt(Vector3.Zero);
        _moonLight.LightEnergy = moonEnergy;
        _moonLight.ShadowEnabled = moonVis > 0.05f && phaseLit > 0.05f;
    }

    // —— 工具 ——

    private static float Smooth01(float x, float a, float b)
        => Mathf.Clamp((x - a) / (b - a), 0f, 1f);

    /// <summary>太阳/光晕材质：自定义 unshaded 空间着色器，圆盘形状由 UV 现场计算（不采样纹理，无需绑定贴图），
    /// 且不含任何雾处理 → 不受 Main 的地平雾影响，太阳在地平附近（早/黄昏）仍鲜亮红黄。
    /// 颜色经 vec3 tint、不透明度经 float alpha 每帧写入（与月亮同机制；Godot 着色器 vec4 不接受 Color，故拆 vec3+float）。
    /// additive=true 时叠加混合（外晕）；核心用 blend_mix（透明），即便参数异常也不会渲染成不透明黑盘。</summary>
    private static ShaderMaterial MakeSunMaterial(bool additive)
    {
        var shader = new Shader
        {
            Code = @"shader_type spatial;
render_mode unshaded" + (additive ? ", blend_add" : ", blend_mix") + @";
uniform vec3 tint;
uniform float alpha;
void fragment() {
    vec2 p = (UV - 0.5) * 2.0;
    float d = clamp(length(p), 0.0, 1.0);
    float a = 1.0 - d;
    a *= a; // 中心实、边缘软
    ALBEDO = tint;
    ALPHA = a * alpha;
}
"
        };
        return new ShaderMaterial { Shader = shader };
    }

    /// <summary>程序化径向渐变纹理（中心不透明 → 边缘透明），供太阳核心/光晕复用，无需外部资产。</summary>
    private static ImageTexture MakeRadialTexture()
    {
        int s = 128;
        var img = Image.CreateEmpty(s, s, false, Image.Format.Rgba8);
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                float dx = (x + 0.5f) / s - 0.5f;
                float dy = (y + 0.5f) / s - 0.5f;
                float d = Mathf.Sqrt(dx * dx + dy * dy) * 2f; // 0 中心 .. 1 边缘
                float a = Mathf.Clamp(1f - d, 0f, 1f);
                a *= a; // 软边
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
