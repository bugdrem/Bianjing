using Godot;

namespace Bianjing;

/// <summary>
/// 真·毛玻璃面板：把自身 Material 设为「屏幕背景采样 + 高斯模糊 + 宣纸色调」着色器，
/// 实时把面板背后的 3D 世界虚化透出来（区别于之前那种仅半透明的假玻璃）。
/// 实现为 PanelContainer 子类：通过 Material 绘制面板本体，不新增子节点，
/// 因此完全不破坏 PanelContainer 的「单一子节点」布局约定。
/// 圆角由透明 StyleBoxFlat 提供几何形状（裁剪到圆角），1px 极淡边与玻璃质感在着色器内绘制。
/// </summary>
public partial class FrostedPanel : PanelContainer
{
    // —— 模糊着色器：采样 SCREEN_TEXTURE（面板背后的屏幕内容）做 5×5 模糊，混入宣纸色 ——
    private const string ShaderCode = """
        shader_type canvas_item;

        uniform vec2 texel = vec2(0.001);      // 1 / 屏幕像素尺寸（C# 每帧/随尺寸更新）
        uniform vec2 panel_size = vec2(256.0); // 面板像素尺寸（用于圆角/边框遮罩）
        uniform float blur_radius = 8.0;       // 模糊半径（像素）
        uniform float radius = 14.0;           // 圆角半径（像素）
        uniform vec3 paper_tint = vec3(0.984, 0.973, 0.949); // 宣纸白（≈#FBF8F2）
        uniform float tint_strength = 0.34;    // 宣纸色调混入强度
        uniform float alpha = 0.9;             // 玻璃整体不透明度
        uniform vec3 border_color = vec3(0.906, 0.875, 0.812); // 极淡边 ≈#E7DFCF
        uniform float border_width = 1.0;

        void fragment() {
            // —— 5×5 均匀模糊（取背后屏幕）——
            vec3 col = vec3(0.0);
            float total = 0.0;
            for (int x = -2; x <= 2; x++) {
                for (int y = -2; y <= 2; y++) {
                    vec2 off = vec2(float(x), float(y)) * blur_radius * texel;
                    col += texture(SCREEN_TEXTURE, SCREEN_UV + off).rgb;
                    total += 1.0;
                }
            }
            col /= total;
            // 混入宣纸白，弱化对比、提亮，呈「磨砂」感
            col = mix(col, paper_tint, tint_strength);

            // —— 圆角遮罩 + 1px 极淡边 ——
            vec2 p = UV * panel_size;
            vec2 half = panel_size * 0.5;
            vec2 d = abs(p - half) - (half - radius);
            float dist = length(max(d, vec2(0.0))) - radius; // <0 内部，>0 外部
            float mask = 1.0 - smoothstep(-1.0, 1.0, dist);
            float border = (1.0 - smoothstep(-border_width, 0.0, dist))
                         * smoothstep(border_width, 0.0, dist);
            vec3 outc = mix(col, border_color, border);

            COLOR = vec4(outc, mask * alpha);
        }
        """;

    private static readonly Shader _shader = new() { Code = ShaderCode };
    private ShaderMaterial _mat;

    public FrostedPanel()
    {
        // 不透明圆角框：仅提供几何形状（圆角裁剪）；填充颜色由自定义着色器接管输出，这里的值不参与显示
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 1f), // 不透明以确保几何被绘制（着色器会覆盖其颜色）
            CornerRadiusTopLeft = 14, CornerRadiusTopRight = 14,
            CornerRadiusBottomLeft = 14, CornerRadiusBottomRight = 14,
            BorderWidthLeft = 0, BorderWidthRight = 0,
            BorderWidthTop = 0, BorderWidthBottom = 0,
        };
        AddThemeStyleboxOverride("panel", sb);

        _mat = new ShaderMaterial { Shader = _shader };
        _mat.SetShaderParameter("paper_tint", new Vector3(UiTheme.PaperSolid.R, UiTheme.PaperSolid.G, UiTheme.PaperSolid.B));
        _mat.SetShaderParameter("border_color", new Vector3(UiTheme.PaperEdge.R, UiTheme.PaperEdge.G, UiTheme.PaperEdge.B));
        _mat.SetShaderParameter("blur_radius", 8.0f);
        _mat.SetShaderParameter("radius", 14.0f);
        _mat.SetShaderParameter("tint_strength", 0.34f);
        _mat.SetShaderParameter("alpha", 0.9f);
        _mat.SetShaderParameter("border_width", 1.0f);
        _mat.SetShaderParameter("panel_size", Size);
        _mat.SetShaderParameter("texel", new Vector2(1f / 1280f, 1f / 720f));
        Material = _mat;

        Resized += OnResized;
    }

    private void OnResized()
    {
        _mat.SetShaderParameter("panel_size", Size);
        var win = GetWindow();
        if (win != null && win.Size.X > 0)
            _mat.SetShaderParameter("texel", new Vector2(1f / win.Size.X, 1f / win.Size.Y));
    }
}
