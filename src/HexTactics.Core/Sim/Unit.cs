using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public sealed class ResourceState
{
    public ResourceDef Def { get; init; } = null!;
    /// <summary>Maximum and regeneration of the unit definition; passives change Max/Regen from them.</summary>
    public double BaseMax { get; set; }
    public double BaseRegen { get; set; }
    internal bool BaseCaptured { get; set; }
    public double Max { get; set; }
    public double Current { get; set; }
    public double Regen { get; set; }
}

/// <summary>Runtime status instance on a unit.</summary>
public sealed class ShieldInstance
{
    public int Id { get; init; }
    public ShieldSpecDef Spec { get; init; } = new();
    public double Amount { get; set; }
    public double Max { get; set; }
    /// <summary>-1 — until depleted.</summary>
    public long ExpiresTick { get; set; } = -1;
    public int SourceUnit { get; init; } = -1;
}

public sealed class StatusInstance
{
    public StatusDef Def { get; init; } = null!;
    /// <summary>Accumulated points (accumulating statuses).</summary>
    public double Points { get; set; }
    /// <summary>Stacks / intensity (timed statuses).</summary>
    public int Stacks { get; set; } = 1;
    /// <summary>Remaining ticks, -1 = unlimited (timed statuses).</summary>
    public long RemainingTicks { get; set; } = -1;
    public long TotalTicks { get; set; } = -1;
    public int LevelIndex { get; set; } = -1;
    public long TickTimer { get; set; }
    /// <summary>Cell where the status was applied (for «пока юнит стоит на клетке»).</summary>
    public HexTactics.Core.Hex.HexCoord AppliedCell { get; set; }
    public int SourceUnit { get; set; } = -1;
    /// <summary>Points gained this tick (to show "accumulating" in UI).</summary>
    public double LastGain { get; set; }
    /// <summary>Periodic ticks done (for statuses with a tick count).</summary>
    public int TicksDone { get; set; }
    /// <summary>Snapshot power: amounts of OnTick effects fixed at application (by level index and effect index).</summary>
    public Dictionary<(int Level, int Effect), double>? Snapshot { get; set; }

    public StatusLevelDef? Level => LevelIndex >= 0 && LevelIndex < Def.Levels.Count ? Def.Levels[LevelIndex] : null;

    public string LevelName
    {
        get
        {
            var l = Level;
            return l == null || string.IsNullOrEmpty(l.Name) ? Def.DisplayName : $"{Def.DisplayName}: {l.Name}";
        }
    }
}

/// <summary>
/// Stat block: base values from the unit definition (or stat defaults) and modifiers from statuses, passives,
/// stance… summed by the single order of docs/DECISIONS_3.md §46 (<see cref="ModifierMath"/>) and the stat's
/// «порядок расчёта». Old modifiers (add + fraction) are «прибавка до процентов» and «проценты»:
/// final = clamp((base + ΣAdd) × (1 + ΣMul), min, max) as before.
/// </summary>
public sealed class StatBlock
{
    private readonly ContentDatabase _db;
    private readonly Dictionary<string, double> _base = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Contribution>> _mods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _cache = new(StringComparer.Ordinal);

    public StatBlock(ContentDatabase db, UnitDef def)
    {
        _db = db;
        foreach (var kv in def.Stats) _base[kv.Key] = kv.Value;
    }

    public static string ResistStat(string damageType) => "resist_" + damageType;

    public double Base(string id)
    {
        if (_base.TryGetValue(id, out var v)) return v;
        return _db.Stat(id)?.Default ?? 0.0;
    }

    public void SetBase(string id, double value)
    {
        _base[id] = value;
        _cache.Clear();
    }

    public double Get(string id)
    {
        if (_cache.TryGetValue(id, out var c)) return c;
        double v = Compute(id, out _);
        _cache[id] = v;
        return v;
    }

