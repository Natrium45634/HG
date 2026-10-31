using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>The link of a summoned unit to its summon (ТЗ ч. 6, 20; §106). Units of the legacy summon have none.</summary>
public sealed class SummonState
{
    public SummonDef Def { get; init; } = null!;
    /// <summary>The root of the chain of summons (the summoner itself for the first level).</summary>
    public int OwnerId { get; set; } = -1;
    /// <summary>One call of a summon: «<summoner>:<request>».</summary>
    public string Group { get; init; } = "";
    public int Depth { get; init; } = 1;
    public long Serial { get; init; }
    /// <summary>The link was broken: no summoner, the behaviour «после призывателя».</summary>
    public bool Independent { get; set; }
    public SummonBehavior Behavior { get; set; }
    /// <summary>Where it appeared: the post of «держать позицию», the start of a patrol.</summary>
    public HexCoord Home { get; set; }
    /// <summary>The hex of the skill: the goal of «идти к точке».</summary>
    public HexCoord Point { get; init; }
    public bool Arrived { get; set; }
    public int PatrolIndex { get; set; }
    /// <summary>The target of the summoning skill («до гибели цели»).</summary>
    public int TargetUnitId { get; init; } = -1;
    /// <summary>The cast that summoned («до конца навыка»).</summary>
    public int CastId { get; init; } = -1;
    public long EndTick { get; set; } = -1;
    /// <summary>An order of the player runs («приказы владельца важнее»): the AI waits until the queue is empty.</summary>
    public bool PlayerOrder { get; set; }
    /// <summary>The faction of the summoner last seen (following a change of side).</summary>
    public string SummonerFaction { get; set; } = "";
}

/// <summary>
/// Summons of sub-units (ТЗ ч. 6, 20; docs/DECISIONS_6.md §106): a request of a skill, a passive or a scenario action places the
/// summoned by the description — limits and depth first, then the pattern of hexes, the policy for taken hexes and the refund —
/// and keeps their life: the time, the summoner, the skill, the target; the end goes by the policy (vanish, die, neutral, independent,
/// transfer). Everything else of a summoned unit is the ordinary battle: movement, combat, statuses, auras, scales, friendly fire.
/// </summary>
public sealed class SummonSystem
{
    private readonly Simulation _sim;

    private sealed class Request
    {
        public SummonDef Def = null!;
        public int Summoner = -1;
        public string Faction = "";
        public HexCoord Anchor;
        public int Target = -1;
        public int Cast = -1;
        public Dictionary<string, double> Paid = new();
        public double PaidDp;
        public int Requested;
        public int Remaining;
        public long Due;
        public long Deadline = -1;
        public int Serial;
        public string Cause = "";
    }

    private readonly List<Request> _pending = new();
    private readonly Dictionary<string, int> _totalByDef = new(StringComparer.Ordinal);
    private int _requests;
    private long _serial;

    public SummonSystem(Simulation sim) => _sim = sim;

    /// <summary>Summoned during the battle (all descriptions).</summary>
    public int Total { get; private set; }
    public int Failed { get; private set; }
    public int ActivePeak { get; private set; }
    public int PendingCount => _pending.Sum(p => p.Remaining);

    private SummonRulesDef Rules => _sim.Scenario.Def?.Summons ?? Default;
    private static readonly SummonRulesDef Default = new();

    public IEnumerable<Unit> Alive => _sim.Units.Where(u => u.Alive && u.Summon != null);

    /// <summary>The summoned of a summoner alive now (the HUD's counter).</summary>
    public List<Unit> Of(Unit summoner) => _sim.Units.Where(u => u.Alive && u.SummonerId == summoner.Id).ToList();

    // ------------------------------------------------------------------ requests

