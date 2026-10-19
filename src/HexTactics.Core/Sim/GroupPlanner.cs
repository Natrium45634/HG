using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>Result of distributing a group over individual positions.</summary>
public sealed class GroupAssignment
{
    /// <summary>Unit id → its end hex (missing — no place for it).</summary>
    public Dictionary<int, HexCoord> Cells { get; } = new();
    /// <summary>Unit id → facing at the end (formations facing outward, common facing).</summary>
    public Dictionary<int, HexDirection> Facings { get; } = new();
    public List<int> Unassigned { get; } = new();
    public double TotalCost { get; set; }
    public string Note { get; set; } = "";
    public bool Partial => Unassigned.Count > 0;
}

/// <summary>
/// Group orders (docs/DECISIONS_3.md §44): free hexes around a point (or formation slots, or attack
/// positions), path costs «unit → hex» by each unit's own movement rules, and a minimal-total-cost
/// assignment of distinct hexes (Hungarian algorithm) with role and slot penalties. Deterministic: the
/// same situation always gives the same assignment. Read-only.
/// </summary>
public sealed class GroupPlanner
{
    private readonly Simulation _sim;
    private const double Unreachable = 1e7;

    public GroupPlanner(Simulation sim)
    {
        _sim = sim;
    }

    /// <summary>Effective role: the unit's setting, or by the basic attack (range ≥ 3 — ranged; healing skills — support).</summary>
    public UnitRole RoleOf(Unit u)
    {
        if (u.Def.Role != UnitRole.Auto) return u.Def.Role;
        var basic = u.BasicAttack;
        bool heals = u.Skills.Any(s => s.Stages.Any(st => st.Effects.Any(e => e.Type == EffectType.Heal)));
        if (basic != null && basic.Targeting is TargetingMode.Unit or TargetingMode.Cell && basic.RangeMax >= 3) return UnitRole.Ranged;
        if (heals) return UnitRole.Support;
        if (u.Skills.Count(s => s.Kind == SkillKind.Spell) >= 2) return UnitRole.Caster;
        return UnitRole.Melee;
    }

    /// <summary>Movement costs from a unit's position to every reachable hex (other units of the group do not block).</summary>
    private Dictionary<HexCoord, double> CostsOf(Unit u, HashSet<int> group)
    {
        // Group members are ignored as obstacles (they move too); everyone else blocks as usual.
        var mc = MoveContext.For(_sim, u);
        var reach = _sim.Pathfinder.Flood(_sim.Map, mc, u.StepTo ?? u.Pos, 200, new PathOptions { IgnoreUnits = true, MaxExpanded = 20000 });
        var result = new Dictionary<HexCoord, double>();
        foreach (var (c, cost) in reach)
        {
            var cell = _sim.Map.Get(c)!;
            bool blockedByOther = cell.Occupants.Any(o => o.Alive && o != u && !group.Contains(o.Id) && !Occupancy.CanShare(mc, o));
            if (!blockedByOther) result[c] = cost;
        }
        return result;
    }

    /// <summary>A hex where a group member may end: exists, the unit can stand there, nobody else (outside the group) is there or bound for it.</summary>
    private bool CanEnd(Unit u, Cell cell, HashSet<int> group)
    {
        var mc = MoveContext.For(_sim, u);
        var st = MovementRules.Check(mc, null, cell, true, out _);
        if (st is not (StepCheck.Ok or StepCheck.TooHigh or StepCheck.TooLow)) return false;
        if (!cell.AllowsPlacement) return false;
        foreach (var o in cell.Occupants)
            if (o.Alive && !group.Contains(o.Id) && !Occupancy.CanShare(mc, o))
                return false;
        if (cell.DestinationOf >= 0 && !group.Contains(cell.DestinationOf) && _sim.GetUnit(cell.DestinationOf) is { Alive: true }) return false;
        return true;
    }

    /// <summary>Direction of the group's movement (from its centre to the point), snapped to a hex direction.</summary>
    public HexDirection Heading(IReadOnlyList<Unit> units, HexCoord point)
    {
        double q = units.Average(u => (double)u.Pos.Q), r = units.Average(u => (double)u.Pos.R);
        var centre = new HexCoord((int)Math.Round(q), (int)Math.Round(r));
        return centre.DirectionTo(point) ?? ApproxDirection(point.Q - q, point.R - r);
    }