    private double Compute(string id, out Combined combined)
    {
        double b = Base(id);
        var sd = _db.Stat(id);
        var list = _mods.TryGetValue(id, out var l) ? l : null;
        combined = ModifierMath.Combine(list ?? (IEnumerable<Contribution>)Array.Empty<Contribution>());
        if (list == null) return sd != null ? Math.Clamp(b, sd.Min, Math.Max(sd.Min, sd.Max)) : b;
        var order = sd?.CalcOrder ?? StatCalcOrder.Standard;
        var percents = order == StatCalcOrder.Diminishing ? list.Where(x => x.Mode == ModifierMode.Percent).Select(x => x.Value).ToList() : null;
        double v = ModifierMath.Apply(b, combined, order, percents);
        if (sd != null)
        {
            v = ModifierMath.Round(v, sd.Rounding);
            v = Math.Clamp(v, sd.Min, Math.Max(sd.Min, sd.Max));
        }
        return v;
    }

    public void ClearModifiers()
    {
        _mods.Clear();
        _cache.Clear();
    }

    public void Add(string stat, Contribution c)
    {
        if (string.IsNullOrEmpty(stat)) return;
        if (!_mods.TryGetValue(stat, out var l)) _mods[stat] = l = new List<Contribution>();
        l.Add(c);
        _cache.Clear();
    }

    public void AddModifier(StatModDef m, double scale = 1.0, string source = "")
    {
        if (string.IsNullOrEmpty(m.Stat)) return;
        if (m.Add != 0) Add(m.Stat, new Contribution(ModifierMode.FlatBefore, m.Add * scale, 0, "", source));
        if (m.Mul != 0) Add(m.Stat, new Contribution(ModifierMode.Percent, m.Mul * 100 * scale, 0, "", source));
    }

    public void AddFlat(string stat, double add, string source = "")
    {
        if (add != 0) Add(stat, new Contribution(ModifierMode.FlatBefore, add, 0, "", source));
    }

    public IEnumerable<string> ModifiedStats => _mods.Keys;

    /// <summary>Final value and the share of every source (unit panel, editor): what acted and what did not.</summary>
    public StatBreakdown Explain(string id)
    {
        double v = Compute(id, out var c);
        return new StatBreakdown(id, Base(id), v, _db.Stat(id)?.CalcOrder ?? StatCalcOrder.Standard, c.Items);
    }
}

/// <summary>Result of <see cref="StatBlock.Explain"/>.</summary>
public sealed record StatBreakdown(string Stat, double Base, double Final, StatCalcOrder Order, List<(Contribution C, bool Applied, string Note)> Items);

public sealed class Unit
{
    /// <summary>Why the unit last stopped (failed/skipped order) and when — shown to the player (docs/DECISIONS_3.md §41).</summary>
    public string StopReason { get; internal set; } = "";
    /// <summary>The ally this unit is waiting for to free a hex (-1 — none); used to break waiting cycles.</summary>
    public int WaitingFor { get; internal set; } = -1;
    /// <summary>Auto-attack priority set by a group order (applied before the auto-attack's own rules).</summary>
    public AutoTargetRule? AutoAttackPriority { get; set; }
    public long StopTick { get; internal set; } = -1;

    public Unit(int id, UnitDef def, ContentDatabase db)
    {
        Id = id;
        Def = def;
        Name = def.DisplayName;
        FactionId = def.Faction;
        Stats = new StatBlock(db, def);
    }

    public int Id { get; }
    public UnitDef Def { get; }
    public string Name { get; set; }
    public string FactionId { get; set; }
    /// <summary>Original faction (mind control changes FactionId temporarily).</summary>
    public string BaseFactionId { get; set; } = "";
    public string Tag { get; set; } = "";

    public HexCoord Pos { get; internal set; }
    public HexDirection Facing { get; internal set; }
    public bool Alive { get; internal set; } = true;
    public long DiedAtTick { get; internal set; } = -1;
    public DeathCause DeathCause { get; internal set; }

