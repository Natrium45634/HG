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

    public static void Play(Node3D parent, VfxDef? def, Vector3 pos, ContentDatabase db) => Start(parent, def, pos, db, persistent: false);

    /// <summary>
    /// Starts an effect (docs/DECISIONS_3.md §47). persistent — a cyclic effect of a cell or a terrain that lives
    /// until the caller stops it (<see cref="Stop"/>); otherwise it frees itself after its duration. Returns the node.
    /// </summary>
    public static Node3D? Start(Node3D parent, VfxDef? def, Vector3 pos, ContentDatabase db, bool persistent, int depth = 0)
    {
        if (!parent.IsInsideTree() || depth > 4) return null;
        def ??= Default;
        // Weak systems: the simplified variant.
        if (App.Main.Instance.Settings.SimplifiedEffects && db.Get<VfxDef>(def.Simplified) is { } simple && simple != def) def = simple;
        Played++;
        bool loop = persistent && def.Loop;
        float dur = (float)Math.Clamp(def.Duration, 0.05, 10);
        float size = (float)Math.Clamp(def.Size, 0.05, 10);
        var col = Ui.Parse(def.Color, Colors.White);
        col.A *= (float)Math.Clamp(def.Opacity, 0, 1);
        var col2 = string.IsNullOrEmpty(def.Color2) ? col with { A = 0 } : Ui.Parse(def.Color2, col) with { A = 0 };

        var root = new Node3D { Name = "Vfx_" + def.Id };
        parent.AddChild(root);
        root.GlobalPosition = pos + new Vector3(0, (float)def.Height, 0);
        root.RotationDegrees = new Vector3(0, (float)def.Orientation, 0);
        root.SetMeta("vfx", def.Id);
        if (def.FadeIn > 0)
        {
            root.Scale = Vector3.One * 0.05f;
            root.CreateTween().TweenProperty(root, "scale", Vector3.One, def.FadeIn);
        }

        switch (def.Type)
        {
            case VfxType.Composite:
                foreach (var id in def.Layers)
                    if (db.Get<VfxDef>(id) is { } layer && layer != def)
                        Start(root, layer, root.GlobalPosition, db, persistent, depth + 1);
                break;
            case VfxType.Texture or VfxType.AnimatedTexture:
                root.AddChild(TexturePlate(def, db, size, col, loop));
                break;
            case VfxType.Decal:
            {
                var tex = Assets.Texture(db.Get<AssetDef>(def.Texture));
                var decal = new Decal { Size = new Vector3(size * 1.7f, 1.2f, size * 1.7f), Modulate = col, AlbedoMix = 1, EmissionEnergy = (float)def.Intensity };
                if (tex != null) decal.TextureAlbedo = tex;
                else root.AddChild(TexturePlate(def, db, size, col, loop));
                root.AddChild(decal);
                break;
            }
            case VfxType.Glow:
            {
                var mat = Glowing(col, (float)def.Intensity, def.Order);
                root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = size * 0.4f, Height = size * 0.8f }, MaterialOverride = mat, Position = new Vector3(0, size * 0.3f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                root.AddChild(new OmniLight3D { LightColor = col, LightEnergy = (float)(def.LightEnergy * def.Intensity), OmniRange = Math.Max(1.2f, size * 3), Position = new Vector3(0, 0.4f, 0) });
                break;
            }
            case VfxType.Line:
                root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(size * 1.7f, 0.04f, 0.06f) }, MaterialOverride = Glowing(col, (float)def.Intensity, def.Order), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                break;
            case VfxType.Cone:
                root.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = size * 0.6f, Height = size * 1.2f },
                    MaterialOverride = Glowing(col with { A = col.A * 0.5f }, (float)def.Intensity, def.Order), Position = new Vector3(0, size * 0.6f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
                break;
            case VfxType.AreaOutline:
            {
                var mat = Glowing(col, (float)def.Intensity, def.Order);
                float r = Board.HexSize * 0.92f * Math.Max(0.3f, size);
                for (int i = 0; i < 6; i++)
                {
                    float a0 = Mathf.DegToRad(60 * i + 30), a1 = Mathf.DegToRad(60 * i + 90);
                    var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r;
                    var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r;
                    var seg = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(p0.DistanceTo(p1), 0.03f, 0.05f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    root.AddChild(seg);
                    seg.Position = (p0 + p1) / 2;
                    seg.Rotation = new Vector3(0, -Mathf.Atan2(p1.Z - p0.Z, p1.X - p0.X), 0);
                }
                break;
            }
            case VfxType.Model:
            {
                var model = Assets.Model(db.Get<AssetDef>(def.Model));
                if (model != null)
                {
                    model.Scale = Vector3.One * size;
                    root.AddChild(model);
                    if (Assets.PlayerOf(model) is { } ap && ap.GetAnimationList().Length > 0) ap.Play(ap.GetAnimationList()[0]);
                }
                else Particles(root, def, true, col, col2, size, dur, db, loop);
                break;
            }
            default:
                if (def.Count > 0) Particles(root, def, false, col, col2, size, dur, db, loop);
                break;
        }

        if (def.Light && def.LightEnergy > 0 && def.Type is not VfxType.Glow)
        {
            var light = new OmniLight3D { LightColor = col, LightEnergy = (float)(def.LightEnergy * def.Intensity), OmniRange = Math.Max(1.2f, size * 3), Position = new Vector3(0, 0.4f, 0) };
            root.AddChild(light);
            if (!loop) root.CreateTween().TweenProperty(light, "light_energy", 0f, dur);
        }

        if (!string.IsNullOrEmpty(def.Sound)) Assets.PlayAt(parent, db.Get<AssetDef>(def.Sound), pos);
        if (loop) return root;
        if (def.FadeOut > 0 && def.Type != VfxType.Particles)
            root.GetTree().CreateTimer(Math.Max(0.01, dur)).Timeout += () =>
            {
                if (GodotObject.IsInstanceValid(root)) root.CreateTween().TweenProperty(root, "scale", Vector3.One * 0.01f, def.FadeOut);
            };
        root.GetTree().CreateTimer(dur * 2 + def.FadeOut + 0.2).Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(root)) root.QueueFree();
        };
        return root;
    }

    /// <summary>Stops a persistent effect with its fade-out.</summary>
    public static void Stop(Node3D? node, VfxDef? def)
    {
        if (node == null || !GodotObject.IsInstanceValid(node)) return;
        double fade = def?.FadeOut ?? 0.2;
        if (fade <= 0 || !node.IsInsideTree())
        {
            node.QueueFree();
            return;
        }
        var tw = node.CreateTween();
        tw.TweenProperty(node, "scale", Vector3.One * 0.01f, fade);
        tw.TweenCallback(Callable.From(node.QueueFree));
    }

    /// <summary>Flight time of a stage's projectile, seconds (0 — a beam or no projectile); capped so a far shot never lags.</summary>
    /// <summary>Real seconds of the flight at a game speed (the flight is in battle seconds, like everything else).</summary>
    public static double FlightTime(ProjectileDef p, Vector3 from, Vector3 to, double timeScale = 1)
    {
        if (!p.Enabled || p.Trajectory == ProjectileTrajectory.Instant) return 0;
        double hexes = from.DistanceTo(to) / (Board.HexSize * Mathf.Sqrt(3));
        double battle = Math.Clamp(hexes / Math.Max(0.5, p.Speed), 0.05, 2.0);
        return battle / (timeScale > 0.01 ? Math.Clamp(timeScale, 0.25, 16) : 1);
    }

    /// <summary>
    /// The projectile of a skill stage (docs/DECISIONS_3.md §47): flies from the caster's attachment point to the
    /// target — straight, along an arc or as an instant beam — with spin and a trail, then plays the hit or miss
    /// effect, the cell effect and <paramref name="arrival"/>. Display only: the stage has already computed the hit.
    /// </summary>
    public static Node3D? Projectile(Node3D parent, ProjectileDef p, Vector3 from, Vector3 to, Vector3 cell, Color col, ContentDatabase db, bool hit, string arrival = "",
        double timeScale = 1)
    {
        if (!parent.IsInsideTree()) return null;
        if (!string.IsNullOrEmpty(p.LaunchVfx)) Play(parent, db.Get<VfxDef>(p.LaunchVfx), from, db);
        if (!string.IsNullOrEmpty(p.Sound)) Assets.PlayAt(parent, db.Get<AssetDef>(p.Sound), from);
        void Arrive()
        {
            string fx = hit ? p.HitVfx : p.MissVfx;
            if (!string.IsNullOrEmpty(fx)) Play(parent, db.Get<VfxDef>(fx), to, db);
            if (!string.IsNullOrEmpty(p.CellVfx)) Play(parent, db.Get<VfxDef>(p.CellVfx), cell, db);
            if (hit && !string.IsNullOrEmpty(arrival)) Play(parent, db.Get<VfxDef>(arrival), to, db);
        }

        double flight = FlightTime(p, from, to, timeScale);
        if (flight <= 0)
        {
            // A beam: a stretched glowing bar for a moment.
            var beam = new MeshInstance3D
            {
                Name = "Projectile_beam", Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, Math.Max(0.05f, from.DistanceTo(to))) },
                MaterialOverride = Glowing(col, 1.6f, 1), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            parent.AddChild(beam);
            beam.GlobalPosition = (from + to) / 2;
            if (from.DistanceTo(to) > 0.01f) beam.LookAt(to, Math.Abs((to - from).Normalized().Y) > 0.99f ? Vector3.Right : Vector3.Up);
            var tw = beam.CreateTween();
            tw.TweenProperty(beam, "scale", new Vector3(0.1f, 0.1f, 1), 0.22);
            tw.TweenCallback(Callable.From(beam.QueueFree));
            Arrive();
            return beam;
        }

        var root = new Node3D { Name = "Projectile" };
        parent.AddChild(root);
        root.GlobalPosition = from;
        var body = new Node3D { Name = "Body" };
        root.AddChild(body);
        var model = string.IsNullOrEmpty(p.Model) ? null : Assets.Model(db.Get<AssetDef>(p.Model));
        if (model != null) body.AddChild(model);
        else
        {
            body.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.09f, Height = 0.18f }, MaterialOverride = Glowing(col, 2f, 2), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            if (!App.Main.Instance.Settings.SimplifiedEffects) body.AddChild(new OmniLight3D { LightColor = col, LightEnergy = 0.6f, OmniRange = 1.4f });
        }
        if (!string.IsNullOrEmpty(p.Trail) && !App.Main.Instance.Settings.SimplifiedEffects)
        {
            var tc = Ui.Parse(p.Trail, col);
            var grad = new Gradient();
            grad.SetColor(0, tc);
            grad.SetColor(1, tc with { A = 0 });
            root.AddChild(new CpuParticles3D
            {
                Name = "Trail", Amount = 40, Lifetime = 0.3, LocalCoords = false, Emitting = true,
                Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f, RadialSegments = 6, Rings = 3, Material = Glowing(Colors.White, 1.2f, 1) },
                Direction = Vector3.Zero, Spread = 180, InitialVelocityMin = 0, InitialVelocityMax = 0.1f, Gravity = Vector3.Zero,
                ScaleAmountMin = 0.5f, ScaleAmountMax = 1f, ColorRamp = grad,
            });
        }
        // The arc rises over walls when asked (display only — line of sight already decided the hit).
        float dist = from.DistanceTo(to);
        float arc = p.Trajectory == ProjectileTrajectory.Arc ? Math.Max(dist * 0.28f, p.OverObstacles ? 0.8f : 0.3f) : 0;
        float spin = (float)(p.Spin * Math.Tau);
        Vector3 At(float t) => from.Lerp(to, t) + new Vector3(0, arc * 4 * t * (1 - t), 0);
        var flightTw = root.CreateTween();
        flightTw.TweenMethod(Callable.From<float>(t =>
        {
            if (!GodotObject.IsInstanceValid(root)) return;
            var pos = At(t);
            var ahead = At(Math.Min(1, t + 0.02f));
            root.GlobalPosition = pos;
            if (ahead.DistanceTo(pos) > 0.001f) root.LookAt(ahead, Math.Abs((ahead - pos).Normalized().Y) > 0.99f ? Vector3.Right : Vector3.Up);
            body.Rotation = new Vector3(0, 0, spin * t * (float)flight);
        }), 0f, 1f, flight);
        flightTw.TweenCallback(Callable.From(Arrive));
        switch (p.Vanish)
        {
            case ProjectileVanish.AtTarget:
                flightTw.TweenInterval(0.35);
                break;
            case ProjectileVanish.Fade:
                flightTw.TweenProperty(body, "scale", Vector3.One * 0.01f, 0.3);
                break;
        }
        flightTw.TweenCallback(Callable.From(root.QueueFree));
        return root;
    }

    private static StandardMaterial3D Glowing(Color col, float intensity, int order) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = col, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = Math.Max(0.1f, intensity),
        RenderPriority = Math.Clamp(order, -128, 127), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>A flat plate over the hex with a texture; a sprite sheet is played frame by frame.</summary>
    private static MeshInstance3D TexturePlate(VfxDef def, ContentDatabase db, float size, Color col, bool loop)
    {
        var tex = Assets.Texture(db.Get<AssetDef>(def.Texture));
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = col, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            RenderPriority = Math.Clamp(def.Order, -128, 127), CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = def.Intensity > 1, Emission = col, EmissionEnergyMultiplier = (float)Math.Max(0, def.Intensity - 1),
        };
        if (tex != null) mat.AlbedoTexture = tex;
        var plate = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(size * 1.7f, size * 1.7f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (def.Type == VfxType.AnimatedTexture && tex != null)
        {
            int fx = Math.Max(1, def.FramesX), fy = Math.Max(1, def.FramesY), frames = fx * fy;
            mat.Uv1Scale = new Vector3(1f / fx, 1f / fy, 1);
            float secs = (float)(frames / Math.Max(1, def.Fps));
            void SetFrame(float f)
            {
                int i = Math.Clamp((int)f, 0, frames - 1);
                mat.Uv1Offset = new Vector3((float)(i % fx) / fx, (float)(i / fx) / fy, 0);
            }
            plate.Ready += () =>
            {
                var tw = plate.CreateTween();
                if (loop || def.Loop) tw.SetLoops();
                tw.TweenMethod(Callable.From<float>(SetFrame), 0f, frames - 0.001f, secs);
            };
        }
        return plate;
    }

    private static void Particles(Node3D root, VfxDef def, bool with_default, Color col, Color col2, float size, float dur, ContentDatabase db, bool loop)
    {
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
            int count = with_default && def.Count <= 0 ? 16 : def.Count;
            var ramp = new Gradient();
            ramp.SetColor(0, col);
            ramp.SetColor(1, col2);
            var p = new CpuParticles3D
            {
                Mesh = quad,
                Amount = Math.Clamp(count, 1, 500),
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
            if (loop)
            {
                p.OneShot = false;
                p.Explosiveness = 0.1f;
            }
            root.AddChild(p);
            p.Emitting = true;
        }
    }
}
