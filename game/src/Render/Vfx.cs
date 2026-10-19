using System;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Render;

/// <summary>
/// Plays «Визуальный эффект» presets (docs/DECISIONS_2.md §34): one-shot particles by shape, a
/// light flash and a sound. A missing sprite or sound only drops that part; an unknown effect ID
/// plays a small neutral burst. Purely presentational.
/// </summary>
public static class Vfx
{
    public static readonly VfxDef Default = new() { Id = "", Name = "по умолчанию", Shape = VfxShape.Burst, Color = "#ffffff", Duration = 0.4, Size = 0.35, Count = 12, Speed = 1.5, LightEnergy = 0.8 };

    public static int Played { get; private set; }

    public static void Play(Node3D parent, VfxDef? def, Vector3 pos, ContentDatabase db)
    {
        if (!parent.IsInsideTree()) return;
        def ??= Default;
        Played++;
        float dur = (float)Math.Clamp(def.Duration, 0.05, 10);
        float size = (float)Math.Clamp(def.Size, 0.05, 10);
        var col = Ui.Parse(def.Color, Colors.White);
        var col2 = string.IsNullOrEmpty(def.Color2) ? col with { A = 0 } : Ui.Parse(def.Color2, col) with { A = 0 };

        var root = new Node3D { Name = "Vfx_" + def.Id };
        parent.AddChild(root);
        root.GlobalPosition = pos;

        if (def.Count > 0)
        {
            var mat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BlendMode = def.Shape == VfxShape.Smoke ? BaseMaterial3D.BlendModeEnum.Mix : BaseMaterial3D.BlendModeEnum.Add,
            };
            if (!string.IsNullOrEmpty(def.Sprite) && Assets.Texture(db.Get<AssetDef>(def.Sprite)) is { } tex) mat.AlbedoTexture = tex;
            var quad = new QuadMesh { Size = new Vector2(0.12f, 0.12f) * Math.Max(0.5f, size), Material = mat };
            var ramp = new Gradient();
            ramp.SetColor(0, col);
            ramp.SetColor(1, col2);
            var p = new CpuParticles3D
            {
                Mesh = quad,
                Amount = Math.Clamp(def.Count, 1, 500),
                Lifetime = dur,
                OneShot = true,
                Explosiveness = def.Shape is VfxShape.Pillar or VfxShape.Smoke ? 0.6f : 0.95f,
                ColorRamp = ramp,
                InitialVelocityMin = (float)def.Speed * 0.5f,
                InitialVelocityMax = (float)def.Speed,
                Emitting = false,
            };
            switch (def.Shape)
            {
                case VfxShape.Burst:
                    p.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere;
                    p.EmissionSphereRadius = size * 0.2f;
                    p.Direction = Vector3.Up;
                    p.Spread = 180;
                    p.Gravity = new Vector3(0, -1.5f, 0);
                    break;
                case VfxShape.Ring:
                    p.EmissionShape = CpuParticles3D.EmissionShapeEnum.Ring;
                    p.EmissionRingAxis = Vector3.Up;
                    p.EmissionRingRadius = size * 0.25f;
                    p.EmissionRingInnerRadius = size * 0.2f;
                    p.EmissionRingHeight = 0.02f;
                    p.Direction = Vector3.Right;
                    p.Spread = 180;
                    p.Flatness = 1;
                    p.Gravity = Vector3.Zero;
                    break;
                case VfxShape.Pillar:
                    p.EmissionShape = CpuParticles3D.EmissionShapeEnum.Box;
                    p.EmissionBoxExtents = new Vector3(size * 0.3f, 0.05f, size * 0.3f);
                    p.Direction = Vector3.Up;
                    p.Spread = 8;
                    p.Gravity = Vector3.Zero;
                    break;
                case VfxShape.Sparks:
                    p.EmissionShape = CpuParticles3D.EmissionShapeEnum.Point;
                    p.Direction = Vector3.Up;
                    p.Spread = 50;
                    p.Gravity = new Vector3(0, -6f, 0);
                    break;
                case VfxShape.Smoke:
                    p.EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere;
                    p.EmissionSphereRadius = size * 0.3f;
                    p.Direction = Vector3.Up;
                    p.Spread = 35;
                    p.Gravity = new Vector3(0, 0.4f, 0);
                    p.ScaleAmountMin = 1.5f;
                    p.ScaleAmountMax = 3f;
                    break;
            }
            root.AddChild(p);
            p.Emitting = true;
        }

        if (def.Light && def.LightEnergy > 0)
        {
            var light = new OmniLight3D { LightColor = col, LightEnergy = (float)def.LightEnergy, OmniRange = Math.Max(1.2f, size * 3), Position = new Vector3(0, 0.4f, 0) };
            root.AddChild(light);
            var tw = root.CreateTween();
            tw.TweenProperty(light, "light_energy", 0f, dur);
        }

        if (!string.IsNullOrEmpty(def.Sound)) Assets.PlayAt(parent, db.Get<AssetDef>(def.Sound), pos);

        root.GetTree().CreateTimer(dur * 2 + 0.2).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(root)) root.QueueFree();
        };
    }
}
