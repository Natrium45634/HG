using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>Where an enemy is on a route graph: the node it left, the edge it walks, loop counters, waiting.</summary>
public sealed class RouteNav
{
    public string Route { get; set; } = "";
    public string Node { get; set; } = "";
    public string Edge { get; set; } = "";
    public Dictionary<string, int> Uses { get; } = new(StringComparer.Ordinal);
    public long DwellUntil { get; set; } = -1;
    public bool Finished { get; set; }
    /// <summary>Walking a loop that forbids fighting on the way.</summary>
    public bool Focus { get; set; }
    /// <summary>Nodes passed so far (diagnostics, tests).</summary>
    public List<string> Passed { get; } = new();
    /// <summary>Edges taken so far, in order.</summary>
    public List<string> Taken { get; } = new();
}

public enum RouteStep
{
    /// <summary>New hexes are planned: keep walking.</summary>
    Continue,
    /// <summary>Standing in a node (dwell or no usable exit yet).</summary>
    Wait,
    /// <summary>The route is over.</summary>
    Finished,
}

/// <summary>
/// Walking a route graph (docs/DECISIONS_4.md §57): at the end of an edge the enemy passes the node (its flag, its
/// dwell), then the node's fork rule picks the next edge among the usable ones — the edge's condition holds, its
/// loop has repeats left and no exit condition, its hexes are passable for the enemy. No usable exit: the route's
/// fallback route, otherwise waiting in the node. No exit at all: the end (a patrol starts over).
/// </summary>
public sealed class RouteNavigator
{
    private readonly Simulation _sim;
    private readonly Dictionary<(string Route, string Node), int> _turns = new();

    public RouteNavigator(Simulation sim)
    {
        _sim = sim;
    }

    public static bool IsGraph(RouteDef? r) => r != null && r.Nodes.Count > 0;

    /// <summary>
    /// Puts a unit on a route of the scenario: from its start (a new enemy) or from the nearest node (a switch in
    /// battle). A route of the map (points) is followed as before.
    /// </summary>
    public void Assign(Unit u, string routeId, bool fromStart)
    {
        var r = _sim.Scenario.Def?.Route(routeId);
        u.RouteId = routeId;
        if (!IsGraph(r))
        {
            u.Nav = null;
            var points = _sim.Scenario.RoutePoints(routeId);
            u.Route = points != null ? new List<HexCoord>(points) : null;
            if (u.Route != null)
            {
                int at = fromStart ? u.Route.IndexOf(u.Pos) : -1;
                u.RouteIndex = at >= 0 ? at + 1 : 0;
            }
            return;
        }
        var nav = new RouteNav { Route = r!.Id };
        u.Nav = nav;
        var node = fromStart ? r.StartNode()! : Nearest(r, u.Pos);
        nav.Node = node.Id;
        nav.Passed.Add(node.Id);
        u.Route = new List<HexCoord> { node.C };
        u.RouteIndex = 0;
        if (fromStart || u.Pos == node.C)
        {
            // A new enemy plans its first edge at once (it may stand next to a taken start hex).
            Plan(u, r, node);
            int at = u.Route.IndexOf(u.Pos);
            u.RouteIndex = at >= 0 ? at + 1 : 0;
        }
    }

    /// <summary>The node nearest to a hex (ties — the earlier one), the start when the route has a single node.</summary>
    private static RouteNodeDef Nearest(RouteDef r, HexCoord at) =>
        r.Nodes.OrderBy(n => HexCoord.Distance(at, n.C)).ThenBy(n => r.Nodes.IndexOf(n)).First();

    /// <summary>The unit reached the end of its planned hexes: the next step on the graph.</summary>
    public RouteStep Advance(Unit u, out string why)
    {
        why = "";
        var nav = u.Nav;
        var r = nav == null ? null : _sim.Scenario.Def?.Route(nav.Route);
        if (nav == null || r == null || nav.Finished) return RouteStep.Finished;
        var node = nav.Edge != "" ? r.Node(r.Edge(nav.Edge)?.To ?? "") : r.Node(nav.Node);
        if (node == null) return RouteStep.Finished;
        if (nav.Edge != "")
        {
            // Passing the node: its flag, then its dwell.
            nav.Node = node.Id;
            nav.Edge = "";
            nav.Passed.Add(node.Id);
            if (node.Event != "") _sim.Scenario.SetFlag(node.Event);
            if (node.DwellSeconds > 0) nav.DwellUntil = _sim.Tick + _sim.TicksFor(node.DwellSeconds);
        }
        if (nav.DwellUntil >= 0)
        {
            if (_sim.Tick < nav.DwellUntil)
            {
                why = $"стоит в узле «{(node.Name != "" ? node.Name : node.Id)}»";
                return RouteStep.Wait;
            }
            nav.DwellUntil = -1;
        }
        return Plan(u, r, node, out why);
    }

    private void Plan(Unit u, RouteDef r, RouteNodeDef node) => Plan(u, r, node, out _);