    /// <summary>
    /// A summon by the description: from a skill (its cast — for «до конца навыка» and the refund), a passive or a scenario action
    /// (no summoner: the faction given). False — refused (the reason is logged).
    /// </summary>
    public bool Summon(SummonDef def, Unit? summoner, HexCoord anchor, int targetUnit, SkillCast? cast, string cause, string faction = "")
    {
        var unit = _sim.Content.Unit(def.Unit);
        string Who() => summoner?.Name ?? cause;
        if (unit == null) return Fail(def, summoner, $"{Who()}: нет шаблона «{def.Unit}» для призыва «{def.DisplayName}»");
        // The chain (ТЗ 20.5): a summoned summons only when its own description allows, and not deeper than the limits.
        var parent = summoner?.Summon;
        if (parent != null && !parent.Def.AllowSubSummon)
            return Fail(def, summoner, $"{Who()}: призванный не может призывать («{parent.Def.DisplayName}»)");
        int depth = (parent?.Depth ?? 0) + 1;
        int maxDepth = Rules.MaxDepth > 0 ? Math.Min(Rules.MaxDepth, def.MaxDepth) : def.MaxDepth;
        if (parent != null) maxDepth = Math.Min(maxDepth, parent.Def.MaxDepth);
        if (depth > Math.Max(1, maxDepth)) return Fail(def, summoner, $"{Who()}: глубина цепочки призывов {depth} больше {maxDepth}");
        double dp = 0;
        if (def.DeployCost > 0 && _sim.Deployment.Enabled)
        {
            if (!_sim.Deployment.Spend(def.DeployCost, $"призыв «{def.DisplayName}»"))
                return Fail(def, summoner, $"{Who()}: мало очков развёртывания на «{def.DisplayName}» ({def.DeployCost:0.#})");
            dp = def.DeployCost;
        }
        var r = new Request
        {
            Def = def, Summoner = summoner?.Id ?? -1, Faction = faction, Anchor = anchor, Target = targetUnit, Cast = cast?.Id ?? -1,
            Paid = cast != null ? new Dictionary<string, double>(cast.PaidResources) : new(), PaidDp = dp,
            Requested = Math.Max(1, def.Count), Remaining = Math.Max(1, def.Count), Serial = ++_requests, Cause = cause,
            Due = _sim.Tick + (def.Delay > 0 ? _sim.TicksFor(def.Delay) : 0),
        };
        if (def.Delay > 0) _pending.Add(r);
        else Place(r);
        return true;
    }

    private bool Fail(SummonDef def, Unit? summoner, string text)
    {
        Failed++;
        _sim.Emit(SimEventType.SummonFailed, LogLevel.Normal, text, summoner?.Id ?? -1, damageType: def.Id);
        return false;
    }

