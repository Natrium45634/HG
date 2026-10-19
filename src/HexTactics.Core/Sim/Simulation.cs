using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum BattleOutcome
{
    None,
    Victory,
    Defeat,
}

public sealed class SimOptions
{
    public ulong Seed { get; init; } = 12345;
    /// <summary>Factions controlled by the player (others are AI or passive).</summary>
    public HashSet<string> PlayerFactions { get; init; } = new() { "player" };
    /// <summary>Factions controlled by AI.</summary>
    public HashSet<string> AiFactions { get; init; } = new() { "enemy" };
    /// <summary>Part 1 compatibility: true = everyone may share cells (StackingMode.AllowAll).</summary>
    public bool AllowStacking { get; init; }
    public StackingMode Stacking { get; init; } = StackingMode.ByUnitType;
    public bool? FogOfWar { get; init; }
    /// <summary>Admin/diagnostic mode: the player may command every unit and sees everything.</summary>
    public bool AdminMode { get; init; }
    /// <summary>Rules of a preset chosen by the scenario (null = the global rules of the content).</summary>
    public RulesDef? Rules { get; init; }
}

/// <summary>
/// The battle simulation. Fixed-step, deterministic, independent from rendering.
/// The UI only reads state and calls the command API (Enqueue/Remove/...).
/// See docs/ARCHITECTURE.md §5 for the tick order.
/// </summary>
public sealed class Simulation
{
    private readonly List<Unit> _units = new();
    private readonly Dictionary<int, Unit> _unitsById = new();
    private readonly List<SimEvent> _pending = new();
    private int _nextUnitId = 1;
    private int _nextCommandId = 1;
    private int _eventsThisTick;
    private bool _eventLimitReported;

    public Simulation(ContentDatabase content, MapDef mapDef, SimOptions? options = null)
    {
        Content = content;
        Options = options ?? new SimOptions();
        Rules = Options.Rules ?? content.Rules;
        Rng = new SimRng(Options.Seed);
        TicksPerSecond = Math.Max(1, Rules.Time.TicksPerSecond);
        Dt = 1.0 / TicksPerSecond;
        var issues = new List<ContentIssue>();
        Map = BattleMap.Build(mapDef, content, issues);
        BuildIssues = issues;
        Stacking = Options.AllowStacking ? StackingMode.AllowAll : Options.Stacking;
        FogOfWar = Options.FogOfWar ?? Rules.Vision.FogOfWar;

        Pathfinder = new Pathfinder();
        Los = new LineOfSight();
        Statuses = new StatusSystem(this);
        CellEffects = new CellEffectSystem(this);
        Combat = new CombatSystem(this);
        Damage = new DamagePipeline(this);
        Skills = new SkillSystem(this);
        Commands = new CommandSystem(this);
        Falls = new FallSystem(this);
        Blocking = new BlockingSystem(this);
        Knowledge = new KnowledgeSystem(this);
        Vision = new VisionSystem(this);
        Reactions = new ReactionSystem(this);
        Ai = new AiSystem(this);
        AutoAttacks = new AutoAttackSystem(this);
        Scenario = new ScenarioSystem(this);
        Planner = new Planner(this);

        CellEffects.ApplyIntrinsicEffects();
        foreach (var cd in mapDef.Cells)
        {
            if (cd.Effects == null) continue;
            var cell = Map.Get(cd.C);
            if (cell == null) continue;
            foreach (var e in cd.Effects)
            {
                var def = content.CellEffect(e);
                if (def != null) CellEffects.Add(cell, def, 1, 0, -1, 0, 0, EffectOrigin.MapStart);
            }
        }
    }

    public ContentDatabase Content { get; }
    public RulesDef Rules { get; }
    public SimOptions Options { get; }
    public SimRng Rng { get; }
    public BattleMap Map { get; }
    public List<ContentIssue> BuildIssues { get; }
    public StackingMode Stacking { get; set; }
    public bool FogOfWar { get; set; }

