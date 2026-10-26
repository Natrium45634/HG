using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// The deployment during the battle (ТЗ ч. 6, 3; docs/DECISIONS_6.md §97): the side's pool of deployment points (ОР),
/// gained in ticks; calling a fighter of the squad onto a hex of a zone with a facing; taking one back with a refund and a
/// pause; the states of the squad. Every check is made here, by the simulation — the interface only asks (3.7). A call
/// that fails changes nothing: no points are spent and no unit is created.
/// </summary>
public sealed class DeploymentSystem
{
    private readonly Simulation _sim;
    private long _nextGain;

    public DeploymentSystem(Simulation sim)
    {
        _sim = sim;
        sim.Scenario.BetweenWaves += _ => OnWaveCleared();
    }

    /// <summary>The rules in force; disabled — nothing of this system acts.</summary>
    public DeploymentRulesDef Rules { get; private set; } = new();
    public bool Enabled => Rules.Enabled && _sim.Squad.Enabled;
    public double Points { get; private set; }
    /// <summary>Points gained, spent and refunded so far (the report and the tests).</summary>
    public double Gained { get; private set; }
    public double Spent { get; private set; }
    public double Refunded { get; private set; }

    /// <summary>The kind of the points: the name and the colour of the resource of the rules.</summary>
    public string PointsName => _sim.Content.Get<ResourceDef>(Rules.Resource)?.DisplayName ?? "Очки развёртывания";

    internal void Load(DeploymentRulesDef rules)
    {
        Rules = rules;
        if (!Enabled) return;
        Points = Clamp(Rules.StartingPoints);
        _nextGain = _sim.TicksFor(Rules.GainInterval);
    }

    private double Clamp(double v) => Math.Max(0, Rules.MaxPoints > 0 ? Math.Min(Rules.MaxPoints, v) : v);

    private void Gain(double amount, string why)
    {
        if (amount <= 0) return;
        double before = Points;
        Points = Clamp(Points + amount);
        Gained += Points - before;
        if (Points != before) _sim.Emit(SimEventType.Diagnostic, LogLevel.Detail, $"{PointsName}: +{Points - before:0.##} ({why}) → {Points:0.##}");
    }

    /// <summary>The tick (part of the fixed step): the gain by time, the pauses of the squad running out.</summary>
    internal void Update()
    {
        if (!Enabled) return;
        if (Rules.GainAmount > 0 && (!_sim.Scenario.Preparing || Rules.GainInPreparation))
        {
            if (--_nextGain <= 0)
            {
                _nextGain = _sim.TicksFor(Rules.GainInterval);
                Gain(Rules.GainAmount, "начисление");
            }
        }
        foreach (var e in _sim.Squad.Entries)
            if (e.State is RosterState.WithdrawnCooldown or RosterState.DefeatedCooldown && _sim.Tick >= e.ReadyAt)
            {
                e.State = RosterState.Ready;
                _sim.Emit(SimEventType.Info, LogLevel.Normal, $"«{_sim.Content.Unit(e.Template)?.DisplayName ?? e.Template}» снова готов к вызову");
            }
    }

    /// <summary>Points for a killed enemy (ТЗ ч. 6, 3.1 «по событию»).</summary>
    internal void OnKilled(Unit u)
    {
        if (!Enabled || Rules.GainPerKill <= 0 || u.IsStructure || _sim.IsPlayerFaction(u.FactionId)) return;
        if (_sim.Options.PlayerFactions.Any(f => _sim.Content.RelationOf(f, u.FactionId) == Relation.Enemy)) Gain(Rules.GainPerKill, $"убит «{u.Name}»");
    }

    internal void OnWaveCleared() => Gain(Enabled ? Rules.GainPerWave : 0, "волна пройдена");

    /// <summary>A fighter of the squad fell: out, or back after a pause (by the rules).</summary>
    internal void OnFallen(RosterEntry e)
    {
        if (!Enabled || Rules.AfterDeath != AfterDeathPolicy.Cooldown) return;
        e.State = RosterState.DefeatedCooldown;
        e.ReadyAt = _sim.Tick + _sim.TicksFor(Rules.DeathCooldown);
    }

    public int DeployedCount => _sim.Squad.Count(RosterState.Deployed);

    /// <summary>The price of a fighter: its own, the default of the rules, or its threat cost.</summary>
    public double CostOf(RosterEntry e)
    {
        var def = _sim.Content.Unit(e.Template);
        if (def == null) return 0;
        if (def.Deployment is { Cost: >= 0 } own) return own.Cost;
        return Rules.DefaultCost > 0 ? Rules.DefaultCost : Math.Round(ThreatCost.Of(_sim.Content, def), 1);
    }

    public double CooldownOf(UnitDef def) => def.Deployment is { RedeployCooldown: >= 0 } own ? own.RedeployCooldown : Rules.RedeployCooldown;