    /// <summary>Places what a request can now; the rest — by the policy for taken hexes (refund, queue).</summary>
    private void Place(Request r)
    {
        var def = r.Def;
        var unit = _sim.Content.Unit(def.Unit)!;
        var summoner = r.Summoner >= 0 ? _sim.GetUnit(r.Summoner) : null;
        if (r.Summoner >= 0 && summoner is not { Alive: true })
        {
            Refund(r, r.Remaining, "призыватель погиб до появления");
            r.Remaining = 0;
            return;
        }
        string faction = FactionFor(def, summoner, r.Faction, unit);
        var pattern = Pattern(def, summoner, r.Anchor, r.Target);
        var free = pattern.Where(c => Free(def, unit, faction, c)).ToList();
        if (free.Count < r.Remaining && def.OnBlocked == SummonFailure.SearchNearest)
            foreach (var c in pattern.SelectMany(p => HexCoord.Spiral(p, def.SearchRadius)).Distinct())
                if (!free.Contains(c) && Free(def, unit, faction, c)) free.Add(c);
        if (free.Count < r.Remaining && def.OnBlocked == SummonFailure.CancelAll && r.Deadline < 0)
        {
            Failed++;
            _sim.Emit(SimEventType.SummonFailed, LogLevel.Normal, $"{summoner?.Name ?? r.Cause}: «{def.DisplayName}» отменён — свободных гексов {free.Count} из {r.Remaining}",
                r.Summoner, damageType: def.Id, cell: r.Anchor);
            Refund(r, r.Remaining, "призыв отменён");
            r.Remaining = 0;
            return;
        }
        int placed = 0;
        foreach (var c in free)
        {
            if (r.Remaining == 0) break;
            if (!Free(def, unit, faction, c)) continue; // a summoned one of this same request stands here now
            var limit = LimitProblem(def, summoner, faction);
            if (limit != null && def.OnLimit == SummonLimitPolicy.ReplaceOldest && Oldest(def, summoner, faction) is { } old)
            {
                End(old, def.Despawn is SummonEnd.ByRules or SummonEnd.Keep ? SummonEnd.Vanish : def.Despawn, "заменён новым призывом");
                limit = LimitProblem(def, summoner, faction);
            }
            if (limit != null)
            {
                Failed++;
                _sim.Emit(SimEventType.SummonFailed, LogLevel.Normal, $"{summoner?.Name ?? r.Cause}: «{def.DisplayName}» — {limit}", r.Summoner, damageType: def.Id, cell: r.Anchor);
                Refund(r, r.Remaining, limit);
                r.Remaining = 0;
                return;
            }
            if (Create(r, def, unit, summoner, faction, c) == null) break;
            r.Remaining--;
            placed++;
        }
        if (r.Remaining == 0) return;
        if (def.OnBlocked == SummonFailure.QueueUntilFree)
        {
            if (r.Deadline < 0)
            {
                r.Deadline = _sim.Tick + _sim.TicksFor(def.QueueTimeout);
                _sim.Emit(SimEventType.Info, LogLevel.Detail, $"«{def.DisplayName}»: {r.Remaining} ждут свободного места до {def.QueueTimeout:0.#} с", r.Summoner, cell: r.Anchor);
                if (!_pending.Contains(r)) _pending.Add(r);
            }
            if (_sim.Tick < r.Deadline) return;
        }
        Failed++;
        _sim.Emit(SimEventType.SummonFailed, LogLevel.Normal,
            $"{summoner?.Name ?? r.Cause}: «{def.DisplayName}» — нет свободных гексов для {r.Remaining} из {r.Requested}" + (placed > 0 ? $" (появилось {placed})" : ""),
            r.Summoner, damageType: def.Id, cell: r.Anchor);
        Refund(r, r.Remaining, "нет места");
        r.Remaining = 0;
    }

    /// <summary>The share of the price of the skill (and of the deployment points) for those who did not appear.</summary>
    private void Refund(Request r, int missing, string why)
    {
        if (!r.Def.Refund || missing <= 0 || r.Requested <= 0) return;
        double share = (double)missing / r.Requested;
        var summoner = r.Summoner >= 0 ? _sim.GetUnit(r.Summoner) : null;
        if (summoner is { Alive: true })
            foreach (var kv in r.Paid)
                if (summoner.Resources.TryGetValue(kv.Key, out var res) && kv.Value * share > 0)
                {
                    double before = res.Current;
                    res.Current = Math.Min(res.Max, res.Current + kv.Value * share);
                    _sim.Emit(SimEventType.ResourceChanged, LogLevel.Detail, $"{summoner.Name}: возврат {res.Current - before:0.#} «{res.Def.DisplayName}» — {why}", summoner.Id, summoner.Id,
                        value: res.Current - before);
                }
        if (r.PaidDp > 0) _sim.Deployment.Spend(-r.PaidDp * share, $"возврат за призыв «{r.Def.DisplayName}»");
    }

    // ------------------------------------------------------------------ placement

    /// <summary>The hexes of the pattern in a fixed order (the front of the summoner first, then clockwise and counter-clockwise).</summary>
    public List<HexCoord> Pattern(SummonDef def, Unit? summoner, HexCoord anchor, int targetUnit)
    {
        var center = summoner?.Pos ?? anchor;
        var facing = summoner?.Facing ?? HexDirection.E;
        var target = targetUnit >= 0 ? _sim.GetUnit(targetUnit) : null;
        return PatternOf(def, center, facing, anchor, target?.Pos);
    }

