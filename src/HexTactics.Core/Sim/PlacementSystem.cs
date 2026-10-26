using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>A defender placed in the preparation phase: what it cost.</summary>
public sealed record PlacedUnit(int Unit, string Template, double Cost);

/// <summary>
/// The preparation phase of «TD с расстановкой» (docs/DECISIONS_4.md §64): the player places defenders from the
/// available templates within the budget, the limits and the zones; may take them back while preparing, move them
/// between waves when the scenario allows, upgrade them. Between waves the defenders recover. «Автотест» places the
/// scenario's preset placement at the start.
/// </summary>
public sealed class PlacementSystem
{
    private readonly Simulation _sim;
    private readonly List<PlacedUnit> _placed = new();
    private HashSet<HexCoord>? _zone;

    public PlacementSystem(Simulation sim)
    {
        _sim = sim;
        sim.Scenario.BetweenWaves += OnWaveCleared;
    }

    public PlacementDef? Def => _sim.Scenario.Def?.Placement;
    public IReadOnlyList<PlacedUnit> Placed => _placed;

    /// <summary>Placing is possible now (the preparation goes on).</summary>
    public bool Active => _sim.Scenario.Preparing;

    public double Budget => Def?.Budget ?? 0;
    public double Spent => _placed.Where(p => _sim.GetUnit(p.Unit)?.Alive == true || Active).Sum(p => p.Cost);
    /// <summary>What is left of the budget (infinity without one).</summary>
    public double Left => Budget > 0 ? Budget - _placed.Sum(p => p.Cost) : double.PositiveInfinity;

    /// <summary>No wave is going on: every wave is waiting, cleared or cancelled.</summary>
    public bool BetweenWaves => !_sim.Scenario.WavesHeld && _sim.Scenario.Waves.All(w => w.State is WaveState.Pending or WaveState.Cleared or WaveState.Cancelled);

    /// <summary>Placed defenders may be moved now: in the preparation, or between waves if the scenario allows.</summary>
    public bool CanRelocateNow => Active || (Def?.RelocateBetweenWaves == true && BetweenWaves && _sim.Outcome == BattleOutcome.None);

    public string Faction => _sim.Options.PlayerFactions.FirstOrDefault() ?? "player";

    /// <summary>
    /// What may be placed of a template: the scenario's list; with a squad of the player (part 6, §96) — the fighters of
    /// the squad, priced as the list says (or by threat).
    /// </summary>
    public PlacementUnitDef? Entry(string template)
    {
        var listed = Def?.Units.FirstOrDefault(u => u.Unit == template);
        if (!_sim.Squad.Enabled) return listed;
        if (_sim.Squad.Entries.All(e => e.Template != template)) return null;
        return listed ?? new PlacementUnitDef { Unit = template };
    }

    public double CostOf(PlacementUnitDef e)
    {
        if (e.Cost > 0) return e.Cost;
        var def = _sim.Content.Unit(e.Unit);
        return def == null ? 0 : ThreatCost.Of(_sim.Content, def);
    }

    public int CountOf(string template) => _placed.Count(p => p.Template == template && _sim.GetUnit(p.Unit)?.Alive == true);

    /// <summary>The hexes defenders may stand on: the zones of the scenario; without zones — around the player's bases (or anywhere).</summary>
    public HashSet<HexCoord> Zone
    {
        get
        {
            if (_zone != null) return _zone;
            var sc = _sim.Scenario.Def;
            _zone = sc == null ? new HashSet<HexCoord>() : sc.Placement.ZoneHexes(sc);
            if (_zone.Count == 0 && sc != null)
                foreach (var b in sc.Objects.Where(o => o.Type == ScenarioObjectType.PlayerBase))
                foreach (var c in b.Cells())
                foreach (var h in HexCoord.Spiral(c, Math.Max(1, sc.Placement.ZoneRadius)))
                    _zone.Add(h);
            _zone.RemoveWhere(h => !_sim.Map.Contains(h));
            return _zone;
        }
    }

