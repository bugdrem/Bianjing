using Godot;

namespace Bianjing;

/// <summary>RTS 相机：WASD/屏幕边缘平移、滚轮缩放、Q/E 或中键拖动旋转，带俯仰与范围限制。
/// 默认面向正北（yaw=0，相机看向地图 -Z 即北）；进场时从高空俯瞰整张画卷逐步落向落点
/// （默认地图中心；读档经 RestartIntro 指定王爷府为落点，动画节奏不变）。</summary>
public partial class RtsCameraRig : Node3D
{
    private const float MinDist = CameraConfig.MinDist;
    private const float MaxDist = CameraConfig.MaxDist;
    private const float MinPitch = CameraConfig.MinPitch;
    private const float MaxPitch = CameraConfig.MaxPitch;
    private const float EdgeMargin = CameraConfig.EdgeMargin;

    public Camera3D Cam { get; private set; }

    /// <summary>当前缩放拉距（米）：供 Main 按拉距开关深度雾化（凑近地图内关雾省 pass）。</summary>
    public float Distance => _dist;

    private Node3D _pitchPivot;
    private float _yaw; // 0 = 面向正北（地图 -Z 向）；Q/E 或中键拖转可调
    private float _pitch = CameraConfig.DefaultPitch;
    private float _dist = CameraConfig.DefaultDist;
    private bool _rotating;
    private Tween _focusTween; // 定位 tween（批次七十）：玩家手动平移即打断

    /// <summary>进场动画进行中：从俯瞰画卷逐步落向落点（期间忽略玩家输入）。</summary>
    private bool _introActive = true;
    private float _introT;

    /// <summary>进场动画落点（批次八十二）：默认地图中心（世界原点）；读档重放动画时指定王爷府。</summary>
    private Vector3 _introTarget = Vector3.Zero;

