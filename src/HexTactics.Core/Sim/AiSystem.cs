using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Basic utility AI driven by profile parameters. Uses exactly the same command API and rules
/// as the player (movement, AP, skills, statuses). Stores a human-readable explanation of each
/// decision for the admin view.
/// </summary>
public sealed class AiSystem
{
    private readonly Simulation _sim;
    private const double MemorySeconds = 20;

    public AiSystem(Simulation sim)
    {
        _sim = sim;
    }

    public bool Enabled { get; set; } = true;
    public long Decisions { get; private set; }

    private static readonly AiProfileDef DefaultProfile = new() { Id = "default", Name = "По умолчанию" };

    public void Update()
    {
        if (!Enabled) return;
        foreach (var u in _sim.Units)
        {
            if (!u.Alive || !u.AiControlled) continue;
            if (_sim.Tick < u.Ai.NextDecisionTick) continue;
            var p = u.AiProfile ?? DefaultProfile;
            u.Ai.NextDecisionTick = _sim.Tick + Math.Max(1, _sim.TicksFor(p.DecisionInterval));
            Decide(u, p);
        }
    }

    private void Explain(Unit u, string text)
    {
        if (u.Ai.Explanation == text) return;
        u.Ai.Explanation = text;
        _sim.Emit(SimEventType.AiDecision, LogLevel.Diagnostic, $"ИИ {u.Name}: {text}", u.Id);
    }

    private void UpdateMemory(Unit u, AiProfileDef p)
    {
        long forget = _sim.TicksFor(MemorySeconds);
        foreach (var e in _sim.Units)
        {
            if (!e.Alive || !_sim.IsHostile(u, e)) continue;
            if (_sim.Vision.CanFactionSee(u.FactionId, e)) u.Ai.KnownEnemies[e.Id] = (e.Pos, _sim.Tick);
        }
        foreach (var id in u.Ai.KnownEnemies.Keys.ToList())
        {
            var e = _sim.GetUnit(id);
            if (e == null || !e.Alive || !_sim.IsHostile(u, e) || _sim.Tick - u.Ai.KnownEnemies[id].Tick > forget) u.Ai.KnownEnemies.Remove(id);
        }
        if (!p.Alert) return;
        foreach (var ally in _sim.Units)
        {
            if (ally == u || !ally.Alive || !ally.AiControlled || ally.FactionId != u.FactionId) continue;
            if (HexCoord.Distance(ally.Pos, u.Pos) > 8) continue;
            foreach (var kv in u.Ai.KnownEnemies)
                if (!ally.Ai.KnownEnemies.TryGetValue(kv.Key, out var known) || known.Tick < kv.Value.Tick)
                    ally.Ai.KnownEnemies[kv.Key] = kv.Value;
        }
    }