    private bool InZone(HexCoord c) => Zone.Count == 0 || Zone.Contains(c);

    /// <summary>
    /// The first free hex for a template at the start of a battle (part 6, «весь отряд на поле»): the zone of placement,
    /// nearest to its middle first; without a zone — near the player's units of the scenario, else from the west edge.
    /// The order is fixed: the same squad stands the same way every time.
    /// </summary>
    public HexCoord? AutoSpot(UnitDef def)
    {
        IEnumerable<HexCoord> candidates;
        if (Zone.Count > 0)
        {
            var mid = Middle(Zone);
            candidates = Zone.OrderBy(c => HexCoord.Distance(c, mid)).ThenBy(c => c.R).ThenBy(c => c.Q);
        }
        else
        {
            var own = _sim.Units.Where(x => x.Alive && _sim.IsPlayerFaction(x.FactionId)).Select(x => x.Pos).ToList();
            var cells = _sim.Map.Cells.Select(x => x.Coord);
            candidates = own.Count > 0
                ? cells.OrderBy(c => own.Min(p => HexCoord.Distance(c, p))).ThenBy(c => c.R).ThenBy(c => c.Q)
                : cells.OrderBy(c => c.Q + c.R / 2.0).ThenBy(c => c.R).ThenBy(c => c.Q);
        }
        foreach (var c in candidates)
            if (CellProblem(def, c) == null)
                return c;
        return null;
    }

    private static HexCoord Middle(IEnumerable<HexCoord> cells)
    {
        var list = cells.ToList();
        double q = list.Average(c => c.Q), r = list.Average(c => c.R);
        return list.OrderBy(c => Math.Abs(c.Q - q) + Math.Abs(c.R - r)).ThenBy(c => c.R).ThenBy(c => c.Q).First();
    }

    /// <summary>The facing of a fighter placed automatically: toward the nearest enemy base or enemy, else east.</summary>
    public HexDirection FacingFrom(HexCoord c)
    {
        var sc = _sim.Scenario.Def;
        var targets = sc?.Objects.Where(o => o.Type == ScenarioObjectType.SpawnBase).Select(o => o.Anchor).ToList() ?? new List<HexCoord>();
        if (targets.Count == 0) targets = _sim.Units.Where(x => x.Alive && !_sim.IsPlayerFaction(x.FactionId) && !x.IsStructure).Select(x => x.Pos).ToList();
        if (targets.Count == 0) return HexDirection.E;
        var t = targets.OrderBy(x => HexCoord.Distance(c, x)).ThenBy(x => x.R).ThenBy(x => x.Q).First();
        return c.DirectionTo(t) ?? HexDirection.E;
    }

    private string? CellProblem(UnitDef def, HexCoord c, Unit? moving = null)
    {
        var cell = _sim.Map.Get(c);
        if (cell == null) return "вне карты";
        if (!InZone(c)) return "вне зоны расстановки";
        if (!cell.AllowsPlacement) return "сюда нельзя ставить";
        if (cell.Occupants.Any(o => o.Alive && o != moving)) return "гекс занят";
        if (!Occupancy.CanEnter(MoveContext.For(_sim, def, Faction), cell)) return "местность непроходима для юнита";
        return null;
    }