    /// <summary>Position layer id (ground/air/underground/custom) and its definition.</summary>
    public string Layer { get; internal set; } = Layers.Ground;
    public LayerDef LayerDef { get; internal set; } = new() { Id = Layers.Ground };
    /// <summary>Last cell that was not a pit for this unit (fall "return" consequence).</summary>
    public HexCoord LastSafePos { get; internal set; }

    public double Hp { get; internal set; }
    public double Ap { get; internal set; }

    public StatBlock Stats { get; }
    public Dictionary<string, ResourceState> Resources { get; } = new(StringComparer.Ordinal);
    public List<StatusInstance> Statuses { get; } = new();

    /// <summary>Stat changes of the «изменить характеристику» operation: until the tick (-1 — until the end of the battle).</summary>
    public List<(StatModDef Mod, long UntilTick)> StatChanges { get; } = new();
    public ControlFlagsDef Flags { get; } = new();
    public List<PassiveDef> Passives { get; } = new();
    /// <summary>All passives with their sources and whether they act now (docs/DECISIONS_3.md §46).</summary>
    public List<PassiveInstance> PassiveInstances { get; internal set; } = new();
    /// <summary>Triggered passives acting for their time after the event: passive → until tick.</summary>
    public Dictionary<string, long> TimedPassives { get; } = new(StringComparer.Ordinal);
    /// <summary>Times each triggered passive fired in this battle.</summary>
    public Dictionary<string, int> PassiveFired { get; } = new(StringComparer.Ordinal);
    /// <summary>Health lost since each «потерял N здоровья» passive last fired.</summary>
    public Dictionary<string, double> HpLostSince { get; } = new(StringComparer.Ordinal);
    public List<SkillDef> Skills { get; } = new();
    public SkillDef? BasicAttack { get; internal set; }
    /// <summary>Defensive skill of the radial menu's centre (null — the stance of the rules).</summary>
    public SkillDef? DefenseSkill { get; internal set; }