    /// <summary>Seconds left of the pause of an entry (0 — none).</summary>
    public double CooldownLeft(RosterEntry e) =>
        e.State is RosterState.WithdrawnCooldown or RosterState.DefeatedCooldown ? Math.Max(0, (e.ReadyAt - _sim.Tick) * _sim.Dt) : 0;

    /// <summary>Why the fighter cannot be called now (state, phase, limit, points); null — it can, given a hex.</summary>
    public string? CanDeploy(RosterEntry e)
    {
        if (!Enabled) return "вызов за очки развёртывания выключен";
        if (_sim.Outcome != BattleOutcome.None) return "бой окончен";
        bool prep = _sim.Scenario.Preparing;
        if (prep && !Rules.InPreparation) return "в подготовке вызывать нельзя";
        if (!prep && !Rules.InCombat) return "в бою вызывать нельзя";
        var def = _sim.Content.Unit(e.Template);
        if (def == null) return $"шаблон «{e.Template}» не найден";
        switch (e.State)
        {
            case RosterState.Deployed: return "уже на поле";
            case RosterState.Defeated: return "пал";
            case RosterState.Unavailable: return "недоступен";
            case RosterState.WithdrawnCooldown: return $"перерыв после отзыва: ещё {CooldownLeft(e):0.#} с";
            case RosterState.DefeatedCooldown: return $"перерыв после гибели: ещё {CooldownLeft(e):0.#} с";
        }
        if (def.Deployment is { CanDeploy: false }) return "этого бойца нельзя вызывать";
        if (e.Deployments > 0 && !Rules.AllowRedeploy) return "повторный вызов запрещён";
        if (Rules.MaxDeployed > 0 && DeployedCount >= Rules.MaxDeployed) return $"на поле уже {DeployedCount} из {Rules.MaxDeployed}";
        double cost = CostOf(e);
        if (cost > Points + 1e-9) return $"не хватает очков: нужно {cost:0.#}, есть {Points:0.#}";
        return null;
    }

    /// <summary>
    /// The zone that decides on a hex for a fighter (the highest priority, then the first); null — no zone of the scenario
    /// takes the hex. Without zones of deployment the zone of placement of part 4 is used.
    /// </summary>
    public DeploymentZoneDef? ZoneAt(HexCoord c)
    {
        var zones = _sim.Scenario.Def?.DeploymentZones;
        if (zones == null || zones.Count == 0) return null;
        DeploymentZoneDef? best = null;
        foreach (var z in zones)
            if (z.Cells.Contains(c) && _sim.IsPlayerFaction(z.Faction) && (best == null || z.Priority > best.Priority))
                best = z;
        return best;
    }

    /// <summary>Why a fighter cannot stand on a hex (out of a zone, taken, impassable, hazard, near an enemy); null — it can.</summary>
    public string? CellProblem(RosterEntry e, HexCoord c)
    {
        var def = _sim.Content.Unit(e.Template);
        if (def == null) return "нет шаблона";
        var cell = _sim.Map.Get(c);
        if (cell == null) return "вне карты";
        var zones = _sim.Scenario.Def?.DeploymentZones;
        if (zones is { Count: > 0 })
        {
            var z = ZoneAt(c);
            if (z == null) return "вне зон развёртывания";
            if (!z.Active) return $"зона «{z}» закрыта";
            bool prep = _sim.Scenario.Preparing;
            if (z.Phase == ZonePhase.Preparation && !prep) return $"зона «{z}» — только в подготовке";
            if (z.Phase == ZonePhase.Combat && prep) return $"зона «{z}» — только в бою";
            if (z.UnitTags.Count > 0 && !z.UnitTags.Any(t => SquadRules.Has(_sim.Content, def, t))) return $"в зону «{z}» этого бойца нельзя";
            if (def.Deployment is { ZoneTags.Count: > 0 } prof && !prof.ZoneTags.Any(z.Tags.Contains)) return "боец встаёт только в зоны: " + string.Join(", ", prof.ZoneTags);
        }
        else if (_sim.Placement.Zone.Count > 0 && !_sim.Placement.Zone.Contains(c)) return "вне зоны расстановки";
        if (cell.Occupants.Any(o => o.Alive)) return "гекс занят";
        var mc = MoveContext.For(_sim, def, _sim.Placement.Faction);
        if (cell.Blocked || !mc.Passable(cell.Terrain)) return "местность непроходима для бойца";
        if (!Occupancy.CanEnter(mc, cell)) return "гекс занят";
        if (!cell.AllowsPlacement) return "сюда нельзя ставить";
        if (Rules.ForbidHazard && cell.IsHazard) return "опасная клетка";
        if (Rules.EnemyRadius > 0)
            foreach (var u in _sim.Units)
                if (u.Alive && !u.IsStructure && !_sim.IsPlayerFaction(u.FactionId) && _sim.Options.PlayerFactions.Any(f => _sim.Content.RelationOf(f, u.FactionId) == Relation.Enemy)
                    && HexCoord.Distance(u.Pos, c) <= Rules.EnemyRadius && _sim.Vision.IsVisibleToPlayer(u))
                    return $"враг ближе {Rules.EnemyRadius + 1} гексов";
        return null;
    }