    /// <summary>Why another one of a template cannot be placed (availability, limits, budget); null — it can.</summary>
    public string? CanAfford(string template)
    {
        var e = Entry(template);
        var def = _sim.Content.Unit(template);
        if (def == null) return $"шаблон «{template}» не найден";
        if (e == null) return _sim.Squad.Enabled ? $"«{def.DisplayName}» нет в отряде" : $"«{def.DisplayName}» недоступен для расстановки";
        if (_sim.Squad.Enabled && _sim.Squad.Ready(template) == null)
            return _sim.Squad.Entries.Any(x => x.Template == template && x.State == RosterState.Deployed) ? $"все «{def.DisplayName}» отряда уже на поле" : $"«{def.DisplayName}» выбыл";
        if (e.MaxCount > 0 && CountOf(template) >= e.MaxCount) return $"не более {e.MaxCount} «{def.DisplayName}»";
        if (Def!.MaxUnits > 0 && _placed.Count(p => _sim.GetUnit(p.Unit)?.Alive == true) >= Def.MaxUnits) return $"не более {Def.MaxUnits} юнитов";
        double cost = CostOf(e);
        if (Budget > 0 && cost > Left + 1e-9) return $"не хватает бюджета: нужно {cost:0.#}, осталось {Left:0.#}";
        return null;
    }

    /// <summary>Why a template cannot be placed on a hex now (null — it can).</summary>
    public string? CanPlace(string template, HexCoord c, bool free = false)
    {
        if (!Active && !free) return "подготовка завершена";
        var def = _sim.Content.Unit(template);
        if (def == null) return $"шаблон «{template}» не найден";
        if (!free && CanAfford(template) is { } no) return no;
        var cell = _sim.Map.Get(c);
        if (free && cell != null && cell.Occupants.Any(o => o.Alive)) return "гекс занят";
        return free ? null : CellProblem(def, c);
    }

    /// <summary>Places a defender (null — why not in <paramref name="why"/>).</summary>
    public Unit? Place(string template, HexCoord c, out string why, HexDirection facing = HexDirection.E, bool free = false, string aiProfile = "")
    {
        why = CanPlace(template, c, free) ?? "";
        if (why != "") return null;
        var def = _sim.Content.Unit(template)!;
        var e = Entry(template);
        double cost = free ? 0 : e != null ? CostOf(e) : 0;
        string profile = aiProfile != "" ? aiProfile : e?.AiProfile ?? "";
        var u = _sim.Spawn(def, Faction, c, facing, null, null, profile == "" ? null : profile);
        _placed.Add(new PlacedUnit(u.Id, template, cost));
        // A fighter of the squad: one entry — one instance (ТЗ ч. 6, 3.9).
        if (_sim.Squad.Enabled && _sim.Squad.Ready(template) is { } entry) _sim.Squad.Bind(entry, u);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Размещён «{def.DisplayName}» в {c}" + (cost > 0 ? $" (−{cost:0.#}, осталось {(Budget > 0 ? Left.ToString("0.#") : "∞")})" : ""), u.Id, cell: c);
        return u;
    }

    /// <summary>Takes a placed defender back while preparing (its cost returns).</summary>
    public bool Remove(Unit u)
    {
        if (!Active) return false;
        int i = _placed.FindIndex(p => p.Unit == u.Id);
        if (i < 0) return false;
        _placed.RemoveAt(i);
        _sim.Squad.OnRemoved(u, died: false);
        _sim.Remove(u);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"«{u.Name}» снят с поля (бюджет возвращён)", -1, -1, u.Pos);
        return true;
    }

    public bool IsPlaced(Unit u) => _placed.Any(p => p.Unit == u.Id);

    public string? CanMove(Unit u, HexCoord c)
    {
        if (!IsPlaced(u)) return "юнит не из расстановки";
        if (!CanRelocateNow) return Active ? "подготовка завершена" : Def?.RelocateBetweenWaves == true ? "идёт волна" : "перестановка между волнами запрещена";
        return CellProblem(u.Def, c, u);
    }

    /// <summary>Moves a placed defender instantly (preparation, or between waves).</summary>
    public bool Move(Unit u, HexCoord c, out string why)
    {
        why = CanMove(u, c) ?? "";
        if (why != "") return false;
        u.Queue.Clear();
        _sim.Relocate(u, c, null, "перестановка");
        u.Ai.HoldCenter = c;
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"«{u.Name}» переставлен в {c}", u.Id, cell: c);
        return true;
    }

