using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Render;

/// <summary>
/// A unit as a board-game piece: faction-shaped base (circle/diamond/square/triangle — shape,
/// not only colour), a low-poly figure by unit shape, a bright facing arrow, HP/AP bars and
/// status letters. Position is interpolated between cells during a step.
/// </summary>
public partial class UnitView : Node3D
{
    private readonly Node3D _body = new() { Name = "Body" };
    private readonly Node3D _bars = new() { Name = "Bars" };
    private MeshInstance3D _hpFill = null!;
    private MeshInstance3D _shieldFill = null!;
    private MeshInstance3D _apFill = null!;
    private MeshInstance3D _apReserve = null!;
    private Label3D _status = null!;
    private Label3D _name = null!;
    private Label3D _symbol = null!;
    private MeshInstance3D _arrow = null!;
    private readonly List<(MeshInstance3D Mesh, int Offset, bool Fixed)> _extraArrows = new();
    private MeshInstance3D? _omniRing;
    private MeshInstance3D _selectRing = null!;
    private Label3D _stackBadge = null!;
    private Label3D _blockLabel = null!;
    private float _visualYaw;
    private float _deathTime = -1;
    private float _hitFlash;
    private Color _bodyColor;
    private readonly List<MeshInstance3D> _bodyParts = new();

    // Replaceable representation (docs/DECISIONS_2.md §34): an imported model instead of the figure.
    private Node3D? _model;
    private AnimationPlayer? _anim;
    private readonly List<GeometryInstance3D> _modelParts = new();
    private readonly Dictionary<AnimRole, (string Clip, float Speed, bool Loop)> _clips = new();
    /// <summary>The role mapping of the model (variants, priority, interruption, blend, end, markers).</summary>
    private readonly Dictionary<AnimRole, AnimationMapDef> _maps = new();
    private AssetDef? _asset;
    /// <summary>Seconds until the cast's release when its role started (the hit marker is fitted to it).</summary>
    private double _toRelease = -1;
    private float _baseScale = 1;
    private float _roleTime;
    private bool _roleStarted;

    /// <summary>The animation role being shown (ожидание, движение, атака…).</summary>
    public AnimRole Role { get; private set; } = AnimRole.Spawn;
    /// <summary>The model clip playing for the role ("" — procedural animation).</summary>
    public string CurrentClip { get; private set; } = "";
    public bool UsesModel => _model != null;
    /// <summary>Why the model was not used (missing/damaged file) — the figure is shown instead.</summary>
    public string ModelProblem { get; private set; } = "";

    public Unit Unit { get; }
    public bool Selected { get; set; }
    /// <summary>Preview of a new facing while the player drags the facing arrow (the simulation is not changed).</summary>
    public float? PreviewYaw { get; set; }
    public bool Hovered { get; set; }
    public bool ShowName { get; set; }
    public bool Ghost { get; private set; }

    /// <summary>Offset inside a shared hex (docs/DECISIONS_2.md §26) and the group size for the ×N badge.</summary>
    public Vector3 StackOffset { get; set; }
    public int StackIndex { get; set; }
    public int StackCount { get; set; } = 1;

    /// <summary>Blocking caption: "Блок 2/3" on a blocker, "заблокирован"/"удерживается" on an enemy (empty — none).</summary>
    public string BlockText { get; set; } = "";
    public Color BlockColor { get; set; } = Colors.White;

    private const float BarWidth = 0.8f;

    public UnitView(Unit unit, ContentDatabase db)
    {
        Unit = unit;
        Name = "Unit" + unit.Id;
        var faction = db.Faction(unit.FactionId);
        var fcol = Ui.Parse(faction?.Color, new Color(0.6f, 0.6f, 0.6f));
        _bodyColor = Ui.Parse(unit.Def.Visual.Color, fcol.Lightened(0.15f));
        // Colour variants (docs/DECISIONS_3.md §47): by the unit's number — the look only.
        var variants = unit.Def.Visual.ColorVariants;
        if (variants.Count > 0) _bodyColor = Ui.Parse(variants[unit.Id % variants.Count], _bodyColor);
        float scale = (float)Math.Clamp(unit.Def.Visual.Scale, 0.3, 3.0) * 1.2f;
        if (unit.IsStructure)
        {
            AddChild(_body);
            BuildStructure(fcol);
            AttachModel(db);
            BuildBars(1.75f);
            return;
        }
        BuildBase(faction?.Shape ?? FactionShape.Circle, fcol);
        AddChild(_body);
        BuildBody(unit.Def.Visual.Shape, _bodyColor, fcol);
        _baseScale = scale;
        _body.Scale = Vector3.One * scale;
        AttachModel(db);
        BuildBars(scale);
        _visualYaw = Board.YawOf(unit.Facing);
    }

    /// <summary>
    /// Puts the unit's model in place of the figure (docs/DECISIONS_3.md §47): loaded in the background — the figure is
    /// shown until it is ready; the simplified model with «упрощённые эффекты»; on a problem — the resource's fallback,
    /// the unit's fallback, then the figure.
    /// </summary>
    private void AttachModel(ContentDatabase db)
    {
        var v = Unit.Def.Visual;
        var id = App.Main.Instance.Settings.SimplifiedEffects && !string.IsNullOrEmpty(v.SimplifiedModel) ? v.SimplifiedModel : v.Model;
        if (string.IsNullOrEmpty(id)) return;
        var chain = new List<AssetDef>();
        if (db.Get<AssetDef>(id) is { } first)
        {
            chain.Add(first);
            if (db.Get<AssetDef>(first.Fallback) is { } fb && fb != first) chain.Add(fb);
        }
        if (db.Get<AssetDef>(v.FallbackModel) is { } unitFb && !chain.Contains(unitFb)) chain.Add(unitFb);
        if (chain.Count == 0)
        {
            ModelProblem = $"ресурс «{id}» не найден";
            return;
        }
        ModelLoading = true;
        TryLoad(chain, 0);
    }

