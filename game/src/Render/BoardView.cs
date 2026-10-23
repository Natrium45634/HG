using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Render;

/// <summary>
/// The 3D board shared by the battle screen and the editors: environment and lights, terrain,
/// cell effects, overlays, unit pieces, fog of war shading and floating combat text.
/// Purely visual: reads the simulation, never changes it.
/// </summary>
public partial class BoardView : Node3D
{
    public CameraRig Rig { get; } = new() { Name = "CameraRig" };
    public TerrainNode Terrain { get; } = new() { Name = "Terrain" };
    public OverlayLayer Overlay { get; } = new() { Name = "Overlay" };
    public CellEffectsView Effects { get; } = new() { Name = "Effects" };
    /// <summary>Scenario objects without health: spawn portals, checkpoints, markers (docs/DECISIONS_4.md §56).</summary>
    public ScenarioObjectsView Objects { get; } = new() { Name = "Objects" };
    private readonly Node3D _units = new() { Name = "Units" };
    private readonly Node3D _fx = new() { Name = "Fx" };
    private readonly MeshInstance3D _fog = new() { Name = "Fog", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly ImmediateMesh _fogMesh = new();
    private MeshInstance3D? _table;
    private DirectionalLight3D? _sun;
    private WorldEnvironment? _env;

    private readonly Dictionary<int, UnitView> _views = new();
    private readonly List<(Label3D Label, float Age, float Life)> _floating = new();
    private BattleMap? _map;
    private ContentDatabase? _db;
    private int _terrainVersion = -1;
    private List<float> _pickLevels = new();
    private int _fogKey = -1;

    public BattleMap? Map => _map;
    public IReadOnlyDictionary<int, UnitView> Views => _views;

    /// <summary>Inside an editor SubViewport (glow off: it washes the image out there).</summary>
    public bool Embedded { get; set; }

    public override void _Ready()
    {
        AddChild(Rig);
        AddChild(Terrain);
        AddChild(Effects);
        AddChild(Objects);
        AddChild(_units);
        AddChild(_fog);
        AddChild(Overlay);
        AddChild(_fx);
        _fog.Mesh = _fogMesh;
        SetupEnvironment();
        // Window/preview resized while the board was fitted: fit again.
        Rig.RefitRequested += () => FitCamera(immediate: true);
    }

    private void SetupEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.075f, 0.085f, 0.10f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.68f, 0.78f),
            AmbientLightEnergy = 0.45f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = 1.05f,
            SsaoEnabled = true,
            SsaoRadius = 0.9f,
            SsaoIntensity = 1.4f,
            GlowEnabled = true,
            GlowIntensity = 0.7f,
            GlowBloom = 0.03f,
            GlowHdrThreshold = 1.05f,
            AdjustmentEnabled = true,
            AdjustmentSaturation = 1.05f,
        };
        if (Embedded)
        {
            env.GlowEnabled = false;
            env.SsaoEnabled = false;
        }
        _env = new WorldEnvironment { Environment = env, Name = "Env" };
        AddChild(_env);
        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightColor = new Color(1f, 0.95f, 0.88f),
            LightEnergy = 1.25f,
            ShadowEnabled = true,
            ShadowBias = 0.04f,
            ShadowNormalBias = 1.2f,
            DirectionalShadowMaxDistance = 80,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            RotationDegrees = new Vector3(-52, -38, 0),
        };
        AddChild(_sun);
        AddChild(new DirectionalLight3D
        {
            Name = "Fill",
            LightColor = new Color(0.62f, 0.72f, 0.95f),
            LightEnergy = 0.28f,
            ShadowEnabled = false,
            RotationDegrees = new Vector3(-35, 140, 0),
        });
    }

    public void ApplySettings(App.Settings s)
    {
        Rig.TiltDeg = (float)s.CameraTilt;
        Rig.EdgeScroll = s.EdgeScroll;
        Rig.Speed = (float)s.CameraSpeed;
        if (_sun != null) _sun.ShadowEnabled = s.Shadows;
        if (_env != null) _env.Environment.SsaoEnabled = s.Ssao;
    }

    public void Bind(BattleMap map, ContentDatabase db)
    {
        _map = map;
        _db = db;
        Effects.Db = db;
        Overlay.Bind(map);
        RebuildTerrain();
        FrameCamera();
    }

    /// <summary>Fits the whole board into the free screen area (core CameraFit).</summary>
    public void FitCamera(bool immediate = false)
    {
        if (_map == null || _map.Count == 0) return;
        var cells = new List<(HexTactics.Core.Hex.HexCoord, float, float)>();
        foreach (var c in _map.Cells) cells.Add((c.Coord, Math.Max(Board.SurfaceY(c), Board.TopY(c)), Terrain.BaseY));
        Rig.FitPoints(HexTactics.Core.Geometry.CameraFit.BoardPoints(cells), immediate);
    }

    public void FrameCamera(bool fitAll = false)
    {
        if (_map == null || _map.Count == 0) return;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var c in _map.Cells)
        {
            var p = Board.Center(c.Coord);
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minZ = Math.Min(minZ, p.Z);
            maxZ = Math.Max(maxZ, p.Z);
        }
        Rig.Frame(new Rect2(minX, minZ, maxX - minX, maxZ - minZ), fitAll || Embedded);
        FitCamera(immediate: true);
    }

    public void RebuildTerrain()
    {
        if (_map == null) return;
        Terrain.Build(_map, true, _db);
        _terrainVersion = _map.TerrainVersion;
        _pickLevels = Board.PickLevels(_map);
        if (_table == null)
        {
            _table = new MeshInstance3D
            {
                Name = "Table",
                Mesh = new PlaneMesh { Size = new Vector2(400, 400) },
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/table.gdshader") },
            };
            AddChild(_table);
        }
        _table.Position = new Vector3(0, Terrain.BaseY - 0.02f, 0);
    }

    /// <summary>Call every frame: rebuilds terrain when the map changed (destruction, freezing...).</summary>
    public void SyncMap(Func<Cell, bool> effectVisible)
    {
        // Models loaded in the background, one per frame (docs/DECISIONS_3.md §47).
        Assets.ProcessPending();
        if (_map == null) return;
        if (_map.TerrainVersion != _terrainVersion) RebuildTerrain();
        Effects.Sync(_map, effectVisible);
    }

    public Cell? PickCell(Vector2 screen)
    {
        if (_map == null) return null;
        var (o, d) = Rig.Ray(screen);
        return Board.Pick(_map, o, d, _pickLevels);
    }

    public HexCoord? PickHex(Vector2 screen)
    {
        var c = PickCell(screen);
        if (c != null) return c.Coord;
        var (o, d) = Rig.Ray(screen);
        return Board.PickPlane(o, d, 0);
    }

    // ------------------------------------------------------------------ units

    public UnitView ViewOf(Unit u)
    {
        if (_views.TryGetValue(u.Id, out var v)) return v;
        v = new UnitView(u, _db!);
        _units.AddChild(v);
        if (_map != null) v.Position = Board.Center(u.Pos, Board.TopY(_map.Get(u.Pos)));
        _views[u.Id] = v;
        return v;
    }

    /// <summary>
    /// The game speed for the look (docs/DECISIONS_3.md §47): model clips and procedural animation follow it — a pause
    /// freezes them, acceleration speeds them up, so the hit marker meets the battle's moment at every speed.
    /// </summary>
    public float TimeScale { get; set; } = 1;

    public void SyncUnits(Simulation sim, double alpha, Func<Unit, bool> visible, Func<Unit, bool> ghost, float delta)
    {
        LayoutStacks(sim, visible);
        foreach (var u in sim.Units)
        {
            var v = ViewOf(u);
            v.TimeScale = TimeScale;
            v.Sync(sim, sim.Map, alpha, visible(u), ghost(u), delta);
        }
        // Units removed from the simulation entirely.
        foreach (var id in new List<int>(_views.Keys))
        {
            if (sim.GetUnit(id) != null) continue;
            _views[id].QueueFree();
            _views.Remove(id);
        }
    }

    private readonly Dictionary<(HexCoord, string), List<Unit>> _stacks = new();

    /// <summary>Several visible creatures on one hex and layer: spread them out (cluster / row / ring) and badge ×N.</summary>
    private void LayoutStacks(Simulation sim, Func<Unit, bool> visible)
    {
        foreach (var list in _stacks.Values) list.Clear();
        foreach (var u in sim.Units)
        {
            if (!u.Alive || u.StepTo.HasValue || !visible(u)) continue;
            var key = (u.Pos, u.Layer);
            if (!_stacks.TryGetValue(key, out var list)) _stacks[key] = list = new List<Unit>(2);
            list.Add(u);
        }
        foreach (var u in sim.Units)
        {
            var v = ViewOf(u);
            v.StackOffset = Vector3.Zero;
            v.StackIndex = 0;
            v.StackCount = 1;
        }
        foreach (var list in _stacks.Values)
        {
            if (list.Count < 2) continue;
            var layout = list[0].Def.Stacking.Layout;
            for (int i = 0; i < list.Count; i++)
            {
                var v = ViewOf(list[i]);
                v.StackIndex = i;
                v.StackCount = list.Count;
                v.StackOffset = StackOffset(layout, i, list.Count);
            }
        }
    }

    public static Vector3 StackOffset(StackLayout layout, int i, int n)
    {
        if (n < 2) return Vector3.Zero;
        switch (layout)
        {
            case StackLayout.Row:
            {
                float w = Math.Min(0.75f, 0.32f * (n - 1));
                return new Vector3(-w / 2 + w * i / (n - 1), 0, 0.05f * (i % 2 == 0 ? 1 : -1));
            }
            case StackLayout.Ring:
            {
                float a = Mathf.Tau * i / n - Mathf.Pi / 2;
                return new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.36f;
            }
            default:
            {
                // Cluster: a tight triangle/diamond; from five on, one in the centre.
                if (n >= 5 && i == 0) return Vector3.Zero;
                int k = n >= 5 ? i - 1 : i;
                int m = n >= 5 ? n - 1 : n;
                float a = Mathf.Tau * k / m + Mathf.Pi / 6;
                return new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (m == 2 ? 0.3f : 0.38f);
            }
        }
    }

    public void ClearUnits()
    {
        foreach (var v in _views.Values) v.QueueFree();
        _views.Clear();
    }

    // ------------------------------------------------------------------ fog of war

    /// <summary>Darkens cells the player side cannot currently see (rebuilt only when vision changes).</summary>
    public void SyncFog(Simulation sim, bool enabled)
    {
        int key = enabled ? (int)sim.Vision.Recomputes * 31 + sim.Map.TerrainVersion : -2;
        if (key == _fogKey) return;
        _fogKey = key;
        _fogMesh.ClearSurfaces();
        if (!enabled) return;
        var visible = sim.Vision.PlayerVisibleCells;
        bool any = false;
        foreach (var cell in sim.Map.Cells)
        {
            if (visible.Contains(cell.Coord)) continue;
            if (!any)
            {
                _fogMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, Mats.VertexColor(unshaded: true, transparent: true));
                any = true;
            }
            float y = Math.Max(Board.SurfaceY(cell), Board.TopY(cell)) + 0.015f;
            var col = new Color(0.03f, 0.04f, 0.06f, 0.42f);
            var center = Board.Center(cell.Coord, y);
            for (int i = 0; i < 6; i++)
            {
                _fogMesh.SurfaceSetColor(col);
                _fogMesh.SurfaceAddVertex(center);
                _fogMesh.SurfaceSetColor(col);
                _fogMesh.SurfaceAddVertex(Board.Corner(cell.Coord, i, y, 1.0f));
                _fogMesh.SurfaceSetColor(col);
                _fogMesh.SurfaceAddVertex(Board.Corner(cell.Coord, (i + 1) % 6, y, 1.0f));
            }
        }
        if (any) _fogMesh.SurfaceEnd();
    }

    // ------------------------------------------------------------------ floating text

    public void FloatText(Vector3 pos, string text, Color color, float size = 1f, float life = 1.3f)
    {
        var l = new Label3D
        {
            Text = text,
            FontSize = (int)(46 * size),
            PixelSize = 0.006f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Modulate = color,
            OutlineSize = 10,
            OutlineModulate = new Color(0, 0, 0, 0.85f),
            Position = pos + new Vector3((float)GD.RandRange(-0.15, 0.15), 1.5f, 0),
            RenderPriority = 20,
            OutlineRenderPriority = 19,
        };
        _fx.AddChild(l);
        _floating.Add((l, 0, life));
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        Overlay.WidthScale = Math.Clamp(Rig.Distance / Math.Max(1f, Rig.StandardDistance), 1f, 3f);
        for (int i = _floating.Count - 1; i >= 0; i--)
        {
            var (l, age, life) = _floating[i];
            age += dt;
            if (age >= life)
            {
                l.QueueFree();
                _floating.RemoveAt(i);
                continue;
            }
            l.Position += new Vector3(0, dt * 0.7f, 0);
            float a = age < life * 0.7f ? 1 : 1 - (age - life * 0.7f) / (life * 0.3f);
            l.Modulate = l.Modulate with { A = a };
            l.OutlineModulate = l.OutlineModulate with { A = a * 0.85f };
            _floating[i] = (l, age, life);
        }
    }

    public Vector3 UnitPos(Unit u) => _views.TryGetValue(u.Id, out var v) ? v.Position : (_map != null ? Board.Center(u.Pos, Board.TopY(_map.Get(u.Pos))) : Vector3.Zero);
}
