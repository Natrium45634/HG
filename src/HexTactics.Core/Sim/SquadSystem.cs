using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>Where a fighter of the squad is (ТЗ ч. 6, 3.9): the transitions of part 6 grow from these.</summary>
public enum RosterState
{
    /// <summary>In the squad, not on the field: may be placed.</summary>
    Ready,
    /// <summary>On the field.</summary>
    Deployed,
    /// <summary>Fell in battle.</summary>
    Defeated,
}

/// <summary>A fighter of the squad: one entry — one instance at most (ТЗ ч. 6, 3.9).</summary>
public sealed class RosterEntry
{
    public int Index { get; init; }
    public string Template { get; init; } = "";
    public RosterState State { get; set; } = RosterState.Ready;
    /// <summary>The unit on the field (−1 — none).</summary>
    public int Unit { get; set; } = -1;

    public override string ToString() => $"#{Index + 1} {Template}: {State}" + (Unit >= 0 ? $" (юнит {Unit})" : "");
}

/// <summary>
/// The player's squad in battle (ТЗ ч. 6, 2.4–2.6; docs/DECISIONS_6.md §96): the fighters chosen before the battle, who of
/// them is on the field, in reserve or fallen. «Размер отряда» (the entries) is not «одновременно на поле». Placing goes
/// through the placement system — the same checks of zone, terrain and occupancy.
/// </summary>
public sealed class SquadSystem
{
    private readonly Simulation _sim;
    private readonly List<RosterEntry> _entries = new();

    public SquadSystem(Simulation sim) => _sim = sim;

    /// <summary>The scenario has a squad of the player.</summary>
    public bool Enabled { get; private set; }
    public IReadOnlyList<RosterEntry> Entries => _entries;

    public RosterEntry? EntryOf(Unit u) => _entries.FirstOrDefault(e => e.Unit == u.Id);

    public int Count(RosterState s) => _entries.Count(e => e.State == s);

    /// <summary>The first fighter of a template waiting in the squad.</summary>
    public RosterEntry? Ready(string template) => _entries.FirstOrDefault(e => e.Template == template && e.State == RosterState.Ready);

    internal void Load(ScenarioDef sc)
    {
        if (sc.Squad is not { Enabled: true } squad) return;
        Enabled = true;
        var roster = _sim.Options.Roster ?? SquadRules.DefaultRoster(_sim.Content, sc);
        foreach (var id in roster)
            if (_sim.Content.Unit(id) != null)
                _entries.Add(new RosterEntry { Index = _entries.Count, Template = id });
        // Who stands on the field at once (the rules of the mode).
        var onField = _sim.Options.SquadStart switch
        {
            SquadStart.AllOnField => _entries.ToList(),
            SquadStart.StartDeployed => Starting(squad.StartDeployed),
            _ => new List<RosterEntry>(),
        };
        foreach (var e in onField)
        {
            var def = _sim.Content.Unit(e.Template)!;
            var spot = _sim.Placement.AutoSpot(def);
            if (spot == null)
            {
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"Отряд: для «{def.DisplayName}» нет свободного гекса в зоне расстановки — остаётся в резерве");
                continue;
            }
            var u = _sim.Placement.Place(e.Template, spot.Value, out var why, _sim.Placement.FacingFrom(spot.Value), free: true);
            if (u == null)
            {
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"Отряд: «{def.DisplayName}» не поставлен — {why}");
                continue;
            }
            Bind(e, u);
        }
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Отряд: {_entries.Count} бойцов, на поле {Count(RosterState.Deployed)}");
    }

    /// <summary>The entries of the starting deployment: each ID of the list takes one waiting fighter of its template.</summary>
    private List<RosterEntry> Starting(List<string> ids)
    {
        var taken = new List<RosterEntry>();
        foreach (var id in ids)
            if (_entries.FirstOrDefault(e => e.Template == id && !taken.Contains(e)) is { } e)
                taken.Add(e);
        return taken;
    }

    internal void Bind(RosterEntry e, Unit u)
    {
        e.State = RosterState.Deployed;
        e.Unit = u.Id;
    }

    /// <summary>A unit of the squad left the field: it fell, or was taken back while preparing.</summary>
    internal void OnRemoved(Unit u, bool died)
    {
        if (EntryOf(u) is not { } e) return;
        e.State = died ? RosterState.Defeated : RosterState.Ready;
        e.Unit = -1;
    }
}
