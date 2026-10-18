using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Pits and falling (docs/DECISIONS_2.md §21). A unit that ends up in a pit cell that affects it
/// (layer, immunity, rescue statuses) falls: death, damage + return to the last safe cell, or
/// removal from battle. Death goes through the common death procedure, so the unit's queue,
/// preparation, reservations, blocking links and auto-attacks are all cleared.
/// </summary>
public sealed class FallSystem
{
    private readonly Simulation _sim;

    public FallSystem(Simulation sim)
    {
        _sim = sim;
    }

    public int Falls { get; private set; }

    public bool Affects(Unit u, Cell c) => u.Alive && LayerResolver.PitAffects(c.Terrain, u.Layer, u.LayerDef, u.Def, u);

    /// <summary>Checks the unit's cell; returns true if the unit fell.</summary>
    public bool CheckCell(Unit u, Cell cell, Unit? source, string cause)
    {
        if (!Affects(u, cell)) return false;
        Fall(u, cell, source, cause);
        return true;
    }

    /// <summary>Every unit standing in the cell (terrain became a pit under them).</summary>
    public void CheckOccupants(Cell cell, Unit? source, string cause)
    {
        foreach (var u in cell.Occupants.ToArray())
            if (u.Alive)
                CheckCell(u, cell, source, cause);
    }

    private void Fall(Unit u, Cell cell, Unit? source, string cause)
    {
        Falls++;
        var pit = cell.Terrain.Pit;
        string why = string.IsNullOrEmpty(cause) ? $"падение в «{cell.Terrain.DisplayName}»" : $"падение в «{cell.Terrain.DisplayName}» ({cause})";
        _sim.Emit(SimEventType.Fall, LogLevel.Normal, $"{u.Name} падает в {cell.Terrain.DisplayName} {cell.Coord}", source?.Id ?? -1, u.Id, cell.Coord);
        switch (pit.Consequence)
        {
            case FallConsequence.Death:
                u.DeathCause = DeathCause.Fall;
                _sim.Combat.Kill(u, source, why);
                break;
            case FallConsequence.Remove:
                u.DeathCause = DeathCause.Removed;
                _sim.Combat.Kill(u, null, "убран из боя: " + why);
                break;
            default:
            {
                _sim.Commands.ClearQueue(u, "падение", CommandStatus.Cancelled);
                if (u.Casting != null) _sim.Skills.Interrupt(u, InterruptCause.ForcedMove, "падение");
                var back = SafeCellNear(u, u.LastSafePos);
                if (back == null)
                {
                    u.DeathCause = DeathCause.Fall;
                    _sim.Combat.Kill(u, source, why + " — некуда выбраться");
                    return;
                }
                _sim.Relocate(u, back.Value);
                _sim.Emit(SimEventType.Displaced, LogLevel.Normal, $"{u.Name} выбирается на {back.Value}", -1, u.Id, back.Value);
                if (pit.FallDamage > 0) _sim.Combat.DealDamage(source, u, pit.FallDamage, string.IsNullOrEmpty(pit.DamageType) ? "true" : pit.DamageType, null, why, null, DamageSource.Fall);
                break;
            }
        }
    }

    private HexCoord? SafeCellNear(Unit u, HexCoord around)
    {
        var mc = MoveContext.For(_sim, u);
        foreach (var c in HexCoord.Spiral(around, 4))
        {
            var cell = _sim.Map.Get(c);
            if (cell == null || Affects(u, cell)) continue;
            if (MovementRules.Check(mc, null, cell, false, out _) == StepCheck.Ok) return c;
        }
        return null;
    }
}
