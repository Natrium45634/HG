using System;
using System.Collections.Generic;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public sealed class PathOptions
{
    public bool IgnoreUnits { get; init; }
    /// <summary>Extra cost added for hazardous cells (AI uses its profile; the player gets a small tie-breaker).</summary>
    public double HazardPenalty { get; init; }
    /// <summary>Stop when reaching any cell within this distance of the goal (AI approach). 0 = exact.</summary>
    public int GoalTolerance { get; init; }
    public int MaxExpanded { get; init; } = 20000;
    /// <summary>On failure, also search the closest reachable cell (UI suggestion). Costs an extra bounded flood.</summary>
    public bool ComputeNearest { get; init; }
    /// <summary>Cells treated as impassable (detour around a blocker's zone).</summary>
    public HashSet<HexCoord>? Avoid { get; init; }
}

public sealed class PathResult
{
    public bool Found { get; init; }
    public List<HexCoord> Path { get; init; } = new();
    public List<double> StepCosts { get; init; } = new();
    public double Cost { get; init; }
    public string Reason { get; init; } = "";
    public FailureReason Failure { get; init; }
    /// <summary>Closest reachable cell to the goal when the goal is unreachable (suggestion for the player).</summary>
    public HexCoord? Nearest { get; init; }
    public int Expanded { get; init; }

    public HexCoord? End => Path.Count > 0 ? Path[^1] : null;
}

/// <summary>
/// A* over cell indices with minimal total entry cost. Heuristic = hex distance × minimal
/// step cost on the map (admissible). Ties are broken by insertion order → deterministic.
/// Buffers are reused between calls (generation stamps), so repeated queries do not allocate much.
/// </summary>
public sealed class Pathfinder
{
    private double[] _g = Array.Empty<double>();
    private int[] _from = Array.Empty<int>();
    private int[] _stamp = Array.Empty<int>();
    private int[] _closedStamp = Array.Empty<int>();
    private int _gen;
    private readonly PriorityQueue<int, (double F, long Order)> _open = new();

    public int TotalSearches { get; private set; }

    private void Ensure(int n)
    {
        if (_g.Length >= n) return;
        _g = new double[n];
        _from = new int[n];
        _stamp = new int[n];
        _closedStamp = new int[n];
        _gen = 0;
    }

    public PathResult Find(Simulation sim, Unit unit, HexCoord start, HexCoord goal, PathOptions? options = null) =>
        Find(sim.Map, MoveContext.For(sim, unit), start, goal, options ?? new PathOptions());

    public PathResult Find(BattleMap map, in MoveContext mc, HexCoord start, HexCoord goal, PathOptions opt)
    {
        TotalSearches++;
        var startCell = map.Get(start);
        var goalCell = map.Get(goal);
        if (startCell == null)
            return new PathResult { Found = false, Reason = "юнит вне карты", Failure = FailureReason.Invalid };
        if (start == goal)
            return new PathResult { Found = true };

        if (opt.GoalTolerance == 0)
        {
            if (goalCell == null)
                return new PathResult { Found = false, Reason = "клетки нет на карте", Failure = FailureReason.NoPath };
            var st = MovementRules.Check(mc, null, goalCell, opt.IgnoreUnits, out _);
            if (st != StepCheck.Ok && st != StepCheck.TooHigh && st != StepCheck.TooLow)
            {
                var nearest = opt.ComputeNearest ? NearestReachable(map, mc, start, goal, opt) : null;
                return new PathResult
                {
                    Found = false,
                    Reason = "цель недоступна: " + MovementRules.Describe(st, goalCell),
                    Failure = FailureReason.NoPath,
                    Nearest = nearest,
                };
            }
        }

        Ensure(map.Count);
        _gen++;
        if (_gen == int.MaxValue)
        {
            Array.Clear(_stamp);
            Array.Clear(_closedStamp);
            _gen = 1;
        }
        _open.Clear();
        long order = 0;
        double hScale = map.MinStepCost;

        int s = startCell.Index;
        _g[s] = 0;
        _from[s] = -1;
        _stamp[s] = _gen;
        _open.Enqueue(s, (HexCoord.Distance(start, goal) * hScale, order++));

        int bestIdx = s;
        int bestDist = HexCoord.Distance(start, goal);
        double bestCost = 0;
        int expanded = 0;
        int found = -1;

        while (_open.Count > 0)
        {
            int cur = _open.Dequeue();
            if (_closedStamp[cur] == _gen) continue;
            _closedStamp[cur] = _gen;
            var cell = map.Cells[cur];
            int dist = HexCoord.Distance(cell.Coord, goal);
            if (dist < bestDist || (dist == bestDist && _g[cur] < bestCost))
            {
                bestDist = dist;
                bestIdx = cur;
                bestCost = _g[cur];
            }
            if (dist <= opt.GoalTolerance)
            {
                found = cur;
                break;
            }
            if (++expanded > opt.MaxExpanded) break;

            for (int d = 0; d < 6; d++)
            {
                var n = cell.Neighbors[d];
                if (n == null || _closedStamp[n.Index] == _gen) continue;
                var check = MovementRules.Check(mc, cell, n, opt.IgnoreUnits, out var cost);
                if (check != StepCheck.Ok) continue;
                if (opt.Avoid != null && opt.Avoid.Contains(n.Coord)) continue;
                if (opt.HazardPenalty > 0 && n.IsHazard) cost += opt.HazardPenalty;
                double ng = _g[cur] + cost;
                if (_stamp[n.Index] != _gen || ng < _g[n.Index] - 1e-9)
                {
                    _stamp[n.Index] = _gen;
                    _g[n.Index] = ng;
                    _from[n.Index] = cur;
                    _open.Enqueue(n.Index, (ng + HexCoord.Distance(n.Coord, goal) * hScale, order++));
                }
            }
        }

        if (found < 0)
        {
            return new PathResult
            {
                Found = false,
                Reason = expanded > opt.MaxExpanded ? "поиск пути прерван: слишком большая область" : "нет пути (препятствия, высота или занятые клетки)",
                Failure = FailureReason.NoPath,
                Nearest = bestIdx != s ? map.Cells[bestIdx].Coord : null,
                Expanded = expanded,
            };
        }

        var path = new List<HexCoord>();
        var costs = new List<double>();
        for (int i = found; i != s; i = _from[i])
        {
            path.Add(map.Cells[i].Coord);
            costs.Add(_g[i] - _g[_from[i]]);
        }
        path.Reverse();
        costs.Reverse();
        // Report pure movement cost (without hazard penalty) for AP/time estimates.
        double pure = 0;
        var prev = startCell;
        for (int i = 0; i < path.Count; i++)
        {
            var c = map.Get(path[i])!;
            double ec = MovementRules.EntryCost(mc, prev, c);
            costs[i] = ec;
            pure += ec;
            prev = c;
        }
        return new PathResult { Found = true, Path = path, StepCosts = costs, Cost = pure, Expanded = expanded };
    }