    private void Decide(Unit u, AiProfileDef p)
    {
        Decisions++;
        if (u.Casting != null || u.BusyUntil > _sim.Tick) return;
        if (u.Flags.BlocksActions)
        {
            Explain(u, "оглушён — ждёт");
            return;
        }
        UpdateMemory(u, p);

        // Blocked (docs/DECISIONS_2.md §27): attack the blocker, wait, or look for a detour (handled by the move command).
        if (_sim.Blocking.BlockerOf(u) is { } blocker)
        {
            switch (u.Def.Blockable.WhenBlocked)
            {
                case BlockedBehavior.AttackBlocker:
                    u.Ai.TargetUnitId = blocker.Id;
                    if (TryAttack(u, blocker, p, "заблокирован — атакует блокирующего")) return;
                    FaceTowards(u, blocker.Pos);
                    Explain(u, $"заблокирован {blocker.Name} — нечем атаковать, ждёт");
                    return;
                case BlockedBehavior.Wait:
                    Explain(u, $"заблокирован {blocker.Name} — ждёт");
                    return;
            }
        }

        // Wave enemies with «идти по маршруту, не отвлекаясь» walk on (only blockers stop them, handled above).
        if (p.RouteFocus && u.Route != null && FollowRoute(u)) return;

        var visible = _sim.Units.Where(e => e.Alive && _sim.IsHostile(u, e) && _sim.Vision.CanFactionSee(u.FactionId, e)).ToList();
        var cur = u.Queue.Current;
        bool busyMoving = cur != null && cur.Type == CommandType.Move && cur.Status != CommandStatus.Pending;

        // Fear or low HP: retreat.
        if (visible.Count > 0 && (u.Flags.Fear || (p.RetreatHpFraction > 0 && u.HpFraction < p.RetreatHpFraction)))
        {
            if (Retreat(u, visible, p, u.Flags.Fear ? "страх" : $"HP {u.HpFraction:P0} < {p.RetreatHpFraction:P0}")) return;
        }

        // Support allies.
        if (p.Support && TrySupport(u, p)) return;

        // Only enemies some own skill can reach (layers: a melee brute ignores a high flyer it cannot hit).
        var attackable = visible.Where(e => CanHitLayer(u, e)).ToList();
        if (attackable.Count == 0 && visible.Count > 0) Explain(u, "видит врагов, но не может их поразить (слой)");
        if (attackable.Count > 0)
        {
            var target = ChooseTarget(u, attackable, p, out var why);
            u.Ai.TargetUnitId = target.Id;
            if (TryAttack(u, target, p, why)) return;
            if (p.KeepDistance && TryKeepDistance(u, visible, p)) return;
            bool defensive = p.Aggression < 0.5 && HexCoord.Distance(u.Pos, target.Pos) > p.PreferredRange + 3;
            if (defensive)
            {
                if (cur == null || cur.Type != CommandType.Defend) Issue(u, Command.Defend(), $"осторожен: ждёт приближения {target.Name} в стойке");
                FaceTowards(u, target.Pos);
                return;
            }
            if (Approach(u, target, p, why)) return;
            FaceTowards(u, target.Pos);
            Explain(u, $"цель {target.Name} ({why}) недостижима — держит позицию");
            return;
        }

        u.Ai.TargetUnitId = -1;
        if (busyMoving) return;

        // A wave route: go point by point when no enemy is in reach.
        if (u.Route != null && FollowRoute(u)) return;

        // Pursue last known enemy position.
        if (p.Pursue && u.Ai.KnownEnemies.Count > 0 && !p.HoldPosition)
        {
            var last = u.Ai.KnownEnemies.OrderByDescending(k => k.Value.Tick).ThenBy(k => HexCoord.Distance(u.Pos, k.Value.Pos)).First();
            if (HexCoord.Distance(u.Pos, last.Value.Pos) <= 1)
            {
                u.Ai.KnownEnemies.Remove(last.Key);
            }
            else
            {
                if (MoveTo(u, last.Value.Pos, 1, $"преследует: последняя позиция цели {last.Value.Pos}")) return;
            }
        }

        // Hold position.
        if (u.Ai.HoldCenter is HexCoord center && p.HoldPosition && HexCoord.Distance(u.Pos, center) > p.HoldRadius)
        {
            if (MoveTo(u, center, 0, "возвращается на позицию")) return;
        }

        if (p.AdvanceWhenIdle && !p.HoldPosition && (cur == null || cur.Type == CommandType.Defend))
        {
            var start = _sim.Map.Def.Markers.Where(m => m.Type == MarkerType.PlayerStart).Select(m => (HexCoord?)m.C).FirstOrDefault();
            var goal = start ?? MapCenter();
            if (HexCoord.Distance(u.Pos, goal) > 2 && MoveTo(u, goal, 2, $"наступает к {(start.HasValue ? "стартовой зоне противника" : "центру карты")} {goal}")) return;
        }

        if (u.Ai.KnownEnemies.Count > 0 && p.FaceThreats)
        {
            var k = u.Ai.KnownEnemies.Values.OrderByDescending(v => v.Tick).First();
            FaceTowards(u, k.Pos);
        }
        if (cur == null) Explain(u, "врагов не видно — ожидание");
    }

