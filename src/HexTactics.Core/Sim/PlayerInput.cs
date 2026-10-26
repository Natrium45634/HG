using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Everything the player does to a battle (orders, the queue, group orders, placement), in one place
/// (docs/DECISIONS_5.md §86). The interface calls these instead of the simulation directly; with a journal attached each
/// call is written with its tick — the replay of a battle is the seed, the content and this list. The AI never comes
/// through here, so its orders are not in the journal: they are the result of the simulation.
/// </summary>
public sealed class PlayerInput
{
    private readonly Simulation _sim;

    public PlayerInput(Simulation sim) => _sim = sim;

    /// <summary>Where the calls are written; null — not recording.</summary>
    public List<ReplayEntry>? Journal { get; set; }

    private void Write(ReplayEntry e)
    {
        if (Journal == null) return;
        e.Tick = _sim.Tick;
        Journal.Add(e);
    }

    public string? Enqueue(Unit u, Command c, int insertIndex = -1, bool replaceQueue = false)
    {
        Write(new ReplayEntry { Op = ReplayOp.Enqueue, Unit = u.Id, Command = ReplayCommand.From(c), Index = insertIndex, Flag = replaceQueue });
        return _sim.Enqueue(u, c, insertIndex, replaceQueue);
    }

    public bool RemoveCommand(Unit u, int commandId)
    {
        Write(new ReplayEntry { Op = ReplayOp.Remove, Unit = u.Id, CommandId = commandId });
        return _sim.RemoveCommand(u, commandId);
    }

    public bool ReorderCommand(Unit u, int commandId, int newIndex)
    {
        Write(new ReplayEntry { Op = ReplayOp.Reorder, Unit = u.Id, CommandId = commandId, Index = newIndex });
        return _sim.ReorderCommand(u, commandId, newIndex);
    }

    public void ClearQueue(Unit u, string reason = "отменено игроком")
    {
        Write(new ReplayEntry { Op = ReplayOp.ClearQueue, Unit = u.Id, Text = reason });
        _sim.ClearQueue(u, reason);
    }

    public bool SetArrivalFacing(Unit u, int commandId, HexDirection? dir)
    {
        Write(new ReplayEntry { Op = ReplayOp.Facing, Unit = u.Id, CommandId = commandId, Dir = dir });
        return _sim.SetArrivalFacing(u, commandId, dir);
    }

    public int ClearPending(Unit u, string reason = "отменено игроком")
    {
        Write(new ReplayEntry { Op = ReplayOp.ClearPending, Unit = u.Id, Text = reason });
        return _sim.ClearPending(u, reason);
    }

    public GroupOrderResult IssueGroupOrder(IReadOnlyList<Unit> units, GroupOrder order)
    {
        Write(new ReplayEntry { Op = ReplayOp.Group, Units = units.Select(u => u.Id).ToList(), Group = ContentJson.Clone(order) });
        return _sim.IssueGroupOrder(units, order);
    }

    public Unit? Place(string template, HexCoord c, out string why, HexDirection facing = HexDirection.E, bool free = false, string aiProfile = "")
    {
        Write(new ReplayEntry { Op = ReplayOp.Place, Text = template, Cell = c, Dir = facing, Flag = free, Text2 = aiProfile });
        return _sim.Placement.Place(template, c, out why, facing, free, aiProfile);
    }

    public bool Unplace(Unit u)
    {
        Write(new ReplayEntry { Op = ReplayOp.Unplace, Unit = u.Id });
        return _sim.Placement.Remove(u);
    }

    public bool MovePlaced(Unit u, HexCoord c, out string why)
    {
        Write(new ReplayEntry { Op = ReplayOp.MovePlaced, Unit = u.Id, Cell = c });
        return _sim.Placement.Move(u, c, out why);
    }

    public Unit? Upgrade(Unit u, out string why)
    {
        Write(new ReplayEntry { Op = ReplayOp.Upgrade, Unit = u.Id });
        return _sim.Placement.Upgrade(u, out why);
    }

    public int PlacePreset(bool free)
    {
        Write(new ReplayEntry { Op = ReplayOp.PlacePreset, Flag = free });
        return _sim.Placement.PlacePreset(free);
    }

    /// <summary>Calls a fighter of the squad for deployment points (part 6, §97).</summary>
    public Unit? Deploy(int entryIndex, HexCoord c, HexDirection facing, out string why)
    {
        Write(new ReplayEntry { Op = ReplayOp.Deploy, Index = entryIndex, Cell = c, Dir = facing });
        return _sim.Deployment.Deploy(entryIndex, c, facing, out why);
    }

    /// <summary>Takes a fighter of the squad back from the field.</summary>
    public bool Retreat(Unit u, out string why)
    {
        Write(new ReplayEntry { Op = ReplayOp.Retreat, Unit = u.Id });
        return _sim.Deployment.Retreat(u, out why);
    }

    public bool Confirm()
    {
        Write(new ReplayEntry { Op = ReplayOp.Confirm });
        return _sim.Placement.Confirm();
    }

