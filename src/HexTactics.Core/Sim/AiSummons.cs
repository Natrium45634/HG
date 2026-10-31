using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// The simple AI of summoned units (ТЗ ч. 6, 20.3; docs/DECISIONS_6.md §106): presets over the ordinary actions of the AI — attack,
/// approach, move — with the link to the summoner. Every preset falls back predictably: no summoner — «после призывателя»; no target,
/// lost way, a manual control not allowed — the fallback behaviour; then «держать позицию».
/// </summary>
public sealed partial class AiSystem
{
    private readonly Dictionary<(string, int), AiProfileDef> _leashed = new();

    /// <summary>The profile with the leash as its hold radius (the approach keeps inside it).</summary>
    private AiProfileDef Leashed(AiProfileDef p, int leash)
    {
        if (!_leashed.TryGetValue((p.Id, leash), out var lp))
        {
            lp = ContentJson.Clone(p);
            lp.HoldRadius = leash;
            _leashed[(p.Id, leash)] = lp;
        }
        return lp;
    }

    private bool DecideSummoned(Unit u, AiProfileDef p, SummonState st)
    {
        var summoner = !st.Independent && u.SummonerId >= 0 ? _sim.GetUnit(u.SummonerId) : null;
        if (summoner is { Alive: false }) summoner = null;
        var b = st.Behavior;
        if (summoner == null && NeedsSummoner(b)) b = st.Def.AfterSummoner;
        return RunSummoned(u, p, st, summoner, b, 0);
    }

    private static bool NeedsSummoner(SummonBehavior b) => b is SummonBehavior.FollowSummoner or SummonBehavior.GuardSummoner or SummonBehavior.AttackSummonerTarget;