    /// <summary>Moves to the next point of the unit's route; false when the route is finished.</summary>
    private bool FollowRoute(Unit u)
    {
        var route = u.Route!;
        while (u.RouteIndex < route.Count)
        {
            var pt = route[u.RouteIndex];
            int d = HexCoord.Distance(u.Pos, pt);
            var cell = _sim.Map.Get(pt);
            bool occupied = cell != null && cell.Occupants.Any(o => o != u && o.Alive && o.Layer == u.Layer);
            if (d == 0 || (d == 1 && occupied && u.RouteIndex < route.Count - 1)) u.RouteIndex++;
            else break;
        }
        if (u.RouteIndex >= route.Count) return false;
        return MoveTo(u, route[u.RouteIndex], 0, $"идёт по маршруту: точка {u.RouteIndex + 1}/{route.Count}");
    }

    private HexCoord MapCenter()
    {
        if (_sim.Map.Count == 0) return HexCoord.Zero;
        double q = 0, r = 0;
        foreach (var c in _sim.Map.Cells)
        {
            q += c.Coord.Q;
            r += c.Coord.R;
        }
        return HexCoord.Round(q / _sim.Map.Count, r / _sim.Map.Count);
    }

    private bool CanHitLayer(Unit u, Unit target)
    {
        if (u.BasicAttack != null && LayerResolver.CanTarget(_sim.Rules, u.BasicAttack, target)) return true;
        foreach (var s in u.Skills)
            if (s.IsHarmful && LayerResolver.CanTarget(_sim.Rules, s, target))
                return true;
        return false;
    }