    public Pathfinder Pathfinder { get; }
    public LineOfSight Los { get; }
    public StatusSystem Statuses { get; }
    public CellEffectSystem CellEffects { get; }
    public CombatSystem Combat { get; }
    /// <summary>The unified damage pipeline (docs/DECISIONS_2.md §29).</summary>
    public DamagePipeline Damage { get; }
    public SkillSystem Skills { get; }
    public CommandSystem Commands { get; }
    public FallSystem Falls { get; }
    public BlockingSystem Blocking { get; }
    public KnowledgeSystem Knowledge { get; }
    public VisionSystem Vision { get; }
    public ReactionSystem Reactions { get; }
    public AiSystem Ai { get; }
    /// <summary>Auto-attacks of allies and enemies (docs/DECISIONS_2.md §28).</summary>
    public AutoAttackSystem AutoAttacks { get; }
    public ScenarioSystem Scenario { get; }
    public Planner Planner { get; }

    public CombatLog Log { get; } = new();
    public SimProfiler Profiler { get; } = new();

    public long Tick { get; private set; }
    public int TicksPerSecond { get; }
    public double Dt { get; }
    public double Time => Tick * Dt;

    public BattleOutcome Outcome { get; internal set; }
    public string OutcomeText { get; internal set; } = "";

    /// <summary>Set when an autopause-worthy event happened; the driver pauses and clears it.</summary>
    public string? AutopauseReason { get; private set; }

    public IReadOnlyList<Unit> Units => _units;

    public IEnumerable<Unit> AliveUnits => _units.Where(u => u.Alive);

    public Unit? GetUnit(int id) => _unitsById.TryGetValue(id, out var u) ? u : null;

    public long TicksFor(double seconds)
    {
        if (seconds <= 0) return 0;
        return Math.Max(1, (long)Math.Round(seconds * TicksPerSecond, MidpointRounding.AwayFromZero));
    }

    public bool IsPlayerFaction(string faction) => Options.PlayerFactions.Contains(faction);

    public bool IsPlayerControlled(Unit u) => !u.AiControlled && IsPlayerFaction(u.FactionId);

    public Relation RelationOf(Unit a, Unit b) => a == b ? Relation.Ally : Content.RelationOf(a.FactionId, b.FactionId);

    public bool IsHostile(Unit a, Unit b) => a != b && RelationOf(a, b) == Relation.Enemy;

    /// <summary>Does the filter (relative to source) accept the target unit?</summary>
    public bool FilterAccepts(TargetFilterDef f, Unit? source, Unit target)
    {
        if (source == null) return f.Enemies || f.Neutrals || f.Allies;
        if (source == target) return f.Self;
        var rel = RelationOf(source, target);
        return rel switch
        {
            Relation.Ally => f.Allies,
            Relation.Enemy => f.Enemies,
            _ => f.Neutrals,
        };
    }

    // ------------------------------------------------------------------ units

    public Unit Spawn(UnitDef def, string? faction, HexCoord pos, HexDirection facing, string? name = null, string? tag = null, string? aiProfile = null)
    {
        if (_units.Count(u => u.Alive) >= Rules.Limits.MaxUnits)
            throw new InvalidOperationException($"Достигнут лимит юнитов ({Rules.Limits.MaxUnits})");
        var u = new Unit(_nextUnitId++, def, Content)
        {
            FactionId = string.IsNullOrEmpty(faction) ? def.Faction : faction!,
            Tag = tag ?? "",
        };
        u.BaseFactionId = u.FactionId;
        if (!string.IsNullOrEmpty(name)) u.Name = name!;
        u.Pos = pos;
        u.LastSafePos = pos;
        u.SpawnTick = Tick;
        u.Facing = facing;
        SetLayerInternal(u, LayerResolver.LayerIdOf(Content, def));
        foreach (var sid in def.Skills)
        {
            var s = Content.Skill(sid);
            if (s != null) u.Skills.Add(s);
        }
        u.BasicAttack = Content.Skill(def.BasicAttack);
        u.DefenseSkill = string.IsNullOrEmpty(def.DefenseSkill) ? null : Content.Skill(def.DefenseSkill);
        foreach (var pid in def.Passives)
        {
            var p = Content.Get<PassiveDef>(pid);
            if (p != null) u.Passives.Add(p);
        }
        foreach (var r in def.Resources)
        {
            var rd = Content.Get<ResourceDef>(r.Resource);
            if (rd == null) continue;
            u.Resources[rd.Id] = new ResourceState { Def = rd, Max = r.Max, Current = Math.Min(r.Start, r.Max), Regen = r.Regen };
        }
        u.AiControlled = Options.AiFactions.Contains(u.FactionId) && !Options.PlayerFactions.Contains(u.FactionId);
        var profileId = string.IsNullOrEmpty(aiProfile) ? def.AiProfile : aiProfile;
        u.AiProfile = Content.Get<AiProfileDef>(profileId) ?? Content.All<AiProfileDef>().FirstOrDefault();
        u.Ai.HoldCenter = pos;
        u.Ai.NextDecisionTick = Tick + 1 + (u.Id % 5);

        Statuses.RecalculateModifiers(u);
        u.Hp = u.HpMax;
        u.Ap = u.ApMax;

        _units.Add(u);
        _unitsById[u.Id] = u;
        var cell = Map.Get(pos);
        cell?.Occupants.Add(u);
        foreach (var st in def.StartingStatuses)
        {
            var sd = Content.Status(st.Status);
            if (sd != null) Statuses.Apply(u, sd, st.Amount, st.Duration, -1, force: true);
        }
        Emit(SimEventType.UnitSpawned, LogLevel.Diagnostic, $"{u.Name} появляется в {pos}", source: u.Id, cell: pos);
        Vision.MarkDirty(urgent: true);
        Map.Touch(false);
        return u;
    }