    private RouteStep Plan(Unit u, RouteDef r, RouteNodeDef node, out string why)
    {
        why = "";
        var nav = u.Nav!;
        var outs = r.Out(node.Id).ToList();
        if (outs.Count == 0)
        {
            if (r.End == RouteEndKind.Patrol && r.StartNode() is { } start && start != node)
            {
                // A patrol starts over: back to the start, then along the graph again.
                nav.Node = start.Id;
                nav.Edge = "";
                u.Route = new List<HexCoord> { start.C };
                u.RouteIndex = 0;
                return RouteStep.Continue;
            }
            nav.Finished = true;
            return RouteStep.Finished;
        }
        var e = Choose(u, r, node, outs);
        if (e == null)
        {
            if (r.Fallback != "" && r.Fallback != r.Id && _sim.Scenario.Def?.Route(r.Fallback) is { } alt)
            {
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"{u.Name}: выход из узла «{(node.Name != "" ? node.Name : node.Id)}» маршрута «{r.DisplayName}» закрыт — переходит на «{alt.DisplayName}»", u.Id, -1, u.Pos);
                Assign(u, alt.Id, fromStart: false);
                return RouteStep.Continue;
            }
            why = $"нет доступного выхода из узла «{(node.Name != "" ? node.Name : node.Id)}» — ждёт";
            return RouteStep.Wait;
        }
        nav.Uses[e.Id] = nav.Uses.GetValueOrDefault(e.Id) + 1;
        nav.Taken.Add(e.Id);
        // A cycle nobody controls (an unpublished draft) must not walk forever.
        if (nav.Taken.Count > 10_000 && r.End != RouteEndKind.Patrol)
        {
            nav.Finished = true;
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"{u.Name}: маршрут «{r.DisplayName}» зациклен без выхода — движение по нему остановлено", u.Id, -1, u.Pos);
            return RouteStep.Finished;
        }
        nav.Edge = e.Id;
        nav.Focus = e.Loop is { Engage: false };
        u.Route = r.EdgePath(e);
        u.RouteIndex = u.Route.Count > 0 && u.Route[0] == u.Pos ? 1 : 0;
        return RouteStep.Continue;
    }

    /// <summary>An exit the unit may take now.</summary>
    public bool Usable(Unit u, RouteDef r, RouteEdgeDef e)
    {
        if (e.Condition != null && !_sim.Scenario.Evaluate(e.Condition, u)) return false;
        foreach (var c in e.Cells)
        {
            var cell = _sim.Map.Get(c);
            if (cell == null || !cell.Terrain.IsPassableFor(u.Def.MoveCategory)) return false;
        }
        return true;
    }

    /// <summary>The node's rule among the usable exits (ordered by priority).</summary>
    /// <summary>
    /// A loop is over for this enemy: a loop setting on its cycle reached its repeats or its exit condition holds.
    /// Then the cycle is not entered again from a node that has another way; inside the cycle the enemy still walks
    /// back to that node (it is never trapped).
    /// </summary>
    public bool LoopOver(Unit u, RouteDef r, RouteEdgeDef e)
    {
        if (!r.IsLoopEdge(e)) return false;
        foreach (var f in r.LoopControls(e))
        {
            var loop = f.Loop!;
            int used = u.Nav?.Uses.GetValueOrDefault(f.Id) ?? 0;
            if (loop.MaxRepeats > 0 && used >= loop.MaxRepeats) return true;
            if (loop.ExitCondition != null && _sim.Scenario.Evaluate(loop.ExitCondition, u)) return true;
        }
        return false;
    }

    public RouteEdgeDef? Choose(Unit u, RouteDef r, RouteNodeDef node, List<RouteEdgeDef>? outs = null)
    {
        var open = (outs ?? r.Out(node.Id).ToList()).Where(e => Usable(u, r, e)).ToList();
        var usable = open.Where(e => !LoopOver(u, r, e)).ToList();
        if (usable.Count == 0) usable = open;
        if (usable.Count == 0) return null;
        switch (node.Rule)
        {
            case RouteForkRule.Weights:
            {
                double total = usable.Sum(e => Math.Max(0, e.Weight));
                if (total <= 0) return usable[0];
                double x = _sim.Rng.NextDouble() * total;
                foreach (var e in usable)
                {
                    x -= Math.Max(0, e.Weight);
                    if (x < 0) return e;
                }
                return usable[^1];
            }
            case RouteForkRule.Tags:
            {
                var tags = new HashSet<string>(u.Def.Tags) { u.Def.CreatureCategory, u.Tag, u.Group, u.Def.Id };
                return usable.FirstOrDefault(e => e.Tags.Any(tags.Contains)) ?? usable.FirstOrDefault(e => e.Tags.Count == 0) ?? usable[0];
            }
            case RouteForkRule.Condition:
                return usable.FirstOrDefault(e => e.Condition != null) ?? usable[0];
            case RouteForkRule.Load:
            {
                int Load(RouteEdgeDef e) => _sim.Units.Count(x => x.Alive && x != u && x.Nav?.Route == r.Id && x.Nav.Edge == e.Id);
                return usable.OrderBy(Load).ThenBy(e => usable.IndexOf(e)).First();
            }
            case RouteForkRule.RoundRobin:
            {
                int k = _turns.GetValueOrDefault((r.Id, node.Id));
                _turns[(r.Id, node.Id)] = k + 1;
                return usable[k % usable.Count];
            }
            default:
                return usable[0];
        }
    }
}