    /// <summary>The upgrade of a placed defender and its price (null — none).</summary>
    public (UnitDef To, double Cost)? UpgradeOf(Unit u)
    {
        var p = _placed.FirstOrDefault(x => x.Unit == u.Id);
        var e = p == null ? null : Entry(p.Template);
        var to = e == null || e.UpgradeTo == "" ? null : _sim.Content.Unit(e.UpgradeTo);
        if (to == null) return null;
        double cost = e!.UpgradeCost > 0 ? e.UpgradeCost : Math.Max(0, ThreatCost.Of(_sim.Content, to) - CostOf(e));
        return (to, cost);
    }

    /// <summary>«Улучшить»: the defender is replaced by its upgrade on the same hex (preparation or between waves).</summary>
    public Unit? Upgrade(Unit u, out string why)
    {
        why = "";
        var up = UpgradeOf(u);
        if (up == null)
        {
            why = "улучшения нет";
            return null;
        }
        if (!CanRelocateNow && !Active)
        {
            why = "улучшать можно при подготовке и между волнами";
            return null;
        }
        if (Budget > 0 && up.Value.Cost > Left + 1e-9)
        {
            why = $"не хватает бюджета: нужно {up.Value.Cost:0.#}, осталось {Left:0.#}";
            return null;
        }
        int i = _placed.FindIndex(p => p.Unit == u.Id);
        var old = _placed[i];
        var pos = u.Pos;
        var facing = u.Facing;
        _sim.Remove(u);
        var nu = _sim.Spawn(up.Value.To, Faction, pos, facing);
        _placed[i] = new PlacedUnit(nu.Id, old.Template, old.Cost + up.Value.Cost);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"«{u.Name}» улучшен до «{up.Value.To.DisplayName}» (−{up.Value.Cost:0.#})", nu.Id, cell: pos);
        return nu;
    }

    /// <summary>The scenario's preset placement (autotest; «расставить как в тесте»): within the rules unless free.</summary>
    public int PlacePreset(bool free)
    {
        var d = Def;
        if (d == null) return 0;
        int n = 0;
        foreach (var su in d.Preset)
        {
            if (Place(su.Unit, su.C, out var why, su.Facing, free, su.AiProfile) is { } u)
            {
                if (su.Tag != "") u.Tag = su.Tag;
                if (su.Name != "") u.Name = su.Name;
                n++;
            }
            else _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Расстановка: «{su.Unit}» в {su.C} — {why}");
        }
        return n;
    }

    /// <summary>Confirms the preparation (only where the scenario lets the player end it).</summary>
    public bool Confirm()
    {
        if (!Active || Def == null) return false;
        if (Def.End is PreparationEnd.Timer or PreparationEnd.Condition) return false;
        _sim.Scenario.EndPreparation("подтверждено");
        return true;
    }

    /// <summary>Between waves: the defenders recover part of their health and their resources.</summary>
    private void OnWaveCleared(WaveRuntime w)
    {
        var d = Def;
        if (d == null || _sim.Scenario.Mode is not (BattleMode.TdPlacement or BattleMode.AutoTest)) return;
        if (d.RestoreHealth <= 0 && !d.RestoreResources) return;
        int n = 0;
        foreach (var u in _sim.Units.Where(u => u.Alive && !u.IsStructure && _sim.IsPlayerFaction(u.FactionId)))
        {
            if (d.RestoreHealth > 0 && u.Hp < u.HpMax)
            {
                u.Hp = Math.Min(u.HpMax, u.Hp + (u.HpMax - u.Hp) * d.RestoreHealth);
                n++;
            }
            if (d.RestoreResources)
                foreach (var r in u.Resources.Values)
                    r.Current = r.Max;
        }
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Между волнами: защитники восстанавливаются" + (d.RestoreHealth > 0 ? $" ({d.RestoreHealth:P0} недостающего здоровья, {n})" : ""));
    }
}
