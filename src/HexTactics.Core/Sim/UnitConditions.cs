using System;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>What a condition of a skill stage or a passive is evaluated against.</summary>
public readonly record struct ConditionContext(Unit Self, Unit? Target, HexCoord Anchor, SeriesState? Series);

/// <summary>
/// Evaluates <see cref="UnitConditionDef"/> trees (docs/DECISIONS_3.md §45, §46). Pure reads: no generator
/// draws, so previews and real resolution agree. The tree is finite by construction (JSON has no references),
/// the depth is bounded by the validator.
/// </summary>
public sealed class UnitConditions
{
    private const int MaxDepth = 32;
    private readonly Simulation _sim;

    public UnitConditions(Simulation sim)
    {
        _sim = sim;
    }

    public bool Evaluate(UnitConditionDef? c, in ConditionContext ctx) => c == null || Eval(c, ctx, 0);

    private bool Eval(UnitConditionDef c, in ConditionContext ctx, int depth)
    {
        if (depth > MaxDepth) return false;
        switch (c.Type)
        {
            case UnitConditionType.Always:
                return true;
            case UnitConditionType.All:
                foreach (var x in c.Children)
                    if (!Eval(x, ctx, depth + 1))
                        return false;
                return true;
            case UnitConditionType.Any:
                foreach (var x in c.Children)
                    if (Eval(x, ctx, depth + 1))
                        return true;
                return false;
            case UnitConditionType.Not:
                if (c.Children.Count == 0) return false;
                foreach (var x in c.Children)
                    if (!Eval(x, ctx, depth + 1))
                        return true;
                return false;
            case UnitConditionType.TargetAlive:
                return ctx.Target is { Alive: true };
            case UnitConditionType.PreviousHit:
                return ctx.Series?.LastHit ?? true;
            case UnitConditionType.PreviousEffect:
                return ctx.Series?.LastEffect ?? true;
        }

        var who = c.Subject == ConditionSubject.Self ? ctx.Self : ctx.Target;
        switch (c.Type)
        {
            case UnitConditionType.HasStatus:
            {
                if (who == null || string.IsNullOrEmpty(c.Status)) return false;
                var st = who.FindStatus(c.Status);
                if (st == null) return false;
                return c.Value <= 0 || Math.Max(st.Points, st.Stacks) + 1e-9 >= c.Value;
            }
            case UnitConditionType.HpFraction:
                return who != null && Compare(who.HpFraction, c.Compare, c.Value);
            case UnitConditionType.Resource:
            {
                if (who == null) return false;
                double v = string.IsNullOrEmpty(c.Resource) ? who.Ap : who.Resources.TryGetValue(c.Resource, out var r) ? r.Current : double.NaN;
                return !double.IsNaN(v) && Compare(v, c.Compare, c.Value);
            }
            case UnitConditionType.OnTerrain:
            {
                if (who == null) return false;
                var cell = _sim.Map.Get(who.Pos);
                if (cell == null) return false;
                foreach (var t in c.Terrains)
                {
                    if (t == cell.Terrain.Id) return true;
                    if (t.StartsWith("kind:", StringComparison.Ordinal) && string.Equals(t[5..], cell.Terrain.Kind.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
                    if (t.StartsWith("tag:", StringComparison.Ordinal) && (cell.Terrain.Tags.Contains(t[4..]) || cell.Tags.Contains(t[4..]))) return true;
                    if (cell.Effects.Any(e => e.Def.Id == t)) return true;
                }
                return false;
            }
            case UnitConditionType.UnitType:
                return who != null && c.Types.Any(t => TypeMatches(who, t));
            case UnitConditionType.UnitsInArea:
            {
                var centre = c.Centre switch
                {
                    AreaCentre.Self => ctx.Self.Pos,
                    AreaCentre.Target => ctx.Target?.Pos ?? ctx.Anchor,
                    _ => ctx.Anchor,
                };
                int n = 0;
                foreach (var o in _sim.Units)
                {
                    if (!o.Alive || o == ctx.Self || HexCoord.Distance(o.Pos, centre) > c.Radius) continue;
                    bool ok = c.Side switch
                    {
                        UnitSideFilter.Enemies => _sim.IsHostile(ctx.Self, o),
                        UnitSideFilter.Allies => _sim.RelationOf(ctx.Self, o) == Relation.Ally,
                        _ => true,
                    };
                    if (ok) n++;
                }
                return Compare(n, c.Compare, c.Count);
            }
            case UnitConditionType.AfterSkill:
                return ctx.Self.LastSkillUse.TryGetValue(c.Skill, out var at) && (_sim.Tick - at) * _sim.Dt <= c.Seconds + 1e-9;
            case UnitConditionType.BossPhase:
                return who != null && Compare(_sim.Scenario.PhaseNumberOf(who), c.Compare, c.Value);
            case UnitConditionType.Scenario:
                return c.ScenarioCondition != null && _sim.Scenario.Evaluate(c.ScenarioCondition, ctx.Self);
        }
        return false;
    }

    private static bool Compare(double v, CompareMode mode, double x) => mode == CompareMode.AtLeast ? v + 1e-9 >= x : v <= x + 1e-9;

    /// <summary>Creature category, unit ID, «tag:x» (unit or definition tag) or «role:x».</summary>
    public bool TypeMatches(Unit u, string t)
    {
        if (string.IsNullOrEmpty(t)) return false;
        if (t == u.Def.CreatureCategory || t == u.Def.Id || t == u.Tag) return true;
        if (t.StartsWith("tag:", StringComparison.Ordinal)) return u.Def.Tags.Contains(t[4..]) || u.Tag == t[4..];
        if (t.StartsWith("role:", StringComparison.Ordinal)) return string.Equals(_sim.Groups.RoleOf(u).ToString(), t[5..], StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
