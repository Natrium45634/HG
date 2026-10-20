using System;
using System.Collections.Generic;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public sealed class ResourceState
{
    public ResourceDef Def { get; init; } = null!;
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
/// Stat block: base values from the unit definition (or stat defaults) and modifiers from
/// statuses, passives and stance. final = clamp((base + ΣAdd) × (1 + ΣMul), min, max).
/// </summary>
public sealed class StatBlock
{
    private readonly ContentDatabase _db;
    private readonly Dictionary<string, double> _base = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _add = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _mul = new(StringComparer.Ordinal);
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
        double b = Base(id);
        double add = _add.TryGetValue(id, out var a) ? a : 0.0;
        double mul = _mul.TryGetValue(id, out var m) ? m : 0.0;
        double v = (b + add) * (1.0 + mul);
        var sd = _db.Stat(id);
        if (sd != null) v = Math.Clamp(v, sd.Min, Math.Max(sd.Min, sd.Max));
        _cache[id] = v;
        return v;
    }

    public void ClearModifiers()
    {
        _add.Clear();
        _mul.Clear();
        _cache.Clear();
    }

    public void AddModifier(StatModDef m, double scale = 1.0)
    {
        if (string.IsNullOrEmpty(m.Stat)) return;
        _add[m.Stat] = (_add.TryGetValue(m.Stat, out var a) ? a : 0.0) + m.Add * scale;
        _mul[m.Stat] = (_mul.TryGetValue(m.Stat, out var x) ? x : 0.0) + m.Mul * scale;
        _cache.Clear();
    }

    public void AddFlat(string stat, double add)
    {
        _add[stat] = (_add.TryGetValue(stat, out var a) ? a : 0.0) + add;
        _cache.Clear();
    }

    public IEnumerable<string> ModifiedStats => _add.Keys;
}

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
