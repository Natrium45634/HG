using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum SimEventType
{
    Info,
    UnitSpawned,
    UnitSpotted,
    UnitLost,
    Moved,
    Turned,
    ApSpent,
    ApRefund,
    ApFull,
    CommandStarted,
    CommandCompleted,
    CommandWaiting,
    CommandSkipped,
    CommandFailed,
    QueueCancelled,
    PathBlocked,
    Repath,
    CastStarted,
    CastReleased,
    CastInterrupted,
    AttackNoticed,
    AttackUnnoticed,
    Surprise,
    Hit,
    Miss,
    Block,
    Crit,
    Damage,
    Heal,
    Death,
    StatusApplied,
    StatusLevel,
    StatusRemoved,
    StatusResisted,
    CellEffectAdded,
    CellEffectRemoved,
    CellEffectBlocked,
    Synergy,
    TerrainChanged,
    ObstacleDestroyed,
    Displaced,
    FacingChanged,
    ResourceChanged,
    Summoned,
    Reaction,
    WaveStarted,
    Message,
    Victory,
    Defeat,
    AiDecision,
    Diagnostic,
    Autopause,
    Fall,
    LayerChanged,
    TrapTriggered,
    /// <summary>A visual effect by ID (text) at the cell — catalog operation; drawn by the renderer.</summary>
    Vfx,
    /// <summary>A sound by ID (text) at the cell.</summary>
    Sound,
    // Part 3 (docs/DECISIONS_3.md §45).
    /// <summary>One hit of a series resolved («удар 2 из 3»).</summary>
    SkillHit,
    /// <summary>A series of hits stopped early (target died, condition, resource, caster).</summary>
    SeriesStopped,
    DefenseStarted,
    DefenseEnded,
    /// <summary>An attack avoided by a defensive stance (dodge or parry).</summary>
    DefenseAvoided,
    /// <summary>
    /// A stage (hit) of a skill resolved — for the presentation (projectile, stage effect, sound): DamageType = skill ID,
    /// Value = stage index (≥ 0 — something was hit, −(index + 1) — missed), Cell = the stage's anchor.
    /// </summary>
    StageResolved,
    // Part 4 (docs/DECISIONS_4.md §56).
    /// <summary>A unit reached its target object (a base, a checkpoint): Target — the structure, if any.</summary>
    GoalReached,
    /// <summary>A structure of the scenario (a base, a tower) was destroyed.</summary>
    StructureDestroyed,
    // Part 4, stage 5 (docs/DECISIONS_4.md §64): what the battle report and the breakpoints count.
    /// <summary>A passive with a trigger fired: DamageType = passive ID.</summary>
    PassiveTriggered,
    /// <summary>An AI unit switched its target: Target — the new one.</summary>
    TargetChanged,
    /// <summary>An AI problem (stuck, no path, target unreachable, route blocked): DamageType = the kind.</summary>
    AiProblem,
    /// <summary>A wave is over (cleared, cancelled, expired): Value = wave index.</summary>
    WaveEnded,
    /// <summary>The preparation (placement) phase is over.</summary>
    PreparationDone,
    /// <summary>A unit passed a checkpoint node of its route: DamageType = node ID, Text names the route.</summary>
    CheckpointReached,
}

public enum LogLevel
{
    /// <summary>Main combat log.</summary>
    Normal,
    /// <summary>Detailed log (rolls, AP); shown when "подробно" is enabled.</summary>
    Detail,
    /// <summary>Diagnostic log (editor / admin mode only).</summary>
    Diagnostic,
}

public sealed class SimEvent
{
    public long Tick { get; init; }
    public double Time { get; init; }
    public SimEventType Type { get; init; }
    public LogLevel Level { get; init; }
    public int SourceUnit { get; init; } = -1;
    public int TargetUnit { get; init; } = -1;
    public HexCoord? Cell { get; init; }
    public string Text { get; init; } = "";
    public double Value { get; init; }
    public string DamageType { get; init; } = "";
    /// <summary>
    /// True if the player side could perceive this event (one of the units involved or the cell
    /// was visible to the player side). Hidden events are only shown in diagnostic mode.
    /// </summary>
    public bool PlayerVisible { get; init; } = true;

    public override string ToString() => $"[{Time,7:0.00}] {Text}";
}

/// <summary>Bounded combat log (ring buffer).</summary>
public sealed class CombatLog
{
    private readonly int _capacity;
    private readonly LinkedList<SimEvent> _entries = new();

    public CombatLog(int capacity = 3000)
    {
        _capacity = capacity;
    }

    public long TotalCount { get; private set; }

    public IEnumerable<SimEvent> Entries => _entries;

    public int Count => _entries.Count;

    public void Add(SimEvent e)
    {
        _entries.AddLast(e);
        TotalCount++;
        while (_entries.Count > _capacity) _entries.RemoveFirst();
    }

    public void Clear() => _entries.Clear();
}
