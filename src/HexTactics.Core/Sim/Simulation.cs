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
    /// <summary>«Автотест» (docs/DECISIONS_4.md §64): the player's units are driven by AI profiles too.</summary>
    public bool AutoPlayer { get; init; }
    /// <summary>The profile of AI-driven player units without their own.</summary>
    public string DefaultPlayerProfile { get; init; } = "";
    /// <summary>Part 6 (docs/DECISIONS_6.md §96): blocking by the rules of the mode; off — nobody holds anybody.</summary>
    public bool Blocking { get; init; } = true;
    /// <summary>The squad the player takes into battle (template IDs in order); null — the scenario's default squad.</summary>
    public IReadOnlyList<string>? Roster { get; init; }
    /// <summary>Who of the squad stands on the field at the start.</summary>
    public SquadStart SquadStart { get; init; } = SquadStart.AllOnField;
    /// <summary>The rules of the deployment during the battle (part 6, §97).</summary>
    public DeploymentRulesDef Deployment { get; init; } = new();
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
        // Separate streams from the same seed (docs/DECISIONS_5.md §84): a change in what the AI or the spawn draws does
        // not shift the rolls of the battle. Rules version 1 keeps the single stream of 1.0.
        bool streams = Rules.Time.SimVersion >= 2;
        AiRng = streams ? new SimRng(Options.Seed ^ 0xA1A1_5EED_0000_0001UL) : Rng;
        SpawnRng = streams ? new SimRng(Options.Seed ^ 0x5A5A_5EED_0000_0002UL) : Rng;
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
        Placement = new PlacementSystem(this);
        Squad = new SquadSystem(this);
        Deployment = new DeploymentSystem(this);
        Mission = new MissionModifierSystem(this);
        Interactive = new ObjectSystem(this);
        Structures = new StructureSystem(this);
        Routes = new RouteNavigator(this);
        Categories = new CategoryCache(Content);
        Planner = new Planner(this);
        Targets = new TargetSelector(this);
        Defense = new DefenseSystem(this);
        Conditions = new UnitConditions(this);
        Passives = new PassiveSystem(this);
        Groups = new GroupPlanner(this);
        GroupOrders = new GroupOrders(this);
        Input = new PlayerInput(this);

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
    /// <summary>The battle: hits, crits, chances, statuses, spreading effects.</summary>
    public SimRng Rng { get; }
    /// <summary>Decisions of the AI and route forks (the same object as <see cref="Rng"/> under rules version 1).</summary>
    public SimRng AiRng { get; }
    /// <summary>Spawning: random routes of groups, instance levels (the same object as <see cref="Rng"/> under rules version 1).</summary>
    public SimRng SpawnRng { get; }
    /// <summary>Walking route graphs of the scenario (docs/DECISIONS_4.md §57).</summary>
    public RouteNavigator Routes { get; }
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
    /// <summary>The preparation phase: placing defenders (docs/DECISIONS_4.md §64).</summary>
    public PlacementSystem Placement { get; }
    /// <summary>The player's squad (part 6, docs/DECISIONS_6.md §96).</summary>
    public SquadSystem Squad { get; }
    /// <summary>Calling the squad onto the field for deployment points (part 6, §97).</summary>
    public DeploymentSystem Deployment { get; }
    /// <summary>The modifiers of a mission for units (part 6, §99).</summary>
    public MissionModifierSystem Mission { get; }
    /// <summary>The states, interactions and signals of the objects of the scenario (part 6, §100).</summary>
    public ObjectSystem Interactive { get; }
    /// <summary>Structures of the scenario: bases and towers with health (docs/DECISIONS_4.md §56).</summary>
    public StructureSystem Structures { get; }
    public Planner Planner { get; }
    /// <summary>Automatic target choice (docs/DECISIONS_3.md §43).</summary>
    public TargetSelector Targets { get; }
    /// <summary>Defensive stances of defensive skills (docs/DECISIONS_3.md §45).</summary>
    public DefenseSystem Defense { get; }
    /// <summary>Conditions of skill stages and passives (AND/OR/NOT trees).</summary>
    public UnitConditions Conditions { get; }
    /// <summary>Passives and talents of all sources, their conditions and modifiers (docs/DECISIONS_3.md §46).</summary>
    public PassiveSystem Passives { get; }
    /// <summary>Group positions and formations (docs/DECISIONS_3.md §44).</summary>
    public GroupPlanner Groups { get; }
    public GroupOrders GroupOrders { get; }
    /// <summary>The player's calls (orders, queue, placement), written to the replay journal (docs/DECISIONS_5.md §86).</summary>
    public PlayerInput Input { get; }

    /// <summary>The fingerprint of the state by parts (snapshots, replays).</summary>
    public SimDigest Digest() => SimDigest.Of(this);

    /// <summary>Gives a group order: each unit gets its own commands (docs/DECISIONS_3.md §44).</summary>
    public GroupOrderResult IssueGroupOrder(IReadOnlyList<Unit> units, GroupOrder order) => GroupOrders.Issue(units, order);

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
            u.Resources[rd.Id] = new ResourceState { Def = rd, Max = r.Max, Current = Math.Min(r.Start, r.Max), Regen = r.Regen, BaseMax = r.Max, BaseRegen = r.Regen, BaseCaptured = true };
        }
        u.AiControlled = Options.AiFactions.Contains(u.FactionId) && !Options.PlayerFactions.Contains(u.FactionId)
                         || Options.AutoPlayer && Options.PlayerFactions.Contains(u.FactionId);
        var cats = ApplyCategories(u);
        // An empty profile of the template takes the profile of its categories (docs/DECISIONS_4.md §61).
        // «Автотест»: the defenders take the defenders' profile unless the placement names one (docs/DECISIONS_4.md §64).
        bool autoDefender = Options.AutoPlayer && IsPlayerFaction(u.FactionId) && Options.DefaultPlayerProfile != "";
        var profileId = !string.IsNullOrEmpty(aiProfile) ? aiProfile : autoDefender ? Options.DefaultPlayerProfile : def.AiProfile != "" ? def.AiProfile : cats.AiProfile;
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

    /// <summary>
    /// The category tree of the template (docs/DECISIONS_4.md §61): tags, extra move categories and forbidden
    /// actions are set once at the appearance (conditional rules are checked now); passives of the categories are
    /// collected by the passive system with their conditions.
    /// </summary>
    private CategoryResolution ApplyCategories(Unit u)
    {
        var res = Categories.Of(u.Def, u.Tag);
        u.CategoryInfo = res;
        foreach (var t in u.Def.Tags) u.Tags.Add(t);
        bool Holds(ResolvedRule r) => r.Rule.Mode != CategoryInherit.Conditional || Conditions.Evaluate(r.Rule.Condition, new ConditionContext(u, null, u.Pos, null));
        foreach (var r in res.Acting)
        {
            var rule = Holds(r) ? r.Rule : r.Otherwise;
            if (rule == null) continue;
            string src = $"категория «{r.From.DisplayName}»";
            switch (rule.Kind)
            {
                case CategoryRuleKind.Tag when rule.Tag != "":
                    u.Tags.Add(rule.Tag);
                    break;
                case CategoryRuleKind.MoveCategory when rule.MoveCategory != "":
                    if (!u.CategoryMoves.Contains(rule.MoveCategory)) u.CategoryMoves.Add(rule.MoveCategory);
                    break;
                case CategoryRuleKind.Forbid:
                    u.Forbidden[rule.ActionKey] = $"запрещено: {rule.Describe().Split(": ").Last()} ({src})";
                    break;
            }
        }
        // Forbidden actions: the abilities are taken away, so commands, the AI and auto-attacks all respect it.
        if (u.Forbidden.ContainsKey(nameof(ForbidWhat.BasicAttack))) u.BasicAttack = null;
        if (u.Forbidden.ContainsKey(nameof(ForbidWhat.AllSkills))) u.Skills.Clear();
        foreach (var key in u.Forbidden.Keys.Where(k => k.StartsWith("skills:", StringComparison.Ordinal)).ToList())
        {
            string tag = key[7..];
            u.Skills.RemoveAll(s => s.Tags.Contains(tag));
            if (u.BasicAttack?.Tags.Contains(tag) == true) u.BasicAttack = null;
        }
        if (u.Forbidden.TryGetValue(nameof(ForbidWhat.Movement), out var why))
            u.InstanceMods.Add((new StatModDef { Stat = Stats.MoveSpeed, Mul = -1 }, why));
        return res;
    }

    /// <summary>
    /// Why the unit may not attack this target first («нападать первым» forbidden by its category), or null. The
    /// forbid is a capability: whoever attacked the unit may be answered, by the player and by the AI alike.
    /// </summary>
    public string? InitiationBlock(Unit u, Unit? target)
    {
        if (target == null || u.Forbidden.Count == 0 || !u.Forbidden.TryGetValue(nameof(ForbidWhat.InitiateAttack), out var why)) return null;
        if (!IsHostile(u, target) || u.Provokers.Contains(target.Id)) return null;
        return $"{why}: {target.Name} не нападал";
    }

    /// <summary>Category resolutions per template (the content does not change during a battle).</summary>
    public CategoryCache Categories { get; }

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
        var fromPos = u.Pos;
        from?.Occupants.Remove(u);
        if (from != null && from.Coord != to) CellEffects.OnUnitExited(u, from);
        bool moved = u.Pos != to;
        u.Pos = to;
        if (moved) u.RelocatedTick = Tick;
        var cell = Map.Get(to);
        if (cell != null)
        {
            cell.Occupants.Add(u);
            cell.Reservations.Remove(u);
        }
        if (moved) Statuses.OnMoved(u);
        if (moved) Passives.OnMoved(u, fromPos);
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
        // Observers (battle report, breakpoints) see every event, the scenario turns some into its events (§68).
        Observed?.Invoke(e);
        Scenario.OnSimEvent(e);
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

    /// <summary>
    /// Every event as it is emitted (docs/DECISIONS_4.md §64): the battle report and «остановка на событии» watch the
    /// simulation without changing it.
    /// </summary>
    public event Action<SimEvent>? Observed;

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

    /// <summary>«Остановка на событии»: a pause whose reason wins over an ordinary autopause of the same tick.</summary>
    public void RequestBreak(string reason)
    {
        AutopauseReason = reason;
        Emit(SimEventType.Autopause, LogLevel.Normal, "Остановка: " + reason);
    }

    public string? ConsumeAutopause()
    {
        var r = AutopauseReason;
        AutopauseReason = null;
        return r;
    }

    // ------------------------------------------------------------------ command API (UI / AI)

    /// <summary>Can the player issue commands to this unit?</summary>
    public bool CanPlayerCommand(Unit u) => u.Alive && !u.IsStructure && (Options.AdminMode || IsPlayerControlled(u));

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
        // Part 6 (ТЗ ч. 6, 13.5): the points and the pauses of the squad first, then everything that may spend them.
        Deployment.Update();
        Scenario.Update();
        Mission.Update();
        Interactive.Update();
        p.End("Сценарий", t);
        t = p.Begin();
        Ai.Update();
        p.End("ИИ", t);
        t = p.Begin();
        AutoAttacks.Update();
        p.End("Автоатаки", t);
        t = p.Begin();
        UpdateUnits();
        Passives.Update();
        p.End("Юниты/состояния", t);
        t = p.Begin();
        Blocking.Update();
        p.End("Блокирование", t);
        t = p.Begin();
        Commands.Update();
        Structures.Update();
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
        Stepped?.Invoke();
    }

    /// <summary>After every tick (observers read the state: the battle report, breakpoints with a condition).</summary>
    public event Action? Stepped;

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
                r.Current = Math.Clamp(r.Current + r.Regen * Dt, Math.Min(0, Math.Min(r.Current, r.Def.MinValue)), r.Max);
            }
            Statuses.UpdateUnit(u);
            Defense.Update(u);
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