    internal void SetLayerInternal(Unit u, string layer)
    {
        u.Layer = layer;
        u.LayerDef = LayerResolver.Get(Content, layer);
    }

    /// <summary>Moves a unit to another layer (take off, land, burrow, emerge).</summary>
    public void SetLayer(Unit u, string layer, string cause)
    {
        if (u.Layer == layer) return;
        var old = u.LayerDef.DisplayName;
        SetLayerInternal(u, layer);
        Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name}: слой «{old}» → «{u.LayerDef.DisplayName}» — {cause}", u.Id, cell: u.Pos);
        Vision.MarkDirty(urgent: true);
        Map.Touch(false);
        var cell = Map.Get(u.Pos);
        if (cell != null) Falls.CheckCell(u, cell, null, cause);
    }

    public void Remove(Unit u)
    {
        var cell = Map.Get(u.Pos);
        cell?.Occupants.Remove(u);
        if (u.StepTo.HasValue) Map.Get(u.StepTo.Value)?.Reservations.Remove(u);
        _units.Remove(u);
        _unitsById.Remove(u.Id);
        Vision.MarkDirty(urgent: true);
    }

    /// <summary>
    /// Moves a unit to another cell immediately (step completion, push, teleport). Exit effects
    /// of the old cell fire here; entering a pit that affects the unit makes it fall.
    /// Returns false when the unit fell (and died or was removed/returned).
    /// </summary>
    internal bool Relocate(Unit u, HexCoord to, Unit? source = null, string cause = "")
    {
        var from = Map.Get(u.Pos);
        from?.Occupants.Remove(u);
        if (from != null && from.Coord != to) CellEffects.OnUnitExited(u, from);
        bool moved = u.Pos != to;
        u.Pos = to;
        var cell = Map.Get(to);
        if (cell != null)
        {
            cell.Occupants.Add(u);
            cell.Reservations.Remove(u);
        }
        if (moved) Statuses.OnMoved(u);
        Vision.MarkDirty();
        Map.Touch(false);
        if (cell == null) return true;
        if (Falls.CheckCell(u, cell, source, cause)) return false;
        u.LastSafePos = to;
        return true;
    }

    internal int NextCommandId() => _nextCommandId++;

    // ------------------------------------------------------------------ events

    public SimEvent Emit(SimEventType type, LogLevel level, string text, int source = -1, int target = -1, HexCoord? cell = null,
        double value = 0, string damageType = "")
    {
        bool visible = Options.AdminMode || !FogOfWar || IsEventVisibleToPlayer(source, target, cell);
        var e = new SimEvent
        {
            Tick = Tick,
            Time = Time,
            Type = type,
            Level = level,
            SourceUnit = source,
            TargetUnit = target,
            Cell = cell,
            Text = text,
            Value = value,
            DamageType = damageType,
            PlayerVisible = visible,
        };
        _eventsThisTick++;
        if (_eventsThisTick > Rules.Limits.MaxEventsPerTick)
        {
            if (!_eventLimitReported)
            {
                _eventLimitReported = true;
                var d = new SimEvent
                {
                    Tick = Tick, Time = Time, Type = SimEventType.Diagnostic, Level = LogLevel.Diagnostic,
                    Text = $"Превышен лимит событий за тик ({Rules.Limits.MaxEventsPerTick}) — цепочки реакций обрываются",
                };
                Log.Add(d);
                _pending.Add(d);
            }
            return e;
        }
        Log.Add(e);
        _pending.Add(e);
        return e;
    }

    public bool EventLimitReached => _eventsThisTick > Rules.Limits.MaxEventsPerTick;

    private bool IsEventVisibleToPlayer(int source, int target, HexCoord? cell)
    {
        bool any = false;
        foreach (var id in new[] { source, target })
        {
            if (id < 0) continue;
            any = true;
            var u = GetUnit(id);
            if (u == null) continue;
            if (IsPlayerFaction(u.FactionId) || IsPlayerFaction(u.BaseFactionId)) return true;
            if (Vision.IsVisibleToPlayer(u)) return true;
        }
        if (cell.HasValue)
        {
            any = true;
            if (Vision.IsCellVisibleToPlayer(cell.Value)) return true;
        }
        return !any;
    }

    /// <summary>Events since the last call (renderer: floating numbers, VFX).</summary>
    public List<SimEvent> DrainEvents()
    {
        var copy = new List<SimEvent>(_pending);
        _pending.Clear();
        return copy;
    }

    public void RequestAutopause(string reason)
    {
        if (AutopauseReason == null) AutopauseReason = reason;
        Emit(SimEventType.Autopause, LogLevel.Detail, "Автопауза: " + reason);
    }

    public string? ConsumeAutopause()
    {
        var r = AutopauseReason;
        AutopauseReason = null;
        return r;
    }

    // ------------------------------------------------------------------ command API (UI / AI)

    /// <summary>Can the player issue commands to this unit?</summary>
    public bool CanPlayerCommand(Unit u) => u.Alive && (Options.AdminMode || IsPlayerControlled(u));

    /// <summary>Enqueue (or insert) a command after validation. Returns an error text or null.</summary>
    public string? Enqueue(Unit u, Command c, int insertIndex = -1, bool replaceQueue = false) =>
        Commands.Enqueue(u, c, insertIndex, replaceQueue);

    public bool RemoveCommand(Unit u, int commandId) => Commands.Remove(u, commandId);

    public bool ReorderCommand(Unit u, int commandId, int newIndex) => Commands.Reorder(u, commandId, newIndex);

    public void ClearQueue(Unit u, string reason = "отменено игроком") => Commands.ClearQueue(u, reason, CommandStatus.Cancelled);

    /// <summary>Facing after arrival at a route node (the facing arrow of a node, docs/DECISIONS_3.md §40).</summary>
    public bool SetArrivalFacing(Unit u, int commandId, HexDirection? dir) => Commands.SetArrivalFacing(u, commandId, dir);

    /// <summary>Removes the orders not yet started; the one being executed stays (docs/DECISIONS_3.md §39).</summary>
    public int ClearPending(Unit u, string reason = "отменено игроком") => Commands.ClearPending(u, reason);

    // ------------------------------------------------------------------ tick

    /// <summary>Advances the simulation by exactly one fixed tick.</summary>
    public void Step()
    {
        if (Outcome != BattleOutcome.None && !Scenario.ContinueAfterOutcome) return;
        Tick++;
        _eventsThisTick = 0;
        _eventLimitReported = false;

        var p = Profiler;
        long t = p.Begin();
        Scenario.Update();
        p.End("Сценарий", t);
        t = p.Begin();
        Ai.Update();
        p.End("ИИ", t);
        t = p.Begin();
        AutoAttacks.Update();
        p.End("Автоатаки", t);
        t = p.Begin();
        UpdateUnits();
        p.End("Юниты/состояния", t);
        t = p.Begin();
        Blocking.Update();
        p.End("Блокирование", t);
        t = p.Begin();
        Commands.Update();
        p.End("Команды", t);
        t = p.Begin();
        Skills.UpdatePendingStages();
        CellEffects.Update();
        p.End("Эффекты клеток", t);
        t = p.Begin();
        Reactions.Process();
        p.End("Реакции", t);
        t = p.Begin();
        Vision.Update();
        Knowledge.Update();
        p.End("Видимость", t);
        t = p.Begin();
        CleanupDead();
        Scenario.CheckOutcome();
        p.End("Сценарий", t);
        p.TickDone();
    }

    public void Run(int ticks)
    {
        for (int i = 0; i < ticks; i++) Step();
    }

    public void RunSeconds(double seconds) => Run((int)TicksFor(seconds));

    private void UpdateUnits()
    {
        foreach (var u in _units)
        {
            if (!u.Alive) continue;
            // AP regeneration.
            if (!u.Flags.BlocksApRegen)
            {
                double regen = u.Stat(Stats.ApRegen) * Dt;
                if (u.Defending) regen *= Rules.Ap.DefendRegenMultiplier;
                double max = u.ApMax;
                if (u.Ap < max)
                {
                    u.Ap = Math.Min(max, u.Ap + regen);
                    if (u.Ap >= max - 1e-9) Emit(SimEventType.ApFull, LogLevel.Diagnostic, $"{u.Name}: ОД восстановлены до максимума ({max:0.#})", source: u.Id);
                }
                else if (u.Ap > max)
                {
                    u.Ap = max;
                }
            }
            foreach (var r in u.Resources.Values)
            {
                if (r.Regen == 0) continue;
                r.Current = Math.Clamp(r.Current + r.Regen * Dt, 0, r.Max);
            }
            Statuses.UpdateUnit(u);
            Damage.UpdateShields(u);
            Reactions.UpdatePeriodic(u);
            // Standing over a pit (rescue status expired, layer changed): fall now.
            if (u.Alive && u.StepTo == null)
            {
                var here = Map.Get(u.Pos);
                if (here != null && here.Terrain.Kind == TerrainKind.Pit) Falls.CheckCell(u, here, null, "");
            }
            if (u.ExpiresAtTick > 0 && Tick >= u.ExpiresAtTick && u.Alive)
                Combat.Kill(u, null, "время призыва истекло");
        }
    }

    private void CleanupDead()
    {
        // Dead units stay in the list (for the log/UI) but no longer occupy cells.
        foreach (var u in _units)
        {
            if (u.Alive) continue;
            var cell = Map.Get(u.Pos);
            if (cell != null && cell.Occupants.Remove(u)) Map.Touch(false);
        }
    }

    /// <summary>FNV-1a string hash (stable across processes, unlike string.GetHashCode).</summary>
    public static long StableHash(string s)
    {
        unchecked
        {
            long h = (long)14695981039346656037UL;
            foreach (char ch in s)
            {
                h ^= ch;
                h *= 1099511628211L;
            }
            return h;
        }
    }

    /// <summary>Stable hash of the simulation state (determinism tests).</summary>
    public string StateHash()
    {
        unchecked
        {
            long h = 17;
            foreach (var u in _units.OrderBy(x => x.Id))
            {
                h = h * 31 + u.Id;
                h = h * 31 + u.Pos.GetHashCode();
                h = h * 31 + (int)u.Facing;
                h = h * 31 + (long)Math.Round(u.Hp * 1000);
                h = h * 31 + (long)Math.Round(u.Ap * 1000);
                h = h * 31 + (u.Alive ? 1 : 0);
                foreach (var s in u.Statuses) h = h * 31 + StableHash(s.Def.Id) + (long)Math.Round(s.Points * 100) + s.RemainingTicks;
            }
            foreach (var c in Map.Cells)
            foreach (var e in c.Effects)
                h = h * 31 + c.Index * 7 + StableHash(e.Def.Id) + e.Level + e.RemainingTicks;
            return $"{Tick}:{h:X}:{Rng.Draws}";
        }
    }
}
