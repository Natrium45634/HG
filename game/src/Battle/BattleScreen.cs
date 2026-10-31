using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.App;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

public enum InputMode
{
    None,
    Move,
    Turn,
    Attack,
    Skill,
}

/// <summary>
/// Battle screen: drives the simulation in fixed ticks (pause/speeds), turns player input into
/// commands, and draws previews (paths, areas, odds), queue markers, telegraphs and the HUD.
/// It never changes unit state directly — everything goes through the command API.
/// </summary>
public partial class BattleScreen : Node
{
    private ScenarioDef _scenario;
    private ContentDatabase _db;
    private readonly bool _admin;
    // The start frozen (docs/DECISIONS_5.md §86): «начать заново» returns exactly here; the player's calls are recorded.
    private BattleSnapshot? _snapshot;
    public BattleSnapshot? Snapshot => _snapshot;
    public BattleReplay? Replay { get; private set; }
    private readonly Action? _onExit;
    public Action? OnExit => _onExit;

    public Simulation Sim { get; private set; } = null!;
    public SimDriver Driver { get; private set; } = null!;
    public BoardView Board { get; private set; } = null!;
    private BattleHud _hud = null!;
    private LabelPool _labels = null!;

    public Unit? Selected { get; private set; }
    public InputMode Mode { get; private set; }
    public SkillDef? ModeSkill { get; private set; }
    /// <summary>Insertion index for new commands (-1 = append to the end of the queue).</summary>
    public int InsertIndex { get; private set; } = -1;

    private Cell? _hover;
    private Unit? _hoverUnit;
    private Vector2 _mouse;
    private Dictionary<HexCoord, double>? _reach;
    private (HexCoord From, double Ap, int Ver, long Tick)? _reachKey;
    private List<ProjectedCommand> _proj = new();
    private (int Unit, string Key, long Tick)? _projKey;
    private readonly Dictionary<int, List<HexCoord>> _trails = new();
    // Presentation of a series (docs/DECISIONS_3.md §47): resolved stages since the unit's last release, and hit
    // numbers held back until the stage's projectile arrives (the log is never delayed).
    private readonly Dictionary<int, int> _stagesShown = new();
    private readonly List<(double At, SimEvent Event)> _lagged = new();
    private double _clock;
    /// <summary>Hit presentations waiting for their projectile (UI tests).</summary>
    public int LaggedHits => _lagged.Count;
    private SkillPreview? _skillPreview;
    private string _hint = "";
    private bool _diag;
    private long _lastAreaTick;

    public bool Admin => _admin;
    public BattleHud Hud => _hud;
    public bool DiagnosticsVisible => _diag;
    public ContentDatabase Db => _db;
    public Settings Settings => Main.Instance.Settings;
    public string Hint => _hint;
    public List<ProjectedCommand> Projection => _proj;

    /// <summary>«Небезопасный тест» (ТЗ ч. 5, 26.4): started despite errors of the check; a failing tick pauses the battle.</summary>
    public bool Unsafe { get; init; }
    public List<string> UnsafeErrors { get; init; } = new();

    /// <summary>A battle from a snapshot: «начать заново».</summary>
    public BattleScreen(BattleSnapshot snapshot, bool admin, Action? onExit) : this(snapshot.Content.Get<ScenarioDef>(snapshot.Scenario)!, snapshot.Content, admin, onExit) =>
        _snapshot = snapshot;

    /// <summary>The squad chosen before the battle (part 6, §96); null — the scenario's default.</summary>
    public IReadOnlyList<string>? Roster { get; init; }

    public BattleScreen(ScenarioDef scenario, ContentDatabase db, bool admin, Action? onExit)
    {
        _scenario = scenario;
        _db = db;
        _admin = admin;
        _onExit = onExit;
        Name = "Battle";
    }

    public override void _Ready()
    {
        ulong? seed = Main.Instance.Args.TryGetValue("seed", out var s) && ulong.TryParse(s, out var v) ? v : null;
        if (_snapshot == null)
        {
            (_snapshot, var sim) = BattleSnapshot.Begin(_db, _scenario, seed, _admin, roster: Roster);
            Sim = sim;
            _db = _snapshot.Content;
            _scenario = _db.Get<ScenarioDef>(_snapshot.Scenario)!;
        }
        else if (Loaded != null)
        {
            // A saved battle (part 6, T28): played back to its tick — the report hears every event on the way.
            var res = Loaded.Resume(AttachReport);
            Sim = res.Sim ?? _snapshot.Restore();
            Sim.Input.Journal = Loaded.Entries;
            LoadText = res.Matches ? $"Бой загружен: {Ui.FormatTime(Sim.Time)}" : "Бой загружен, но состояние отличается от сохранённого: " + res.Text;
        }
        else Sim = _snapshot.Restore();
        Replay = Loaded ?? BattleReplay.Record(_snapshot, Sim);
        if (Unsafe)
            Sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"НЕБЕЗОПАСНЫЙ ТЕСТ: проигнорировано ошибок проверки — {UnsafeErrors.Count}" +
                (UnsafeErrors.Count > 0 ? ": " + string.Join("; ", UnsafeErrors.Take(6)) : ""));
        // Events raised while setting up (e.g. enemies visible at start) must not pause the first tick.
        Sim.ConsumeAutopause();
        Driver = new SimDriver(Sim) { CatchErrors = Unsafe };
        Driver.Failed += e => _hud.ShowBanner("Небезопасный тест: ошибка симуляции — бой на паузе. " + e, Ui.Bad, 8f);
        Driver.Autopaused += r => _hud.ShowBanner("Автопауза: " + r, Ui.Warn);
        if (Main.Instance.Args.TryGetValue("speed", out var sp) && int.TryParse(sp, out var si))
        {
            Driver.SetSpeedIndex(si);
            Driver.Paused = false;
        }
        InitAutobattle();
        Board = new BoardView();
        AddChild(Board);
        Board.ApplySettings(Settings);
        Board.Bind(Sim.Map, _db);
        InitControl();
        _labels = new LabelPool { Name = "Labels" };
        Board.AddChild(_labels);
        _hud = new BattleHud(this);
        var layer = new CanvasLayer { Layer = 1 };
        layer.AddChild(_hud);
        AddChild(layer);
        if (Loaded != null)
        {
            Driver.Paused = true;
            _hud.ShowBanner(LoadText, LoadText.StartsWith("Бой загружен:") ? Ui.Good : Ui.Warn, 5f);
        }
        if (Unsafe) _hud.ShowBanner($"НЕБЕЗОПАСНЫЙ ТЕСТ: проигнорировано ошибок проверки — {UnsafeErrors.Count} (см. журнал)", Ui.Bad, 8f);

        Board.Rig.InsetsProvider = _hud.WorkInsets;
        var first = Sim.Units.FirstOrDefault(u => Sim.CanPlayerCommand(u));
        if (first != null) Select(first, byPlayer: false);
        // The HUD gets its final layout after a couple of frames; fit the camera then.
        _initialCameraFrames = 3;
        _initialCameraUnit = first;
        _diag = _admin;

