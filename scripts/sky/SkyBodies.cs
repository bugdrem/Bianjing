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
    private const float MoonSize = 0.34f;    // 月盘 ~44 世界单位

    private Sprite3D _sunCore;
    private Sprite3D _sunGlow;
    private Sprite3D _moon;
    private DirectionalLight3D _moonLight;
    private ShaderMaterial _moonMat;
    private ImageTexture _glowTex;

    public override void _Ready()
    {
        Build();
    }

    private void Build()
    {
        _glowTex = MakeRadialTexture();

        // —— 太阳核心（不透明感的光盘）——
        _sunCore = new Sprite3D
        {
            Texture = _glowTex,
            PixelSize = 1f,
            Scale = new Vector3(SunCoreSize, SunCoreSize, 1f),
            MaterialOverride = MakeUnlit(Colors.White, false),
        };
        AddChild(_sunCore);

        // —— 太阳光晕（叠加混合，柔和外晕）——
        _sunGlow = new Sprite3D
        {
            Texture = _glowTex,
            PixelSize = 1f,
            Scale = new Vector3(SunGlowSize, SunGlowSize, 1f),
            MaterialOverride = MakeUnlit(Colors.White, true),
        };
        AddChild(_sunGlow);

        // —— 月亮（相位 Shader：满月→弦月→新月循环）——
        _moonMat = MakeMoonMaterial();
        _moon = new Sprite3D
        {
            Texture = _glowTex,
            PixelSize = 1f,
            Scale = new Vector3(MoonSize, MoonSize, 1f),
            MaterialOverride = _moonMat,
        };
        AddChild(_moon);

        // —— 月光平行光（夜间投影，深浅挂钩月亮亮度）——
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

        // 太阳位置 / 颜色 / 可见度
        _sunCore.GlobalPosition = sunDir * Dist;
        _sunGlow.GlobalPosition = sunDir * Dist;
        SetUnlitColor(_sunCore, sunCol);
        SetUnlitColor(_sunGlow, sunCol);
        _sunCore.Modulate = new Color(1f, 1f, 1f, sunVis);
        _sunGlow.Modulate = new Color(1f, 1f, 1f, sunVis * 0.55f);

        // 月亮方向 = -太阳方向；位置 / 相位 / 可见度
        Vector3 moonDir = -sunDir;
        _moon.GlobalPosition = moonDir * Dist;
        _moonMat.SetShaderParameter("phase", moonPhase);
        _moonMat.SetShaderParameter("tint", WorldConfig.MoonTintColor);
        _moonMat.SetShaderParameter("alpha", moonVis);

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

    private static StandardMaterial3D MakeUnlit(Color col, bool additive)
    {
        var m = new StandardMaterial3D
        {
            ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = StandardMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = col,
        };
        if (additive)
            m.BlendMode = StandardMaterial3D.BlendModeEnum.Add;
        return m;
    }

    private static void SetUnlitColor(Sprite3D s, Color col)
    {
        if (s.MaterialOverride is StandardMaterial3D m)
            m.AlbedoColor = col;
    }

    private static ShaderMaterial MakeMoonMaterial()
    {
        var shader = new Shader
        {
            Code = @"shader_type spatial;
render_mode unshaded, blend_mix;

uniform float phase;   // 0/1=新月，0.5=满月
uniform vec3 tint;
uniform float alpha;

void fragment() {
    vec2 p = (UV - 0.5) * 2.0;          // 中心 (0,0)，边缘半径 1
    float d = length(p);
    float disk = smoothstep(1.0, 0.97, d);
    // 阴影盘：满月(phase=0.5)时移到圆外、新月(phase=0)时 concentric 全遮
    float o = (phase <= 0.5) ? (phase * 2.0) : ((1.0 - phase) * 2.0);
    float dir = (phase <= 0.5) ? 1.0 : -1.0;
    vec2 sc = vec2(dir * o, 0.0);
    float shadow = smoothstep(1.0, 0.97, length(p - sc));
    float lit = disk * (1.0 - shadow);
    ALBEDO = tint;
    ALPHA = lit * alpha;
}
"
        };
        return new ShaderMaterial
        {
            Shader = shader,
        };
    }

    /// <summary>程序化径向渐变纹理（中心不透明 → 边缘透明），供太阳/光晕/月盘复用，无需外部资产。</summary>
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