    /// <summary>Repeats a written call (the replay); false — its unit is not there (the content or the seed differ).</summary>
    internal bool Apply(ReplayEntry e)
    {
        var u = e.Unit >= 0 ? _sim.GetUnit(e.Unit) : null;
        if (e.Unit >= 0 && u == null) return false;
        switch (e.Op)
        {
            case ReplayOp.Enqueue: _sim.Enqueue(u!, e.Command!.ToCommand(), e.Index, e.Flag); break;
            case ReplayOp.Remove: _sim.RemoveCommand(u!, e.CommandId); break;
            case ReplayOp.Reorder: _sim.ReorderCommand(u!, e.CommandId, e.Index); break;
            case ReplayOp.ClearQueue: _sim.ClearQueue(u!, e.Text); break;
            case ReplayOp.Facing: _sim.SetArrivalFacing(u!, e.CommandId, e.Dir); break;
            case ReplayOp.ClearPending: _sim.ClearPending(u!, e.Text); break;
            case ReplayOp.Group:
            {
                var units = (e.Units ?? new()).Select(_sim.GetUnit).Where(x => x != null).Select(x => x!).ToList();
                if (units.Count != (e.Units?.Count ?? 0)) return false;
                _sim.IssueGroupOrder(units, ContentJson.Clone(e.Group!));
                break;
            }
            case ReplayOp.Place: _sim.Placement.Place(e.Text, e.Cell!.Value, out _, e.Dir ?? HexDirection.E, e.Flag, e.Text2); break;
            case ReplayOp.Unplace: _sim.Placement.Remove(u!); break;
            case ReplayOp.MovePlaced: _sim.Placement.Move(u!, e.Cell!.Value, out _); break;
            case ReplayOp.Upgrade: _sim.Placement.Upgrade(u!, out _); break;
            case ReplayOp.PlacePreset: _sim.Placement.PlacePreset(e.Flag); break;
            case ReplayOp.Confirm: _sim.Placement.Confirm(); break;
            case ReplayOp.Deploy: _sim.Deployment.Deploy(e.Index, e.Cell!.Value, e.Dir ?? HexDirection.E, out _); break;
            case ReplayOp.Retreat: _sim.Deployment.Retreat(u!, out _); break;
        }
        return true;
    }
}

public enum ReplayOp
{
    Enqueue,
    Remove,
    Reorder,
    ClearQueue,
    Facing,
    ClearPending,
    Group,
    Place,
    Unplace,
    MovePlaced,
    Upgrade,
    PlacePreset,
    Confirm,
    Deploy,
    Retreat,
}

/// <summary>One call of the player with its tick.</summary>
public sealed class ReplayEntry
{
    public long Tick { get; set; }
    public ReplayOp Op { get; set; }
    public int Unit { get; set; } = -1;
    public List<int>? Units { get; set; }
    public ReplayCommand? Command { get; set; }
    public GroupOrder? Group { get; set; }
    public int CommandId { get; set; } = -1;
    public int Index { get; set; } = -1;
    public bool Flag { get; set; }
    public HexCoord? Cell { get; set; }
    public HexDirection? Dir { get; set; }
    public string Text { get; set; } = "";
    public string Text2 { get; set; } = "";
}

/// <summary>An order as the player gave it (what <see cref="Command.CloneTemplate"/> keeps).</summary>
public sealed class ReplayCommand
{
    public CommandType Type { get; set; }
    public HexCoord? TargetCell { get; set; }
    public int TargetUnitId { get; set; } = -1;
    public HexDirection? Direction { get; set; }
    public string SkillId { get; set; } = "";
    public double Duration { get; set; }
    public FailurePolicyDef? PolicyOverride { get; set; }
    public HexDirection? ArrivalFacing { get; set; }
    public double WaitAfter { get; set; }
    public ConditionDef? Until { get; set; }
    public bool Optional { get; set; }
    public RouteMode? Routing { get; set; }
    public RouteWeights? RouteWeights { get; set; }
    public bool Approach { get; set; }
    public bool AutoTarget { get; set; }
    public TargetLostPolicy? OnTargetLost { get; set; }

    public static ReplayCommand From(Command c) => new()
    {
        Type = c.Type, TargetCell = c.TargetCell, TargetUnitId = c.TargetUnitId, Direction = c.Direction, SkillId = c.SkillId, Duration = c.Duration,
        PolicyOverride = c.PolicyOverride?.Clone(), ArrivalFacing = c.ArrivalFacing, WaitAfter = c.WaitAfter, Until = c.Until == null ? null : ContentJson.Clone(c.Until),
        Optional = c.Optional, Routing = c.Routing, RouteWeights = c.RouteWeights?.Clone(), Approach = c.Approach, AutoTarget = c.AutoTarget, OnTargetLost = c.OnTargetLost,
    };

    public Command ToCommand() => new()
    {
        Type = Type, TargetCell = TargetCell, TargetUnitId = TargetUnitId, Direction = Direction, SkillId = SkillId, Duration = Duration,
        PolicyOverride = PolicyOverride?.Clone(), ArrivalFacing = ArrivalFacing, WaitAfter = WaitAfter, Until = Until == null ? null : ContentJson.Clone(Until),
        Optional = Optional, Routing = Routing, RouteWeights = RouteWeights?.Clone(), Approach = Approach, AutoTarget = AutoTarget, OnTargetLost = OnTargetLost,
    };
}
