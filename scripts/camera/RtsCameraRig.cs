using Godot;

namespace Bianjing;

/// <summary>RTS 相机：WASD/屏幕边缘平移、滚轮缩放、Q/E 或中键拖动旋转，带俯仰与范围限制。</summary>
public partial class RtsCameraRig : Node3D
{
    private const float MinDist = CameraConfig.MinDist;
    private const float MaxDist = CameraConfig.MaxDist;
    private const float MinPitch = CameraConfig.MinPitch;
    private const float MaxPitch = CameraConfig.MaxPitch;
    private const float EdgeMargin = CameraConfig.EdgeMargin;

    public Camera3D Cam { get; private set; }

    private Node3D _pitchPivot;
    private float _yaw = 0.7f;
    private float _pitch = -0.95f;
    private float _dist = 90f;
    private bool _rotating;

    public override void _Ready()
    {
        _pitchPivot = new Node3D();
        AddChild(_pitchPivot);
        Cam = new Camera3D { Far = CameraConfig.FarClip, Current = true };
        _pitchPivot.AddChild(Cam);
        ApplyTransform();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

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
