using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Checks of the zones of deployment on the battle as it starts (ТЗ ч. 6, 12.2; docs/DECISIONS_6.md §97): which hexes
/// take a chosen fighter and why the others do not; whether a fighter called into a zone can walk to the points that
/// matter — the hexes of the objectives, the spawn bases of the enemy, the ends of their routes.
/// </summary>
public static class DeploymentCheck
{
    /// <summary>The battle of the scenario as it starts, without the squad (the hexes are checked for any template).</summary>
    public static Simulation? Battle(ContentDatabase db, ScenarioDef sc)
    {
        var copy = ContentJson.Clone(sc);
        copy.Squad = null;
        try
        {
            return ScenarioSystem.Create(db, copy, 1, admin: true);
        }
        catch (System.Exception)
        {
            return null; // a broken scenario — the other checks say why
        }
    }

    /// <summary>The hexes the calls go to: those of the zones of deployment, or the zone of placement.</summary>
    public static IEnumerable<HexCoord> Hexes(Simulation sim, ScenarioDef sc) =>
        sc.DeploymentZones.Count > 0 ? sc.DeploymentZones.Where(z => sim.IsPlayerFaction(z.Faction)).SelectMany(z => z.Cells).Distinct() : sim.Placement.Zone;

    /// <summary>Every hex of the zones for a template: null — it may stand there, otherwise the reason.</summary>
    public static Dictionary<HexCoord, string?> For(Simulation sim, ScenarioDef sc, UnitDef def) =>
        Hexes(sim, sc).ToDictionary(c => c, c => sim.Deployment.CellProblem(def, c));

    /// <summary>The points a called fighter should be able to reach.</summary>
    public static List<(HexCoord At, string Name)> Targets(ScenarioDef sc)
    {
        var list = new List<(HexCoord, string)>();
        foreach (var o in sc.Objectives)
            foreach (var c in o.Cells.Take(1))
                list.Add((c, $"цель «{o.Name}»"));
        foreach (var o in sc.Objects.Where(o => o.Type == ScenarioObjectType.SpawnBase))
            list.Add((o.Anchor, $"база спавна «{o.DisplayName}»"));
        return list;
    }

    /// <summary>
    /// The zones from which a fighter of <paramref name="walker"/> (on foot when null) reaches none of the targets. Empty —
    /// all fine, or nothing to reach.
    /// </summary>
    public static List<string> Unreachable(ContentDatabase db, ScenarioDef sc, UnitDef? walker = null)
    {
        var problems = new List<string>();
        var targets = Targets(sc);
        if (targets.Count == 0 || sc.DeploymentZones.Count == 0) return problems;
        var sim = Battle(db, sc);
        if (sim == null) return problems;
        walker ??= SquadRules.PoolOf(db, sc).FirstOrDefault() ?? db.All<UnitDef>().FirstOrDefault(u => u.Faction == "player");
        if (walker == null) return problems;
        var mc = MoveContext.For(sim, walker, sim.Placement.Faction);
        foreach (var z in sc.DeploymentZones.Where(z => z.Cells.Count > 0 && sim.IsPlayerFaction(z.Faction)))
        {
            var starts = z.Cells.Where(c => sim.Map.Get(c) is { } cell && !cell.Blocked && mc.Passable(cell.Terrain)).Take(3).ToList();
            if (starts.Count == 0) continue; // «all hexes closed» is told by the validator
            bool any = targets.Any(t => starts.Any(s =>
            {
                var p = sim.Pathfinder.Find(sim.Map, mc, s, t.At, new PathOptions { IgnoreUnits = true, GoalTolerance = 1, MaxExpanded = 6000 });
                return p.Found;
            }));
            if (!any) problems.Add($"из зоны «{(z.Name != "" ? z.Name : z.Id)}» «{walker.DisplayName}» не дойдёт ни до одной цели ({string.Join(", ", targets.Take(3).Select(t => t.Name))})");
        }
        return problems;
    }
}
