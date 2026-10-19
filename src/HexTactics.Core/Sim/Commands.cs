using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum CommandType
{
    Move,
    Turn,
    Attack,
    Skill,
    Defend,
    Wait,
}

public enum CommandStatus
{
    Pending,
    Active,
    Waiting,
    Done,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>
/// A queued unit order. Created by the UI or the AI; executed only by <see cref="CommandSystem"/>.
/// </summary>
public sealed class Command
{
    public int Id { get; internal set; }
    public CommandType Type { get; init; }
    public HexCoord? TargetCell { get; set; }
    public int TargetUnitId { get; set; } = -1;
    public HexDirection? Direction { get; set; }
    public string SkillId { get; set; } = "";
    /// <summary>Wait/defend duration in seconds (defend: 0 = until another command is queued).</summary>
    public double Duration { get; set; }
    /// <summary>Optional per-command failure policy (overrides the rules for every reason).</summary>
    public FailurePolicyDef? PolicyOverride { get; set; }

    // Route node (docs/DECISIONS_3.md §41): a Move command is a node of the player's route.
    /// <summary>Facing taken after arriving (by the manual turn rules).</summary>
    public HexDirection? ArrivalFacing { get; set; }
    /// <summary>Waiting after arriving, seconds.</summary>
    public double WaitAfter { get; set; }
    /// <summary>Continue only when this condition holds (owner = the unit); bounded by the queue's maximal wait.</summary>
    public ConditionDef? Until { get; set; }
    /// <summary>Not mandatory: reaching a neighbouring hex is enough, and an unreachable node is skipped.</summary>
    public bool Optional { get; set; }
    /// <summary>Player routing (null — the fast cell search of the AI).</summary>
    public RouteMode? Routing { get; set; }
    public RouteWeights? RouteWeights { get; set; }
    /// <summary>Attack/skill: approach the target first when it is out of range or sight (if the skill allows, docs/DECISIONS_3.md §42).</summary>
    public bool Approach { get; set; }

    public CommandStatus Status { get; internal set; } = CommandStatus.Pending;
    public string StatusText { get; internal set; } = "";
    /// <summary>Estimated AP reserved for this command (set at enqueue, updated during execution).</summary>
    public double EstimatedAp { get; internal set; }
    /// <summary>AP already spent by this command.</summary>
    public double SpentAp { get; internal set; }

    // Execution state.
    internal List<HexCoord>? Path { get; set; }
    /// <summary>Path computed when the estimate was made (reused on start if still fresh).</summary>
    internal List<HexCoord>? PlannedPath { get; set; }
    internal HexCoord PlannedFrom { get; set; }
    internal long PlannedTick { get; set; } = -1;
    internal long WaitStartTick { get; set; } = -1;
    internal FailureReason WaitReason { get; set; }
    internal int RepathAttempts { get; set; }
    internal long TotalWaitTicks { get; set; }
    internal int Phase { get; set; }
    internal long PhaseUntil { get; set; }
    internal long StartedTick { get; set; } = -1;
    /// <summary>Route node: 0 — moving, 1 — arrival facing, 2 — waiting, 3 — condition.</summary>
    internal int NodePhase { get; set; }
    /// <summary>Approach before an attack: the chosen position and the target position it was chosen for.</summary>
    internal HexCoord? ApproachCell { get; set; }
    internal HexCoord? ApproachFor { get; set; }
    internal int ApproachSearches { get; set; }
    /// <summary>Facing before the skill (for «keep facing after the attack»).</summary>
    internal HexDirection FacingBefore { get; set; }

    public bool IsFinished => Status is CommandStatus.Done or CommandStatus.Skipped or CommandStatus.Failed or CommandStatus.Cancelled;

    public bool IsAttackLike => Type is CommandType.Attack or CommandType.Skill;

    public double RemainingReserve => IsFinished ? 0 : System.Math.Max(0, EstimatedAp - SpentAp);