    private Unit ChooseTarget(Unit u, List<Unit> visible, AiProfileDef p, out string why)
    {
        switch (p.TargetPriority)
        {
            case AiTargetPriority.Weakest:
                why = "самая слабая";
                return visible.OrderBy(e => e.Hp).ThenBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).First();
            case AiTargetPriority.LowestHpFraction:
                why = "наименьший % HP";
                return visible.OrderBy(e => e.HpFraction).ThenBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).First();
            case AiTargetPriority.MostDangerous:
                why = "самая опасная";
                return visible.OrderByDescending(e => Threat(e) / (1.0 + HexCoord.Distance(u.Pos, e.Pos) / 5.0)).ThenBy(e => e.Id).First();
            default:
                why = "ближайшая";
                // Keep the current target when it is about as close (avoid flip-flopping).
                var cur = visible.FirstOrDefault(e => e.Id == u.Ai.TargetUnitId);
                var nearest = visible.OrderBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).First();
                if (cur != null && HexCoord.Distance(u.Pos, cur.Pos) <= HexCoord.Distance(u.Pos, nearest.Pos) + 1) return cur;
                return nearest;
        }
    }

    public static double Threat(Unit e)
    {
        double t = e.Stat(Stats.Power);
        foreach (var s in e.Skills.Append(e.BasicAttack))
        {
            if (s == null) continue;
            foreach (var st in s.Stages)
            foreach (var ef in st.Effects)
                if (ef.Type == EffectType.Damage)
                    t += ef.Amount * 0.5;
        }
        return t;
    }

    private IEnumerable<SkillDef> AttackOptions(Unit u)
    {
        if (u.BasicAttack != null) yield return u.BasicAttack;
        foreach (var s in u.Skills)
            if (s.IsHarmful && s.Targeting != TargetingMode.Self)
                yield return s;
    }

    private bool Affordable(Unit u, SkillDef s)
    {
        if (_sim.Content.DisabledSkills.ContainsKey(s.Id)) return false;
        if (u.CooldownRemaining(s.Id, _sim.Tick) > 0) return false;
        if (s.ApCost > u.ApMax) return false;
        foreach (var rc in s.ResourceCosts)
            if (!u.Resources.TryGetValue(rc.Resource, out var r) || r.Current < rc.Amount)
                return false;
        if (u.Flags.BlocksSkills && s != u.BasicAttack) return false;
        return true;
    }

    /// <summary>Cheap score: damage × enemies in the (unobstructed) area − penalty for allies, minus cost.</summary>
    private double ExpectedValue(Unit u, SkillDef s, Unit target, HexCoord from)
    {
        double dmg = 0;
        foreach (var st in s.Stages)
        foreach (var e in st.Effects)
            if (e.Type == EffectType.Damage)
                dmg += _sim.Combat.EffectAmount(e, u, target, 1.0);
        int enemies = 0, allies = 0;
        var anchor = s.UsesChosenDirection ? from : target.Pos;
        var orient = from.DirectionTo(target.Pos) ?? u.Facing;
        foreach (var st in s.Stages)
        {
            if (st.Area.Shape == AreaShape.Chain)
            {
                enemies += 1 + st.Area.ChainJumps / 2;
                continue;
            }
            foreach (var ac in AreaResolver.Resolve(st.Area, anchor, orient, _sim.Map))
            {
                var cell = _sim.Map.Get(ac.Cell);
                if (cell == null) continue;
                foreach (var o in cell.Occupants)
                {
                    if (!o.Alive || o == u) continue;
                    if (_sim.IsHostile(u, o)) enemies++;
                    else if (_sim.RelationOf(u, o) == Relation.Ally && st.Effects.Any(e => e.Type == EffectType.Damage && _sim.FilterAccepts(e.Filter, u, o))) allies++;
                }
            }
        }
        double score = dmg * Math.Max(1, enemies) - dmg * 1.5 * allies;
        score -= s.ApCost * 0.5;
        return score;
    }

    private bool TryAttack(Unit u, Unit target, AiProfileDef p, string why)
    {
        SkillDef? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var s in AttackOptions(u))
        {
            if (!Affordable(u, s)) continue;
            var chk = _sim.Skills.Validate(u, s, target.Pos, target.Id, u.Pos.DirectionTo(target.Pos), u.Pos, u.Facing, checkCosts: false);
            if (!chk.Ok) continue;
            double score = ExpectedValue(u, s, target, u.Pos);
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }
        if (best == null || bestScore <= 0 && best != u.BasicAttack) return false;

        foreach (var q in u.Queue.Items)
            if (q.IsAttackLike && q.TargetUnitId == target.Id && (q.Status != CommandStatus.Pending || u.Queue.Current == q))
                return true;
        if (u.Casting != null) return true;
        var cmd = MakeSkillCommand(u, best, target);
        Issue(u, cmd, $"атакует {target.Name} ({why}) навыком «{best.DisplayName}»");
        return true;
    }

    private Command MakeSkillCommand(Unit u, SkillDef s, Unit target)
    {
        if (s == u.BasicAttack) return Command.Attack(target.Id);
        return s.Targeting switch
        {
            TargetingMode.Unit => Command.Skill(s.Id, null, target.Id),
            TargetingMode.Cell => Command.Skill(s.Id, target.Pos),
            _ => Command.Skill(s.Id, null, -1, u.Pos.DirectionTo(target.Pos)),
        };
    }

    private bool Approach(Unit u, Unit target, AiProfileDef p, string why)
    {
        var options = AttackOptions(u).Where(s => Affordable(u, s)).ToList();
        if (options.Count == 0 && u.BasicAttack != null) options.Add(u.BasicAttack);
        if (options.Count == 0) return false;
        var skill = options.OrderByDescending(s => p.KeepDistance ? s.RangeMax : -s.ApCost).First();
        int maxRange = skill.Targeting is TargetingMode.Unit or TargetingMode.Cell ? skill.RangeMax : Math.Max(1, skill.Stages.FirstOrDefault()?.Area.Size ?? 1);
        int minRange = skill.Targeting is TargetingMode.Unit or TargetingMode.Cell ? skill.RangeMin : 1;
        int desired = p.KeepDistance ? Math.Clamp(p.PreferredRange, minRange, maxRange) : minRange;
        // A structure is approached at its nearest hex (docs/DECISIONS_4.md §56).
        var aim = StructureSystem.AimCell(target, u.Pos);

        // Keep going if already heading to an attack position and the target barely moved.
        var curMove = u.Queue.Current;
        if (curMove != null && curMove.Type == CommandType.Move && curMove.Status != CommandStatus.Pending && u.Ai.MoveGoal.HasValue
            && u.Ai.TargetUnitId == target.Id && curMove.TargetCell.HasValue && HexCoord.Distance(u.Ai.MoveGoal.Value, target.Pos) <= 1)
            return true;

        // Melee: A* to any cell next to the target (cheap). Ranged: bounded flood + range/LoS filter.
        if (maxRange <= 1 && !p.HoldPosition)
        {
            var path = _sim.Pathfinder.Find(_sim, u, u.Pos, aim, new PathOptions { GoalTolerance = 1, HazardPenalty = p.HazardAvoidance, MaxExpanded = 3000 });
            var dest = path.Found ? path.End : path.Nearest;
            if (dest == null || dest.Value == u.Pos) return false;
            u.Ai.MoveGoal = target.Pos;
            Issue(u, Command.Move(dest.Value), $"идёт к {target.Name} ({why}), чтобы атаковать «{skill.DisplayName}»");
            if (Affordable(u, skill) && StructureSystem.DistanceTo(dest.Value, target) <= 1) _sim.Enqueue(u, MakeSkillCommand(u, skill, target));
            return true;
        }

        double budget = Math.Min(40, HexCoord.Distance(u.Pos, aim) * 2.0 + 6);
        var reach = _sim.Pathfinder.Flood(_sim, u, u.Pos, budget, new PathOptions { MaxExpanded = 1500 });
        HexCoord? best = null;
        double bestScore = double.MaxValue;
        foreach (var kv in reach)
        {
            var c = kv.Key;
            var at = StructureSystem.AimCell(target, c);
            int d = HexCoord.Distance(c, at);
            int bonus = _sim.Skills.RangeBonus(skill, c, at);
            if (d < minRange || d > maxRange + bonus) continue;
            if (p.HoldPosition && u.Ai.HoldCenter is HexCoord hc && HexCoord.Distance(c, hc) > p.HoldRadius) continue;
            if (skill.RequiresLoS && !_sim.Los.Has(_sim.Map, _sim.Rules, c, at)) continue;
            var cell = _sim.Map.Get(c)!;
            double score = kv.Value + Math.Abs(d - desired) * 0.7 + (cell.IsHazard ? p.HazardAvoidance : 0);
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        if (best == null)
        {
            // Get closer even if no attack cell is reachable now.
            if (p.HoldPosition) return false;
            return MoveTo(u, aim, Math.Max(1, minRange), $"сближается с {target.Name} ({why})");
        }
        if (best.Value == u.Pos) return false;
        var cur = u.Queue.Current;
        if (cur != null && cur.Type == CommandType.Move && cur.TargetCell == best.Value) return true;
        var move = Command.Move(best.Value);
        u.Ai.MoveGoal = target.Pos;
        Issue(u, move, $"идёт в {best.Value}, чтобы атаковать {target.Name} ({why}) навыком «{skill.DisplayName}»");
        if (Affordable(u, skill)) _sim.Enqueue(u, MakeSkillCommand(u, skill, target));
        return true;
    }

    private bool MoveTo(Unit u, HexCoord goal, int tolerance, string why)
    {
        var cur = u.Queue.Current;
        if (cur != null && cur.Type == CommandType.Move && cur.TargetCell.HasValue && HexCoord.Distance(cur.TargetCell.Value, goal) <= tolerance) return true;
        var path = _sim.Pathfinder.Find(_sim, u, u.Pos, goal, new PathOptions { GoalTolerance = tolerance, HazardPenalty = u.AiProfile?.HazardAvoidance ?? 0, MaxExpanded = 6000 });
        HexCoord? dest = path.Found ? path.End : path.Nearest;
        if (dest == null || dest.Value == u.Pos) return false;
        Issue(u, Command.Move(dest.Value), why);
        return true;
    }

    private bool Retreat(Unit u, List<Unit> enemies, AiProfileDef p, string why)
    {
        var reach = _sim.Pathfinder.Flood(_sim, u, u.Pos, 6, new PathOptions { MaxExpanded = 1500 });
        HexCoord best = u.Pos;
        double bestScore = enemies.Min(e => HexCoord.Distance(u.Pos, e.Pos));
        foreach (var kv in reach)
        {
            double minD = enemies.Min(e => HexCoord.Distance(kv.Key, e.Pos));
            var cell = _sim.Map.Get(kv.Key)!;
            double score = minD - kv.Value * 0.1 - (cell.IsHazard ? p.HazardAvoidance : 0);
            if (score > bestScore + 0.5)
            {
                bestScore = score;
                best = kv.Key;
            }
        }
        if (best == u.Pos) return false;
        var cur = u.Queue.Current;
        if (cur != null && cur.Type == CommandType.Move && cur.TargetCell == best) return true;
        Issue(u, Command.Move(best), $"отступает ({why}) в {best}");
        return true;
    }

    private bool TryKeepDistance(Unit u, List<Unit> enemies, AiProfileDef p)
    {
        int nearest = enemies.Min(e => HexCoord.Distance(u.Pos, e.Pos));
        if (nearest >= Math.Max(2, p.PreferredRange - 1)) return false;
        return Retreat(u, enemies, p, "держит дистанцию");
    }

    private bool TrySupport(Unit u, AiProfileDef p)
    {
        var heals = u.Skills.Where(s => Affordable(u, s) && s.Stages.Any(st => st.Effects.Any(e => e.Type == EffectType.Heal))).ToList();
        if (heals.Count == 0) return false;
        var allies = _sim.Units.Where(a => a.Alive && _sim.RelationOf(u, a) == Relation.Ally && a.HpFraction < p.SupportHpThreshold)
            .OrderBy(a => a.HpFraction).ThenBy(a => a.Id).ToList();
        foreach (var a in allies)
        foreach (var s in heals)
        {
            var chk = _sim.Skills.Validate(u, s, a.Pos, a.Id, null, u.Pos, u.Facing, checkCosts: false);
            if (chk.Ok)
            {
                var cur = u.Queue.Current;
                if (cur != null && cur.IsAttackLike && cur.SkillId == s.Id && cur.TargetUnitId == a.Id) return true;
                var cmd = s.Targeting == TargetingMode.Unit ? Command.Skill(s.Id, null, a.Id)
                    : s.Targeting == TargetingMode.Cell ? Command.Skill(s.Id, a.Pos) : Command.Skill(s.Id, null);
                Issue(u, cmd, $"лечит {a.Name} ({a.HpFraction:P0}) навыком «{s.DisplayName}»");
                return true;
            }
            if (chk.Reason == FailureReason.OutOfRange || chk.Reason == FailureReason.NoLineOfSight)
            {
                if (MoveTo(u, a.Pos, Math.Max(1, s.RangeMax), $"идёт к {a.Name}, чтобы вылечить")) return true;
            }
        }
        return false;
    }

    private void FaceTowards(Unit u, HexCoord pos)
    {
        var d = u.Pos.DirectionTo(pos);
        if (d == null || d.Value == u.Facing) return;
        if (u.Queue.Count > 0 && u.Queue.Current!.Type != CommandType.Defend) return;
        if (u.FreeAp < _sim.Rules.Turn.ApPerStep * 2) return;
        _sim.Enqueue(u, Command.Turn(d.Value));
    }

    private void Issue(Unit u, Command c, string explanation)
    {
        var err = _sim.Enqueue(u, c, -1, replaceQueue: true);
        u.Ai.LastAction = c.Describe(_sim);
        Explain(u, err == null ? explanation : explanation + " — не удалось: " + err);
    }
}