    /// <summary>The facing a called fighter gets: the player's choice, toward the enemy, or the fixed one of its profile.</summary>
    public HexDirection FacingFor(UnitDef def, HexCoord c, HexDirection chosen) => def.Deployment?.Facing switch
    {
        DeployFacing.Auto => _sim.Placement.FacingFrom(c),
        DeployFacing.Fixed => def.Deployment.FixedFacing,
        _ => chosen,
    };

    /// <summary>
    /// Calls a fighter of the squad (ТЗ ч. 6, 3.6 F–G): checked again in full, then — points spent, the hex taken, the unit
    /// created, the limit counted, «UnitDeployed» raised. Null and the reason — nothing changed.
    /// </summary>
    public Unit? Deploy(int entryIndex, HexCoord c, HexDirection facing, out string why)
    {
        var e = entryIndex >= 0 && entryIndex < _sim.Squad.Entries.Count ? _sim.Squad.Entries[entryIndex] : null;
        why = e == null ? "нет такого бойца в отряде" : CanDeploy(e) ?? CellProblem(e, c) ?? "";
        if (why != "") return null;
        var def = _sim.Content.Unit(e!.Template)!;
        double cost = CostOf(e);
        var entry = _sim.Placement.Entry(e.Template);
        string profile = entry?.AiProfile ?? "";
        Unit u;
        try
        {
            u = _sim.Spawn(def, _sim.Placement.Faction, c, FacingFor(def, c, facing), null, null, profile == "" ? null : profile);
        }
        catch (InvalidOperationException ex)
        {
            why = ex.Message;
            return null;
        }
        Points -= cost;
        Spent += cost;
        e.Deployments++;
        e.Cost = cost;
        _sim.Squad.Bind(e, u);
        if (Rules.Leash > 0)
        {
            u.LeashAnchor = c;
            u.LeashRadius = Rules.Leash;
        }
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Вызван «{u.Name}» в {c} (−{cost:0.#}, осталось {Points:0.#})", u.Id, cell: c);
        _sim.Scenario.Raise(ScenarioEventType.UnitDeployed, u);
        return u;
    }

    /// <summary>Why a unit cannot be taken back now; null — it can.</summary>
    public string? CanRetreat(Unit u)
    {
        if (!Enabled) return "вызов за очки развёртывания выключен";
        if (!Rules.AllowRetreat) return "отзыв запрещён правилами";
        if (_sim.Squad.EntryOf(u) is not { State: RosterState.Deployed }) return "это не боец отряда на поле";
        if (!u.Alive) return "пал";
        if (u.Def.Deployment is { CanRetreat: false }) return "этого бойца нельзя отозвать";
        if (!Rules.RetreatWhileControlled && (u.Flags.BlocksActions || u.Flags.BlocksMovement || u.Flags.MindControl)) return "под контролем — отозвать нельзя";
        return null;
    }

    /// <summary>
    /// Takes a fighter back (ТЗ ч. 6, 3.8): off the map at once — the hex freed, the orders stopped, the auras gone with it,
    /// links of blocking released, the limit counted again; the pause starts and part of the price returns. A fallen unit is
    /// not taken back, so a death and a retreat never both pay.
    /// </summary>
    public bool Retreat(Unit u, out string why)
    {
        why = CanRetreat(u) ?? "";
        if (why != "") return false;
        var e = _sim.Squad.EntryOf(u)!;
        _sim.Commands.ClearQueue(u, "отозван", CommandStatus.Cancelled);
        _sim.Blocking.OnRemoved(u);
        _sim.Remove(u);
        double refund = Math.Round(e.Cost * Rules.RetreatRefund, 2);
        double before = Points;
        Points = Clamp(Points + refund);
        Refunded += Points - before;
        e.State = Rules.AllowRedeploy ? RosterState.WithdrawnCooldown : RosterState.Unavailable;
        e.ReadyAt = _sim.Tick + _sim.TicksFor(CooldownOf(u.Def));
        e.Unit = -1;
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"«{u.Name}» отозван (+{Points - before:0.#}, перерыв {CooldownOf(u.Def):0.#} с)", -1, -1, u.Pos);
        _sim.Scenario.Raise(ScenarioEventType.UnitWithdrawn, u);
        return true;
    }
}