    public CommandQueue Queue { get; } = new();
    public Dictionary<string, long> CooldownUntil { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> PassiveCooldownUntil { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> PassivePeriodicTimer { get; } = new(StringComparer.Ordinal);

    public bool Defending { get; internal set; }
    /// <summary>Active defensive stance of a defensive skill (docs/DECISIONS_3.md §45); null — none or the rules' stance.</summary>
    public DefenseState? Stance { get; internal set; }
    /// <summary>Tick of the last use of each skill (conditions «применил навык недавно»).</summary>
    public Dictionary<string, long> LastSkillUse { get; } = new(StringComparer.Ordinal);
    public SkillCast? Casting { get; internal set; }
    /// <summary>Unit is busy (recovery after a skill, turning) until this tick.</summary>
    public long BusyUntil { get; internal set; }

    // Movement visual/state: current step in progress.
    public HexCoord? StepFrom { get; internal set; }
    public HexCoord? StepTo { get; internal set; }
    public long StepTicksTotal { get; internal set; }
    public long StepTicksDone { get; internal set; }
    public double StepProgress => StepTicksTotal <= 0 ? 0 : (double)StepTicksDone / StepTicksTotal;

    public bool AiControlled { get; set; }
    public AiProfileDef? AiProfile { get; set; }
    public AiState Ai { get; } = new();

    public int SummonerId { get; set; } = -1;
    /// <summary>Route of a wave unit (map route points), and the next point index.</summary>
    public List<HexCoord>? Route { get; set; }
    public int RouteIndex { get; set; }
    /// <summary>Wave index of a wave unit (-1 — placed by the scenario).</summary>
    public int Wave { get; set; } = -1;
    // Part 4 (docs/DECISIONS_4.md §56–58).
    /// <summary>Route of the scenario the unit follows, its group and the object it goes to.</summary>
    public string RouteId { get; set; } = "";
    public string Group { get; set; } = "";
    public string TargetObject { get; set; } = "";
    /// <summary>A structure of the scenario (a base, a tower): it does not move and occupies <see cref="Footprint"/>.</summary>
    public bool IsStructure { get; internal set; }
    public string ObjectId { get; internal set; } = "";
    /// <summary>Every hex a structure occupies (the anchor first); null for ordinary units.</summary>
    public List<HexCoord>? Footprint { get; internal set; }
    /// <summary>Damage leaves at least 1 health (an indestructible object).</summary>
    public bool Indestructible { get; internal set; }
    public long ExpiresAtTick { get; set; } = -1;

    public int Kills { get; internal set; }
    /// <summary>Highest HP fraction threshold already reported (autopause on critical damage once).</summary>
    public bool CriticalReported { get; internal set; }
    public bool LowHpTriggered { get; internal set; }

    public double HpMax => Stats.Get(Content.Stats.HpMax);
    public double ApMax => Stats.Get(Content.Stats.ApMax);
    public double HpFraction => HpMax <= 0 ? 0 : Hp / HpMax;

    public double Stat(string id) => Stats.Get(id);

    /// <summary>Legacy resistance stat resist_&lt;type&gt; as a fraction (0.5 = −50 % damage); part of the modifier sum.</summary>
    public double ResistStat(string damageType) => Stats.Get(StatBlock.ResistStat(damageType));

    /// <summary>Shields (separate absorption pools), highest priority first.</summary>
    public List<ShieldInstance> Shields { get; } = new();
    public double ShieldTotal
    {
        get
        {
            double s = 0;
            foreach (var x in Shields) s += x.Amount;
            return s;
        }
    }

    /// <summary>Defensive/offensive blocks in effect: the unit type, passives and status levels (rebuilt with modifiers).</summary>
    public List<DefenseDef> Defenses { get; } = new();
    public List<OffenseDef> Offenses { get; } = new();
    public long LastDamagedTick { get; internal set; } = -1;

    /// <summary>Tick the unit appeared on the field (for the «появление» animation).</summary>
    public long SpawnTick { get; set; } = -1;

    public double ReservedAp => Queue.ReservedAp;
    public double FreeAp => Ap - ReservedAp;

    public double ResourceValue(string id) => Resources.TryGetValue(id, out var r) ? r.Current : 0.0;

    public bool IsImmuneTo(string id, IEnumerable<string> tags)
    {
        foreach (var im in Def.Immunities)
        {
            if (im == id) return true;
            if (im.StartsWith("tag:", StringComparison.Ordinal))
            {
                var t = im.Substring(4);
                foreach (var tag in tags)
                    if (tag == t)
                        return true;
            }
        }
        return false;
    }

    public StatusInstance? FindStatus(string id)
    {
        foreach (var s in Statuses)
            if (s.Def.Id == id)
                return s;
        return null;
    }

    public long CooldownRemaining(string skillId, long now) => CooldownUntil.TryGetValue(skillId, out var t) ? Math.Max(0, t - now) : 0;

    public bool KnowsSkill(string id)
    {
        if (BasicAttack != null && BasicAttack.Id == id) return true;
        if (DefenseSkill != null && DefenseSkill.Id == id) return true;
        foreach (var s in Skills)
            if (s.Id == id)
                return true;
        return false;
    }

    public override string ToString() => $"#{Id} {Name}";
}

public enum DeathCause
{
    Killed,
    Fall,
    Removed,
    Expired,
}

/// <summary>Per-unit AI memory and explanation for the admin view.</summary>
public sealed class AiState
{
    public long NextDecisionTick { get; set; }
    public int TargetUnitId { get; set; } = -1;
    public string Explanation { get; set; } = "";
    public string LastAction { get; set; } = "";
    public HexCoord? HoldCenter { get; set; }
    public Dictionary<int, (HexCoord Pos, long Tick)> KnownEnemies { get; } = new();
    public HexCoord? MoveGoal { get; set; }
}