    private HexCoord? NearestReachable(BattleMap map, in MoveContext mc, HexCoord start, HexCoord goal, PathOptions opt)
    {
        var reach = Flood(map, mc, start, double.MaxValue, new PathOptions { IgnoreUnits = opt.IgnoreUnits, MaxExpanded = 4000 });
        HexCoord? best = null;
        int bestD = int.MaxValue;
        double bestC = double.MaxValue;
        foreach (var kv in reach)
        {
            if (kv.Key == start) continue;
            int d = HexCoord.Distance(kv.Key, goal);
            if (d < bestD || (d == bestD && kv.Value < bestC))
            {
                bestD = d;
                bestC = kv.Value;
                best = kv.Key;
            }
        }
        if (best.HasValue && HexCoord.Distance(start, goal) <= bestD) return null;
        return best;
    }

    /// <summary>Dijkstra flood: every reachable cell with its minimal cost, up to maxCost.</summary>
    public Dictionary<HexCoord, double> Flood(Simulation sim, Unit u, HexCoord start, double maxCost, PathOptions? opt = null) =>
        Flood(sim.Map, MoveContext.For(sim, u), start, maxCost, opt);

    public Dictionary<HexCoord, double> Flood(BattleMap map, in MoveContext mc, HexCoord start, double maxCost, PathOptions? opt = null)
    {
        opt ??= new PathOptions();
        var result = new Dictionary<HexCoord, double>();
        var startCell = map.Get(start);
        if (startCell == null) return result;
        var pq = new PriorityQueue<Cell, (double, long)>();
        long order = 0;
        var best = new Dictionary<int, double> { [startCell.Index] = 0 };
        pq.Enqueue(startCell, (0, order++));
        int expanded = 0;
        while (pq.TryDequeue(out var cell, out var pri))
        {
            if (result.ContainsKey(cell.Coord)) continue;
            result[cell.Coord] = pri.Item1;
            if (++expanded > opt.MaxExpanded) break;
            for (int d = 0; d < 6; d++)
            {
                var n = cell.Neighbors[d];
                if (n == null || result.ContainsKey(n.Coord)) continue;
                if (MovementRules.Check(mc, cell, n, opt.IgnoreUnits, out var cost) != StepCheck.Ok) continue;
                double ng = pri.Item1 + cost;
                if (ng > maxCost + 1e-9) continue;
                if (best.TryGetValue(n.Index, out var old) && old <= ng) continue;
                best[n.Index] = ng;
                pq.Enqueue(n, (ng, order++));
            }
        }
        return result;
    }
}