    /// <summary>The same without a battle (the editor's preview): the caster at <paramref name="center"/> looking <paramref name="facing"/>.</summary>
    public static List<HexCoord> PatternOf(SummonDef def, HexCoord center, HexDirection facing, HexCoord anchor, HexCoord? target)
    {
        int Fan(HexCoord from, HexCoord c)
        {
            var d = from.DirectionTo(c);
            if (d == null) return 0;
            int k = (((int)d.Value - (int)facing) % 6 + 6) % 6; // 0 front, 5 front-right, 1 front-left…
            return k switch { 0 => 0, 5 => 1, 1 => 2, 4 => 3, 2 => 4, _ => 5 };
        }
        IEnumerable<HexCoord> Around(HexCoord from, int min, int max) =>
            Enumerable.Range(min, max - min + 1).SelectMany(k => HexCoord.Ring(from, k).OrderBy(c => Fan(from, c)).ThenBy(c => c.Q).ThenBy(c => c.R));
        return def.Placement switch
        {
            SummonPlacement.Adjacent => Around(center, 1, 1).ToList(),
            SummonPlacement.Radius => Around(center, 1, Math.Max(1, def.Radius)).ToList(),
            SummonPlacement.AroundCaster => HexCoord.Ring(center, Math.Max(1, def.Radius)).OrderBy(c => Fan(center, c)).ThenBy(c => c.Q).ThenBy(c => c.R).ToList(),
            SummonPlacement.SelectedHex => Around(anchor, 0, 1).OrderBy(c => HexCoord.Distance(c, anchor)).ThenBy(c => HexCoord.Distance(c, center)).ThenBy(c => c.Q).ThenBy(c => c.R).ToList(),
            SummonPlacement.AtTarget => Around(target ?? anchor, 1, 1).OrderBy(c => HexCoord.Distance(c, center)).ThenBy(c => c.Q).ThenBy(c => c.R).ToList(),
            _ => def.Offsets.Select(o => o.Rotate((int)facing) + center).Distinct().ToList(),
        };
    }

    private bool Free(SummonDef def, UnitDef unit, string faction, HexCoord c)
    {
        var cell = _sim.Map.Get(c);
        if (cell == null || !cell.AllowsPlacement) return false;
        if (def.Terrains.Count > 0 && !def.Terrains.Contains(cell.Terrain.Id)) return false;
        return MovementRules.Check(MoveContext.For(_sim, unit, faction), null, cell, false, out _) == StepCheck.Ok;
    }

    private string FactionFor(SummonDef def, Unit? summoner, string given, UnitDef unit)
    {
        switch (def.Faction)
        {
            case SummonFaction.Fixed when def.FixedFaction != "": return def.FixedFaction;
            case SummonFaction.Neutral: return NeutralFaction();
            case SummonFaction.Owner when summoner?.Summon is { } st && _sim.GetUnit(st.OwnerId) is { } owner: return owner.FactionId;
        }
        return summoner?.FactionId ?? (given != "" ? given : unit.Faction);
    }

    private string NeutralFaction() => _sim.Content.Get<FactionDef>("neutral") != null ? "neutral" : "";

    // ------------------------------------------------------------------ limits

    /// <summary>The first limit a new summoned breaks, or null (ТЗ 20.2, 20.5).</summary>
    private string? LimitProblem(SummonDef def, Unit? summoner, string faction)
    {
        var alive = Alive.ToList();
        if (Rules.MaxAlive > 0 && alive.Count >= Rules.MaxAlive) return $"лимит сценария: живых призванных {Rules.MaxAlive}";
        if (Rules.MaxTotal > 0 && Total >= Rules.MaxTotal) return $"лимит сценария: призывов за бой {Rules.MaxTotal}";
        var mine = alive.Where(u => u.Summon!.Def.Id == def.Id).ToList();
        if (def.MaxPerSummoner > 0 && summoner != null && mine.Count(u => u.SummonerId == summoner.Id) >= def.MaxPerSummoner)
            return $"у призывателя уже {def.MaxPerSummoner} живых";
        if (def.MaxPerTeam > 0 && mine.Count(u => u.FactionId == faction) >= def.MaxPerTeam) return $"у стороны уже {def.MaxPerTeam} живых";
        if (def.MaxPerScenario > 0 && mine.Count >= def.MaxPerScenario) return $"в сценарии уже {def.MaxPerScenario} живых";
        if (def.MaxTotal > 0 && _totalByDef.GetValueOrDefault(def.Id) >= def.MaxTotal) return $"за бой уже {def.MaxTotal}";
        return null;
    }