    private bool RunSummoned(Unit u, AiProfileDef p, SummonState st, Unit? summoner, SummonBehavior b, int depth)
    {
        if (depth > 3)
        {
            Explain(u, "призванный: ждёт");
            return true;
        }
        bool Next(string why)
        {
            var fb = st.Def.Fallback;
            if (fb == b || summoner == null && NeedsSummoner(fb) || fb == SummonBehavior.ManualSimple)
                fb = summoner != null && b != SummonBehavior.FollowSummoner ? SummonBehavior.FollowSummoner
                    : b != SummonBehavior.HoldPosition ? SummonBehavior.HoldPosition : SummonBehavior.AttackNearestEnemy;
            Explain(u, why);
            return RunSummoned(u, p, st, summoner, fb, depth + 1);
        }
        var enemies = _sim.Units.Where(e => e.Alive && _sim.IsHostile(u, e) && _sim.Vision.CanFactionSee(u.FactionId, e) && CanHitLayer(u, e))
            .OrderBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).ToList();
        var def = st.Def;
        switch (b)
        {
            case SummonBehavior.FollowSummoner:
            {
                if (summoner == null) return Next("призывателя нет");
                if (HexCoord.Distance(u.Pos, summoner.Pos) > def.Distance)
                    return MoveTo(u, summoner.Pos, def.Distance, $"следует за {summoner.Name}") || Next($"путь к {summoner.Name} потерян");
                // Close by: hits whom it reaches from here, never leaves the summoner for a chase.
                foreach (var e in enemies.Where(e => StructureSystem.DistanceTo(u.Pos, e) <= MaxReach(u)))
                {
                    SetTarget(u, e);
                    if (TryAttack(u, e, p, $"рядом с {summoner.Name}")) return true;
                }
                Explain(u, $"держится рядом с {summoner.Name}");
                return true;
            }
            case SummonBehavior.GuardSummoner:
            {
                if (summoner == null) return Next("охранять некого");
                u.Ai.HoldCenter = summoner.Pos;
                var lp = Leashed(p, def.Leash);
                var threats = enemies.Where(e => Threatens(e, summoner) || HexCoord.Distance(e.Pos, summoner.Pos) <= def.GuardRadius)
                    .OrderBy(e => Threatens(e, summoner) ? 0 : 1).ThenBy(e => HexCoord.Distance(e.Pos, summoner.Pos)).ThenBy(e => e.Id).Take(3);
                foreach (var t in threats)
                {
                    SetTarget(u, t);
                    if (HexCoord.Distance(u.Pos, summoner.Pos) <= def.Leash && TryAttack(u, t, lp, $"охраняет {summoner.Name}")) return true;
                    if (Approach(u, t, lp, $"перехватывает {t.Name} у {summoner.Name}", hold: true)) return true;
                }
                if (HexCoord.Distance(u.Pos, summoner.Pos) > def.Distance && MoveTo(u, summoner.Pos, def.Distance, $"возвращается к {summoner.Name}")) return true;
                Explain(u, $"охраняет {summoner.Name}");
                return true;
            }
            case SummonBehavior.AttackSummonerTarget:
            {
                if (summoner == null) return Next("призывателя нет");
                var t = SummonerTarget(summoner);
                if (t == null || !t.Alive || !_sim.IsHostile(u, t)) return Next($"у {summoner.Name} нет цели");
                SetTarget(u, t);
                if (TryAttack(u, t, p, $"цель {summoner.Name}") || Approach(u, t, p, $"цель {summoner.Name}", hold: false)) return true;
                return Next($"цель {t.Name} недостижима");
            }
            case SummonBehavior.AttackNearestEnemy:
            {
                // Reachable first, then the distance, then the stable number (ТЗ 20.3 D).
                foreach (var t in enemies.OrderBy(e => CanAttackNow(u, e) ? 0 : 1).ThenBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).Take(3))
                {
                    SetTarget(u, t);
                    if (TryAttack(u, t, p, "ближайший враг") || Approach(u, t, p, "ближайший враг", hold: false)) return true;
                }
                if (u.Ai.KnownEnemies.Count > 0)
                {
                    var last = u.Ai.KnownEnemies.OrderByDescending(k => k.Value.Tick).ThenBy(k => k.Key).First();
                    if (MoveTo(u, last.Value.Pos, 1, $"ищет врага: последняя позиция {last.Value.Pos}")) return true;
                }
                if (summoner != null && depth == 0) return RunSummoned(u, p, st, summoner, SummonBehavior.FollowSummoner, depth + 1);
                Explain(u, "врагов не видно — ждёт");
                return true;
            }
            case SummonBehavior.HoldPosition:
                return Hold(u, p, st, enemies, st.Home, "держит позицию");
            case SummonBehavior.PatrolSimple:
            {
                if (def.Patrol.Count == 0) return Hold(u, p, st, enemies, st.Home, "патруль без точек — держит позицию");
                var points = new List<HexCoord> { st.Home };
                points.AddRange(def.Patrol.Select(o => st.Home + o));
                var here = points[st.PatrolIndex % points.Count];
                u.Ai.HoldCenter = here;
                var lp = Leashed(p, def.Leash);
                foreach (var t in enemies.Where(e => HexCoord.Distance(e.Pos, u.Pos) <= def.GuardRadius).Take(3))
                {
                    SetTarget(u, t);
                    if (TryAttack(u, t, lp, "патруль") || Approach(u, t, lp, "патруль", hold: true)) return true;
                }
                if (u.Pos == here)
                {
                    st.PatrolIndex = (st.PatrolIndex + 1) % points.Count;
                    here = points[st.PatrolIndex];
                }
                if (MoveTo(u, here, 0, $"патрулирует: точка {st.PatrolIndex + 1} из {points.Count}")) return true;
                st.PatrolIndex = (st.PatrolIndex + 1) % points.Count;
                Explain(u, "точка патруля недостижима — к следующей");
                return true;
            }
            case SummonBehavior.MoveToPoint:
                if (!st.Arrived)
                {
                    if (u.Pos != st.Point && MoveTo(u, st.Point, 0, $"идёт к точке {st.Point}")) return true;
                    st.Arrived = true;
                    st.Home = u.Pos;
                    if (u.Pos != st.Point) Explain(u, $"точка {st.Point} недостижима — держит место");
                }
                return Hold(u, p, st, enemies, st.Home, "держит точку");
            default:
                // «Ручное управление» that is not allowed (the AI drives this unit): the fallback.
                return Next("ручное управление недоступно");
        }
    }

    /// <summary>Holds a hex: hits enemies inside the leash and its reach, never chases further; comes back.</summary>
    private bool Hold(Unit u, AiProfileDef p, SummonState st, List<Unit> enemies, HexCoord center, string what)
    {
        u.Ai.HoldCenter = center;
        var lp = Leashed(p, st.Def.Leash);
        int reach = MaxReach(u);
        foreach (var t in enemies.Where(e => HexCoord.Distance(e.Pos, center) <= st.Def.Leash + reach)
                     .OrderBy(e => HexCoord.Distance(e.Pos, center)).ThenBy(e => e.Id).Take(3))
        {
            SetTarget(u, t);
            if (TryAttack(u, t, lp, what) || Approach(u, t, lp, what, hold: true)) return true;
        }
        if (u.Pos != center && MoveTo(u, center, 0, "возвращается на позицию")) return true;
        Explain(u, what);
        return true;
    }

    /// <summary>Whether an enemy goes for the summoner: it attacked it, aims at it, or orders an attack on it.</summary>
    private static bool Threatens(Unit e, Unit summoner) =>
        summoner.Provokers.Contains(e.Id) || e.Ai.TargetUnitId == summoner.Id || e.Queue.Current is { IsAttackLike: true } c && c.TargetUnitId == summoner.Id
        || e.Casting?.TargetUnitId == summoner.Id;

    /// <summary>The current target of a summoner: its AI's, its order's, its cast's.</summary>
    private Unit? SummonerTarget(Unit summoner)
    {
        if (summoner.Ai.TargetUnitId >= 0 && _sim.GetUnit(summoner.Ai.TargetUnitId) is { Alive: true } a) return a;
        if (summoner.Queue.Current is { IsAttackLike: true, TargetUnitId: >= 0 } c && _sim.GetUnit(c.TargetUnitId) is { Alive: true } b) return b;
        if (summoner.Casting is { TargetUnitId: >= 0 } cast && _sim.GetUnit(cast.TargetUnitId) is { Alive: true } d) return d;
        return null;
    }

    /// <summary>Some own attack reaches the enemy from where the unit stands now.</summary>
    private bool CanAttackNow(Unit u, Unit e) =>
        AttackOptions(u).Any(s => Affordable(u, s) && _sim.Skills.Validate(u, s, e.Pos, e.Id, u.Pos.DirectionTo(e.Pos), u.Pos, u.Facing, checkCosts: false).Ok);
}