    /// <summary>The model is still being loaded (the figure is shown).</summary>
    public bool ModelLoading { get; private set; }

    /// <summary>Game speed for clips and procedural animation (0 — pause).</summary>
    public float TimeScale { get; set; } = 1;

    private void TryLoad(List<AssetDef> chain, int i)
    {
        if (i >= chain.Count)
        {
            ModelLoading = false;
            ModelProblem = Assets.Problems.GetValueOrDefault(chain[0].Id, "модель не загружена");
            return;
        }
        Assets.ModelLater(chain[i], model =>
        {
            if (!GodotObject.IsInstanceValid(this)) return;
            if (model == null)
            {
                TryLoad(chain, i + 1);
                return;
            }
            ModelLoading = false;
            if (i > 0) ModelProblem = $"«{chain[0].DisplayName}» не загружена — резервная «{chain[i].DisplayName}»";
            Install(chain[i], model);
        });
    }

    private void Install(AssetDef asset, Node3D model)
    {
        _asset = asset;
        var holder = new Node3D
        {
            Name = "Model",
            Position = new Vector3(0, 0.12f + (float)asset.YOffset, 0),
            RotationDegrees = new Vector3(0, (float)asset.RotationY, 0),
            Scale = Vector3.One * (float)asset.Scale,
        };
        holder.AddChild(model);
        _body.AddChild(holder);
        _model = holder;
        foreach (var part in _bodyParts) part.Visible = false;
        CollectGeometry(model);
        // Colour variant of a model: a light tint over its materials.
        var variants = Unit.Def.Visual.ColorVariants;
        if (variants.Count > 0)
        {
            var tint = new StandardMaterial3D { AlbedoColor = _bodyColor with { A = 0.3f }, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
            foreach (var g in _modelParts) g.MaterialOverlay = tint;
        }
        _anim = Assets.PlayerOf(model);
        if (_anim == null) return;
        _anim.AnimationFinished += OnClipFinished;
        var clips = _anim.GetAnimationList();
        foreach (var m in asset.Animations)
            if (clips.Contains(m.Clip) && !_clips.ContainsKey(m.Role))
            {
                _clips[m.Role] = (m.Clip, (float)m.Speed, m.End == AnimEndRule.Loop || m.Loop || VisualState.Loops(m.Role));
                _maps[m.Role] = m;
            }
        foreach (var role in Enum.GetValues<AnimRole>())
            if (!_clips.ContainsKey(role) && AssetValidator.GuessClip(role, clips) is { } guess)
                _clips[role] = (guess, 1f, VisualState.Loops(role));
        _roleStarted = false; // replay the current role with the model
    }

    /// <summary>Default priorities of roles when the mapping gives none: death interrupts everything.</summary>
    private static int DefaultPriority(AnimRole r) => r switch
    {
        AnimRole.Death => 100,
        AnimRole.Control => 80,
        AnimRole.Hit => 60,
        AnimRole.Attack or AnimRole.Skill => 50,
        AnimRole.Block or AnimRole.Dodge => 40,
        AnimRole.Spawn => 30,
        AnimRole.Move or AnimRole.Fly or AnimRole.Burrow => 20,
        AnimRole.Turn => 15,
        _ => 0,
    };

    private int PriorityOf(AnimRole r) => _maps.TryGetValue(r, out var m) && m.Priority > 0 ? m.Priority : DefaultPriority(r);

    private void OnClipFinished(StringName name)
    {
        if (name.ToString() != CurrentClip) return;
        _clipBusy = false;
        if (_maps.TryGetValue(Role, out var m) && m.End == AnimEndRule.HoldLast) return;
        if (Role != AnimRole.Idle && _clips.TryGetValue(AnimRole.Idle, out var idle) && _anim != null && !VisualState.Loops(Role))
        {
            _anim.Play(idle.Clip, 0.2f, idle.Speed);
            CurrentClip = idle.Clip;
        }
    }

    private bool _clipBusy;

    /// <summary>
    /// A hit of a series resolved (docs/DECISIONS_3.md §45, §47): the strike clip plays again, from just before its
    /// «проверка попадания» marker — the look follows the battle's moment, never the other way round.
    /// </summary>
    public void ReplayStrike(AnimRole role)
    {
        if (_anim == null || !_clips.TryGetValue(role, out var c)) return;
        double at = 0;
        if (_maps.TryGetValue(role, out var m) && m.Markers.FirstOrDefault(x => x.Kind == AnimMarker.HitCheck) is { } hit) at = Math.Max(0, hit.Time - 0.12);
        _anim.Play(c.Clip, 0.05f, c.Speed);
        _anim.Seek(at, true);
        CurrentClip = c.Clip;
        _clipBusy = true;
    }

    /// <summary>
    /// Position of an attachment point (weapon, projectile, magic source, hit, death, status, facing): the model's node
    /// by name or the offset set by hand; without a model — sensible points of the figure.
    /// </summary>
    public Vector3 AttachmentPos(AttachmentPoint p)
    {
        var a = _asset?.Attachments.FirstOrDefault(x => x.Point == p);
        if (a != null && _model != null)
        {
            if (!string.IsNullOrEmpty(a.Node) && _model.FindChild(a.Node, true, false) is Node3D n) return n.GlobalPosition;
            return _model.GlobalTransform * new Vector3((float)a.X, (float)a.Y, -(float)a.Z);
        }
        var fwd = new Vector3(-Mathf.Sin(_visualYaw), 0, -Mathf.Cos(_visualYaw));
        return p switch
        {
            AttachmentPoint.Weapon or AttachmentPoint.Projectile or AttachmentPoint.MagicSource => GlobalPosition + fwd * 0.3f + new Vector3(0, 0.6f, 0),
            AttachmentPoint.Status => GlobalPosition + new Vector3(0, 1.2f, 0),
            AttachmentPoint.Death => GlobalPosition + new Vector3(0, 0.1f, 0),
            AttachmentPoint.Facing => GlobalPosition + fwd * 0.5f + new Vector3(0, 0.15f, 0),
            _ => GlobalPosition + new Vector3(0, 0.5f, 0),
        };
    }

    private void CollectGeometry(Node n)
    {
        if (n is GeometryInstance3D g) _modelParts.Add(g);
        foreach (var c in n.GetChildren()) CollectGeometry(c);
    }

    /// <summary>
    /// Starts the animation of a new role: the model clip if mapped (a random variant, with its blend), otherwise
    /// procedural. A non-interruptible clip keeps playing unless the new role has a higher priority; the clip of an
    /// attack or a skill is sped up or slowed down so that its «проверка попадания» marker meets the release.
    /// </summary>
    private void EnterRole(AnimRole role)
    {
        if (_roleStarted && _clipBusy && CurrentClip.Length > 0 && _maps.TryGetValue(Role, out var cur) && !cur.Interruptible && PriorityOf(role) <= PriorityOf(Role)) return;
        _roleStarted = true;
        Role = role;
        _roleTime = 0;
        CurrentClip = "";
        _clipBusy = false;
        if (_anim == null || !_clips.TryGetValue(role, out var c)) return;
        string clip = c.Clip;
        float speed = c.Speed;
        float blend = -1;
        if (_maps.TryGetValue(role, out var map))
        {
            var options = map.Variants.Where(x => _anim.HasAnimation(x)).Prepend(map.Clip).ToList();
            clip = options[(int)(GD.Randi() % (uint)options.Count)];
            blend = (float)map.Blend;
            if (_toRelease > 0.05 && role is AnimRole.Attack or AnimRole.Skill && map.Markers.FirstOrDefault(x => x.Kind == AnimMarker.HitCheck) is { } hit && hit.Time > 0)
                speed = (float)Math.Clamp(hit.Time / _toRelease * speed, 0.25, 4);
        }
        var anim = _anim.GetAnimation(clip);
        if (anim != null) anim.LoopMode = c.Loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
        _anim.Play(clip, blend, speed);
        CurrentClip = clip;
        _clipBusy = !c.Loop;
    }

    /// <summary>
    /// Procedural animation of the figure (and of model roles without a clip): breathing, lean when
    /// moving, lunge on attack, rise on a skill, shake on a hit, crouch when blocking, wobble under
    /// control, grow on appearance.
    /// </summary>
    private void Procedural(float yaw)
    {
        float t = _roleTime;
        var pos = Vector3.Zero;
        var rot = new Vector3(0, yaw, 0);
        var scl = Vector3.One;
        var fwd = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        if (CurrentClip.Length == 0)
            switch (Role)
            {
                case AnimRole.Idle:
                    scl = new Vector3(1, 1 + 0.025f * Mathf.Sin(t * 2.2f + Unit.Id), 1);
                    break;
                case AnimRole.Move:
                    rot.X = -0.1f;
                    break;
                case AnimRole.Attack:
                    pos = fwd * 0.16f * Mathf.Sin(Mathf.Clamp(t / 0.35f, 0, 1) * Mathf.Pi);
                    break;
                case AnimRole.Skill:
                    pos = new Vector3(0, 0.08f * (1 - Mathf.Cos(t * 5f)), 0);
                    rot.Y += 0.25f * Mathf.Sin(t * 4f);
                    break;
                case AnimRole.Hit:
                    pos = -fwd * 0.06f * Mathf.Sin(t * 50f) * Math.Max(0, 1 - t / (float)VisualState.HitSeconds);
                    break;
                case AnimRole.Block:
                    scl = new Vector3(1.04f, 0.9f, 1.04f);
                    rot.X = 0.08f;
                    break;
                case AnimRole.Control:
                    rot.Z = 0.14f * Mathf.Sin(t * 5f);
                    break;
                case AnimRole.Spawn:
                {
                    float k = Mathf.Clamp(t / (float)VisualState.SpawnSeconds, 0, 1);
                    scl = Vector3.One * (0.2f + 0.8f * k);
                    pos = new Vector3(0, -0.3f * (1 - k), 0);
                    break;
                }
            }
        _body.Position = pos;
        _body.Rotation = rot;
        _body.Scale = scl * _baseScale;
    }

    private MeshInstance3D Part(Node3D parent, Mesh mesh, Material mat, Vector3 pos, Vector3? rot = null, Vector3? scale = null, bool shadow = true)
    {
        var m = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Position = pos,
            RotationDegrees = rot ?? Vector3.Zero,
            Scale = scale ?? Vector3.One,
            CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(m);
        return m;
    }

    private void BuildBase(FactionShape shape, Color fcol)
    {
        int seg = shape switch { FactionShape.Diamond => 4, FactionShape.Square => 4, FactionShape.Triangle => 3, _ => 28 };
        float rot = shape switch { FactionShape.Square => 45, FactionShape.Triangle => 30, _ => 0 };
        float r = shape == FactionShape.Circle ? 0.42f : 0.5f;
        var baseMesh = new CylinderMesh { TopRadius = r * 0.94f, BottomRadius = r, Height = 0.12f, RadialSegments = seg, Rings = 1 };
        Part(this, baseMesh, Mats.Solid(fcol.Darkened(0.15f), 0.6f), new Vector3(0, 0.06f, 0), new Vector3(0, rot, 0));
        var rim = new CylinderMesh { TopRadius = r * 0.78f, BottomRadius = r * 0.78f, Height = 0.135f, RadialSegments = seg, Rings = 1 };
        Part(this, rim, Mats.Solid(fcol.Lightened(0.25f), 0.5f), new Vector3(0, 0.07f, 0), new Vector3(0, rot, 0));
        // Facing arrow: flat bright wedge sticking out of the base towards the facing direction.
        var arrowMesh = new PrismMesh { Size = new Vector3(0.36f, 0.34f, 0.05f), LeftToRight = 0.5f };
        _arrow = Part(this, arrowMesh, Mats.Emissive(new Color(1f, 0.92f, 0.55f), 0.9f), new Vector3(0, 0.145f, -0.5f), new Vector3(-90, 0, 0), shadow: false);
        // Perception model (docs/DECISIONS_2.md §23): one arrow, several arrows, a ring (omni) or nothing.
        var perception = Unit.Def.Perception;
        if (perception.Model == FacingModel.Multiple)
        {
            // Extra lines of sight (docs/DECISIONS_3.md §40) or the extra facings of part 2.
            var extras = new List<(int Off, bool Fixed)>();
            if (perception.ActiveLines is { } lines)
            {
                var primary = perception.PrimaryLine();
                int p0 = primary?.Offset ?? 0;
                foreach (var l in lines)
                    if (!l.Blind && l != primary)
                        extras.Add(l.Fixed ? (((l.Offset % 6) + 6) % 6, true) : (l.Offset - p0, false));
            }
            else
            {
                foreach (var off in perception.ExtraFacings.Distinct().Where(o => ((o % 6) + 6) % 6 != 0)) extras.Add((off, false));
            }
            foreach (var (off, fix) in extras)
            {
                var m = Part(this, new PrismMesh { Size = new Vector3(0.28f, 0.26f, 0.05f), LeftToRight = 0.5f },
                    Mats.Emissive(new Color(1f, 0.78f, 0.45f), 0.6f), Vector3.Zero, new Vector3(-90, 0, 0), shadow: false);
                _extraArrows.Add((m, off, fix));
            }
        }
        if (perception.Model == FacingModel.Omni)
            _omniRing = Part(this, new TorusMesh { InnerRadius = 0.47f, OuterRadius = 0.5f, Rings = 36, RingSegments = 4 },
                Mats.Emissive(new Color(1f, 0.92f, 0.55f), 0.7f), new Vector3(0, 0.15f, 0), shadow: false);
        _arrow.Visible = perception.HasFacing;
        _selectRing = Part(this, new TorusMesh { InnerRadius = 0.52f, OuterRadius = 0.6f, Rings = 32, RingSegments = 6 },
            Mats.Emissive(new Color(1, 1, 1), 1.4f), new Vector3(0, 0.03f, 0), shadow: false);
        _selectRing.Visible = false;
    }

    /// <summary>
    /// A structure (docs/DECISIONS_4.md §56): a block on every occupied hex in the team colour and a building on the
    /// anchor by the object type — a keep for a base, a turret for a defence, a crystal for a goal.
    /// </summary>
    private void BuildStructure(Color fcol)
    {
        var u = Unit;
        var origin = Board.Center(u.Pos);
        var wall = Mats.Solid(fcol.Darkened(0.35f), 0.75f);
        var top = Mats.Solid(fcol.Darkened(0.05f), 0.55f);
        var trim = Mats.Emissive(fcol.Lightened(0.35f), 0.7f);
        var body = Mats.Solid(_bodyColor, 0.5f);
        void P(Mesh m, Material mt, Vector3 p, Vector3? r = null) => _bodyParts.Add(Part(_body, m, mt, p, r));
        foreach (var c in u.Footprint ?? new List<HexCoord> { u.Pos })
        {
            var p = Board.Center(c) - origin;
            P(new CylinderMesh { TopRadius = 0.8f, BottomRadius = 0.88f, Height = 0.3f, RadialSegments = 6, Rings = 1 }, wall, p + new Vector3(0, 0.15f, 0), new Vector3(0, 30, 0));
            P(new CylinderMesh { TopRadius = 0.72f, BottomRadius = 0.72f, Height = 0.05f, RadialSegments = 6, Rings = 1 }, top, p + new Vector3(0, 0.32f, 0), new Vector3(0, 30, 0));
            P(new TorusMesh { InnerRadius = 0.74f, OuterRadius = 0.8f, Rings = 6, RingSegments = 3 }, trim, p + new Vector3(0, 0.31f, 0), new Vector3(0, 30, 0));
        }
        if (u.Def.Tags.Contains("defense"))
        {
            P(new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.3f, Height = 0.7f, RadialSegments = 8 }, body, new Vector3(0, 0.7f, 0));
            P(new BoxMesh { Size = new Vector3(0.12f, 0.12f, 0.55f) }, Mats.Solid(new Color(0.75f, 0.77f, 0.8f), 0.3f, 0.7f), new Vector3(0, 1.0f, -0.3f));
            P(new SphereMesh { Radius = 0.2f, Height = 0.3f, RadialSegments = 10, Rings = 5 }, trim, new Vector3(0, 1.08f, 0));
        }
        else if (u.Def.Tags.Contains("goal"))
        {
            P(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.34f, Height = 0.6f, RadialSegments = 4 }, trim, new Vector3(0, 0.95f, 0));
            P(new CylinderMesh { TopRadius = 0.34f, BottomRadius = 0f, Height = 0.45f, RadialSegments = 4 }, trim, new Vector3(0, 0.42f, 0));
        }
        else
        {
            // Keep with four battlements and a roof: reads as «база» from any side.
            P(new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.44f, Height = 0.95f, RadialSegments = 6 }, body, new Vector3(0, 0.8f, 0), new Vector3(0, 30, 0));
            for (int i = 0; i < 6; i++)
            {
                float a = Mathf.Tau * i / 6;
                P(new BoxMesh { Size = new Vector3(0.14f, 0.16f, 0.14f) }, body, new Vector3(Mathf.Cos(a) * 0.34f, 1.34f, Mathf.Sin(a) * 0.34f));
            }
            P(new PrismMesh { Size = new Vector3(0.42f, 0.32f, 0.42f) }, trim, new Vector3(0, 1.5f, 0));
        }
        // No facing arrow on a structure; the selection ring spans the anchor hex.
        _arrow = Part(this, new BoxMesh { Size = Vector3.One * 0.01f }, Mats.Solid(fcol), Vector3.Zero, shadow: false);
        _arrow.Visible = false;
        _selectRing = Part(this, new TorusMesh { InnerRadius = 0.84f, OuterRadius = 0.94f, Rings = 6, RingSegments = 4 },
            Mats.Emissive(new Color(1, 1, 1), 1.4f), new Vector3(0, 0.36f, 0), new Vector3(0, 30, 0), shadow: false);
        _selectRing.Visible = false;
    }

    private void BuildBody(UnitShape shape, Color c, Color fcol)
    {
        var mat = Mats.Solid(c, 0.55f);
        var dark = Mats.Solid(c.Darkened(0.3f), 0.6f);
        var skin = Mats.Solid(new Color(0.93f, 0.85f, 0.74f), 0.6f);
        var metal = Mats.Solid(new Color(0.75f, 0.77f, 0.8f), 0.3f, 0.7f);
        float b = 0.12f;
        void P(Mesh m, Material mt, Vector3 p, Vector3? r = null, Vector3? s = null) => _bodyParts.Add(Part(_body, m, mt, p, r, s));
        switch (shape)
        {
            case UnitShape.Soldier:
                P(new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.25f, Height = 0.5f, RadialSegments = 8 }, mat, new Vector3(0, b + 0.25f, 0));
                P(new SphereMesh { Radius = 0.15f, Height = 0.3f, RadialSegments = 10, Rings = 6 }, metal, new Vector3(0, b + 0.63f, 0));
                P(new BoxMesh { Size = new Vector3(0.38f, 0.42f, 0.06f) }, Mats.Solid(fcol.Lightened(0.1f), 0.5f), new Vector3(0, b + 0.3f, -0.26f));
                P(new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.02f, Height = 0.6f, RadialSegments = 4 }, metal, new Vector3(0.27f, b + 0.4f, -0.08f), new Vector3(15, 0, 0));
                break;
            case UnitShape.Archer:
                P(new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.22f, Height = 0.5f, RadialSegments = 8 }, mat, new Vector3(0, b + 0.25f, 0));
                P(new SphereMesh { Radius = 0.14f, Height = 0.28f, RadialSegments = 10, Rings = 6 }, skin, new Vector3(0, b + 0.62f, 0));
                P(new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.16f, Height = 0.18f, RadialSegments = 8 }, dark, new Vector3(0, b + 0.78f, 0));
                P(new TorusMesh { InnerRadius = 0.28f, OuterRadius = 0.31f, Rings = 16, RingSegments = 4 }, Mats.Solid(new Color(0.55f, 0.38f, 0.2f)), new Vector3(-0.2f, b + 0.42f, -0.12f), new Vector3(0, 0, 90), new Vector3(1, 1, 0.5f));
                break;
            case UnitShape.Mage:
                P(new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.28f, Height = 0.62f, RadialSegments = 10 }, mat, new Vector3(0, b + 0.31f, 0));
                P(new SphereMesh { Radius = 0.13f, Height = 0.26f, RadialSegments = 10, Rings = 6 }, skin, new Vector3(0, b + 0.72f, 0));
                P(new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.19f, Height = 0.36f, RadialSegments = 10 }, dark, new Vector3(0, b + 0.98f, 0), new Vector3(-8, 0, 0));
                P(new SphereMesh { Radius = 0.06f, Height = 0.12f }, Mats.Emissive(new Color(0.6f, 0.85f, 1f), 1.5f), new Vector3(0.24f, b + 0.6f, -0.12f));
                P(new CylinderMesh { TopRadius = 0.015f, BottomRadius = 0.015f, Height = 0.75f, RadialSegments = 4 }, Mats.Solid(new Color(0.5f, 0.36f, 0.22f)), new Vector3(0.24f, b + 0.25f, -0.12f));
                break;
            case UnitShape.Beast:
                P(new CapsuleMesh { Radius = 0.17f, Height = 0.62f, RadialSegments = 10, Rings = 4 }, mat, new Vector3(0, b + 0.22f, 0.02f), new Vector3(90, 0, 0));
                P(new SphereMesh { Radius = 0.14f, Height = 0.26f, RadialSegments = 10, Rings = 6 }, mat, new Vector3(0, b + 0.32f, -0.32f));
                P(new PrismMesh { Size = new Vector3(0.08f, 0.12f, 0.04f) }, dark, new Vector3(0.07f, b + 0.47f, -0.3f));
                P(new PrismMesh { Size = new Vector3(0.08f, 0.12f, 0.04f) }, dark, new Vector3(-0.07f, b + 0.47f, -0.3f));
                P(new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.05f, Height = 0.3f, RadialSegments = 5 }, dark, new Vector3(0, b + 0.3f, 0.36f), new Vector3(-55, 0, 0));
                break;
            case UnitShape.Brute:
                P(new BoxMesh { Size = new Vector3(0.52f, 0.5f, 0.38f) }, mat, new Vector3(0, b + 0.3f, 0));
                P(new SphereMesh { Radius = 0.15f, Height = 0.28f, RadialSegments = 10, Rings = 6 }, dark, new Vector3(0, b + 0.66f, -0.04f));
                P(new SphereMesh { Radius = 0.12f, Height = 0.24f }, mat, new Vector3(0.32f, b + 0.48f, 0));
                P(new SphereMesh { Radius = 0.12f, Height = 0.24f }, mat, new Vector3(-0.32f, b + 0.48f, 0));
                P(new BoxMesh { Size = new Vector3(0.12f, 0.12f, 0.55f) }, metal, new Vector3(0.36f, b + 0.32f, -0.2f));
                break;
            case UnitShape.Construct:
                P(new BoxMesh { Size = new Vector3(0.46f, 0.34f, 0.4f) }, mat, new Vector3(0, b + 0.19f, 0));
                P(new BoxMesh { Size = new Vector3(0.34f, 0.3f, 0.32f) }, dark, new Vector3(0, b + 0.52f, 0));
                P(new BoxMesh { Size = new Vector3(0.2f, 0.06f, 0.04f) }, Mats.Emissive(new Color(1f, 0.4f, 0.25f), 2f), new Vector3(0, b + 0.55f, -0.17f));
                break;
            case UnitShape.Orb:
                P(new SphereMesh { Radius = 0.26f, Height = 0.52f, RadialSegments = 16, Rings = 8 }, Mats.Emissive(c, 0.6f), new Vector3(0, b + 0.5f, 0));
                break;
            case UnitShape.Totem:
                P(new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.2f, Height = 0.9f, RadialSegments = 6 }, mat, new Vector3(0, b + 0.45f, 0));
                P(new BoxMesh { Size = new Vector3(0.5f, 0.08f, 0.12f) }, dark, new Vector3(0, b + 0.75f, 0));
                break;
            default: // Pawn
                P(new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.24f, Height = 0.45f, RadialSegments = 10 }, mat, new Vector3(0, b + 0.22f, 0));
                P(new SphereMesh { Radius = 0.15f, Height = 0.3f, RadialSegments = 10, Rings = 6 }, mat, new Vector3(0, b + 0.55f, 0));
                P(new BoxMesh { Size = new Vector3(0.1f, 0.05f, 0.05f) }, dark, new Vector3(0, b + 0.56f, -0.14f));
                break;
        }
        _symbol = new Label3D
        {
            Text = Unit.Def.Visual.Symbol,
            FontSize = 48,
            PixelSize = 0.004f,
            Position = new Vector3(0, 0.137f, 0.12f),
            RotationDegrees = new Vector3(-90, 0, 0),
            Modulate = new Color(1, 1, 1, 0.9f),
            OutlineSize = 6,
            OutlineModulate = new Color(0, 0, 0, 0.5f),
            Shaded = false,
        };
        AddChild(_symbol);
    }

    private void BuildBars(float scale)
    {
        _bars.Position = new Vector3(0, 1.25f * Math.Max(1, scale), 0);
        AddChild(_bars);
        var quad = new QuadMesh { Size = new Vector2(1, 1) };
        MeshInstance3D Q(Color c, Vector3 pos, Vector3 size, int prio)
        {
            var mat = new StandardMaterial3D
            {
                AlbedoColor = c,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
                BillboardKeepScale = true,
                NoDepthTest = true,
                RenderPriority = prio,
                Transparency = c.A < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
            };
            var m = new MeshInstance3D { Mesh = quad, MaterialOverride = mat, Position = pos, Scale = size, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _bars.AddChild(m);
            return m;
        }
        Q(new Color(0.05f, 0.05f, 0.06f, 0.85f), new Vector3(0, 0, 0), new Vector3(BarWidth + 0.06f, 0.2f, 1), 10);
        _hpFill = Q(Ui.HpColor, new Vector3(0, 0.04f, 0), new Vector3(BarWidth, 0.08f, 1), 11);
        // Shield: a light strip over the health bar (share of max health, capped at the bar width).
        _shieldFill = Q(new Color(0.6f, 0.88f, 1f), new Vector3(0, 0.1f, 0), new Vector3(0.01f, 0.035f, 1), 12);
        _apFill = Q(Ui.ApColor, new Vector3(0, -0.055f, 0), new Vector3(BarWidth, 0.05f, 1), 11);
        _apReserve = Q(Ui.ReserveColor, new Vector3(0, -0.055f, 0), new Vector3(0.01f, 0.05f, 1), 12);
        _status = new Label3D
        {
            FontSize = 36,
            PixelSize = 0.006f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Position = new Vector3(0, 0.19f, 0),
            OutlineSize = 8,
            RenderPriority = 13,
            OutlineRenderPriority = 12,
        };
        _bars.AddChild(_status);
        _name = new Label3D
        {
            FontSize = 30,
            PixelSize = 0.0055f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Position = new Vector3(0, -0.2f, 0),
            OutlineSize = 8,
            RenderPriority = 13,
            OutlineRenderPriority = 12,
            Visible = false,
        };
        _bars.AddChild(_name);
        _stackBadge = new Label3D
        {
            FontSize = 44,
            PixelSize = 0.0085f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Position = new Vector3(BarWidth / 2 + 0.3f, 0.0f, 0),
            OutlineSize = 8,
            RenderPriority = 13,
            OutlineRenderPriority = 12,
            Modulate = new Color(1f, 0.95f, 0.7f),
            Visible = false,
        };
        _bars.AddChild(_stackBadge);
        _blockLabel = new Label3D
        {
            FontSize = 34,
            PixelSize = 0.0075f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Position = new Vector3(0, 0.47f, 0),
            OutlineSize = 8,
            RenderPriority = 13,
            OutlineRenderPriority = 12,
            Visible = false,
        };
        _bars.AddChild(_blockLabel);
    }

    public void Flash() => _hitFlash = 0.25f;

    private MeshInstance3D? _groundShadow;
    private MeshInstance3D? _beam;
    private MeshInstance3D? _burrowRing;
    private float _transparency = -1;

    private void SetTransparency(float t)
    {
        if (Math.Abs(t - _transparency) < 0.01f) return;
        _transparency = t;
        foreach (var m in _bodyParts) m.Transparency = t;
        foreach (var m in _modelParts) m.Transparency = t;
    }

    /// <summary>Hover shadow + beam for flyers, translucency + ring for hidden (underground) layers.</summary>
    private void SetLayerVisuals(LayerDef? layer, float lift = 0)
    {
        bool air = layer != null && lift > 0.01f;
        if (air && _groundShadow == null)
        {
            _groundShadow = Part(this, new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.36f, Height = 0.01f, RadialSegments = 24, Rings = 1 },
                Mats.Transparent(new Color(0, 0, 0, 0.42f), unshaded: true), Vector3.Zero, shadow: false);
            _beam = Part(this, new CylinderMesh { TopRadius = 0.015f, BottomRadius = 0.015f, Height = 1f, RadialSegments = 6, Rings = 1 },
                Mats.Transparent(new Color(0.55f, 0.85f, 1f, 0.35f), unshaded: true), Vector3.Zero, shadow: false);
        }
        if (_groundShadow != null)
        {
            _groundShadow.Visible = air;
            _beam!.Visible = air;
            if (air)
            {
                _groundShadow.Position = new Vector3(0, -lift + 0.02f, 0);
                _beam.Position = new Vector3(0, -lift / 2, 0);
                _beam.Scale = new Vector3(1, Math.Max(0.05f, lift), 1);
            }
        }
        bool hidden = layer != null && layer.Translucent;
        if (hidden && _burrowRing == null)
            _burrowRing = Part(this, new TorusMesh { InnerRadius = 0.36f, OuterRadius = 0.44f, Rings = 24, RingSegments = 4 },
                Mats.Transparent(new Color(0.85f, 0.62f, 0.35f, 0.7f), unshaded: true), new Vector3(0, 0.02f, 0), shadow: false);
        if (_burrowRing != null) _burrowRing.Visible = hidden;
        if (layer != null) SetTransparency(hidden ? 0.55f : 0f);
    }

    /// <summary>Updates transform and indicators from the simulation state.</summary>
    public void Sync(Simulation sim, BattleMap map, double alpha, bool visible, bool ghost, float delta)
    {
        Ghost = ghost;
        var u = Unit;
        if (u.IsStructure && !u.Alive)
        {
            // A destroyed structure stays as ruins: it sinks, darkens and keeps its place on the board.
            if (_deathTime < 0) _deathTime = 0;
            _deathTime += delta;
            float k = Math.Min(1, _deathTime / 1.2f);
            _bars.Visible = false;
            _selectRing.Visible = false;
            _body.Scale = new Vector3(1, Math.Max(0.3f, 1 - 0.7f * k), 1);
            foreach (var part in _bodyParts) part.Transparency = 0.35f * k;
            Visible = visible || ghost;
            return;
        }
        if (!u.Alive)
        {
            if (Role != AnimRole.Death || !_roleStarted) EnterRole(AnimRole.Death);
            if (_deathTime < 0) _deathTime = 0;
            _deathTime += delta;
            _bars.Visible = false;
            _arrow.Visible = false;
            foreach (var (m, _, _) in _extraArrows) m.Visible = false;
            if (_omniRing != null) _omniRing.Visible = false;
            SetLayerVisuals(null);
            if (u.DeathCause == DeathCause.Fall || u.DeathCause == DeathCause.Removed)
            {
                // Falling into a pit: drop / fade / shrink by the pit's settings.
                var pit = map.Get(u.Pos)?.Terrain.Pit ?? new PitRulesDef();
                float dur = Math.Max(0.2f, (float)pit.AnimationSeconds);
                float f = Math.Min(1, _deathTime / dur);
                if (pit.Animation != FallAnimation.None)
                {
                    var target = Board.Center(u.Pos, Board.TopY(map.Get(u.Pos)));
                    Position = new Vector3(Mathf.Lerp(Position.X, target.X, 0.3f), Position.Y, Mathf.Lerp(Position.Z, target.Z, 0.3f));
                    if (pit.Animation == FallAnimation.Drop) Position = Position with { Y = target.Y - 2.2f * f * f };
                    if (pit.Animation == FallAnimation.Shrink) Scale = Vector3.One * Math.Max(0.02f, 1 - f);
                    SetTransparency(pit.Animation == FallAnimation.Fade ? f : f * f * 0.8f);
                    _body.RotationDegrees = new Vector3(f * 35, f * 140, 0);
                }
                Visible = f < 1 && visible;
                return;
            }
            float k = Math.Min(1, _deathTime / 0.8f);
            if (CurrentClip.Length > 0)
            {
                // The model's own death clip, then the figure fades out.
                foreach (var g in _modelParts) g.Transparency = Math.Max(0, (_deathTime - 1.0f) / 0.6f);
                Visible = _deathTime < 1.6f && visible;
                return;
            }
            _body.RotationDegrees = new Vector3(k * 80, 0, 0);
            Scale = Vector3.One * (1 - k * 0.3f);
            Visible = _deathTime < 1.6f && visible;
            Position = Position with { Y = Position.Y - delta * 0.15f * k };
            return;
        }
        Visible = visible || ghost;
        if (!Visible) return;

        // Position: interpolate along the current step.
        Vector3 pos;
        var cell = map.Get(u.Pos);
        if (u.StepFrom.HasValue && u.StepTo.HasValue)
        {
            var a = map.Get(u.StepFrom.Value);
            var b = map.Get(u.StepTo.Value);
            double t = (u.StepTicksDone + alpha) / Math.Max(1, u.StepTicksTotal);
            t = Math.Clamp(t, 0, 1);
            var pa = Board.Center(u.StepFrom.Value, Board.TopY(a));
            var pb = Board.Center(u.StepTo.Value, Board.TopY(b));
            pos = pa.Lerp(pb, (float)t);
            pos.Y += Mathf.Sin((float)t * Mathf.Pi) * 0.12f; // small hop
        }
        else
        {
            pos = Board.Center(u.Pos, Board.TopY(cell)) + StackOffset;
        }
        // Layer: flyers hover (with a ground shadow and a thin beam), underground units are translucent at the surface.
        var layer = u.LayerDef;
        float lift = layer.DisplayHeight > 0 ? (float)layer.DisplayHeight * Board.LevelHeight : 0f;
        pos.Y += lift + (lift > 0 ? Mathf.Sin(Time.GetTicksMsec() / 450f + u.Id) * 0.05f : 0);
        Position = Position.DistanceTo(pos) > 3 ? pos : Position.Lerp(pos, Math.Min(1, delta * 25));
        SetLayerVisuals(layer, lift);

        float targetYaw = PreviewYaw ?? Board.YawOf(u.Facing);
        _visualYaw = Mathf.LerpAngle(_visualYaw, targetYaw, Math.Min(1, delta * (PreviewYaw.HasValue ? 24 : 14)));
        var role = VisualState.RoleOf(sim, u);
        _toRelease = u.Casting != null && !u.Casting.Released ? (u.Casting.ReleaseTick - sim.Tick) * sim.Dt : -1;
        if (role != Role || !_roleStarted) EnterRole(role);
        if (_anim != null) _anim.SpeedScale = TimeScale;
        _roleTime += delta * TimeScale;
        if (u.IsStructure)
        {
            _visualYaw = 0;
            _body.Scale = Vector3.One;
        }
        else Procedural(_visualYaw);
        _arrow.Position = new Vector3(-Mathf.Sin(_visualYaw) * 0.52f, 0.145f, -Mathf.Cos(_visualYaw) * 0.52f);
        _arrow.Basis = new Basis(Vector3.Up, _visualYaw) * new Basis(Vector3.Right, -Mathf.Pi / 2);
        foreach (var (m, off, fix) in _extraArrows)
        {
            float yaw = fix ? Board.YawOf((HexDirection)off) : _visualYaw + Mathf.AngleDifference(Board.YawOf(HexDirection.E), Board.YawOf(HexDirection.E.Rotate(off)));
            m.Position = new Vector3(-Mathf.Sin(yaw) * 0.5f, 0.145f, -Mathf.Cos(yaw) * 0.5f);
            m.Basis = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, -Mathf.Pi / 2);
            m.Visible = true;
        }
        if (_omniRing != null) _omniRing.Visible = true;
        float stackScale = StackCount switch { <= 1 => 1f, 2 => 0.72f, _ => 0.62f };
        Scale = Scale.Lerp(Vector3.One * stackScale, Math.Min(1, delta * 12));

        _selectRing.Visible = Selected || Hovered;
        if (_selectRing.Visible)
        {
            float p = 0.5f + 0.5f * Mathf.Sin((float)Time.GetTicksMsec() / 180f);
            _selectRing.Scale = Vector3.One * (Selected ? 1.0f + p * 0.06f : 0.95f);
            ((StandardMaterial3D)_selectRing.MaterialOverride).AlbedoColor = Selected ? new Color(1, 1, 1) : new Color(1, 1, 1, 0.6f);
        }

        // Bars.
        float hpf = (float)Math.Clamp(u.HpFraction, 0, 1);
        _hpFill.Scale = new Vector3(Math.Max(0.001f, BarWidth * hpf), 0.08f, 1);
        _hpFill.Position = new Vector3(-BarWidth / 2 + BarWidth * hpf / 2, 0.04f, 0);
        float shf = u.HpMax <= 0 ? 0 : (float)Math.Clamp(u.ShieldTotal / u.HpMax, 0, 1);
        _shieldFill.Visible = shf > 0.001f;
        if (_shieldFill.Visible)
        {
            _shieldFill.Scale = new Vector3(BarWidth * shf, 0.035f, 1);
            _shieldFill.Position = new Vector3(-BarWidth / 2 + BarWidth * shf / 2, 0.1f, 0);
        }
        ((StandardMaterial3D)_hpFill.MaterialOverride).AlbedoColor = hpf > 0.5f ? Ui.HpColor : hpf > 0.25f ? Ui.Warn : Ui.Bad;
        float apf = (float)Math.Clamp(u.ApMax <= 0 ? 0 : u.Ap / u.ApMax, 0, 1);
        _apFill.Scale = new Vector3(Math.Max(0.001f, BarWidth * apf), 0.05f, 1);
        _apFill.Position = new Vector3(-BarWidth / 2 + BarWidth * apf / 2, -0.055f, 0);
        float rf = (float)Math.Clamp(u.ApMax <= 0 ? 0 : Math.Min(u.ReservedAp, u.Ap) / u.ApMax, 0, 1);
        _apReserve.Visible = rf > 0.001f && sim.CanPlayerCommand(u);
        _apReserve.Scale = new Vector3(Math.Max(0.001f, BarWidth * rf), 0.05f, 1);
        _apReserve.Position = new Vector3(-BarWidth / 2 + BarWidth * (apf - rf / 2), -0.055f, 0);
        // Shared hex: one set of bars (the first figure, or the selected/hovered one) keeps the stack readable.
        _bars.Visible = !ghost && (StackCount < 2 || StackIndex == 0 || Selected || Hovered);

        // Status letters (coloured, but each status also has its own letter).
        if (u.Statuses.Count > 0)
        {
            var parts = u.Statuses.GroupBy(s => s.Def.Id).Select(g => g.First()).Take(6)
                .Select(s => $"{(string.IsNullOrEmpty(s.Def.Icon) ? s.Def.DisplayName.Substring(0, 1) : s.Def.Icon)}");
            _status.Text = string.Join(" ", parts);
            var first = u.Statuses[0];
            _status.Modulate = Ui.Parse(first.Def.Color, Colors.White);
            _status.Visible = true;
        }
        else
        {
            _status.Visible = false;
        }
        if (u.Casting != null && !u.Casting.Released)
        {
            _status.Text = "◷ " + u.Casting.Skill.DisplayName + (_status.Visible ? "  " + _status.Text : "");
            _status.Modulate = Ui.Parse(u.Casting.Skill.Color, Ui.Accent);
            _status.Visible = true;
        }
        _name.Visible = ShowName || Selected || Hovered;
        if (_name.Visible) _name.Text = u.Name;
        _stackBadge.Visible = StackCount > 1 && StackIndex == 0 && !ghost;
        if (_stackBadge.Visible) _stackBadge.Text = $"×{StackCount}";
        _blockLabel.Visible = BlockText.Length > 0 && !ghost;
        if (_blockLabel.Visible)
        {
            _blockLabel.Text = BlockText;
            _blockLabel.Modulate = BlockColor;
        }

        // Hit flash: briefly brighten the figure.
        if (_hitFlash > 0)
        {
            _hitFlash -= delta;
            float f = Math.Max(0, _hitFlash) * 4;
            _body.Scale *= 1 + f * 0.08f;
        }

        // Ghost (last known position): semi-transparent.
        foreach (var part in _bodyParts) part.Transparency = ghost ? 0.65f : 0f;
        foreach (var part in _modelParts) part.Transparency = ghost ? 0.65f : 0f;
    }
}