    public override void _Ready()
    {
        _pitchPivot = new Node3D();
        AddChild(_pitchPivot);
        // 主相机同摄地图内（Map）与卷轴装裱（Scroll）两层：两层可独立开关/特效，但默认同屏呈现
        Cam = new Camera3D { Far = CameraConfig.FarClip, Current = true, CullMask = RenderLayers.All };
        _pitchPivot.AddChild(Cam);

        // 进场起点：地图中心高空，近乎垂直俯瞰整张画卷（拉距超出常态上限，仅动画起点使用）
        _dist = CameraConfig.IntroStartDist;
        _pitch = CameraConfig.MinPitch;
        Position = Vector3.Zero;
        ApplyTransform();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // 进场动画：俯瞰画卷 → 落向落点（默认地图中心；读档落王爷府），减速缓入，像人逐步靠近；期间忽略输入
        if (_introActive)
        {
            _introT += dt / CameraConfig.IntroDuration;
            if (_introT >= 1f)
            {
                _introT = 1f;
                _introActive = false;
            }
            float t = 1f - Mathf.Pow(1f - _introT, 3f); // easeOutCubic：起步快、临近放缓
            _dist = Mathf.Lerp(CameraConfig.IntroStartDist, CameraConfig.DefaultDist, t);
            _pitch = Mathf.Lerp(CameraConfig.MinPitch, CameraConfig.DefaultPitch, t);
            Position = _introTarget * t; // 与俯冲同步缓动渐移至落点（默认中心=不动，读档王府=边俯冲边平移）
            ApplyTransform();
            return;
        }

        // 平移：WASD + 屏幕边缘
        var move = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W)) move.Y -= 1;
        if (Input.IsKeyPressed(Key.S)) move.Y += 1;
        if (Input.IsKeyPressed(Key.A)) move.X -= 1;
        if (Input.IsKeyPressed(Key.D)) move.X += 1;

        var vp = GetViewport();
        var mouse = vp.GetMousePosition();
        var size = vp.GetVisibleRect().Size;
        if (mouse.X >= 0 && mouse.Y >= 0 && mouse.X <= size.X && mouse.Y <= size.Y)
        {
            if (mouse.X < EdgeMargin) move.X -= 1;
            else if (mouse.X > size.X - EdgeMargin) move.X += 1;
            if (mouse.Y < EdgeMargin) move.Y -= 1;
            else if (mouse.Y > size.Y - EdgeMargin) move.Y += 1;
        }

        if (move != Vector2.Zero)
        {
            _focusTween?.Kill(); // 玩家手动平移：打断面板定位的 tween
            move = move.Normalized();
            float panSpeed = _dist * 0.9f;
            var offset = new Vector3(move.X, 0f, move.Y).Rotated(Vector3.Up, _yaw) * panSpeed * dt;
            Position += offset;

            float limit = MapGrid.Size * MapGrid.CellSize / 2f + 40f;
            Position = new Vector3(
                Mathf.Clamp(Position.X, -limit, limit), Position.Y,
                Mathf.Clamp(Position.Z, -limit, limit));
        }

        // Q/E 旋转
        if (Input.IsKeyPressed(Key.Q)) _yaw += 1.5f * dt;
        if (Input.IsKeyPressed(Key.E)) _yaw -= 1.5f * dt;

        ApplyTransform();
        ClampAboveTerrain();
    }

    /// <summary>重放进场动画（批次八十二）：从高空俯瞰画卷俯冲落向指定落点，节奏与新建完全一致——
    /// 读档定位王爷府用（玩家读档看到与新图相同的进场动画，最后停驻王府上空）；
    /// F9 游戏中读档同样重放（加载面板遮挡期间完成瞬移归零，无感）。落点默认传地图中心。</summary>
    public void RestartIntro(Vector3 target)
    {
        _focusTween?.Kill();
        _introTarget = target;
        _introActive = true;
        _introT = 0f;
        _dist = CameraConfig.IntroStartDist;
        _pitch = CameraConfig.MinPitch;
        Position = Vector3.Zero;
        ApplyTransform();
    }

    /// <summary>定位镜头到目标（批次七十）：0.4s 平滑平移云台到目标上方（保持当前高度与视角），
    /// 水平位置钳制在地图卷边内；进场动画未结束先结束动画；玩家手动平移即打断定位。</summary>
    public void FocusOn(Vector3 world)
    {
        if (_introActive)
        {
            _introT = 1f;
            _introActive = false;
        }
        _focusTween?.Kill();
        float limit = MapGrid.Size * MapGrid.CellSize / 2f + 40f;
        _focusTween = CreateTween();
        _focusTween.TweenProperty(this, "position", new Vector3(
            Mathf.Clamp(world.X, -limit, limit), Position.Y,
            Mathf.Clamp(world.Z, -limit, limit)), 0.4f)
            .SetTrans(Tween.TransitionType.Quad)
            .SetEase(Tween.EaseType.Out);
    }

    /// <summary>镜头不入地：镜头低于脚下地形 + 最小净空时抬升整个云台（Position.Y），
    /// 离开山体后平滑回落——防平移/低角度时镜头钻进山体透视。</summary>
    private void ClampAboveTerrain()
    {
        var map = GameState.I?.Map;
        if (map == null)
            return;
        var cam = Cam.GlobalPosition;
        float baseCamY = cam.Y - Position.Y; // 云台不抬升时的镜头高度
        float minCamY = map.Height.SampleWorld(cam.X, cam.Z) + CameraConfig.MinAboveTerrain;
        float targetLift = Mathf.Max(0f, minCamY - baseCamY);
        // 上抬立即到位（防穿山不能慢），回落渐进（免下山时镜头磕地感）
        float y = targetLift > Position.Y ? targetLift : Mathf.Lerp(Position.Y, targetLift, 0.12f);
        Position = new Vector3(Position.X, y, Position.Z);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                    _dist = Mathf.Clamp(_dist * 0.87f, MinDist, MaxDist);
                else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                    _dist = Mathf.Clamp(_dist * 1.15f, MinDist, MaxDist);
                else if (mb.ButtonIndex == MouseButton.Middle)
                    _rotating = mb.Pressed;
                break;

            case InputEventMouseMotion mm when _rotating:
                _yaw -= mm.Relative.X * 0.005f;
                _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.005f, MinPitch, MaxPitch);
                break;
        }
    }

    private void ApplyTransform()
    {
        Rotation = new Vector3(0f, _yaw, 0f);
        _pitchPivot.Rotation = new Vector3(_pitch, 0f, 0f);
        Cam.Position = new Vector3(0f, 0f, _dist);
    }
}