    public Command CloneTemplate() => new()
    {
        Type = Type,
        TargetCell = TargetCell,
        TargetUnitId = TargetUnitId,
        Direction = Direction,
        SkillId = SkillId,
        Duration = Duration,
        PolicyOverride = PolicyOverride?.Clone(),
        ArrivalFacing = ArrivalFacing,
        WaitAfter = WaitAfter,
        Until = Until,
        Optional = Optional,
        Routing = Routing,
        RouteWeights = RouteWeights?.Clone(),
        Approach = Approach,
    };

    public static Command Move(HexCoord to) => new() { Type = CommandType.Move, TargetCell = to };
    public static Command Turn(HexDirection d) => new() { Type = CommandType.Turn, Direction = d };
    public static Command Attack(int unitId) => new() { Type = CommandType.Attack, TargetUnitId = unitId };
    public static Command Skill(string skill, HexCoord? cell, int unitId = -1, HexDirection? dir = null) =>
        new() { Type = CommandType.Skill, SkillId = skill, TargetCell = cell, TargetUnitId = unitId, Direction = dir };
    public static Command Defend(double seconds = 0) => new() { Type = CommandType.Defend, Duration = seconds };
    public static Command Wait(double seconds) => new() { Type = CommandType.Wait, Duration = seconds };

    public string Describe(Simulation? sim = null)
    {
        string unitName(int id)
        {
            var u = sim?.GetUnit(id);
            return u == null ? $"#{id}" : u.Name;
        }
        string skillName(string id) => sim?.Content.Skill(id)?.DisplayName ?? id;
        return Type switch
        {
            CommandType.Move => $"Движение к {TargetCell}" + (ArrivalFacing.HasValue ? $", взгляд {ArrivalFacing.Value.RuShortName()}" : "")
                                + (WaitAfter > 0 ? $", ждать {WaitAfter:0.#} с" : "") + (Optional ? " (необязательная)" : ""),
            CommandType.Turn => $"Поворот: {Direction?.RuName()}",
            CommandType.Attack => $"Атака: {unitName(TargetUnitId)}",
            CommandType.Skill => TargetUnitId >= 0
                ? $"{skillName(SkillId)} → {unitName(TargetUnitId)}"
                : TargetCell.HasValue
                    ? $"{skillName(SkillId)} → {TargetCell}{(Direction.HasValue ? " " + Direction.Value.RuShortName() : "")}"
                    : $"{skillName(SkillId)}{(Direction.HasValue ? " на " + Direction.Value.RuName() : "")}",
            CommandType.Defend => Duration > 0 ? $"Защитная стойка {Duration:0.#} с" : "Защитная стойка",
            CommandType.Wait => $"Ожидание {Duration:0.#} с",
            _ => Type.ToString(),
        };
    }

    public override string ToString() => $"{Id}:{Type}:{Status}";
}

/// <summary>Queue of a unit. Reserved AP is derived from unfinished commands, so it cannot leak.</summary>
public sealed class CommandQueue
{
    private readonly List<Command> _items = new();

    public IReadOnlyList<Command> Items => _items;

    public Command? Current => _items.Count > 0 ? _items[0] : null;

    public int Count => _items.Count;

    public double ReservedAp
    {
        get
        {
            double s = 0;
            foreach (var c in _items) s += c.RemainingReserve;
            return s;
        }
    }

    internal void Add(Command c) => _items.Add(c);

    internal void Insert(int index, Command c) => _items.Insert(System.Math.Clamp(index, 0, _items.Count), c);

    internal bool Remove(Command c) => _items.Remove(c);

    internal void RemoveAt(int i) => _items.RemoveAt(i);

    internal void Clear() => _items.Clear();

    public int IndexOf(int commandId)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Id == commandId)
                return i;
        return -1;
    }

    public Command? Find(int commandId) => _items.FirstOrDefault(c => c.Id == commandId);

    internal void Move(int from, int to)
    {
        var c = _items[from];
        _items.RemoveAt(from);
        _items.Insert(System.Math.Clamp(to, 0, _items.Count), c);
    }
}
