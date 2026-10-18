using System;
using Godot;

namespace HexTactics.Game.Render;

/// <summary>
/// "Table" camera: looks at a pivot on the board from a fixed tilt (default 60° above the table
/// plane), with panning (keys / middle mouse / screen edge), zoom (wheel) and yaw in 60° steps
/// (Q/E) that animates smoothly. Input is taken only from unhandled events (UI clicks are not seen).
/// </summary>
public partial class CameraRig : Node3D
{
    public Camera3D Camera { get; } = new() { Fov = 38, Near = 0.1f, Far = 400 };

    public float TiltDeg { get; set; } = 60;
    public float Distance { get; set; } = 18;
    public float MinDistance { get; set; } = 5;
    public float MaxDistance { get; set; } = 60;
    public float Speed { get; set; } = 1;
    public bool EdgeScroll { get; set; } = true;
    public bool KeyboardPan { get; set; } = true;
    public Rect2 Bounds { get; set; } = new(-50, -50, 100, 100);

    private float _yaw;
    private float _targetYaw;
    private float _targetDistance = 18;
    private Vector3 _pivot;
    private bool _dragging;
    private Vector2 _lastMouse;

    public CameraRig()
    {
        AddChild(Camera);
    }

    public Vector3 Pivot
    {
        get => _pivot;
        set => _pivot = ClampPivot(value);
    }

    public float YawDeg => Mathf.RadToDeg(_yaw);

    public void Frame(Rect2 boardRect)
    {
        Bounds = boardRect.Grow(2);
        _pivot = new Vector3(boardRect.GetCenter().X, 0, boardRect.GetCenter().Y);
        float size = Math.Max(boardRect.Size.X, boardRect.Size.Y * 1.4f);
        MaxDistance = Math.Max(MaxDistance, size * 1.6f);
        _targetDistance = Distance = Math.Clamp(size * 0.75f, MinDistance, 22);
        UpdateTransform();
    }

    private Vector3 ClampPivot(Vector3 p) =>
        new(Math.Clamp(p.X, Bounds.Position.X, Bounds.End.X), p.Y, Math.Clamp(p.Z, Bounds.Position.Y, Bounds.End.Y));

    public void Rotate(int steps) => _targetYaw += steps * Mathf.Pi / 3;

    public void FocusOn(Vector3 p) => _pivot = ClampPivot(new Vector3(p.X, 0, p.Z));

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var move = Vector2.Zero;
        if (KeyboardPan && GetViewport().GuiGetFocusOwner() is not LineEdit and not TextEdit and not SpinBox)
        {
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) move.Y -= 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) move.Y += 1;
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) move.X -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) move.X += 1;
        }
        if (EdgeScroll && DisplayServer.WindowIsFocused() && !_dragging)
        {
            var vp = GetViewport().GetVisibleRect().Size;
            var m = GetViewport().GetMousePosition();
            const float edge = 6;
            if (m.X >= 0 && m.Y >= 0 && m.X <= vp.X && m.Y <= vp.Y)
            {
                if (m.X < edge) move.X -= 1;
                if (m.X > vp.X - edge) move.X += 1;
                if (m.Y < edge) move.Y -= 1;
                if (m.Y > vp.Y - edge) move.Y += 1;
            }
        }
        if (move != Vector2.Zero)
        {
            float s = Distance * 0.9f * Speed * dt;
            var fwd = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
            var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
            _pivot = ClampPivot(_pivot + (right * move.X - fwd * move.Y) * s);
        }
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, Math.Min(1, dt * 10));
        Distance = Mathf.Lerp(Distance, _targetDistance, Math.Min(1, dt * 12));
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        float tilt = Mathf.DegToRad(Math.Clamp(TiltDeg, 20, 89.5f));
        var offset = new Vector3(Mathf.Sin(_yaw) * Mathf.Cos(tilt), Mathf.Sin(tilt), Mathf.Cos(_yaw) * Mathf.Cos(tilt)) * Distance;
        Camera.GlobalPosition = _pivot + offset;
        Camera.LookAt(_pivot, Vector3.Up);
    }

    /// <summary>Handles camera input; returns true if the event was consumed.</summary>
    public bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed && !mb.CtrlPressed:
                _targetDistance = Math.Max(MinDistance, _targetDistance * 0.88f);
                return true;
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed && !mb.CtrlPressed:
                _targetDistance = Math.Min(MaxDistance, _targetDistance / 0.88f);
                return true;
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Middle:
                _dragging = mb.Pressed;
                _lastMouse = mb.Position;
                return true;
            case InputEventMouseMotion mm when _dragging:
            {
                var d = mm.Position - _lastMouse;
                _lastMouse = mm.Position;
                float s = Distance * 0.0016f;
                var fwd = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
                var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
                _pivot = ClampPivot(_pivot - right * d.X * s + fwd * d.Y * s * 1.15f);
                return true;
            }
            case InputEventKey k when k.Pressed && !k.Echo && k.Keycode == Key.Q:
                Rotate(-1);
                return true;
            case InputEventKey k when k.Pressed && !k.Echo && k.Keycode == Key.E:
                Rotate(1);
                return true;
            case InputEventKey k when k.Pressed && k.Keycode == Key.Pageup:
                _targetDistance = Math.Max(MinDistance, _targetDistance * 0.85f);
                return true;
            case InputEventKey k when k.Pressed && k.Keycode == Key.Pagedown:
                _targetDistance = Math.Min(MaxDistance, _targetDistance / 0.85f);
                return true;
        }
        return false;
    }

    public (Vector3 Origin, Vector3 Dir) Ray(Vector2 screen) => (Camera.ProjectRayOrigin(screen), Camera.ProjectRayNormal(screen));
}