    /// <summary>The earliest summoned of this description the replacement takes: the summoner's own first, then the side's.</summary>
    private Unit? Oldest(SummonDef def, Unit? summoner, string faction)
    {
        var mine = Alive.Where(u => u.Summon!.Def.Id == def.Id).OrderBy(u => u.Summon!.Serial).ToList();
        return mine.FirstOrDefault(u => summoner != null && u.SummonerId == summoner.Id) ?? mine.FirstOrDefault(u => u.FactionId == faction) ?? mine.FirstOrDefault();
    }

    // ------------------------------------------------------------------ creation

    private Unit? Create(Request r, SummonDef def, UnitDef unit, Unit? summoner, string faction, HexCoord c)
    {
        var facing = def.Facing switch
        {
            SummonFacing.Away when summoner != null => summoner.Pos.DirectionTo(c) ?? summoner.Facing,
            SummonFacing.Target => c.DirectionTo(r.Target >= 0 && _sim.GetUnit(r.Target) is { } t ? t.Pos : r.Anchor) ?? summoner?.Facing ?? HexDirection.E,
            _ => summoner?.Facing ?? HexDirection.E,
        };
        Unit u;
        try
        {
            u = _sim.Spawn(unit, faction, c, facing, null, null, def.AiProfile == "" ? null : def.AiProfile);
        }
        catch (InvalidOperationException ex)
        {
            Failed++;
            _sim.Emit(SimEventType.SummonFailed, LogLevel.Normal, $"«{def.DisplayName}»: {ex.Message}", r.Summoner, damageType: def.Id);
            return null;
        }
        var parent = summoner?.Summon;
        u.SummonerId = summoner?.Id ?? -1;
        u.Summon = new SummonState
        {
            Def = def, OwnerId = parent != null && parent.OwnerId >= 0 ? parent.OwnerId : summoner?.Id ?? -1, Group = $"{r.Summoner}:{r.Serial}",
            Depth = (parent?.Depth ?? 0) + 1, Serial = ++_serial, Behavior = def.Behavior, Home = c, Point = r.Anchor, TargetUnitId = r.Target, CastId = r.Cast,
            EndTick = def.Lifetime == SummonLifetime.Duration ? _sim.Tick + _sim.TicksFor(def.Duration) : -1, SummonerFaction = summoner?.FactionId ?? "",
        };
        // Who gives orders (ТЗ 20.4): the AI by default; a player's summoner may give orders when the description lets.
        bool playerSummoner = summoner != null && _sim.IsPlayerControlled(summoner);
        bool manual = def.Behavior == SummonBehavior.ManualSimple && Rules.AllowManual;
        u.AiControlled = !(playerSummoner && (def.Control == SummonControl.FullControl || manual));
        // Only what is named is inherited: shares of characteristics, tags.
        if (summoner != null)
        {
            foreach (var t in def.InheritTags)
                if (summoner.HasTag(t))
                    u.Tags.Add(t);
            if (def.InheritStats.Count > 0)
            {
                foreach (var s in def.InheritStats.Where(s => s.Stat != "" && s.Share != 0))
                    u.InstanceMods.Add((new StatModDef { Stat = s.Stat, Add = summoner.Stat(s.Stat) * s.Share }, $"наследие призывателя ({summoner.Name})"));
                _sim.Statuses.RecalculateModifiers(u);
                u.Hp = u.HpMax;
            }
        }
        Total++;
        _totalByDef[def.Id] = _totalByDef.GetValueOrDefault(def.Id) + 1;
        ActivePeak = Math.Max(ActivePeak, Alive.Count());
        _sim.Emit(SimEventType.Summoned, LogLevel.Normal, $"{summoner?.Name ?? r.Cause} призывает «{u.Name}» — {def.DisplayName}", summoner?.Id ?? -1, u.Id, c, damageType: def.Id);
        _sim.CellEffects.OnUnitEntered(u, _sim.Map.Get(c)!);
        return u;
    }