        if (Main.Instance.Args.TryGetValue("run", out var run) && double.TryParse(run, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs))
        {
            // Automation: advance the battle before the screenshot.
            Sim.Rules.Autopause.EnemySpotted = false;
            for (long i = 0; i < Sim.TicksFor(secs); i++) Sim.Step();
            Sim.ConsumeAutopause();
        }
        if (Main.Instance.Args.TryGetValue("cast", out var cast)) AutomationCast(cast);
        if (Main.Instance.Args.TryGetValue("mode", out var mode)) ApplyAutomationMode(mode);
        if (Main.Instance.Args.TryGetValue("pick", out var pick))
        {
            // Automation: select the creature on a hex (enemies too — shows the information panel).
            Unit? who;
            if (char.IsLetter(pick[0]))
            {
                who = Sim.Units.Where(o => o.Alive && o.Def.Id == pick && UnitVisible(o)).OrderBy(o => o.Id).FirstOrDefault();
            }
            else
            {
                var p = pick.Split(',');
                var cell = Sim.Map.Get(new HexCoord(int.Parse(p[0]), int.Parse(p[1])));
                who = cell?.Occupants.Where(o => o.Alive && UnitVisible(o)).OrderBy(o => o.Id).FirstOrDefault();
            }
            if (who != null)
            {
                Select(who);
                _stackOpen = StackOf(who).Count > 1;
            }
        }
        if (Main.Instance.Args.TryGetValue("focus", out var f))
        {
            var p = f.Split(',');
            Board.Rig.FocusOn(Render.Board.Center(new HexCoord(int.Parse(p[0]), int.Parse(p[1]))));
        }
        if (Main.Instance.Args.TryGetValue("zoom", out var z) && float.TryParse(z, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dist))
            Callable.From(() => Board.Rig.SetZoom(dist)).CallDeferred();
    }

    /// <summary>Automation: --cast=skill:q,r[;skill:q,r…] — the first unit knowing the skill uses it, then the battle runs --after seconds.</summary>
    private void AutomationCast(string spec)
    {
        foreach (var part in spec.Split(';'))
        {
            var kv = part.Split(':');
            var sk = _db.Skill(kv[0]);
            var u = Sim.Units.FirstOrDefault(x => x.Alive && Sim.IsPlayerFaction(x.FactionId) && x.KnowsSkill(kv[0]));
            if (sk == null || u == null) continue;
            var xy = kv[1].Split(',');
            var c = new HexCoord(int.Parse(xy[0]), int.Parse(xy[1]));
            var target = Sim.Map.Get(c)?.Occupant;
            var cmd = sk.Targeting switch
            {
                TargetingMode.Unit => Command.Skill(sk.Id, null, target?.Id ?? -1),
                TargetingMode.Cell => Command.Skill(sk.Id, c),
                _ => Command.Skill(sk.Id, null, -1, u.Pos.DirectionTo(c)),
            };
            if (sk == u.BasicAttack && target != null) cmd = Command.Attack(target.Id);
            var err = Sim.Input.Enqueue(u, cmd);
            if (err != null) GD.Print("cast: " + err);
        }
        if (Main.Instance.Args.TryGetValue("after", out var a) && double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs))
        {
            Sim.Rules.Autopause.EnemySpotted = false;
            for (long i = 0; i < Sim.TicksFor(secs); i++) Sim.Step();
            Sim.ConsumeAutopause();
        }
    }

    private void ApplyAutomationMode(string mode)
    {
        if (Selected == null) return;
        if (mode == "move") EnterMode(InputMode.Move);
        else if (mode.StartsWith("skill:"))
        {
            var sk = _db.Skill(mode[6..]);
            var u = Sim.Units.FirstOrDefault(x => Sim.CanPlayerCommand(x) && x.KnowsSkill(mode[6..]));
            if (u != null && sk != null)
            {
                Select(u);
                EnterSkill(sk);
            }
        }
        if (Main.Instance.Args.ContainsKey("radial"))
        {
            // Screenshots: the skill menu against the nearest visible enemy.
            var enemy = Sim.Units.Where(e => e.Alive && Sim.IsHostile(Selected, e) && UnitVisible(e)).OrderBy(e => HexCoord.Distance(e.Pos, Selected.Pos)).FirstOrDefault();
            OpenSkillMenu(enemy, GetViewport().GetVisibleRect().Size / 2);
        }
        if (Main.Instance.Args.TryGetValue("hover", out var h))
        {
            var parts = h.Split(',');
            var c = Sim.Map.Get(new HexCoord(int.Parse(parts[0]), int.Parse(parts[1])));
            if (c != null) _forcedHover = c;
        }
        if (Main.Instance.Args.ContainsKey("plan"))
        {
            // Demo queue: move → attack/skill → move → defend (for screenshots).
            var u = Selected;
            var enemy = Sim.Units.FirstOrDefault(e => Sim.IsHostile(u, e));
            Sim.Input.Enqueue(u, Command.Move(u.Pos + new HexCoord(3, 0)));
            Sim.Input.Enqueue(u, Command.Turn(HexDirection.NE));
            if (enemy != null) Sim.Input.Enqueue(u, Command.Move(u.Pos + new HexCoord(5, -1)));
            Sim.Input.Enqueue(u, Command.Defend());
        }
    }

    private Cell? _forcedHover;

    // ------------------------------------------------------------------ selection & modes

    public void Select(Unit? u) => Select(u, byPlayer: true);

    /// <summary>
    /// Selects a unit (null — clears). A repeated selection of the same unit changes nothing and never
    /// touches the time; a new selection by the player may pause the game (docs/DECISIONS_3.md §39).
    /// </summary>
    public void Select(Unit? u, bool byPlayer)
    {
        if (u == Selected && u != null && !GroupActive) return;
        CancelGesture();
        ClearGroup();
        if (Selected != null && Board.Views.TryGetValue(Selected.Id, out var old))
        {
            old.Selected = false;
            old.PreviewYaw = null;
        }
        Selected = u;
        _stackOpen = false;
        if (u != null) Board.ViewOf(u).Selected = true;
        ExitMode();
        _hud.OnSelectionChanged();
        if (u == null) OnPlanningEnded();
        else if (byPlayer && Sim.CanPlayerCommand(u)) PlanningPause("выбран " + u.Name, s => s.PauseOnSelect);
    }

    public void ExitMode()
    {
        Mode = InputMode.None;
        ModeSkill = null;
        InsertIndex = -1;
        _skillPreview = null;
        _hint = "";
        _hud?.OnModeChanged();
    }

    public void EnterMode(InputMode mode, int insertIndex = -1)
    {
        if (Selected == null || !Sim.CanPlayerCommand(Selected)) return;
        Mode = mode;
        ModeSkill = mode == InputMode.Attack ? Selected.BasicAttack : null;
        InsertIndex = insertIndex;
        if (mode is InputMode.Move or InputMode.Attack) PlanningPause(mode == InputMode.Move ? "выбор точки" : "выбор цели", s => s.PauseOnTargeting);
        _hint = mode switch
        {
            InputMode.Move => "Движение: ЛКМ — точка маршрута (можно несколько), ПКМ/Esc — готово. Shift — " + (Settings.AppendByDefault ? "заменить очередь" : "добавить в очередь"),
            InputMode.Turn => "Поворот: укажите клетку в нужном направлении",
            InputMode.Attack => "Атака: ЛКМ по врагу. Показаны шансы попадания и обнаружения",
            _ => "",
        };
        _hud.OnModeChanged();
    }

    public void EnterSkill(SkillDef skill, int insertIndex = -1)
    {
        if (Selected == null || !Sim.CanPlayerCommand(Selected)) return;
        Mode = InputMode.Skill;
        ModeSkill = skill;
        InsertIndex = insertIndex;
        PlanningPause("выбор цели навыка", s => s.PauseOnTargeting);
        _hint = skill.Targeting switch
        {
            TargetingMode.Unit => $"«{skill.DisplayName}»: ЛКМ по цели",
            TargetingMode.Cell => $"«{skill.DisplayName}»: ЛКМ по клетке в пределах дальности",
            TargetingMode.Direction => $"«{skill.DisplayName}»: наведите в нужном направлении и нажмите ЛКМ",
            _ => $"«{skill.DisplayName}»: ЛКМ — применить (направление — по курсору)",
        };
        _hud.OnModeChanged();
    }

    /// <summary>Activates an action from the menu/hotkey (respecting availability).</summary>
    public void Activate(ActionInfo a, int insertIndex = -1)
    {
        if (Selected == null) return;
        if (!a.Enabled)
        {
            _hud.ShowBanner($"{a.Label}: недоступно — {a.Reason}", Ui.Bad);
            return;
        }
        switch (a.Key)
        {
            case "move":
                EnterMode(InputMode.Move, insertIndex);
                break;
            case "turn":
                EnterMode(InputMode.Turn, insertIndex);
                break;
            case "attack":
                EnterMode(InputMode.Attack, insertIndex);
                break;
            case "defend":
                InsertIndex = insertIndex;
                Submit(Command.Defend());
                InsertIndex = -1;
                break;
            case "wait":
                InsertIndex = insertIndex;
                Submit(Command.Wait(2));
                InsertIndex = -1;
                break;
            case "queue":
                _hud.FocusQueue();
                break;
            case "info":
                _hud.ToggleInfo();
                break;
            default:
                if (a.Skill != null)
                {
                    if (a.Skill.Targeting == TargetingMode.Self && a.Skill.Stages.All(s => s.Area.Shape is AreaShape.Single or AreaShape.Radius or AreaShape.Ring or AreaShape.Cross or AreaShape.Global))
                    {
                        InsertIndex = insertIndex;
                        Submit(Command.Skill(a.Skill.Id, null));
                        InsertIndex = -1;
                    }
                    else
                    {
                        EnterSkill(a.Skill, insertIndex);
                    }
                }
                break;
        }
        if (!string.IsNullOrEmpty(a.Warning)) _hud.ShowBanner(a.Warning, Ui.Warn);
    }

    public (HexCoord Pos, HexDirection Facing) PlanStart()
    {
        if (Selected == null) return (HexCoord.Zero, HexDirection.E);
        if (InsertIndex < 0)
            return _proj.Count == 0 ? (Selected.StepTo ?? Selected.Pos, Selected.Facing) : (_proj[^1].EndPos, _proj[^1].EndFacing);
        int i = Math.Min(InsertIndex - 1, _proj.Count - 1);
        if (i < 0) return (Selected.StepTo ?? Selected.Pos, Selected.Facing);
        return (_proj[i].EndPos, _proj[i].EndFacing);
    }

    private bool Replace => Input.IsKeyPressed(Key.Shift) == Settings.AppendByDefault;

    private void Submit(Command c)
    {
        var u = Selected;
        if (u == null) return;
        string? err;
        if (InsertIndex >= 0)
        {
            err = Sim.Input.Enqueue(u, c, InsertIndex);
            if (err == null) InsertIndex++;
        }
        else
        {
            err = Sim.Input.Enqueue(u, c, -1, Replace);
        }
        _projKey = null;
        if (err != null) _hud.ShowBanner("Команда не принята: " + err, Ui.Bad);
        else OnOrderConfirmed();
        _hud.OnQueueChanged();
    }

    // ------------------------------------------------------------------ frame

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (_initialCameraFrames > 0 && --_initialCameraFrames == 0) InitialCamera(_initialCameraUnit);
        Driver.Advance(delta);
        // A breakpoint already paused the driver (its reason is in the autopause banner); it may fire again.
        Breaks.Consume();
        _clock += delta;
        var batch = Sim.DrainEvents();
        var lag = ProjectileLag(batch);
        for (int i = 0; i < batch.Count; i++)
        {
            if (lag.TryGetValue(i, out double d)) OnSimEvent(batch[i], d);
            else OnSimEvent(batch[i]);
        }
        for (int i = 0; i < _lagged.Count; i++)
            if (_lagged[i].At <= _clock)
            {
                PresentEvent(_lagged[i].Event);
                _lagged.RemoveAt(i--);
            }

        bool fog = Sim.FogOfWar && !_admin;
        Board.SyncMap(c => !fog || Sim.Vision.IsCellVisibleToPlayer(c.Coord));
        UpdateBlockCaptions();
        Board.TimeScale = (float)Driver.Speed;
        Board.SyncUnits(Sim, Driver.Alpha, UnitVisible, _ => false, dt);
        Board.Objects.Sync(_scenario, Sim.Map, _db, o => Sim.Interactive.CurrentState(o.Id) is { Color.Length: > 0 } st ? Ui.Parse(st.Color, Colors.White) : null);
        Board.SetNight(Sim.Scenario.Variable("time_of_day") == "night");
        foreach (var v in Board.Views.Values) v.ShowName = _diag && v.Unit.AiControlled;
        Board.SyncFog(Sim, fog);
        Board.Effects.UpdateVisibility(c => !fog || Sim.Vision.IsCellVisibleToPlayer(c.Coord));

        UpdateProjection();
        UpdateSkillPress();
        UpdateHover();
        UpdateArrows();
        DrawOverlay();
        _hud.Refresh();
    }

    public bool UnitVisible(Unit u)
    {
        if (_admin || !Sim.FogOfWar) return true;
        if (Sim.IsPlayerFaction(u.FactionId) || Sim.IsPlayerFaction(u.BaseFactionId)) return true;
        return Sim.Vision.IsVisibleToPlayer(u);
    }

    /// <summary>Events the screen received from the simulation (UI tests: none may be lost at any speed).</summary>
    public long EventsSeen { get; private set; }

    private void OnSimEvent(SimEvent e, double lag = 0)
    {
        EventsSeen++;
        bool visible = e.PlayerVisible || _admin;
        _hud.AppendLog(e, visible);
        if (!visible) return;
        if (lag > 0) _lagged.Add((_clock + lag, e));
        else PresentEvent(e);
    }

    /// <summary>
    /// The hits of a stage with a flying projectile (docs/DECISIONS_3.md §47): their numbers, flashes and hit effects
    /// wait for the projectile to arrive — index in the batch → delay, s. Only the look waits; the battle has already
    /// counted the damage.
    /// </summary>
    private Dictionary<int, double> ProjectileLag(List<SimEvent> batch)
    {
        var lag = new Dictionary<int, double>();
        for (int j = 0; j < batch.Count; j++)
        {
            var e = batch[j];
            if (e.Type != SimEventType.StageResolved || StageOf(e) is not { } st || !st.Projectile.Enabled) continue;
            var src = Sim.GetUnit(e.SourceUnit);
            if (src == null) continue;
            var (from, to, _) = StageEnds(e, src);
            double flight = Render.Vfx.FlightTime(st.Projectile, from, to, Driver.Speed);
            if (flight <= 0) continue;
            for (int i = j - 1; i >= 0; i--)
            {
                var h = batch[i];
                if (h.Type is SimEventType.StageResolved or SimEventType.CastReleased && h.SourceUnit == e.SourceUnit) break;
                if (h.SourceUnit == e.SourceUnit && h.Type is SimEventType.Damage or SimEventType.Miss or SimEventType.Block or SimEventType.Crit
                        or SimEventType.Surprise or SimEventType.Heal or SimEventType.StatusApplied)
                    lag[i] = flight;
            }
        }
        return lag;
    }

    private SkillStageDef? StageOf(SimEvent e)
    {
        var skill = Sim.Content.Skill(e.DamageType);
        int idx = e.Value >= 0 ? (int)Math.Round(e.Value) : -(int)Math.Round(e.Value) - 1;
        return skill != null && idx >= 0 && idx < skill.Stages.Count ? skill.Stages[idx] : null;
    }

    /// <summary>Where a stage's look starts (the caster's projectile point) and ends (the target or the anchor cell).</summary>
    private (Vector3 From, Vector3 To, Vector3 Cell) StageEnds(SimEvent e, Unit src)
    {
        var from = Board.ViewOf(src).AttachmentPos(AttachmentPoint.Projectile);
        var target = Sim.GetUnit(e.TargetUnit);
        var anchor = e.Cell ?? src.Pos;
        var cell = Render.Board.Center(anchor, Render.Board.TopY(Sim.Map.Get(anchor)) + 0.05f);
        var to = target != null ? Board.UnitPos(target) + new Vector3(0, 0.45f, 0) : cell + new Vector3(0, 0.2f, 0);
        return (from, to, cell);
    }

    private void PresentEvent(SimEvent e)
    {
        if (NoEffects) return;
        PlayEventVisuals(e);
        if (!Settings.FloatingNumbers) return;
        var target = Sim.GetUnit(e.TargetUnit);
        Vector3? pos = target != null ? Board.UnitPos(target) : e.Cell.HasValue ? Render.Board.Center(e.Cell.Value, Render.Board.TopY(Sim.Map.Get(e.Cell.Value))) : null;
        if (pos == null) return;
        switch (e.Type)
        {
            case SimEventType.Damage when e.Value > 0:
                Board.FloatText(pos.Value, $"-{e.Value:0}", Ui.Parse(Sim.Damage.Taxonomy.ColorOf(e.DamageType), Ui.Bad).Lightened(0.1f), 1.1f);
                if (target != null) Board.ViewOf(target).Flash();
                break;
            case SimEventType.Heal:
                Board.FloatText(pos.Value, $"+{e.Value:0}", Ui.Good, 1.05f);
                break;
            case SimEventType.Miss:
                Board.FloatText(pos.Value, "промах", Ui.TextDim, 0.8f);
                break;
            case SimEventType.Block:
                Board.FloatText(pos.Value + new Vector3(0, 0.25f, 0), "блок", new Color(0.7f, 0.85f, 1f), 0.8f);
                break;
            case SimEventType.Crit:
                Board.FloatText(pos.Value + new Vector3(0, 0.45f, 0), "КРИТ!", Ui.Accent, 1.0f);
                break;
            case SimEventType.Surprise:
                Board.FloatText(pos.Value + new Vector3(0, 0.65f, 0), "неожиданно!", new Color(1f, 0.55f, 0.3f), 0.85f);
                break;
            case SimEventType.CastInterrupted:
                var src = Sim.GetUnit(e.SourceUnit);
                if (src != null) Board.FloatText(Board.UnitPos(src), "прервано", Ui.Warn, 0.85f);
                break;
            case SimEventType.StatusApplied when e.Level == LogLevel.Normal && target != null:
                Board.FloatText(pos.Value + new Vector3(0, 0.3f, 0), e.Text.Contains('«') ? e.Text.Substring(e.Text.IndexOf('«')) : "состояние", new Color(0.85f, 0.75f, 1f), 0.7f);
                break;
            case SimEventType.Synergy when e.Cell.HasValue:
                Board.FloatText(pos.Value, "синергия!", new Color(1f, 0.85f, 0.4f), 0.9f, 1.6f);
                break;
            case SimEventType.Moved when e.SourceUnit >= 0 && e.Cell.HasValue:
            {
                if (!_trails.TryGetValue(e.SourceUnit, out var t)) _trails[e.SourceUnit] = t = new List<HexCoord>();
                t.Add(e.Cell.Value);
                if (t.Count > 24) t.RemoveAt(0);
                break;
            }
        }
    }

    /// <summary>Visual effects and sounds of events (docs/DECISIONS_2.md §34) — never part of the battle logic.</summary>
    private void PlayEventVisuals(SimEvent e)
    {
        var db = Sim.Content;
        var visuals = Sim.Rules.Visuals;
        var src = Sim.GetUnit(e.SourceUnit);
        var target = Sim.GetUnit(e.TargetUnit);
        Vector3 CellPos(HexCoord c) => Render.Board.Center(c, Render.Board.TopY(Sim.Map.Get(c)) + 0.05f);
        Vector3? at = target != null ? Board.UnitPos(target) : e.Cell.HasValue ? CellPos(e.Cell.Value) : src != null ? Board.UnitPos(src) : null;
        void Fx(string id, Vector3? p)
        {
            if (string.IsNullOrEmpty(id) || p == null) return;
            Render.Vfx.Play(Board, db.Get<VfxDef>(id), p.Value, db);
        }
        void Snd(string id, Vector3? p)
        {
            if (!string.IsNullOrEmpty(id) && p != null) Render.Assets.PlayAt(Board, db.Get<AssetDef>(id), p.Value);
        }
        switch (e.Type)
        {
            case SimEventType.Vfx:
                if (at != null) Render.Vfx.Play(Board, db.Get<VfxDef>(e.Text), at.Value, db); // unknown ID → the default burst
                break;
            case SimEventType.Sound:
                Snd(e.Text, at);
                break;
            case SimEventType.CastStarted when src?.Casting != null:
                Fx(src.Casting.Skill.CastVfx, Board.UnitPos(src));
                break;
            case SimEventType.CastReleased when src != null && db.Skill(e.DamageType) is { } skill:
                // The cast is already released here: the skill comes with the event, the point is the event's cell.
                _stagesShown[src.Id] = 0;
                Fx(skill.ImpactVfx, e.Cell.HasValue ? CellPos(e.Cell.Value) : Board.UnitPos(src));
                Snd(string.IsNullOrEmpty(skill.Sound) ? src.Def.Visual.AttackSound : skill.Sound, Board.UnitPos(src));
                break;
            case SimEventType.StageResolved when src != null && StageOf(e) is { } st:
            {
                // Later hits of a series replay the strike clip from its «проверка попадания» marker.
                int shown = _stagesShown.GetValueOrDefault(src.Id);
                _stagesShown[src.Id] = shown + 1;
                var view = Board.ViewOf(src);
                if (shown > 0) view.ReplayStrike(st.Animation);
                bool hit = e.Value >= 0;
                var (from, to, cell) = StageEnds(e, src);
                if (!hit && target != null) to += (to - from).Normalized() * 0.6f + new Vector3(0, 0.25f, 0);
                Snd(st.Sound, Board.UnitPos(src));
                if (st.Projectile.Enabled)
                {
                    var color = Ui.Parse(db.Skill(e.DamageType)?.Color ?? "", Ui.Accent);
                    Render.Vfx.Projectile(Board, st.Projectile, from, to, cell, color, db, hit, st.Vfx, Driver.Speed);
                }
                else if (hit) Fx(st.Vfx, to);
                break;
            }
            case SimEventType.Damage when e.Value > 0 && target != null:
                Fx(visuals.Hit, at);
                Snd(string.IsNullOrEmpty(target.Def.Visual.HitSound) ? visuals.HitSound : target.Def.Visual.HitSound, at);
                break;
            case SimEventType.Death when (target ?? src) is { } dead:
                Fx(string.IsNullOrEmpty(dead.Def.Visual.DeathVfx) ? visuals.UnitDeath : dead.Def.Visual.DeathVfx, Board.UnitPos(dead));
                Snd(dead.Def.Visual.DeathSound, Board.UnitPos(dead));
                break;
            case SimEventType.UnitSpawned when (target ?? src) is { } born:
                Fx(string.IsNullOrEmpty(born.Def.Visual.SpawnVfx) ? visuals.UnitSpawn : born.Def.Visual.SpawnVfx, Board.UnitPos(born));
                break;
            case SimEventType.CellEffectAdded when e.Cell.HasValue:
            {
                var last = Sim.Map.Get(e.Cell.Value)?.Effects.LastOrDefault();
                Fx(last != null && !string.IsNullOrEmpty(last.Def.AppearVfx) ? last.Def.AppearVfx : visuals.EffectAppear, CellPos(e.Cell.Value));
                break;
            }
            case SimEventType.TrapTriggered:
                Fx(visuals.TrapTriggered, at);
                break;
            case SimEventType.ObstacleDestroyed when e.Cell.HasValue:
                Fx(visuals.ObstacleDestroyed, CellPos(e.Cell.Value));
                break;
            case SimEventType.TerrainChanged when e.Cell.HasValue:
                Fx(visuals.TerrainChanged, CellPos(e.Cell.Value));
                break;
            case SimEventType.Fall:
                Fx(visuals.Fall, at);
                break;
        }
    }

    private void UpdateProjection()
    {
        var u = Selected;
        if (u == null || !u.Alive)
        {
            _proj = new List<ProjectedCommand>();
            return;
        }
        string key = string.Join(",", u.Queue.Items.Select(c => c.Id + ":" + (int)c.Status)) + "|" + u.Pos + u.StepTo;
        if (_projKey is { } k && k.Unit == u.Id && k.Key == key && k.Tick == Sim.Tick) return;
        _proj = Sim.Planner.ProjectQueue(u);
        _projKey = (u.Id, key, Sim.Tick);
    }

    private void UpdateHover()
    {
        bool overUi = _skillPressDragged ? _hud.IsOverUi(_mouse) : _hud.IsMouseOverUi();
        _hover = overUi ? null : Board.PickCell(_mouse);
        if (_forcedHover != null) _hover = _forcedHover;
        _hoverUnit = null;
        if (_hover != null)
        {
            // Several creatures on one hex (docs/DECISIONS_2.md §26): the figure nearest to the cursor.
            float best = float.MaxValue;
            var cam = Board.Rig.Camera;
            foreach (var o in _hover.Occupants)
            {
                if (!o.Alive || !UnitVisible(o)) continue;
                float d = cam.IsInsideTree() ? cam.UnprojectPosition(Board.UnitPos(o) + new Vector3(0, 0.4f, 0)).DistanceTo(_mouse) : 0;
                if (_hoverUnit == null || d < best - 0.5f)
                {
                    best = d;
                    _hoverUnit = o;
                }
            }
        }
        foreach (var v in Board.Views.Values) v.Hovered = _hoverUnit != null && v.Unit == _hoverUnit;
        UpdateDeployFacing();
    }

    /// <summary>Visible living creatures on the hex of a unit (all layers), in a stable order.</summary>
    public List<Unit> StackOf(Unit? u)
    {
        var list = new List<Unit>();
        if (u == null || !u.Alive) return list;
        var cell = Sim.Map.Get(u.Pos);
        if (cell == null) return list;
        foreach (var o in cell.Occupants)
            if (o.Alive && UnitVisible(o))
                list.Add(o);
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>The stack selection strip is open (a shared hex was clicked): Tab cycles inside the stack.</summary>
    public bool StackOpen => _stackOpen && StackOf(Selected).Count > 1;

    private bool _stackOpen;

    public void CycleStack(int dir)
    {
        var stack = StackOf(Selected);
        if (stack.Count < 2) return;
        int i = Selected == null ? -1 : stack.IndexOf(Selected);
        SelectKeepStack(stack[((i + dir) % stack.Count + stack.Count) % stack.Count]);
    }

    public void SelectKeepStack(Unit u)
    {
        Select(u);
        _stackOpen = true;
    }

    /// <summary>Captions over figures: "Блок 2/3" on blockers, "заблокирован" / "удерживается" on the held ones.</summary>
    private void UpdateBlockCaptions()
    {
        var blocking = Sim.Blocking;
        foreach (var v in Board.Views.Values)
        {
            var u = v.Unit;
            v.BlockText = "";
            if (!u.Alive) continue;
            int limit = blocking.Limit(u);
            int load = blocking.Load(u);
            if (limit > 0 && (load > 0 || u == Selected || u == _hoverUnit))
            {
                // Part 6 (ТЗ 4.8): «2/3», and who comes on with no slot left.
                int over = blocking.Overflow(u);
                v.BlockText = $"Блок {load}/{limit}" + (over > 0 ? $" · ещё {over} проходят" : "");
                v.BlockColor = over > 0 ? new Color(1f, 0.4f, 0.3f) : load >= limit ? new Color(1f, 0.62f, 0.3f) : new Color(0.75f, 0.9f, 1f);
                continue;
            }
            if (blocking.Passing(u) && u.Queue.Current?.Type == CommandType.Move)
            {
                v.BlockText = "обходит блок";
                v.BlockColor = new Color(1f, 0.85f, 0.4f);
                continue;
            }
            switch (blocking.HoldOf(u))
            {
                case MovementHold.Blocked:
                    v.BlockText = "заблокирован";
                    v.BlockColor = new Color(1f, 0.62f, 0.3f);
                    break;
                case MovementHold.Held when u.Flags.BlocksMovement || u.Queue.Current?.Type == CommandType.Move:
                    v.BlockText = u.Flags.BlocksMovement ? "удерживается" : "удерживается (замедлен)";
                    v.BlockColor = new Color(0.8f, 0.6f, 1f);
                    break;
            }
        }
    }

    /// <summary>
    /// Spawn warnings (docs/DECISIONS_2.md §25): a white strip one third of a hex side wide above the
    /// surface along the future path; it grows with the enemy's step timing (or runs as a comet) and is
    /// gone one second before the enemy appears. Simulation time drives it: pause stops it, speed-up speeds it.
    /// </summary>
    private void DrawSpawnWarnings(OverlayLayer o)
    {
        var ws = Sim.Scenario.Warnings;
        double now = Sim.Tick + (Driver.Paused ? 0 : Driver.Alpha);
        float width = (float)Sim.Rules.SpawnWarnings.Width * Render.Board.HexSize;
        var col = new Color(1f, 1f, 1f, 0.92f);
        foreach (var w in ws.All)
        {
            if (!ws.IsActive(w, (long)now) || !ws.VisibleToPlayer(w) || w.Path.Count == 0) continue;
            var (from, to) = ws.VisibleSpan(w, now);
            Vector3 P(double cells)
            {
                int i = Math.Clamp((int)Math.Floor(cells), 0, w.Path.Count - 1);
                int j = Math.Min(i + 1, w.Path.Count - 1);
                float f = (float)(cells - i);
                Vector3 A(HexCoord c) => Render.Board.Center(c, Render.Board.TopY(Sim.Map.Get(c)) + 0.09f);
                return A(w.Path[i]).Lerp(A(w.Path[j]), f);
            }
            if (w.Path.Count == 1 || to - from < 1e-3)
            {
                o.Circle(P(0), 0.45f, width * 0.5f, col);
                continue;
            }
            // Segment by segment (partial fog hides the parts over unseen cells).
            double step = 0.25;
            for (double a = from; a < to - 1e-6; a += step)
            {
                double b = Math.Min(to, a + step);
                var cell = w.Path[Math.Clamp((int)Math.Round((a + b) / 2), 0, w.Path.Count - 1)];
                if (!ws.CellVisible(w, cell)) continue;
                o.Strip(P(a), P(b), width, col, true);
            }
            o.Disc(P(to), width * 0.75f, col);
            o.Circle(P(0), 0.45f, 0.05f, new Color(1f, 1f, 1f, 0.6f));
        }
    }

    /// <summary>Objective hexes on the map: defence (blue), capture zone (gold), escort goal (green).</summary>
    private void DrawObjectives(OverlayLayer o)
    {
        foreach (var ob in Sim.Scenario.Objectives)
        {
            if (!ob.Def.ShowOnMap || ob.State is ObjectiveState.Completed or ObjectiveState.Failed) continue;
            if (ob.State == ObjectiveState.Locked && ob.Def.HiddenUntilActive) continue;
            var col = ob.Def.Kind switch
            {
                ObjectiveKind.Defend => new Color(0.4f, 0.75f, 1f, 0.9f),
                ObjectiveKind.Capture => new Color(1f, 0.85f, 0.3f, 0.9f),
                ObjectiveKind.Escort => new Color(0.45f, 1f, 0.55f, 0.9f),
                _ => new Color(0.9f, 0.9f, 0.95f, 0.8f),
            };
            if (ob.State == ObjectiveState.Locked) col.A = 0.35f;
            foreach (var c in Sim.Scenario.CellsOf(ob))
            {
                o.FillHex(c, new Color(col.R, col.G, col.B, 0.16f), 0.92f, 0.04f);
                o.DashedRing(c, col);
            }
            if (ob.Def.Kind is ObjectiveKind.Destroy or ObjectiveKind.KillBoss or ObjectiveKind.Escort)
                foreach (var u in Sim.Units)
                    if (u.Alive && UnitVisible(u) && (string.IsNullOrEmpty(ob.Def.UnitTag) ? ob.Def.Kind == ObjectiveKind.KillBoss && u.Boss.IsBoss : u.Tag == ob.Def.UnitTag))
                        o.Circle(Board.UnitPos(u) + new Vector3(0, 0.05f, 0), 0.62f, 0.05f, ob.Def.Kind == ObjectiveKind.Escort ? col : new Color(1f, 0.35f, 0.3f, 0.9f));
        }
    }

    /// <summary>
    /// Chain between a blocker and every enemy it holds — on request (ТЗ ч. 6, 4.8): of the selected and the hovered unit,
    /// all of them with «Все связи блока» or the diagnostics (F3).
    /// </summary>
    private void DrawBlocking(OverlayLayer o)
    {
        bool all = Settings.AllBlockLinks || _diag;
        foreach (var e in Sim.Units)
        {
            if (!e.Alive || !UnitVisible(e)) continue;
            var b = Sim.Blocking.BlockerOf(e);
            if (b == null || !UnitVisible(b)) continue;
            if (!all && e != Selected && b != Selected && e != _hoverUnit && b != _hoverUnit) continue;
            var a = Board.UnitPos(b) + new Vector3(0, 0.22f, 0);
            var z = Board.UnitPos(e) + new Vector3(0, 0.22f, 0);
            var col = new Color(1f, 0.62f, 0.3f, 0.95f);
            o.Strip(a, z, 0.05f, col, true);
            int links = Math.Max(2, (int)(a.DistanceTo(z) / 0.16f));
            for (int i = 1; i < links; i++) o.Circle(a.Lerp(z, (float)i / links), 0.065f, 0.028f, col, true, 10);
            o.Disc(z, 0.07f, col);
        }
    }

    public Cell? HoverCell => _hover;
    public Unit? HoverUnit => _hoverUnit;
    public PathResult? HoverPath => _gesturePath;
    public SkillPreview? HoverSkillPreview => _skillPreview;

    // ------------------------------------------------------------------ overlay

    private void DrawOverlay()
    {
        var o = Board.Overlay;
        o.Clear();
        _labels.Begin();
        float pulse = o.Pulse(4);

        DrawTelegraphs(o, pulse);
        DrawRecentAreas(o);
        DrawGhosts(o);
        DrawBlocking(o);
        DrawObjectLabels();
        DrawSpawnWarnings(o);
        DrawObjectives(o);
        DrawPlacement(o, pulse);
        DrawDeploy(o, pulse);
        // Enemy routes of the scenario (docs/DECISIONS_4.md §57): faint, so the board stays readable.
        if (_scenario.Routes.Count > 0) Render.ScenarioObjectsView.DrawRoutes(o, _scenario, "", 0.55f);

        if (_diag) DrawAiDebug(o);

        var u = Selected;
        if (GroupActive) DrawGroupPreview(o);
        if (u != null && u.Alive)
        {
            DrawTrail(o, u);
            DrawQueue(o, u);
            if (_gesture is { RouteMode: true } rg)
            {
                DrawRoutePlanning(o, u, rg.Anchor, rg.AnchorFacing);
            }
            else if (_gesture is { Moved: true, Aim: false } tg)
            {
                DrawFacingPreview(o, u, tg);
            }
            else
            {
                switch (Mode)
                {
                    case InputMode.Move:
                    {
                        var (start, facing) = PlanStart();
                        DrawRoutePlanning(o, u, start, facing);
                        break;
                    }
                    case InputMode.Turn:
                        DrawTurnPreview(o, u);
                        break;
                    case InputMode.Attack:
                    case InputMode.Skill:
                        DimMap(o);
                        DrawSkillPreview(o, u);
                        break;
                }
            }
        }
        if (_hover != null) o.RingHex(_hover.Coord, new Color(1, 1, 1, 0.85f), 0.06f, 0.97f);
        o.Commit();
        _labels.End();
    }

    private Vector3 LabelPos(HexCoord c, float lift = 0.5f)
    {
        var cell = Sim.Map.Get(c);
        return Render.Board.Center(c, (cell == null ? 0 : Math.Max(Render.Board.TopY(cell), Render.Board.SurfaceY(cell))) + lift);
    }

    private void DrawTelegraphs(OverlayLayer o, float pulse)
    {
        foreach (var cast in Sim.Skills.ActiveCasts)
        {
            var caster = cast.Caster;
            bool own = Sim.IsPlayerFaction(caster.FactionId);
            bool visible = _admin || own || (cast.VisibleToPlayer && UnitVisible(caster));
            if (!visible || !cast.Telegraphed) continue;
            var col = Ui.Parse(cast.Skill.Color, Ui.Accent);
            bool hostileToPlayer = !own && Sim.Options.PlayerFactions.Any(pf => _db.RelationOf(pf, caster.FactionId) == Relation.Enemy);
            // «Телеграф атаки» of the scenario (part 6, §102): its outline, its timer and text; the area only if it says so.
            if (cast.Rule is { } rule)
            {
                var rc = Ui.Parse(rule.Color, new Color(1f, 0.3f, 0.2f));
                if (rule.ShowArea)
                    foreach (var c in cast.TelegraphCells)
                    {
                        o.FillHex(c, new Color(rc.R, rc.G, rc.B, 0.14f + 0.14f * pulse));
                        o.RingHex(c, new Color(rc.R, rc.G, rc.B, 0.95f), 0.07f);
                    }
                double left = Math.Max(0, (cast.ReleaseTick - Sim.Tick) * Sim.Dt);
                string text = (rule.ShowTimer ? $"{left:0.0} с" : "") + (rule.Text != "" ? (rule.ShowTimer ? " · " : "") + rule.Text : "");
                if (text != "") _labels.Add(LabelPos(cast.Anchor, 1.1f), text, rc, 40);
                var cp = Board.UnitPos(caster) + new Vector3(0, 0.02f, 0);
                o.Circle(cp, 0.66f, 0.07f, new Color(0, 0, 0, 0.5f), true, 40, 1f);
                o.Circle(cp, 0.66f, 0.07f, rc, true, 40, Math.Max(0.02f, (float)cast.Progress(Sim.Tick)));
                continue;
            }
            foreach (var c in cast.TelegraphCells)
            {
                if (hostileToPlayer)
                {
                    o.FillHex(c, new Color(0.9f, 0.2f, 0.15f, 0.12f + 0.12f * pulse));
                    o.HatchHex(c, new Color(1f, 0.35f, 0.25f, 0.55f), phase: (float)Time.GetTicksMsec() / 900f);
                    o.DashedRing(c, new Color(1f, 0.3f, 0.2f, 0.9f));
                }
                else
                {
                    o.FillHex(c, new Color(col.R, col.G, col.B, 0.18f));
                    o.RingHex(c, new Color(col.R, col.G, col.B, 0.7f), 0.05f);
                }
            }
            var p = Board.UnitPos(caster) + new Vector3(0, 0.02f, 0);
            float frac = (float)cast.Progress(Sim.Tick);
            o.Circle(p, 0.66f, 0.07f, new Color(0, 0, 0, 0.5f), true, 40, 1f);
            o.Circle(p, 0.66f, 0.07f, hostileToPlayer ? new Color(1f, 0.35f, 0.25f) : col, true, 40, Math.Max(0.02f, frac));
            if (hostileToPlayer && cast.TelegraphCells.Count > 0)
                _labels.Add(LabelPos(cast.Anchor, 0.9f), "!", new Color(1f, 0.4f, 0.3f), 64);
        }
    }

    private void DrawRecentAreas(OverlayLayer o)
    {
        long window = Sim.TicksPerSecond / 2;
        foreach (var (tick, _, color, cells, visible) in Sim.Skills.RecentAreas)
        {
            if (Sim.Tick - tick > window || (!visible && !_admin)) continue;
            float a = 1f - (float)(Sim.Tick - tick + Driver.Alpha) / window;
            var col = Ui.Parse(color, Ui.Accent);
            foreach (var c in cells) o.FillHex(c, new Color(col.R, col.G, col.B, 0.55f * a), 0.97f, 0.05f);
        }
    }

    private void DrawGhosts(OverlayLayer o)
    {
        if (_admin || !Sim.FogOfWar) return;
        foreach (var (id, pos) in Sim.Vision.LastSeenByPlayer)
        {
            var u = Sim.GetUnit(id);
            if (u == null || !u.Alive || UnitVisible(u)) continue;
            o.DashedRing(pos, new Color(0.8f, 0.8f, 0.85f, 0.7f));
            _labels.Add(LabelPos(pos, 0.45f), "?", new Color(0.85f, 0.85f, 0.9f, 0.85f), 40);
        }
    }

    private void DrawAiDebug(OverlayLayer o)
    {
        foreach (var u in Sim.Units)
        {
            if (!u.Alive || !u.AiControlled) continue;
            var t = Sim.GetUnit(u.Ai.TargetUnitId);
            if (t != null && t.Alive) o.Strip(Board.UnitPos(u) + new Vector3(0, 0.2f, 0), Board.UnitPos(t) + new Vector3(0, 0.2f, 0), 0.04f, new Color(1f, 0.3f, 0.6f, 0.6f), true);
            var cur = u.Queue.Current;
            if (cur?.Type == CommandType.Move && cur.TargetCell.HasValue) o.DashedRing(cur.TargetCell.Value, new Color(1f, 0.3f, 0.6f, 0.8f));
            // Full explanation only for the hovered/selected AI unit (the panel shows it too).
            if (!string.IsNullOrEmpty(u.Ai.Explanation) && (u == _hoverUnit || u == Selected))
                _labels.Add(Board.UnitPos(u) + new Vector3(0, 1.75f, 0), u.Ai.Explanation, new Color(1f, 0.75f, 0.85f), 20);
            else if (t != null && t.Alive)
                _labels.Add(Board.UnitPos(u) + new Vector3(0, 1.6f, 0), "→ " + t.Name, new Color(1f, 0.75f, 0.85f, 0.8f), 18);
        }
    }

    private void DrawTrail(OverlayLayer o, Unit u)
    {
        if (!_trails.TryGetValue(u.Id, out var t)) return;
        for (int i = 0; i < t.Count; i++)
            o.Dot(t[i], new Color(0.75f, 0.75f, 0.78f, 0.12f + 0.3f * i / t.Count), 0.07f, 0.04f, false);
    }

    private static readonly Color RouteColor = new(0.45f, 0.75f, 1f, 0.9f);

    private void DrawQueue(OverlayLayer o, Unit u)
    {
        if (!Sim.CanPlayerCommand(u) && !_admin) return;
        var prevEnd = u.StepTo ?? u.Pos;
        if (u.StepTo.HasValue) o.Line(u.Pos, u.StepTo.Value, RouteColor, 0.08f);
        for (int i = 0; i < _proj.Count; i++)
        {
            var pc = _proj[i];
            var cmd = pc.Command;
            bool insertHere = InsertIndex >= 0 && i == InsertIndex - 1;
            switch (cmd.Type)
            {
                case CommandType.Move:
                {
                    var path = pc.Estimate.Path;
                    var last = pc.StartPos;
                    foreach (var c in path)
                    {
                        o.Line(last, c, RouteColor, 0.075f);
                        last = c;
                    }
                    foreach (var c in path) o.Dot(c, new Color(RouteColor.R, RouteColor.G, RouteColor.B, 0.55f), 0.07f);
                    bool feasible = pc.Estimate.Feasible;
                    o.Disc(LabelPos(pc.EndPos, 0.08f), 0.26f, feasible ? new Color(0.2f, 0.45f, 0.8f, 0.95f) : new Color(0.8f, 0.25f, 0.2f, 0.95f));
                    o.Circle(LabelPos(pc.EndPos, 0.08f), 0.3f, 0.05f, insertHere ? Ui.Accent : Colors.White);
                    break;
                }
                case CommandType.Turn:
                    o.Arrow(pc.EndPos, pc.EndFacing, new Color(1f, 0.9f, 0.5f, 0.95f), 0.85f);
                    break;
                case CommandType.Attack:
                case CommandType.Skill:
                {
                    var target = cmd.TargetUnitId >= 0 ? Sim.GetUnit(cmd.TargetUnitId) : null;
                    var tpos = target?.Pos ?? cmd.TargetCell ?? pc.EndPos;
                    var sk = Sim.Planner.SkillOf(u, cmd);
                    var col = Ui.Parse(sk?.Color, new Color(1f, 0.5f, 0.3f));
                    if (sk != null && (sk.Targeting != TargetingMode.Unit || sk.Stages.Any(s => s.Area.Shape != AreaShape.Single)))
                    {
                        var pv = Sim.Planner.PreviewSkill(u, sk, pc.StartPos, pc.StartFacing, cmd.TargetCell ?? tpos, cmd.TargetUnitId, cmd.Direction);
                        foreach (var c in pv.Cells.Where(c => !c.Blocked)) o.FillHex(c.Cell, new Color(col.R, col.G, col.B, 0.16f));
                    }
                    if (tpos != pc.EndPos) o.Line(pc.EndPos, tpos, new Color(col.R, col.G, col.B, 0.8f), 0.05f);
                    o.RingHex(tpos, new Color(col.R, col.G, col.B, 0.95f), 0.08f, 0.8f);
                    break;
                }
            }
            string label = cmd.Type switch
            {
                CommandType.Move => $"{i + 1}",
                CommandType.Turn => $"{i + 1}↻",
                CommandType.Attack => $"{i + 1}⚔",
                CommandType.Skill => $"{i + 1}★",
                CommandType.Defend => $"{i + 1}▣",
                CommandType.Wait => $"{i + 1}‖",
                _ => $"{i + 1}",
            };
            var color = cmd.Status == CommandStatus.Waiting ? Ui.Warn : cmd.Status == CommandStatus.Active ? Ui.Accent : Colors.White;
            _labels.Add(LabelPos(pc.EndPos, 0.55f + 0.0f), label, color, 36);
            prevEnd = pc.EndPos;
        }
        _ = prevEnd;
    }

    private void DrawTurnPreview(OverlayLayer o, Unit u)
    {
        var (start, facing) = PlanStart();
        HexDirection? hoverDir = _hover != null ? start.DirectionTo(_hover.Coord) : null;
        for (int d = 0; d < 6; d++)
        {
            var dir = (HexDirection)d;
            bool hl = hoverDir == dir;
            o.Arrow(start, dir, hl ? new Color(1f, 0.85f, 0.35f, 1f) : new Color(1, 1, 1, dir == facing ? 0.6f : 0.25f), hl ? 1.05f : 0.9f);
        }
        if (hoverDir.HasValue)
        {
            int steps = HexDirectionExt.StepsBetween(facing, hoverDir.Value);
            _labels.Add(LabelPos(start, 1.0f), $"{hoverDir.Value.RuName()}: {steps * Sim.Rules.Turn.ApPerStep:0.##} ОД", new Color(1f, 0.92f, 0.6f), 28);
        }
    }

    private void DrawSkillPreview(OverlayLayer o, Unit u)
    {
        var skill = ModeSkill;
        _skillPreview = null;
        if (skill == null) return;
        var (start, facing) = PlanStart();
        var col = Ui.Parse(skill.Color, Ui.Accent);
        // Range band.
        if (skill.Targeting is TargetingMode.Unit or TargetingMode.Cell)
        {
            int max = skill.RangeMax + (skill.Ranged ? 3 : 0);
            foreach (var c in HexCoord.Spiral(start, max))
            {
                int d = HexCoord.Distance(start, c);
                if (d < skill.RangeMin || !Sim.Map.Contains(c)) continue;
                int bonus = Sim.Skills.RangeBonus(skill, start, c);
                if (d > skill.RangeMax + bonus) continue;
                o.FillHex(c, new Color(col.R, col.G, col.B, 0.07f), 0.9f, 0.022f);
            }
        }
        // Valid targets ringed, invalid ones crossed with the reason under the cursor (docs/DECISIONS_3.md §43).
        if (skill.Targeting == TargetingMode.Unit)
        {
            int reach = skill.RangeMax + (skill.Ranged ? 3 : 0) + 1;
            foreach (var t in Sim.Units)
            {
                if (!t.Alive || t == u || !UnitVisible(t) || HexCoord.Distance(start, t.Pos) > reach) continue;
                if (!Sim.FilterAccepts(skill.ValidTargets, u, t)) continue;
                var chk = Sim.Skills.Validate(u, skill, t.Pos, t.Id, null, start, facing, checkCosts: false, checkState: false);
                if (chk.Ok) o.RingHex(t.Pos, new Color(0.45f, 1f, 0.55f, 0.85f), 0.07f, 0.74f);
                else _labels.Add(Board.UnitPos(t) + new Vector3(0, 1.45f, 0), "✕", new Color(1f, 0.4f, 0.35f), 30);
            }
        }
        if (_hover == null) return;
        // Line of sight to the cell under the cursor: green, or red up to the obstacle (seen before confirming).
        if (skill.RequiresLoS && skill.Targeting is TargetingMode.Unit or TargetingMode.Cell && _hover.Coord != start)
        {
            var block = Sim.Los.Check(Sim.Map, Sim.Rules, start, _hover.Coord, out var blocker);
            if (block == LosBlock.None) o.Line(start, _hover.Coord, new Color(0.45f, 1f, 0.55f, 0.75f), 0.045f, 0.5f);
            else if (blocker.HasValue)
            {
                o.Line(start, blocker.Value, new Color(1f, 0.35f, 0.3f, 0.85f), 0.05f, 0.5f);
                o.HatchHex(blocker.Value, new Color(1f, 0.35f, 0.3f, 0.7f));
                _labels.Add(LabelPos(blocker.Value, 0.9f), "⛔ " + LineOfSight.Describe(block), new Color(1f, 0.5f, 0.45f), 22);
            }
        }
        HexCoord? targetCell = null;
        int targetUnit = -1;
        HexDirection? dir = null;
        switch (skill.Targeting)
        {
            case TargetingMode.Unit:
                if (_hoverUnit == null) return;
                targetUnit = _hoverUnit.Id;
                targetCell = _hoverUnit.Pos;
                break;
            case TargetingMode.Cell:
                targetCell = _hover.Coord;
                break;
            default:
                dir = start.DirectionTo(_hover.Coord) ?? facing;
                break;
        }
        var pv = Sim.Planner.PreviewSkill(u, skill, start, facing, targetCell, targetUnit, dir);
        _skillPreview = pv;
        foreach (var c in pv.Cells)
        {
            if (c.Blocked)
            {
                o.HatchHex(c.Cell, new Color(0.6f, 0.6f, 0.65f, 0.5f));
                continue;
            }
            o.FillHex(c.Cell, new Color(col.R, col.G, col.B, pv.Valid ? 0.42f : 0.18f));
            o.RingHex(c.Cell, new Color(col.R, col.G, col.B, pv.Valid ? 0.95f : 0.4f), 0.05f);
            if (c.HasMaskEffects) o.Dot(c.Cell, Colors.White, 0.1f);
        }
        foreach (var e in pv.Enemies) o.RingHex(e.Pos, new Color(1f, 0.3f, 0.25f), 0.1f, 0.82f);
        foreach (var a in pv.Allies)
            if (a != u)
            {
                o.RingHex(a.Pos, new Color(1f, 0.65f, 0.15f), 0.1f, 0.82f);
                _labels.Add(Board.UnitPos(a) + new Vector3(0, 1.85f, 0), "⚠ союзник", new Color(1f, 0.7f, 0.25f), 26);
            }
        if (dir.HasValue) o.Arrow(start, dir.Value, new Color(col.R, col.G, col.B, 1f), 1.0f);
        if (!pv.Valid && !string.IsNullOrEmpty(pv.Problem))
            _labels.Add(LabelPos(_hover.Coord, 0.9f), pv.Problem, Ui.Bad, 24);
        foreach (var (t, hit, notice, surprise, sector) in pv.Odds.Take(4))
            _labels.Add(Board.UnitPos(t) + new Vector3(0, 2.05f, 0), $"{hit:P0} · {Names.Of(sector)}" + (surprise > 0 ? $" · неожид. {surprise:P0}" : ""), Colors.White, 24);
    }

    // ------------------------------------------------------------------ input

    public override void _UnhandledInput(InputEvent e)
    {
        if (Board.Rig.HandleInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        switch (e)
        {
            case InputEventMouseMotion mm:
                _mouse = mm.Position;
                _forcedHover = null;
                if (_gesture != null) UpdateGesture(mm.Position);
                if (_boxFrom != null) UpdateBox(mm.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } up:
                _mouse = up.Position;
                if (_gesture != null)
                {
                    UpdateGesture(up.Position);
                    UpdateHover();
                    EndGesture();
                    GetViewport().SetInputAsHandled();
                }
                else if (_boxFrom != null)
                {
                    // No drag: it was a plain click on the ground.
                    if (!EndBox(up.Position) && _boxClick != null)
                    {
                        UpdateHover();
                        OnLeftClick(_boxClick);
                    }
                    _boxClick = null;
                    GetViewport().SetInputAsHandled();
                }
                break;
            case InputEventMouseButton mb when mb.Pressed:
                _mouse = mb.Position;
                _forcedHover = null;
                UpdateHover();
                if (mb.ButtonIndex == MouseButton.Left)
                {
                    UpdateArrows();
                    // Calling a fighter is a mode of its own: no gesture, no box — the click goes to the call (11.5).
                    if (Deploying) DeployClick();
                    else if (GroupMode != null) GroupClick();
                    else if (mb.ShiftPressed && Mode == InputMode.None && _hoverUnit != null && Sim.CanPlayerCommand(_hoverUnit)) ToggleInGroup(_hoverUnit);
                    else if (TryStartGesture(mb.Position)) { }
                    else if (Mode == InputMode.None && _hoverUnit == null)
                    {
                        // The ground: a selection box if the mouse is dragged, otherwise a click on release.
                        BeginBox(mb.Position);
                        _boxClick = mb;
                    }
                    else OnLeftClick(mb);
                }
                else if (mb.ButtonIndex == MouseButton.Right) OnRightClick();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventKey k when k.Pressed && !k.Echo:
                if (OnKey(k)) GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void OnLeftClick(InputEventMouseButton mb)
    {
        if (Deploying)
        {
            DeployClick();
            return;
        }
        if (TryPlacementClick()) return;
        var u = Selected;
        switch (Mode)
        {
            case InputMode.None:
            {
                if (_hoverUnit != null)
                {
                    var stack = StackOf(_hoverUnit);
                    if (stack.Count > 1 && Selected != null && stack.Contains(Selected))
                    {
                        // A repeated click on a shared hex cycles through its creatures.
                        if (_hoverUnit == Selected) CycleStack(1);
                        else SelectKeepStack(_hoverUnit);
                        return;
                    }
                    if (_hoverUnit != Selected) Select(_hoverUnit);
                    _stackOpen = stack.Count > 1;
                    if (Sim.CanPlayerCommand(_hoverUnit) && Settings.ActionMenuOnClick) _hud.ShowActionMenu(mb.Position, -1);
                    return;
                }
                // Click on one of the queue markers opens the waypoint menu.
                if (u != null && _hover != null && Sim.CanPlayerCommand(u))
                {
                    for (int i = _proj.Count - 1; i >= 0; i--)
                    {
                        if (_proj[i].EndPos != _hover.Coord) continue;
                        _hud.ShowWaypointMenu(mb.Position, i);
                        return;
                    }
                }
                if (_hover != null) _hud.ShowCellInfo(_hover);
                break;
            }
            case InputMode.Move:
            {
                if (u == null || _hover == null) return;
                var (start, _) = PlanStart();
                if (_hover.Coord == start) return;
                if (_hoverUnit != null && _hoverUnit != u && Sim.IsHostile(u, _hoverUnit) && UnitVisible(_hoverUnit))
                {
                    // An enemy hex is an attack, never a destination (docs/DECISIONS_3.md §42).
                    OnEnemyPointed(u, _hoverUnit);
                    return;
                }
                var mp = _gesturePath;
                if (mp != null && !mp.Found)
                {
                    if (mp.Nearest.HasValue)
                        _hud.ShowBanner($"Цель недостижима: {mp.Reason}. Ближайшая доступная клетка отмечена зелёным — выберите её, если подходит.", Ui.Warn);
                    else
                        _hud.ShowBanner("Цель недостижима: " + mp.Reason, Ui.Bad);
                    return;
                }
                Submit(PlayerMove(_hover.Coord));
                break;
            }
            case InputMode.Turn:
            {
                if (u == null || _hover == null) return;
                var (start, _) = PlanStart();
                var d = start.DirectionTo(_hover.Coord);
                if (d == null) return;
                Submit(Command.Turn(d.Value));
                ExitMode();
                break;
            }
            case InputMode.Attack:
            case InputMode.Skill:
                ClickSkill();
                break;
        }
    }

    private void ClickSkill()
    {
        var u = Selected;
        var skill = ModeSkill;
        if (u == null || skill == null || _hover == null) return;
        var pv = _skillPreview;
        if (pv != null && !pv.Valid && pv.Reason is FailureReason.Invalid or FailureReason.TargetLost)
        {
            _hud.ShowBanner($"«{skill.DisplayName}»: {pv.Problem}", Ui.Bad);
            return;
        }
        if (pv != null && !pv.Valid) _hud.ShowBanner($"«{skill.DisplayName}»: {pv.Problem} — команда подождёт по правилам очереди", Ui.Warn);
        var (start, facing) = PlanStart();
        Command cmd;
        if (Mode == InputMode.Attack)
        {
            if (_hoverUnit == null || _hoverUnit == u) return;
            cmd = Command.Attack(_hoverUnit.Id);
            cmd.Approach = true;
        }
        else
        {
            switch (skill.Targeting)
            {
                case TargetingMode.Unit:
                    if (_hoverUnit == null) return;
                    cmd = Command.Skill(skill.Id, null, _hoverUnit.Id);
                    cmd.Approach = true;
                    break;
                case TargetingMode.Cell:
                    cmd = Command.Skill(skill.Id, _hover.Coord);
                    cmd.Approach = true;
                    break;
                default:
                    cmd = Command.Skill(skill.Id, null, -1, start.DirectionTo(_hover.Coord) ?? facing);
                    break;
            }
        }
        if (pv != null && pv.Allies.Any(a => a != u) && !Input.IsKeyPressed(Key.Alt))
            _hud.ShowBanner("Внимание: область задевает союзников!", Ui.Warn);
        Submit(cmd);
        int keep = InsertIndex;
        ExitMode();
        if (keep >= 0) InsertIndex = -1;
    }

    private void OnRightClick()
    {
        if (DeployStepBack()) return;
        if (TryInteractClick()) return;
        if (TryPlacementRightClick()) return;
        // During a gesture or planning: cancel / one step back (docs/DECISIONS_3.md §39); otherwise the quick order of part 1.
        if (_gesture != null)
        {
            CancelGesture();
            return;
        }
        if (GroupMode != null)
        {
            CancelGroupMode();
            return;
        }
        if (Mode != InputMode.None)
        {
            ExitMode();
            return;
        }
        if (GroupActive && _hover != null)
        {
            // Quick group orders: RMB on an enemy — attack, on a hex — move with automatic positions.
            var enemy = _hoverUnit != null && Sim.IsHostile(_group[0], _hoverUnit) && UnitVisible(_hoverUnit) ? _hoverUnit : null;
            IssueGroup(enemy != null ? GroupCommand.Attack : GroupCommand.Move, _hover.Coord, enemy, null);
            return;
        }
        var u = Selected;
        if (u == null || !Sim.CanPlayerCommand(u) || _hover == null) return;
        // Quick orders: RMB on an enemy = attack, on a cell = move.
        if (_hoverUnit != null && Sim.IsHostile(u, _hoverUnit) && u.BasicAttack != null)
        {
            Submit(Command.Attack(_hoverUnit.Id));
            return;
        }
        if (_hoverUnit == null)
        {
            var (start, _) = PlanStart();
            var p = Sim.Pathfinder.Find(Sim.Map, MoveContext.For(Sim, u), start, _hover.Coord,
                new PathOptions { IgnoreUnits = start != u.Pos, ComputeNearest = true });
            if (!p.Found)
            {
                _hud.ShowBanner("Цель недостижима: " + p.Reason + (p.Nearest.HasValue ? $" (ближайшая доступная: {p.Nearest})" : ""), Ui.Bad);
                return;
            }
            Submit(PlayerMove(_hover.Coord));
        }
    }

    private InputEventMouseButton? _boxClick;

    private int _initialCameraFrames;
    private Unit? _initialCameraUnit;

    /// <summary>«Вписать карту в экран» (V / button).</summary>
    public void FitMap() => Board.FitCamera();

    /// <summary>
    /// Whole map in view when its hexes stay comfortably large; otherwise the standard zoom
    /// centred on the first unit (big maps). Runs after the HUD has its layout.
    /// </summary>
    private void InitialCamera(Unit? first)
    {
        Board.FitCamera(immediate: true);
        var rig = Board.Rig;
        var vp = GetViewport().GetVisibleRect().Size;
        float hexPx = HexTactics.Core.Geometry.CameraFit.HexWidthPx(new System.Numerics.Vector3(rig.Pivot.X, 0, rig.Pivot.Z), rig.Distance, rig.TiltDeg, rig.YawDeg,
            rig.Camera.Fov, vp.X, vp.Y);
        if (hexPx < 48 * vp.Y / 1080f && first != null)
        {
            rig.ResetStandardZoom();
            rig.FocusOn(Board.UnitPos(first));
        }
    }

    private bool OnKey(InputEventKey k)
    {
        var u = Selected;
        var actions = u != null ? Sim.Planner.Actions(u) : new List<ActionInfo>();
        ActionInfo? A(string key) => actions.FirstOrDefault(a => a.Key == key);
        if (DeployKey(k)) return true;
        switch (k.Keycode)
        {
            case Key.Space:
                TogglePause();
                return true;
            case Key.Z:
            case Key.Minus:
                Driver.Slower();
                return true;
            case Key.X:
            case Key.Equal:
                Driver.Faster();
                return true;
            case Key.Escape:
                // Cancel the operation → leave the planning mode → clear the selection → game menu (docs/DECISIONS_3.md §39).
                if (_gesture != null) CancelGesture();
                else if (_hud.CloseTransient()) { }
                else if (DeployStepBack()) { }
                else if (GroupMode != null) CancelGroupMode();
                else if (GroupActive) ClearGroup();
                else if (Mode != InputMode.None) ExitMode();
                else if (StackOpen) _stackOpen = false;
                else if (Selected != null) Select(null);
                else _hud.ToggleMenu();
                return true;
            case Key.Tab:
                if (StackOpen) CycleStack(k.ShiftPressed ? -1 : 1);
                else CycleUnits(k.ShiftPressed ? -1 : 1);
                return true;
            case Key.M when A("move") is { } a1:
                Activate(a1);
                return true;
            case Key.T when A("turn") is { } a2:
                Activate(a2);
                return true;
            case Key.F when A("attack") is { } a3:
                Activate(a3);
                return true;
            case Key.B when A("defend") is { } a4:
                Activate(a4);
                return true;
            case Key.H when A("wait") is { } a5:
                Activate(a5);
                return true;
            case Key.I:
                _hud.ToggleInfo();
                return true;
            case Key.R when u != null:
                OpenSkillMenu(_hoverUnit != null && Sim.IsHostile(u, _hoverUnit) && UnitVisible(_hoverUnit) ? _hoverUnit : null, _mouse);
                return true;
            case Key.L:
                _hud.ToggleLog();
                return true;
            case Key.O when u != null && Sim.Deployment.Enabled && Sim.Squad.EntryOf(u) != null:
                RetreatSelected();
                return true;
            case Key.F1:
                _hud.ToggleHelp();
                return true;
            case Key.F3:
                _diag = !_diag;
                return true;
            case Key.Period when _admin || Driver.Paused:
                Driver.StepOnce();
                return true;
            case Key.Home:
                if (u != null) Board.Rig.FocusOn(Board.UnitPos(u));
                return true;
            case Key.V:
                FitMap();
                return true;
            case Key.Key0 when k.CtrlPressed:
            case Key.Kp0 when k.CtrlPressed:
                Board.Rig.ResetStandardZoom();
                return true;
            case Key.Backspace:
                UndoLastPoint();
                return true;
            case Key.Delete:
                if (k.ShiftPressed) CancelAll();
                else ClearPendingOrders();
                return true;
        }
        if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9 && (k.CtrlPressed || k.AltPressed))
        {
            // Saved groups: Ctrl+digit — save, Alt+digit — select (digits alone stay skill hotkeys).
            int slot = (int)(k.Keycode - Key.Key1) + 1;
            if (k.CtrlPressed) SaveGroup(slot);
            else RecallGroup(slot);
            return true;
        }
        if (k.Keycode >= Key.Key1 && k.Keycode <= Key.Key9 && u != null)
        {
            int idx = (int)(k.Keycode - Key.Key1);
            var skills = actions.Where(a => a.Key.StartsWith("skill:")).ToList();
            if (idx >= skills.Count) return true;
            var a = skills[idx];
            var sk = a.Skill!;
            // Automatic skills fire by the key; «both»: the first press aims, the second uses it automatically (docs/DECISIONS_3.md §43).
            if (a.Enabled && sk.UseMode == SkillUseMode.Auto) SkillClick(a);
            else if (a.Enabled && sk.UseMode == SkillUseMode.Both && Mode == InputMode.Skill && ModeSkill == sk) SkillClick(a);
            else Activate(a);
            return true;
        }
        return false;
    }

    /// <summary>«↶ Точка»: removes the last element of the plan that is not being executed.</summary>
    public void UndoLastPoint()
    {
        var u = Selected;
        if (u == null || !Sim.CanPlayerCommand(u)) return;
        var last = u.Queue.Items.LastOrDefault(c => c.Status == CommandStatus.Pending);
        if (last == null)
        {
            if (u.Queue.Count > 0) _hud.ShowBanner("Выполняемый приказ отменяется только явно: «Отменить всё» (Shift+Delete)", Ui.TextDim, 2.5f);
            return;
        }
        Sim.Input.RemoveCommand(u, last.Id);
        _projKey = null;
        _hud.OnQueueChanged();
    }

    /// <summary>«Очистить»: removes the orders that are not being executed yet.</summary>
    public void ClearPendingOrders()
    {
        var u = Selected;
        if (u == null || !Sim.CanPlayerCommand(u)) return;
        Sim.Input.ClearPending(u);
        _projKey = null;
        _hud.OnQueueChanged();
    }

    /// <summary>«Отменить всё»: the explicit cancellation, the executing order included.</summary>
    public void CancelAll()
    {
        var u = Selected;
        if (u == null || !Sim.CanPlayerCommand(u)) return;
        Sim.Input.ClearQueue(u);
        _projKey = null;
        _hud.OnQueueChanged();
    }

    /// <summary>Resumes the game (speed buttons): the player takes the pause over from the autopause.</summary>
    public void Resume()
    {
        if (Driver.Paused) OnManualPause(false);
        Driver.Paused = false;
    }

    public void TogglePause()
    {
        Driver.Paused = !Driver.Paused;
        OnManualPause(Driver.Paused);
        _hud.ShowBanner(Driver.Paused ? "Пауза" : $"Скорость {Driver.Speed:0.##}×", Ui.Text, 0.8f);
    }

    public void CycleUnits(int dir)
    {
        var mine = Sim.Units.Where(x => x.Alive && Sim.CanPlayerCommand(x)).ToList();
        if (mine.Count == 0) return;
        int i = Selected == null ? -1 : mine.IndexOf(Selected);
        var next = mine[((i + dir) % mine.Count + mine.Count) % mine.Count];
        Select(next);
        Board.Rig.FocusOn(Board.UnitPos(next));
    }

    public void ExitBattle()
    {
        if (_onExit != null) _onExit();
        else Main.Instance.ShowMainMenu();
    }

    /// <summary>Back to the start: the same content, scenario and seed (the snapshot), the same «небезопасный тест» mark.</summary>
    public void Restart() => Main.Instance.ShowBattle(new BattleScreen(_snapshot!, _admin, _onExit) { Unsafe = Unsafe, UnsafeErrors = UnsafeErrors });

    /// <summary>The replay so far, with the content of the start inside; written to user://replays.</summary>
    public string SaveReplay()
    {
        var copy = Replay!.SaveAt(_snapshot!, Sim);
        var dir = ProjectSettings.GlobalizePath("user://replays");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, $"{_snapshot!.Scenario}_{DateTime.Now:yyyyMMdd_HHmmss}.hexreplay");
        System.IO.File.WriteAllText(path, copy.ToJson());
        return path;
    }
}