    private static HexDirection ApproxDirection(double dq, double dr)
    {
        var best = HexDirection.E;
        double bestDot = double.MinValue;
        for (int i = 0; i < 6; i++)
        {
            var o = ((HexDirection)i).Offset();
            double dot = dq * o.Q + dr * o.R + 0.5 * (dq * o.R + dr * o.Q);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = (HexDirection)i;
            }
        }
        return best;
    }

    /// <summary>
    /// End positions of a «move to point» order: the formation's slots (rotated by the heading) or the nearest
    /// free hexes around the point; one hex per unit; minimal total movement cost with role preferences.
    /// </summary>
    public GroupAssignment AssignMove(IReadOnlyList<Unit> units, HexCoord point, FormationDef? formation = null, int maxSpread = 0, HexCoord? surroundTarget = null)
    {
        var res = new GroupAssignment();
        if (units.Count == 0) return res;
        var group = new HashSet<int>(units.Select(u => u.Id));
        var heading = Heading(units, point);
        // Preferred hexes (slots) with a role each; the rest — rings around the point.
        var preferred = new List<(HexCoord Cell, UnitRole Role)>();
        var kind = formation?.Kind ?? FormationKind.Free;
        if (surroundTarget.HasValue) kind = FormationKind.Surround;
        switch (kind)
        {
            case FormationKind.Slots:
                foreach (var s in formation!.Slots)
                {
                    var off = formation.RotateWithDirection ? Rotate(s.Offset, heading) : s.Offset;
                    preferred.Add((point + off, s.Role));
                }
                break;
            case FormationKind.Surround:
            {
                var centre = surroundTarget ?? point;
                foreach (var c in HexCoord.Ring(centre, Math.Max(1, formation?.Radius ?? 1))) preferred.Add((c, UnitRole.Auto));
                break;
            }
            case FormationKind.Perimeter:
                foreach (var c in HexCoord.Ring(point, Math.Max(1, formation?.Radius ?? 1))) preferred.Add((c, UnitRole.Auto));
                break;
        }
        int need = units.Count;
        var candidates = new List<(HexCoord Cell, UnitRole Role, double Penalty)>();
        var seen = new HashSet<HexCoord>();
        // Slots first: an invalid slot is replaced by the nearest valid hex.
        for (int i = 0; i < preferred.Count; i++)
        {
            var (c, role) = preferred[i];
            var cell = _sim.Map.Get(c);
            HexCoord? use = cell != null && units.Any(u => CanEnd(u, cell, group)) ? c : Nearest(units, c, group, seen, surroundTarget);
            if (use == null || !seen.Add(use.Value)) continue;
            candidates.Add((use.Value, role, i * 0.01));
        }
        // Free hexes in rings around the point, up to twice the group (a margin for a good assignment).
        if (kind != FormationKind.Surround || candidates.Count < need)
        {
            int radiusLimit = maxSpread > 0 ? maxSpread : 12;
            for (int r = 0; r <= radiusLimit && candidates.Count < Math.Max(need * 2, need + 6); r++)
            {
                foreach (var c in r == 0 ? new[] { point } : HexCoord.Ring(point, r).ToArray())
                {
                    if (seen.Contains(c) || (surroundTarget.HasValue && c == surroundTarget.Value)) continue;
                    var cell = _sim.Map.Get(c);
                    if (cell == null || !units.Any(u => CanEnd(u, cell, group))) continue;
                    seen.Add(c);
                    // Closeness to the point weighs more than the walk: the point itself, then the nearest rings (spec §7.2);
                    // for a formation these hexes are only a fallback for missing slots.
                    candidates.Add((c, UnitRole.Auto, (kind == FormationKind.Slots ? 20 : 0) + 1 + r * 2.5));
                }
            }
        }
        var costs = units.Select(u => CostsOf(u, group)).ToList();
        var matrix = new double[units.Count, candidates.Count];
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            var role = RoleOf(u);
            for (int j = 0; j < candidates.Count; j++)
            {
                var (c, slotRole, penalty) = candidates[j];
                var cell = _sim.Map.Get(c)!;
                if (!CanEnd(u, cell, group) || !costs[i].TryGetValue(c, out var cost))
                {
                    matrix[i, j] = Unreachable;
                    continue;
                }
                double m = cost + penalty;
                if (slotRole != UnitRole.Auto && slotRole != role) m += 3;
                // Free movement: melee prefers the front (towards the heading), ranged and support the back.
                if (kind == FormationKind.Free)
                {
                    int along = Along(c - point, heading);
                    m += role is UnitRole.Melee or UnitRole.Tank ? -0.2 * along : role is UnitRole.Ranged or UnitRole.Support or UnitRole.Caster ? 0.2 * along : 0;
                }
                matrix[i, j] = m;
            }
        }
        var assign = Hungarian(matrix);
        for (int i = 0; i < units.Count; i++)
        {
            int j = assign[i];
            if (j < 0 || matrix[i, j] >= Unreachable)
            {
                res.Unassigned.Add(units[i].Id);
                continue;
            }
            res.Cells[units[i].Id] = candidates[j].Cell;
            res.TotalCost += matrix[i, j];
            if (formation?.FaceOutward == true || kind == FormationKind.Perimeter)
                res.Facings[units[i].Id] = (surroundTarget ?? point).DirectionTo(candidates[j].Cell) ?? heading;
            else if (kind == FormationKind.Surround && surroundTarget.HasValue)
                res.Facings[units[i].Id] = candidates[j].Cell.DirectionTo(surroundTarget.Value) ?? heading;
            else
                res.Facings[units[i].Id] = heading;
        }
        if (res.Partial) res.Note = $"мест хватило на {res.Cells.Count} из {units.Count}";
        return res;
    }

    /// <summary>Signed distance along a direction (positive — ahead).</summary>
    private static int Along(HexCoord off, HexDirection d)
    {
        int best = 0;
        var o = d.Offset();
        // Projection by cube coordinates: the step towards d increases (o · off) in the hex metric.
        int dot = off.Q * o.Q + off.R * o.R + (-off.Q - off.R) * (-o.Q - o.R);
        best = dot / 2;
        return best;
    }

    private HexCoord? Nearest(IReadOnlyList<Unit> units, HexCoord c, HashSet<int> group, HashSet<HexCoord> taken, HexCoord? exclude)
    {
        for (int r = 1; r <= 4; r++)
            foreach (var n in HexCoord.Ring(c, r))
            {
                if (taken.Contains(n) || (exclude.HasValue && n == exclude.Value)) continue;
                var cell = _sim.Map.Get(n);
                if (cell != null && units.Any(u => CanEnd(u, cell, group))) return n;
            }
        return null;
    }

    /// <summary>Rotates a slot offset (defined for movement to the east) to the heading.</summary>
    public static HexCoord Rotate(HexCoord off, HexDirection heading)
    {
        // E = 0; direction indices grow counter-clockwise, as Rotate does.
        return off.Rotate((int)heading);
    }

    /// <summary>
    /// Group attack positions (docs/DECISIONS_3.md §44): for each unit the hexes from which its attack (basic or
    /// chosen skill) reaches the target — melee next to it, ranged in range with line of sight, with a penalty
    /// for standing in another shooter's line; support units are not sent into melee. Distinct hexes, minimal cost.
    /// </summary>
    public GroupAssignment AssignAttack(IReadOnlyList<Unit> units, Unit target, Func<Unit, SkillDef?> skillOf)
    {
        var res = new GroupAssignment();
        var group = new HashSet<int>(units.Select(u => u.Id));
        var costs = units.Select(u => CostsOf(u, group)).ToList();
        var candidates = new List<HexCoord>();
        var seen = new HashSet<HexCoord>();
        int maxRange = units.Select(u => skillOf(u)?.RangeMax ?? 1).DefaultIfEmpty(1).Max() + 3;
        foreach (var c in HexCoord.Spiral(target.Pos, maxRange))
        {
            if (c == target.Pos || !seen.Add(c)) continue;
            var cell = _sim.Map.Get(c);
            if (cell != null && units.Any(u => CanEnd(u, cell, group))) candidates.Add(c);
        }
        var matrix = new double[units.Count, candidates.Count];
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            var s = skillOf(u);
            var role = RoleOf(u);
            for (int j = 0; j < candidates.Count; j++)
            {
                var c = candidates[j];
                var cell = _sim.Map.Get(c)!;
                if (s == null || !CanEnd(u, cell, group) || !costs[i].TryGetValue(c, out var cost))
                {
                    matrix[i, j] = Unreachable;
                    continue;
                }
                var face = c.DirectionTo(target.Pos) ?? u.Facing;
                var chk = _sim.Skills.Validate(u, s, target.Pos, target.Id, face, c, face, checkCosts: false, checkState: false);
                if (!chk.Ok)
                {
                    matrix[i, j] = Unreachable;
                    continue;
                }
                double m = cost;
                int d = HexCoord.Distance(c, target.Pos);
                // Support stays out of melee; ranged prefer not to stand next to the target.
                if (role == UnitRole.Support && d <= 1) m += 50;
                if (role is UnitRole.Ranged or UnitRole.Caster && d <= 1 && s.RangeMax > 1) m += 4;
                matrix[i, j] = m;
            }
        }
        // Lines of fire: a hex lying on the straight line between a ranged unit's likely spot and the target is penalised for others.
        var assign = Hungarian(matrix);
        for (int i = 0; i < units.Count; i++)
        {
            int j = assign[i];
            if (j < 0 || matrix[i, j] >= Unreachable)
            {
                res.Unassigned.Add(units[i].Id);
                continue;
            }
            res.Cells[units[i].Id] = candidates[j];
            res.Facings[units[i].Id] = candidates[j].DirectionTo(target.Pos) ?? units[i].Facing;
            res.TotalCost += matrix[i, j];
        }
        // Second pass: shooters whose line of fire passes through another member's hex move if a free alternative exists.
        FixFireLines(units, target, res, candidates, matrix);
        if (res.Partial) res.Note = $"позиций для атаки хватило на {res.Cells.Count} из {units.Count}";
        return res;
    }

    private void FixFireLines(IReadOnlyList<Unit> units, Unit target, GroupAssignment res, List<HexCoord> candidates, double[,] matrix)
    {
        var taken = new HashSet<HexCoord>(res.Cells.Values);
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!res.Cells.TryGetValue(u.Id, out var at) || HexCoord.Distance(at, target.Pos) <= 1) continue;
            bool blocked = HexCoord.Line(at, target.Pos).Skip(1).SkipLast(1).Any(c => taken.Contains(c));
            if (!blocked) continue;
            int bestJ = -1;
            double best = double.MaxValue;
            for (int j = 0; j < candidates.Count; j++)
            {
                var c = candidates[j];
                if (matrix[i, j] >= Unreachable || taken.Contains(c)) continue;
                if (HexCoord.Line(c, target.Pos).Skip(1).SkipLast(1).Any(x => taken.Contains(x))) continue;
                if (matrix[i, j] < best)
                {
                    best = matrix[i, j];
                    bestJ = j;
                }
            }
            if (bestJ < 0) continue;
            taken.Remove(at);
            taken.Add(candidates[bestJ]);
            res.Cells[u.Id] = candidates[bestJ];
            res.Facings[u.Id] = candidates[bestJ].DirectionTo(target.Pos) ?? u.Facing;
        }
    }

    /// <summary>
    /// «Disperse»: every unit takes a hex at least two steps from the others near its own position.
    /// </summary>
    public GroupAssignment AssignDisperse(IReadOnlyList<Unit> units, int spacing = 2)
    {
        var res = new GroupAssignment();
        var group = new HashSet<int>(units.Select(u => u.Id));
        var taken = new List<HexCoord>();
        foreach (var u in units.OrderBy(x => x.Id))
        {
            var costs = CostsOf(u, group);
            HexCoord? best = null;
            double bestCost = double.MaxValue;
            foreach (var (c, cost) in costs.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key.Q).ThenBy(kv => kv.Key.R))
            {
                if (cost > bestCost) break;
                var cell = _sim.Map.Get(c)!;
                if (!CanEnd(u, cell, group) || taken.Any(t => HexCoord.Distance(t, c) < spacing)) continue;
                best = c;
                bestCost = cost;
                break;
            }
            if (best == null)
            {
                res.Unassigned.Add(u.Id);
                continue;
            }
            taken.Add(best.Value);
            res.Cells[u.Id] = best.Value;
            res.Facings[u.Id] = u.Facing;
        }
        return res;
    }

    /// <summary>
    /// Minimal-cost assignment of rows (units) to distinct columns (hexes), rows ≤ columns (Hungarian algorithm,
    /// O(n²m)). Returns the column of every row (-1 when there are fewer columns than rows).
    /// </summary>
    public static int[] Hungarian(double[,] cost)
    {
        int n = cost.GetLength(0), m = cost.GetLength(1);
        var result = Enumerable.Repeat(-1, n).ToArray();
        if (n == 0 || m == 0) return result;
        bool transposed = n > m;
        if (transposed)
        {
            // More units than hexes: solve for the hexes and map back.
            var t = new double[m, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                t[j, i] = cost[i, j];
            var cols = Hungarian(t);
            for (int j = 0; j < m; j++)
                if (cols[j] >= 0)
                    result[cols[j]] = j;
            return result;
        }
        // Classic potentials method, 1-based.
        var u = new double[n + 1];
        var v = new double[m + 1];
        var p = new int[m + 1];
        var way = new int[m + 1];
        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = Enumerable.Repeat(double.MaxValue, m + 1).ToArray();
            var used = new bool[m + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                double delta = double.MaxValue;
                for (int j = 1; j <= m; j++)
                {
                    if (used[j]) continue;
                    double cur = cost[i0 - 1, j - 1] - u[i0] - v[j];
                    if (cur < minv[j] - 1e-12)
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }
                    if (minv[j] < delta - 1e-12)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }
                for (int j = 0; j <= m; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }
                j0 = j1;
            } while (p[j0] != 0);
            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }
        for (int j = 1; j <= m; j++)
            if (p[j] > 0)
                result[p[j] - 1] = j - 1;
        return result;
    }
}
