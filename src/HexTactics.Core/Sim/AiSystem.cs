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
        TrackStuck(u);
        var dir = u.Ai.Directive;
        string rule = u.Ai.DirectiveRule != "" ? $" (правило «{u.Ai.DirectiveRule}»)" : "";

        // Blocked (docs/DECISIONS_2.md §27): attack the blocker, wait, or look for a detour (handled by the move command).
        if (_sim.Blocking.BlockerOf(u) is { } blocker)
        {
            switch (u.Def.Blockable.WhenBlocked)
            {
                case BlockedBehavior.AttackBlocker:
                    SetTarget(u, blocker);
                    if (TryAttack(u, blocker, p, "заблокирован — атакует блокирующего")) return;
                    FaceTowards(u, blocker.Pos);
                    Explain(u, $"заблокирован {blocker.Name} — нечем атаковать, ждёт");
                    return;
                case BlockedBehavior.Wait:
                    Explain(u, $"заблокирован {blocker.Name} — ждёт");
                    return;
            }
        }

        // The target base is gone or the route stays closed: an alternative target (docs/DECISIONS_4.md §59).
        if (u.Ai.Alternative == null && NeedsAlternative(u)) ChooseAlternative(u, p);

        if (dir == AiDirective.Wait)
        {
            Explain(u, "ждёт" + rule);
            return;
        }

        // «Штурм» and hunting groups of «Смешанный»: the player's units first, the route is only the way there.
        if (u.Ai.Hunts && p.RouteOnly) Problem(u, "route_only", "«только маршрут» — на охоту не переключается (профиль сильнее режима и группы)");
        else if (u.Ai.Hunts && dir is not (AiDirective.Hold or AiDirective.Retreat) && Hunt(u, p)) return;

        // Wave enemies with «идти по маршруту, не отвлекаясь» walk on (only blockers stop them, handled above).
        bool focus = p.RouteFocus || u.Nav?.Focus == true || dir == AiDirective.Advance;
        if (focus && u.Route != null && FollowRoute(u)) return;
        if (focus && u.Route == null && u.TargetObject != "" && GoToObject(u)) return;

        var visible = _sim.Units.Where(e => e.Alive && _sim.IsHostile(u, e) && _sim.Vision.CanFactionSee(u.FactionId, e)).ToList();
        var cur = u.Queue.Current;
        bool busyMoving = cur != null && cur.Type == CommandType.Move && cur.Status != CommandStatus.Pending;

        // Fear, low HP or a rule: retreat.
        if (visible.Count > 0 && (dir == AiDirective.Retreat || u.Flags.Fear || (p.RetreatHpFraction > 0 && u.HpFraction < p.RetreatHpFraction)))
        {
            if (Retreat(u, visible, p, dir == AiDirective.Retreat ? "отход" + rule : u.Flags.Fear ? "страх" : $"HP {u.HpFraction:P0} < {p.RetreatHpFraction:P0}")) return;
        }

        // Support allies.
        if ((p.Support || dir == AiDirective.Support) && TrySupport(u, p)) return;

        // The centre a guard keeps: its position, its base (§64 «защитник базы»).
        if (p.GuardBase && GuardedBase(u) is { } guarded) u.Ai.HoldCenter = guarded;
        bool hold = p.HoldPosition || dir == AiDirective.Hold || p.GuardBase;

        // Only enemies some own skill can reach (layers: a melee brute ignores a high flyer it cannot hit).
        var attackable = visible.Where(e => CanHitLayer(u, e)).ToList();
        if (attackable.Count == 0 && visible.Count > 0) Explain(u, "видит врагов, но не может их поразить (слой)");
        // A patrol reacts only inside its zone.
        if (p.Patrol && u.Ai.HoldCenter is HexCoord pc) attackable = attackable.Where(e => HexCoord.Distance(e.Pos, pc) <= p.PatrolRadius + 2).ToList();
        // A route enemy: whom it may leave its route for, and when it comes back (docs/DECISIONS_4.md §59).
        bool onRoute = u.Route != null && (u.Nav != null || u.RouteId != "");
        // «Только маршрут»: whom it reaches from its place, and its own target at the end of the route — never a chase.
        if (p.RouteOnly)
        {
            int reach = MaxReach(u);
            attackable = attackable.Where(e => StructureSystem.DistanceTo(u.Pos, e) <= reach || e.IsStructure && e.ObjectId != "" && e.ObjectId == u.TargetObject).ToList();
        }
        else if (onRoute && p.Encounter != null)
        {
            attackable = EncounterTargets(u, p, p.Encounter, attackable, out var giveUp);
            if (giveUp != "" && u.Ai.OffRouteSince >= 0)
            {
                if (ReturnToRoute(u, giveUp)) return;
            }
        }
        if (attackable.Count > 0)
        {
            var target = ChooseTarget(u, attackable, p, dir, out var why);
            SetTarget(u, target);
            if (onRoute && u.Ai.OffRouteSince < 0 && !p.RouteOnly)
            {
                u.Ai.OffRouteSince = _sim.Tick;
                u.Ai.RouteAnchor = u.Pos;
            }
            if (p.LeadAllies) ShareTarget(u, target);
            if (TryAttack(u, target, p, why)) return;
            if (p.KeepDistance && TryKeepDistance(u, visible, p)) return;
            bool defensive = p.Aggression < 0.5 && HexCoord.Distance(u.Pos, target.Pos) > p.PreferredRange + 3;
            if (defensive)
            {
                if (cur == null || cur.Type != CommandType.Defend) Issue(u, Command.Defend(), $"осторожен: ждёт приближения {target.Name} в стойке");
                FaceTowards(u, target.Pos);
                return;
            }
            if (Approach(u, target, p, why, hold)) return;
            FaceTowards(u, target.Pos);
            Explain(u, $"цель {target.Name} ({why}) недостижима — держит позицию");
            // Out of reach of every own attack and no way to get closer: a real «цель недостижима».
            if (StructureSystem.DistanceTo(u.Pos, target) > MaxReach(u) && !hold) Problem(u, "unreachable", $"цель {target.Name} недостижима");
            return;
        }

        u.Ai.TargetUnitId = -1;
        if (busyMoving) return;

        // Back to the route after a fight: the nearest valid point ahead, not the place it left.
        if (onRoute && u.Ai.OffRouteSince >= 0 && ReturnToRoute(u, "врагов рядом нет")) return;
        if (dir == AiDirective.ReturnToRoute && onRoute && ReturnToRoute(u, "правило" + rule)) return;

        // A wave route: go point by point when no enemy is in reach.
        if (u.Route != null && FollowRoute(u)) return;
        if (u.Route == null && u.TargetObject != "" && GoToObject(u)) return;
        if (u.Ai.Alternative != null && FollowAlternative(u, p)) return;

        // Pursue last known enemy position.
        if (p.Pursue && !p.RouteOnly && u.Ai.KnownEnemies.Count > 0 && !hold)
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

        // Hold position (its own hex, its base).
        if (u.Ai.HoldCenter is HexCoord center && hold && !p.Patrol && HexCoord.Distance(u.Pos, center) > p.HoldRadius)
        {
            if (MoveTo(u, center, 0, p.GuardBase ? "возвращается к охраняемой базе" : "возвращается на позицию")) return;
        }

        // Stay with the pack.
        if ((p.StayWithAllies || dir == AiDirective.Guard) && KeepWithAllies(u, p)) return;

        // Patrol around the position.
        if (p.Patrol && !p.RouteOnly && Patrol(u, p)) return;

        // «Свободный бой» and autotests: look for the other side (docs/DECISIONS_4.md §64).
        if (!hold && !p.RouteOnly && (_sim.Scenario.Mode is BattleMode.FreeBattle || (_sim.Scenario.Mode is BattleMode.AutoTest && u.Route == null && u.Wave < 0)) && Seek(u)) return;

        if (p.AdvanceWhenIdle && !p.RouteOnly && !hold && (cur == null || cur.Type == CommandType.Defend))
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

    // ------------------------------------------------------------------ part 4: encounters, return, alternatives

    /// <summary>The longest range of the unit's attacks.</summary>
    private int MaxReach(Unit u) => AttackOptions(u).Select(s => s.Targeting is TargetingMode.Unit or TargetingMode.Cell ? s.RangeMax : Math.Max(1, s.Stages.FirstOrDefault()?.Area.Size ?? 1))
        .DefaultIfEmpty(1).Max();

    /// <summary>A new target is logged (the report counts target changes).</summary>
    private void SetTarget(Unit u, Unit target)
    {
        int before = u.Ai.TargetUnitId;
        u.Ai.TargetUnitId = target.Id;
        if (before >= 0 && before != target.Id && _sim.GetUnit(before)?.Alive == true)
            _sim.Emit(SimEventType.TargetChanged, LogLevel.Diagnostic, $"ИИ {u.Name}: смена цели → {target.Name}", u.Id, target.Id);
    }

    /// <summary>An AI problem for the diagnostics and the report: once per kind per few seconds.</summary>
    private void Problem(Unit u, string kind, string text)
    {
        if (u.Ai.Problems.TryGetValue(kind, out var at) && _sim.Tick - at < _sim.TicksFor(5)) return;
        u.Ai.Problems[kind] = _sim.Tick;
        _sim.Emit(SimEventType.AiProblem, LogLevel.Diagnostic, $"ИИ {u.Name}: {text}", u.Id, -1, u.Pos, damageType: kind);
    }

    /// <summary>A unit with a move order that has not moved for a while is stuck.</summary>
    private void TrackStuck(Unit u)
    {
        if (u.Pos != u.Ai.LastPos)
        {
            u.Ai.LastPos = u.Pos;
            u.Ai.LastMovedTick = _sim.Tick;
            return;
        }
        var cur = u.Queue.Current;
        if (cur == null || cur.Type != CommandType.Move || u.Flags.BlocksMovement || _sim.Blocking.BlockerOf(u) != null) return;
        // Waiting for AP or for a hex to free is not being stuck: only a full AP pool that does not move for long is.
        if (u.Ap < u.ApMax - 1e-6 || cur.Status == CommandStatus.Waiting) return;
        if (_sim.Tick - u.Ai.LastMovedTick > _sim.TicksFor(10)) Problem(u, "stuck", "застрял: приказ на движение, но позиция не меняется");
    }

    /// <summary>«Непосредственно угрожает»: next to the unit and attacking it (its target, or it hit the unit just now).</summary>
    private bool ThreatensMe(Unit u, Unit e) =>
        e.Ai.TargetUnitId == u.Id || e.Queue.Items.Any(q => q.TargetUnitId == u.Id)
        || u.LastAttackerId == e.Id && _sim.Tick - u.LastAttackedTick <= _sim.TicksFor(3);

    private bool IsMoving(Unit e) => e.StepTo.HasValue || e.Queue.Current is { Type: CommandType.Move, Status: not CommandStatus.Pending };

    private bool TypeMatches(Unit e, string t) => e.Tag == t || _sim.Conditions.TypeMatches(e, t);

    /// <summary>
    /// The defenders a route enemy may fight now: allowed types, a moving target only if allowed, within the pursuit
    /// range of its route (less with a higher «приоритет базы»); a runner or a tank — only those next to it.
    /// <paramref name="giveUp"/> says why it must go back (time off the route, the return condition).
    /// </summary>
    private List<Unit> EncounterTargets(Unit u, AiProfileDef p, EncounterDef enc, List<Unit> attackable, out string giveUp)
    {
        giveUp = "";
        if (u.Ai.OffRouteSince >= 0)
        {
            if (enc.DistractionSeconds > 0 && _sim.Tick - u.Ai.OffRouteSince > _sim.TicksFor(enc.DistractionSeconds))
                giveUp = $"вне маршрута дольше {enc.DistractionSeconds:0.#} с";
            else if (enc.ReturnCondition != null && _sim.Conditions.Evaluate(enc.ReturnCondition, new ConditionContext(u, _sim.GetUnit(u.Ai.TargetUnitId), u.Pos, null)))
                giveUp = "выполнено условие возврата";
            // Going back it fights nobody (a blocker is answered before this).
            if (giveUp != "") return new List<Unit>();
        }
        // Just back on the route: no new chase for a while, only a direct threat is answered.
        bool resting = _sim.Tick < u.Ai.NoChaseUntil;
        int range = enc.PursuitRange >= 0 ? enc.PursuitRange : (int)Math.Round(u.Stat(Stats.PursuitRange));
        range = (int)Math.Floor(range * (1 - Math.Clamp(enc.BasePriority, 0, 1)) + 1e-9);
        if (enc.BasePriority >= 1 || enc.Mode is EncounterMode.Runner or EncounterMode.Tank) range = 0;
        var anchor = u.Ai.RouteAnchor ?? u.Pos;
        var list = attackable.Where(e =>
        {
            int d = StructureSystem.DistanceTo(u.Pos, e);
            if (d <= 1 && ThreatensMe(u, e)) return true; // a direct threat is always answered
            if (resting) return false;
            if (enc.AllowedTargets.Count > 0 && !enc.AllowedTargets.Any(t => TypeMatches(e, t))) return false;
            if (!enc.AttackMoving && IsMoving(e)) return false;
            return StructureSystem.DistanceTo(anchor, e) <= range;
        }).ToList();
        if (enc.Mode == EncounterMode.PackGuard)
        {
            // Those who attack the pack first.
            var attackers = list.Where(e => _sim.GetUnit(e.Ai.TargetUnitId) is { } t && t != u && t.FactionId == u.FactionId).ToList();
            if (attackers.Count > 0) list = attackers;
        }
        if (!enc.ThroughObstacles && list.Count > 0)
        {
            // A target behind obstacles (the path is much longer than the line) is not pursued.
            list = list.Where(e =>
            {
                int d = StructureSystem.DistanceTo(u.Pos, e);
                if (d <= 2) return true;
                var path = _sim.Pathfinder.Find(_sim, u, u.Pos, StructureSystem.AimCell(e, u.Pos), new PathOptions { GoalTolerance = 1, MaxExpanded = 800 });
                return path.Found && path.Path.Count <= d * 2 + 2;
            }).ToList();
        }
        return list;
    }

    /// <summary>
    /// Back to the route (docs/DECISIONS_4.md §59): the nearest valid point by path cost — a node ahead on the graph
    /// or a hex left on the current edge; none reachable — the block is reported.
    /// </summary>
    private bool ReturnToRoute(Unit u, string why)
    {
        if (u.AiProfile?.Encounter is { } enc) u.Ai.NoChaseUntil = _sim.Tick + _sim.TicksFor(Math.Max(3, enc.DistractionSeconds));
        u.Ai.OffRouteSince = -1;
        u.Ai.RouteAnchor = null;
        u.Ai.TargetUnitId = -1;
        if (u.Route == null) return false;
        var candidates = new List<(HexCoord C, string Node, int Index)>();
        for (int i = Math.Max(0, u.RouteIndex - 1); i < u.Route.Count; i += 2) candidates.Add((u.Route[i], "", i));
        if (u.Route.Count > 0) candidates.Add((u.Route[^1], "", u.Route.Count - 1));
        if (u.Nav != null)
            foreach (var n in _sim.Routes.AheadNodes(u).OrderBy(n => HexCoord.Distance(u.Pos, n.C)).Take(8))
                candidates.Add((n.C, n.Id, -1));
        var best = candidates.Distinct().OrderBy(c => HexCoord.Distance(u.Pos, c.C)).Take(12)
            .Select(c => (c, path: c.C == u.Pos ? null : _sim.Pathfinder.Find(_sim, u, u.Pos, c.C, new PathOptions { MaxExpanded = 1500 })))
            .Where(x => x.path == null || x.path.Found)
            .OrderBy(x => x.path?.Cost ?? 0).ThenBy(x => x.c.Index < 0 ? 1 : 0).Select(x => ((HexCoord C, string Node, int Index)?)x.c).FirstOrDefault();
        if (best == null)
        {
            Problem(u, "route_blocked", "нет пути ни к одной точке своего маршрута — блокировка");
            if (u.Ai.BlockedSince < 0) u.Ai.BlockedSince = _sim.Tick;
            return false;
        }
        var b = best.Value;
        if (b.Node != "")
        {
            _sim.Routes.Rejoin(u, b.Node);
            Explain(u, $"возвращается на маршрут ({why}): ближайший узел «{b.Node}»");
        }
        else
        {
            u.RouteIndex = b.Index;
            Explain(u, $"возвращается на маршрут ({why}): гекс {b.C}");
        }
        return FollowRoute(u);
    }

    /// <summary>The enemy's base is destroyed, or its route stays closed longer than the scenario allows.</summary>
    private bool NeedsAlternative(Unit u)
    {
        if (u.Wave < 0 && u.RouteId == "") return false;
        if (u.TargetObject != "" && _sim.Structures.Of(u.TargetObject) is { Alive: false }) return true;
        double after = _sim.Scenario.Def?.AltAfterBlocked ?? 0;
        return after > 0 && u.Ai.BlockedSince >= 0 && _sim.Tick - u.Ai.BlockedSince > _sim.TicksFor(after);
    }

    /// <summary>The first alternative of the profile (or the scenario) that can be done now.</summary>
    private void ChooseAlternative(Unit u, AiProfileDef p)
    {
        var sc = _sim.Scenario.Def;
        var list = p.AltTargets.Count > 0 ? p.AltTargets : sc?.AltTargets ?? new List<AltTargetDef>();
        string lost = u.TargetObject != "" && sc?.Object(u.TargetObject) is { } o ? $"«{o.DisplayName}» {(_sim.Structures.Of(o.Id)?.Alive == false ? "уничтожена" : "недоступна")}" : "маршрут закрыт";
        foreach (var a in list)
        {
            switch (a.Kind)
            {
                case AltTargetKind.OtherBase:
                {
                    var other = sc?.Objects.Where(b => b.Type == ScenarioObjectType.PlayerBase && b.Id != u.TargetObject && _sim.Structures.Of(b.Id) is { Alive: true } s && _sim.IsHostile(u, s))
                        .OrderByDescending(b => b.Health?.Importance ?? 0).ThenBy(b => b.Cells().Min(c => HexCoord.Distance(u.Pos, c))).FirstOrDefault();
                    if (other == null) continue;
                    Switch(u, AltTargetKind.OtherBase, $"{lost} — идёт к базе «{other.DisplayName}»");
                    u.TargetObject = other.Id;
                    return;
                }
                case AltTargetKind.AttackDefenders:
                    if (p.RouteOnly)
                    {
                        _sim.Emit(SimEventType.AiDecision, LogLevel.Detail, $"{u.Name}: {lost} — «атаковать защитников» пропущено: профиль «только маршрут»", u.Id, -1, u.Pos);
                        continue;
                    }
                    if (!_sim.Units.Any(e => e.Alive && !e.IsStructure && _sim.IsHostile(u, e))) continue;
                    Switch(u, AltTargetKind.AttackDefenders, $"{lost} — атакует защитников");
                    u.TargetObject = "";
                    return;
                case AltTargetKind.CaptureObject:
                {
                    var obj = a.Object != "" ? sc?.Object(a.Object)
                        : sc?.Objects.Where(b => b.Type is ScenarioObjectType.Goal or ScenarioObjectType.Checkpoint or ScenarioObjectType.Interaction)
                            .OrderBy(b => b.Cells().Min(c => HexCoord.Distance(u.Pos, c))).FirstOrDefault();
                    if (obj == null) continue;
                    Switch(u, AltTargetKind.CaptureObject, $"{lost} — идёт захватить «{obj.DisplayName}»");
                    u.TargetObject = obj.Id;
                    return;
                }
                case AltTargetKind.BreakObstacle:
                {
                    var cell = _sim.Map.Cells.Where(c => c.Terrain.DestructibleHp > 0).OrderBy(c => HexCoord.Distance(u.Pos, c.Coord)).FirstOrDefault();
                    if (cell == null) continue;
                    Switch(u, AltTargetKind.BreakObstacle, $"{lost} — ломает препятствие {cell.Coord}");
                    u.Ai.AltCell = cell.Coord;
                    u.TargetObject = "";
                    return;
                }
                case AltTargetKind.Stop:
                    Switch(u, AltTargetKind.Stop, $"{lost} — останавливается");
                    u.TargetObject = "";
                    u.Ai.HoldCenter = u.Pos;
                    return;
                case AltTargetKind.Retreat:
                {
                    var home = u.SpawnBase != "" ? sc?.Object(u.SpawnBase) : null;
                    if (home == null) continue;
                    Switch(u, AltTargetKind.Retreat, $"{lost} — отступает к «{home.DisplayName}»");
                    u.TargetObject = "";
                    u.Ai.AltCell = home.Anchor;
                    return;
                }
                case AltTargetKind.Event:
                    Switch(u, AltTargetKind.Event, $"{lost} — событие «{a.Flag}»");
                    _sim.Scenario.SetFlag(a.Flag);
                    u.TargetObject = "";
                    u.Ai.HoldCenter = u.Pos;
                    return;
            }
        }
        Switch(u, AltTargetKind.Stop, $"{lost} — альтернативной цели нет, останавливается");
        u.TargetObject = "";
    }

    private void Switch(Unit u, AltTargetKind kind, string text)
    {
        u.Ai.Alternative = kind;
        u.Ai.BlockedSince = -1;
        u.Route = null;
        u.Nav = null;
        u.Ai.OffRouteSince = -1;
        _sim.Emit(SimEventType.AiDecision, LogLevel.Normal, $"{u.Name}: {text}", u.Id, -1, u.Pos);
        Explain(u, text);
    }

    private bool FollowAlternative(Unit u, AiProfileDef p)
    {
        switch (u.Ai.Alternative)
        {
            case AltTargetKind.AttackDefenders:
                return Seek(u);
            case AltTargetKind.BreakObstacle when u.Ai.AltCell is HexCoord c:
            {
                var cell = _sim.Map.Get(c);
                if (cell == null || cell.Terrain.DestructibleHp <= 0)
                {
                    u.Ai.Alternative = null;
                    return false;
                }
                return BreakObstacle(u, cell);
            }
            case AltTargetKind.Retreat when u.Ai.AltCell is HexCoord home:
                if (HexCoord.Distance(u.Pos, home) <= 1)
                {
                    Explain(u, "отступил к своей базе");
                    return true;
                }
                return MoveTo(u, home, 1, "отступает к своей базе");
            case AltTargetKind.Stop:
            case AltTargetKind.Event:
                Explain(u, "остановился (альтернативная цель)");
                return true;
        }
        return false;
    }

    /// <summary>Goes to its target object (no route): the object's nearest hex.</summary>
    private bool GoToObject(Unit u)
    {
        var o = _sim.Scenario.Def?.Object(u.TargetObject);
        if (o == null) return false;
        var cell = o.Cells().OrderBy(c => HexCoord.Distance(u.Pos, c)).First();
        if (HexCoord.Distance(u.Pos, cell) <= 1) return false;
        if (MoveTo(u, cell, 1, $"идёт к «{o.DisplayName}»")) return true;
        Problem(u, "no_path", $"нет пути к «{o.DisplayName}»");
        if (u.Ai.BlockedSince < 0) u.Ai.BlockedSince = _sim.Tick;
        return false;
    }

    /// <summary>Attacks a destructible obstacle with its basic attack (or a skill that hits cells).</summary>
    private bool BreakObstacle(Unit u, Cell cell)
    {
        var skill = new[] { u.BasicAttack }.Concat(u.Skills).FirstOrDefault(s => s != null && Affordable(u, s) && s.Targeting == TargetingMode.Cell
            && s.Stages.Any(st => st.Effects.Any(e => e.Type is EffectType.DestroyObstacle or EffectType.Damage)));
        if (skill != null)
        {
            var chk = _sim.Skills.Validate(u, skill, cell.Coord, -1, null, u.Pos, u.Facing, checkCosts: false);
            if (chk.Ok)
            {
                if (u.Queue.Current is { IsAttackLike: true }) return true;
                Issue(u, Command.Skill(skill.Id, cell.Coord), $"ломает препятствие {cell.Coord} навыком «{skill.DisplayName}»");
                return true;
            }
        }
        if (HexCoord.Distance(u.Pos, cell.Coord) <= 1)
        {
            // Without a skill for cells it hits the obstacle by its base damage (the same rules as an obstacle hit).
            _sim.CellEffects.DamageObstacle(cell, (int)Math.Max(1, Math.Round(u.Stat(Stats.Power) + 5)), u, "ломает препятствие");
            u.BusyUntil = _sim.Tick + _sim.TicksFor(1);
            Explain(u, $"ломает препятствие {cell.Coord}");
            return true;
        }
        return MoveTo(u, cell.Coord, 1, $"идёт ломать препятствие {cell.Coord}");
    }

    /// <summary>«Свободный бой»: goes to the nearest unit of the other side it knows nothing better about.</summary>
    private bool Seek(Unit u)
    {
        var e = _sim.Units.Where(x => x.Alive && !x.IsStructure && _sim.IsHostile(u, x)).OrderBy(x => HexCoord.Distance(u.Pos, x.Pos)).ThenBy(x => x.Id).FirstOrDefault()
                ?? _sim.Units.Where(x => x.Alive && _sim.IsHostile(u, x)).OrderBy(x => HexCoord.Distance(u.Pos, x.Pos)).ThenBy(x => x.Id).FirstOrDefault();
        if (e == null) return false;
        if (StructureSystem.DistanceTo(u.Pos, e) <= 1) return false;
        if (MoveTo(u, StructureSystem.AimCell(e, u.Pos), 1, $"ищет противника: идёт к {e.Name}")) return true;
        Problem(u, "no_path", $"нет пути к противнику {e.Name}");
        return false;
    }

    /// <summary>
    /// A hunter: attacks a visible enemy, otherwise goes to the nearest known or seen unit of the other side; with nobody
    /// to hunt — false (the route or the base then).
    /// </summary>
    private bool Hunt(Unit u, AiProfileDef p)
    {
        var visible = _sim.Units.Where(e => e.Alive && !e.IsStructure && _sim.IsHostile(u, e) && _sim.Vision.CanFactionSee(u.FactionId, e) && CanHitLayer(u, e)).ToList();
        if (visible.Count > 0)
        {
            var target = ChooseTarget(u, visible, p, u.Ai.Directive, out var why);
            SetTarget(u, target);
            if (TryAttack(u, target, p, "охотится: " + why)) return true;
            if (Approach(u, target, p, "охотится: " + why, false)) return true;
        }
        var cur = u.Queue.Current;
        if (cur != null && cur.Type == CommandType.Move && cur.Status != CommandStatus.Pending) return true;
        var e = _sim.Units.Where(x => x.Alive && !x.IsStructure && _sim.IsHostile(u, x)).OrderBy(x => HexCoord.Distance(u.Pos, x.Pos)).ThenBy(x => x.Id).FirstOrDefault();
        if (e == null) return false;
        if (HexCoord.Distance(u.Pos, e.Pos) <= 1) return false;
        if (MoveTo(u, e.Pos, 1, $"охотится: идёт к {e.Name}")) return true;
        Problem(u, "no_path", $"нет пути к противнику {e.Name}");
        return false;
    }

    /// <summary>The own base a «защитник базы» keeps: the highest defence priority, then the nearest.</summary>
    private HexCoord? GuardedBase(Unit u)
    {
        var sc = _sim.Scenario.Def;
        if (sc == null) return null;
        var b = sc.Objects.Where(o => o.Type == ScenarioObjectType.PlayerBase && _sim.Structures.Of(o.Id) is { Alive: true } s && s.FactionId == u.FactionId)
            .OrderByDescending(o => o.Health?.DefensePriority ?? 0).ThenBy(o => o.Cells().Min(c => HexCoord.Distance(u.Pos, c))).FirstOrDefault();
        return b?.Cells().OrderBy(c => HexCoord.Distance(u.Pos, c)).First();
    }

    /// <summary>«Вести союзников»: nearby allies of the leader attack its target for a while.</summary>
    private void ShareTarget(Unit leader, Unit target)
    {
        foreach (var a in _sim.Units)
        {
            if (a == leader || !a.Alive || !a.AiControlled || a.FactionId != leader.FactionId || a.IsStructure) continue;
            if (HexCoord.Distance(a.Pos, leader.Pos) > 6) continue;
            a.Ai.SharedTarget = target.Id;
            a.Ai.SharedUntil = _sim.Tick + _sim.TicksFor(3);
        }
    }

    /// <summary>«Держаться союзников»: back towards the nearest ally when too far from every one.</summary>
    private bool KeepWithAllies(Unit u, AiProfileDef p)
    {
        var allies = _sim.Units.Where(a => a != u && a.Alive && !a.IsStructure && a.FactionId == u.FactionId).ToList();
        if (allies.Count == 0) return false;
        var near = allies.OrderBy(a => HexCoord.Distance(u.Pos, a.Pos)).First();
        if (HexCoord.Distance(u.Pos, near.Pos) <= Math.Max(1, p.HoldRadius)) return false;
        return MoveTo(u, near.Pos, 1, $"держится союзников: к {near.Name}");
    }

    /// <summary>Walks around its position: the six corners of a ring of the patrol radius.</summary>
    private bool Patrol(Unit u, AiProfileDef p)
    {
        if (u.Ai.HoldCenter is not HexCoord c) return false;
        var points = new List<HexCoord>();
        for (int i = 0; i < 6; i++)
        {
            var pt = c;
            for (int k = 0; k < p.PatrolRadius; k++) pt = pt.Neighbor((HexDirection)i);
            if (_sim.Map.Get(pt) is { } cell && MovementRules.CanPassTerrain(_sim, u, cell.Terrain)) points.Add(pt);
        }
        if (points.Count == 0) return false;
        for (int guard = 0; guard < points.Count; guard++)
        {
            var goal = points[u.Ai.PatrolIndex % points.Count];
            if (HexCoord.Distance(u.Pos, goal) <= 0)
            {
                u.Ai.PatrolIndex++;
                continue;
            }
            if (MoveTo(u, goal, 0, $"патрулирует: точка {u.Ai.PatrolIndex % points.Count + 1}/{points.Count}")) return true;
            u.Ai.PatrolIndex++;
        }
        return false;
    }


    /// <summary>Moves to the next point of the unit's route; false when the route is finished.</summary>
    private bool FollowRoute(Unit u)
    {
        // A route graph plans edge by edge: at the end of the planned hexes the navigator picks the next exit.
        for (int guard = 0; guard < 4 && u.Route != null; guard++)
        {
            var route = u.Route;
            while (u.RouteIndex < route.Count)
            {
                var pt = route[u.RouteIndex];
                int d = HexCoord.Distance(u.Pos, pt);
                var cell = _sim.Map.Get(pt);
                bool occupied = cell != null && cell.Occupants.Any(o => o != u && o.Alive && o.Layer == u.Layer);
                if (d == 0 || (d == 1 && occupied && u.RouteIndex < route.Count - 1)) u.RouteIndex++;
                else break;
            }
            if (u.RouteIndex < route.Count)
            {
                var rdef = u.Nav != null ? _sim.Scenario.Def?.Route(u.Nav.Route) : null;
                // «Только узлы»: straight to the end of the edge, its hexes are not required (rules version 2).
                if (rdef?.Strictness == RouteStrictness.Waypoints && _sim.Rules.Time.SimVersion >= 2 && u.RouteIndex < route.Count - 1) u.RouteIndex = route.Count - 1;
                if (RouteStuck(u, route, rdef)) return true;
                bool moving = MoveTo(u, route[u.RouteIndex], 0, u.Nav != null
                    ? $"идёт по маршруту «{u.Nav.Route}»: ребро {u.Nav.Edge}, гекс {u.RouteIndex + 1}/{route.Count}"
                    : $"идёт по маршруту: точка {u.RouteIndex + 1}/{route.Count}", Allowed(u, route, rdef));
                if (moving) u.Ai.BlockedSince = -1;
                else if (HexCoord.Distance(u.Pos, route[u.RouteIndex]) > 1
                         && !_sim.Pathfinder.Find(_sim, u, u.Pos, route[u.RouteIndex], new PathOptions { IgnoreUnits = true, MaxExpanded = 3000 }).Found)
                {
                    // No path to the next hex of the route even past the units: the route is blocked (an alternative
                    // after a while). A crowd on the way is only waiting.
                    if (u.Ai.BlockedSince < 0) u.Ai.BlockedSince = _sim.Tick;
                    Problem(u, "no_path", $"нет пути к точке маршрута {route[u.RouteIndex]}");
                }
                return moving;
            }
            if (u.Nav == null) return false;
            switch (_sim.Routes.Advance(u, out var why))
            {
                case RouteStep.Wait:
                    Explain(u, why);
                    // A closed exit (not a dwell) counts as a blocked route.
                    if (why.StartsWith("нет доступного выхода", StringComparison.Ordinal))
                    {
                        if (u.Ai.BlockedSince < 0) u.Ai.BlockedSince = _sim.Tick;
                        Problem(u, "route_blocked", why);
                    }
                    return true;
                case RouteStep.Finished:
                    return false;
            }
            u.Ai.BlockedSince = -1;
        }
        return false;
    }

    // ------------------------------------------------------------------ blocked routes (ТЗ ч. 5, 11.2–11.5; docs/DECISIONS_5.md §86)

    private readonly Dictionary<(string Route, string Edge, int Tol), HashSet<HexCoord>> _corridors = new();

    /// <summary>
    /// Where a route enemy may step (rules version 2): «строго» — the hexes of its edge; «коридор» — within the tolerance of
    /// them. An enemy already outside (after a fight) is not restricted until it is back.
    /// </summary>
    private HashSet<HexCoord>? Allowed(Unit u, List<HexCoord> route, RouteDef? r)
    {
        if (r == null || _sim.Rules.Time.SimVersion < 2 || r.Strictness == RouteStrictness.Waypoints || u.Nav == null) return null;
        int tol = r.Strictness == RouteStrictness.StrictHexes ? 0 : Math.Max(1, r.Tolerance);
        var key = (r.Id, u.Nav.Edge, tol);
        if (!_corridors.TryGetValue(key, out var set))
        {
            set = new HashSet<HexCoord>();
            foreach (var c in route)
                foreach (var x in tol == 0 ? new List<HexCoord> { c } : HexCoord.Spiral(c, tol))
                    set.Add(x);
            _corridors[key] = set;
        }
        return set.Contains(u.Pos) ? set : null;
    }

    /// <summary>
    /// «Нет продвижения»: no new hex of the route and no step closer to the next one for the time of the rules — whether the
    /// enemy stands, walks back and forth or replans. Then the next reaction of the route's list; each next one after the
    /// same time again. True — a reaction acted this decision (or the enemy stands aside / gave up).
    /// </summary>
    private bool RouteStuck(Unit u, List<HexCoord> route, RouteDef? r)
    {
        if (_sim.Rules.Time.SimVersion < 2) return false;
        var a = u.Ai;
        if (a.Aborted)
        {
            Explain(u, "остановился: маршрут непроходим");
            return true;
        }
        if (a.YieldUntil > _sim.Tick)
        {
            Explain(u, "уступает дорогу");
            return true;
        }
        long key = (u.Nav?.Taken.Count ?? 0) * 100_000L + u.RouteIndex;
        int dist = HexCoord.Distance(u.Pos, route[u.RouteIndex]);
        long gap = _sim.TicksFor(Math.Max(1.5, 3 * (u.AiProfile?.DecisionInterval ?? 0.5)));
        if (key != a.ProgressKey || dist < a.ProgressDist || a.LastFollowTick < 0 || _sim.Tick - a.LastFollowTick > gap)
        {
            if (key != a.ProgressKey || dist < a.ProgressDist) a.StuckStep = 0;
            a.ProgressKey = key;
            a.ProgressDist = dist;
            a.ProgressSince = _sim.Tick;
        }
        a.LastFollowTick = _sim.Tick;
        long window = _sim.TicksFor(_sim.Rules.Stuck.Seconds);
        if (_sim.Tick - a.ProgressSince < window * (a.StuckStep + 1)) return false;
        var list = r?.Reactions ?? RouteDef.DefaultReactions;
        if (a.StuckStep >= list.Count) return false;
        var reaction = list[a.StuckStep++];
        a.LastReaction = reaction.ToString();
        double secs = (_sim.Tick - a.ProgressSince) * _sim.Dt;
        string strict = r?.Strictness == RouteStrictness.StrictHexes && reaction is BlockedReaction.LocalDetour or BlockedReaction.Repath
            ? " — сходит со строгого маршрута по списку реакций" : "";
        switch (reaction)
        {
            case BlockedReaction.Wait:
                Problem(u, "stuck", $"нет продвижения {secs:0.#} с — ждёт");
                return false;
            case BlockedReaction.LocalDetour:
                return Detour(u, route, secs, strict);
            case BlockedReaction.Repath:
            {
                var end = route[^1];
                var path = _sim.Pathfinder.Find(_sim, u, u.Pos, end, new PathOptions { MaxExpanded = 6000 });
                if (!path.Found)
                {
                    Problem(u, "stuck", $"нет продвижения {secs:0.#} с — к концу ребра пути нет");
                    return false;
                }
                u.RouteIndex = route.Count - 1;
                _sim.Emit(SimEventType.AiProblem, LogLevel.Diagnostic, $"ИИ {u.Name}: нет продвижения {secs:0.#} с — перестраивает путь к узлу {end}{strict}", u.Id, -1, u.Pos, damageType: "repath");
                return MoveTo(u, end, 0, "перестроил путь к узлу маршрута");
            }
            case BlockedReaction.AttackBlocker:
            {
                var blocker = BlockerAhead(u, route);
                if (blocker == null || !_sim.IsHostile(u, blocker) || u.AiProfile == null) return false;
                _sim.Emit(SimEventType.AiProblem, LogLevel.Diagnostic, $"ИИ {u.Name}: путь закрыт {blocker.Name} — атакует", u.Id, blocker.Id, u.Pos, damageType: "attack_blocker");
                SetTarget(u, blocker);
                return TryAttack(u, blocker, u.AiProfile, "путь закрыт — атакует преграду");
            }
            case BlockedReaction.SwitchRoute:
            {
                if (r == null || r.Fallback == "" || r.Fallback == r.Id || _sim.Scenario.Def?.Route(r.Fallback) is not { } alt) return false;
                _sim.Emit(SimEventType.AiProblem, LogLevel.Normal, $"ИИ {u.Name}: нет продвижения {secs:0.#} с — переходит на запасной маршрут «{alt.DisplayName}»", u.Id, -1, u.Pos, damageType: "switch_route");
                _sim.Routes.Assign(u, alt.Id, fromStart: false);
                return true;
            }
            case BlockedReaction.ChangeObjective:
            {
                if (u.Ai.Alternative != null || u.AiProfile == null) return false;
                u.Ai.BlockedSince = Math.Min(u.Ai.BlockedSince < 0 ? long.MaxValue : u.Ai.BlockedSince, a.ProgressSince);
                ChooseAlternative(u, u.AiProfile);
                return u.Ai.Alternative != null;
            }
            default:
                a.Aborted = true;
                _sim.Emit(SimEventType.AiProblem, LogLevel.Normal, $"ИИ {u.Name}: маршрут непроходим ({secs:0.#} с без продвижения, реакции исчерпаны) — остановился", u.Id, -1, u.Pos, damageType: "aborted");
                u.Queue.Clear();
                return true;
        }
    }

    /// <summary>The unit standing on the next hex of the way to the route (null — none).</summary>
    private Unit? BlockerAhead(Unit u, List<HexCoord> route)
    {
        var goal = route[u.RouteIndex];
        var free = _sim.Pathfinder.Find(_sim, u, u.Pos, goal, new PathOptions { IgnoreUnits = true, MaxExpanded = 3000 });
        if (!free.Found || free.Path.Count < 2) return null;
        var step = _sim.Map.Get(free.Path[1]);
        return step?.Occupants.FirstOrDefault(o => o != u && o.Alive && o.Layer == u.Layer);
    }

    /// <summary>
    /// «Уступить или обойти»: two route enemies face each other — the one with the larger ID steps aside into a free hex
    /// off the way and waits there a moment (no one passes through another, ТЗ 11.5); a standing blocker is walked around.
    /// </summary>
    private bool Detour(Unit u, List<HexCoord> route, double secs, string strict)
    {
        var blocker = BlockerAhead(u, route);
        if (blocker != null && blocker.Nav != null && blocker.Ai.YieldUntil <= _sim.Tick && blocker.Id > u.Id && !blocker.Ai.Aborted)
        {
            // The other one yields when its own list gets there; this one waits.
            Problem(u, "stuck", $"нет продвижения {secs:0.#} с — ждёт, пока {blocker.Name} уступит");
            return false;
        }
        var mc = MoveContext.For(_sim, u);
        var here = _sim.Map.Get(u.Pos);
        var onWay = new HashSet<HexCoord>(route);
        if (blocker?.Route != null) onWay.UnionWith(blocker.Route);
        HexCoord? best = null;
        int bestScore = int.MaxValue;
        foreach (var n in u.Pos.Neighbors())
        {
            var cell = _sim.Map.Get(n);
            if (cell == null || here == null || MovementRules.Check(mc, here, cell, false, out _) != StepCheck.Ok) continue;
            int score = (onWay.Contains(n) ? 100 : 0) + HexCoord.Distance(n, route[u.RouteIndex]);
            if (score < bestScore)
            {
                bestScore = score;
                best = n;
            }
        }
        if (best == null)
        {
            Problem(u, "stuck", $"нет продвижения {secs:0.#} с — отойти некуда");
            return false;
        }
        bool yielding = blocker != null && blocker.Nav != null;
        u.Ai.YieldUntil = _sim.Tick + _sim.TicksFor(_sim.Rules.Stuck.YieldSeconds + 1);
        _sim.Emit(SimEventType.AiProblem, LogLevel.Diagnostic,
            $"ИИ {u.Name}: нет продвижения {secs:0.#} с — " + (yielding ? $"уступает дорогу {blocker!.Name}" : "обходит") + $", шаг в {best.Value}{strict}",
            u.Id, blocker?.Id ?? -1, u.Pos, damageType: "detour");
        u.Queue.Clear();
        Issue(u, Command.Move(best.Value), yielding ? $"уступает дорогу {blocker!.Name}" : "обходит препятствие");
        return true;
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

    private Unit ChooseTarget(Unit u, List<Unit> visible, AiProfileDef p, AiDirective dir, out string why)
    {
        // «Установить цель» by an event, then the leader's target, before the profile's priority.
        if (u.Ai.ForcedTargetTag != "" && visible.Where(e => TypeMatches(e, u.Ai.ForcedTargetTag)).OrderBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).FirstOrDefault() is { } forced)
        {
            why = "цель задана событием";
            return forced;
        }
        if (u.Ai.SharedTarget >= 0 && _sim.Tick < u.Ai.SharedUntil && visible.FirstOrDefault(e => e.Id == u.Ai.SharedTarget) is { } shared)
        {
            why = "цель вожака";
            return shared;
        }
        var priority = dir switch
        {
            AiDirective.AttackWeakest => AiTargetPriority.Weakest,
            AiDirective.AttackDangerous => AiTargetPriority.MostDangerous,
            AiDirective.DefendBase => AiTargetPriority.ThreatToBase,
            _ => p.TargetPriority,
        };
        switch (priority)
        {
            case AiTargetPriority.Isolated:
            {
                why = "изолированная";
                double Alone(Unit e) => _sim.Units.Where(a => a != e && a.Alive && !a.IsStructure && a.FactionId == e.FactionId).Select(a => (double)HexCoord.Distance(a.Pos, e.Pos)).DefaultIfEmpty(99).Min();
                return visible.OrderByDescending(Alone).ThenBy(e => e.HpFraction).ThenBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).First();
            }
            case AiTargetPriority.ThreatToBase:
            {
                var bases = _sim.Units.Where(b => b.Alive && b.IsStructure && b.FactionId == u.FactionId).ToList();
                if (bases.Count > 0)
                {
                    why = "угрожает базе";
                    return visible.Where(e => !e.IsStructure).DefaultIfEmpty(visible[0])
                        .OrderBy(e => bases.Min(b => StructureSystem.DistanceTo(e.Pos, b))).ThenBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).First()!;
                }
                break;
            }
            case AiTargetPriority.ByTypes when p.TargetTypes.Count > 0:
            {
                var preferred = visible.Where(e => p.TargetTypes.Any(t => TypeMatches(e, t))).ToList();
                if (preferred.Count > 0)
                {
                    why = "предпочитаемая цель";
                    return preferred.OrderBy(e => HexCoord.Distance(u.Pos, e.Pos)).ThenBy(e => e.Id).First();
                }
                break;
            }
        }
        switch (priority)
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
            // «Предпочитать контроль»: stuns, roots and slows first.
            if (p.PreferControl && IsControl(s)) score = Math.Max(score, 0) + 50;
            if (score > bestScore)
            {
                bestScore = score;
                best = s;
            }
        }
        if (best == null || bestScore <= 0 && best != u.BasicAttack) return false;
        if (p.PreferControl && IsControl(best)) why += ", контроль";

        foreach (var q in u.Queue.Items)
            if (q.IsAttackLike && q.TargetUnitId == target.Id && (q.Status != CommandStatus.Pending || u.Queue.Current == q))
                return true;
        if (u.Casting != null) return true;
        var cmd = MakeSkillCommand(u, best, target);
        Issue(u, cmd, $"атакует {target.Name} ({why}) навыком «{best.DisplayName}»");
        return true;
    }

    /// <summary>A skill that controls: it applies a status that stops moving or acting, or slows.</summary>
    private bool IsControl(SkillDef s) => s.Stages.Any(st => st.Effects.Any(e => e.Type == EffectType.ApplyStatus && _sim.Content.Status(e.Status) is { } sd
        && (sd.Tags.Contains("control") || sd.Levels.Any(l => l.Flags.BlocksMovement || l.Flags.BlocksActions || l.Flags.BlocksSkills))));

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

    private bool Approach(Unit u, Unit target, AiProfileDef p, string why, bool hold)
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
        if (maxRange <= 1 && !hold)
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
            if (hold && u.Ai.HoldCenter is HexCoord hc && HexCoord.Distance(c, hc) > p.HoldRadius) continue;
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
            if (hold) return false;
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

    private bool MoveTo(Unit u, HexCoord goal, int tolerance, string why, HashSet<HexCoord>? allowed = null)
    {
        var cur = u.Queue.Current;
        if (cur != null && cur.Type == CommandType.Move && cur.TargetCell.HasValue && HexCoord.Distance(cur.TargetCell.Value, goal) <= tolerance) return true;
        var path = _sim.Pathfinder.Find(_sim, u, u.Pos, goal, new PathOptions { GoalTolerance = tolerance, HazardPenalty = u.AiProfile?.HazardAvoidance ?? 0, MaxExpanded = 6000, Allowed = allowed });
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