    // ------------------------------------------------------------------ life

    /// <summary>Per tick: delayed and waiting requests, the end of lives, following the summoner's side.</summary>
    public void Update()
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var r = _pending[i];
            if (_sim.Tick < r.Due) continue;
            Place(r);
            if (r.Remaining == 0)
            {
                _pending.RemoveAt(i);
                i--;
            }
        }
        foreach (var u in _sim.Units)
        {
            if (!u.Alive || u.Summon is not { } st) continue;
            var summoner = u.SummonerId >= 0 ? _sim.GetUnit(u.SummonerId) : null;
            string? ended = st.Def.Lifetime switch
            {
                SummonLifetime.Duration when st.EndTick >= 0 && _sim.Tick >= st.EndTick => "время призыва истекло",
                SummonLifetime.UntilSkillEnd when !st.Independent && (summoner is not { Alive: true } || summoner.Casting?.Id != st.CastId) => "навык призыва окончен",
                SummonLifetime.UntilTargetDies when st.TargetUnitId >= 0 && _sim.GetUnit(st.TargetUnitId) is not { Alive: true } => "цель призыва погибла",
                _ => null,
            };
            if (ended != null)
            {
                End(u, st.Def.Despawn, ended);
                continue;
            }
            // The summoner went over to another side: its summoned follow, or keep theirs (ТЗ 20.4).
            if (summoner is { Alive: true } && !st.Independent && summoner.FactionId != st.SummonerFaction)
            {
                st.SummonerFaction = summoner.FactionId;
                if (st.Def.FollowFactionChange && st.Def.Faction == SummonFaction.Summoner && u.FactionId != summoner.FactionId)
                {
                    u.FactionId = summoner.FactionId;
                    _sim.Commands.ClearQueue(u, "призыватель сменил сторону", CommandStatus.Cancelled);
                    _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name} следует за призывателем на сторону «{u.FactionId}»", u.Id, summoner.Id);
                }
            }
        }
    }

    /// <summary>The summoner died (or vanished): each summoned by its description; the legacy ones by the rule of the battle.</summary>
    public void OnSummonerDied(Unit summoner)
    {
        foreach (var s in _sim.Units.ToList())
        {
            if (!s.Alive || s.SummonerId != summoner.Id) continue;
            if (s.Summon is not { } st)
            {
                if (_sim.Rules.Death.SummonsDieWithOwner) _sim.Combat.Kill(s, null, "призыватель погиб");
                continue;
            }
            var how = st.Def.Lifetime == SummonLifetime.UntilSummonerDeath ? st.Def.Despawn : st.Def.OnSummonerDeath;
            End(s, how, "призыватель погиб");
        }
        foreach (var r in _pending.Where(p => p.Summoner == summoner.Id))
        {
            Refund(r, r.Remaining, "призыватель погиб до появления");
            r.Remaining = 0;
        }
        _pending.RemoveAll(p => p.Remaining == 0);
    }

    /// <summary>The end of a summoned unit by a policy (ТЗ 20.2).</summary>
    public void End(Unit u, SummonEnd how, string reason)
    {
        if (!u.Alive) return;
        if (how == SummonEnd.ByRules) how = _sim.Rules.Death.SummonsDieWithOwner ? SummonEnd.Die : SummonEnd.Keep;
        var st = u.Summon;
        switch (how)
        {
            case SummonEnd.Keep:
                return;
            case SummonEnd.Die:
                u.DeathCause = DeathCause.Expired;
                _sim.Combat.Kill(u, null, reason);
                return;
            case SummonEnd.Vanish:
                bool asDeath = st?.Def.VanishFiresDeath == true;
                _sim.Emit(SimEventType.SummonEnded, LogLevel.Normal, $"{u.Name} исчезает — {reason}", u.SummonerId, u.Id, u.Pos, value: asDeath ? 0 : 1);
                if (asDeath)
                {
                    u.DeathCause = DeathCause.Expired;
                    _sim.Combat.Kill(u, null, reason);
                }
                else _sim.Combat.Vanish(u, reason);
                return;
            case SummonEnd.Neutralize:
                Detach(u, SummonBehavior.HoldPosition);
                u.FactionId = NeutralFaction();
                _sim.Emit(SimEventType.SummonEnded, LogLevel.Normal, $"{u.Name} становится нейтральным — {reason}", -1, u.Id, u.Pos);
                return;
            case SummonEnd.TransferControl:
            {
                var old = u.SummonerId >= 0 ? _sim.GetUnit(u.SummonerId) : null;
                var heir = old?.SummonerId >= 0 ? _sim.GetUnit(old.SummonerId) : null;
                if (heir is not { Alive: true } && st != null && st.OwnerId >= 0 && st.OwnerId != old?.Id) heir = _sim.GetUnit(st.OwnerId);
                if (heir is { Alive: true } && heir != u)
                {
                    u.SummonerId = heir.Id;
                    if (st != null) st.SummonerFaction = heir.FactionId;
                    u.FactionId = heir.FactionId;
                    _sim.Commands.ClearQueue(u, "смена хозяина", CommandStatus.Cancelled);
                    _sim.Emit(SimEventType.SummonEnded, LogLevel.Normal, $"{u.Name} переходит к {heir.Name} — {reason}", heir.Id, u.Id, u.Pos);
                    return;
                }
                goto case SummonEnd.BecomeIndependent;
            }
            case SummonEnd.BecomeIndependent:
                Detach(u, st?.Def.AfterSummoner ?? SummonBehavior.AttackNearestEnemy);
                _sim.Emit(SimEventType.SummonEnded, LogLevel.Normal, $"{u.Name} становится независимым — {reason}", -1, u.Id, u.Pos);
                return;
        }
    }

    private void Detach(Unit u, SummonBehavior behavior)
    {
        u.SummonerId = -1;
        u.AiControlled = true;
        if (u.Summon is { } st)
        {
            st.Independent = true;
            st.Behavior = behavior;
            st.Home = u.Pos;
            st.PlayerOrder = false;
        }
        _sim.Commands.ClearQueue(u, "связь с призывателем разорвана", CommandStatus.Cancelled);
    }

    // ------------------------------------------------------------------ control

    /// <summary>«Приказы владельца важнее»: the player may order the summoned of a summoner it controls.</summary>
    public bool PlayerMayOrder(Unit u) =>
        u.Summon is { Independent: false } st && st.Def.Control == SummonControl.OwnerPriorityOrders && u.SummonerId >= 0
        && _sim.GetUnit(u.SummonerId) is { Alive: true } s && _sim.IsPlayerControlled(s);

    /// <summary>The summoned that take places of the deployment (ТЗ 20.6: by default none).</summary>
    public int CountedForDeploy(string faction) => Rules.CountTowardsDeploy ? Alive.Count(u => u.FactionId == faction) : 0;

    /// <summary>For the fingerprint of the battle (save and load, T55): only when there are summons.</summary>
    public IEnumerable<string> Digest() =>
        _sim.Units.Where(u => u.Summon != null).OrderBy(u => u.Id).Select(u =>
            {
                var s = u.Summon!;
                return $"{u.Id},{s.Def.Id},{u.SummonerId},{s.OwnerId},{s.Depth},{s.Independent},{s.Behavior},{s.EndTick},{s.Arrived},{s.PatrolIndex},{s.PlayerOrder}";
            })
            .Concat(_pending.Select(p => $"q{p.Serial},{p.Def.Id},{p.Remaining},{p.Due},{p.Deadline}"))
            .Concat(Total > 0 || Failed > 0 ? new[] { $"n{Total},{Failed}" } : Enumerable.Empty<string>());
}
