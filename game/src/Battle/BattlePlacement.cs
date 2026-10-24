using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// Part 4, stage 5 on the battle screen (docs/DECISIONS_4.md §64): the preparation of «TD с расстановкой» (pick a
/// card, click a hex of the zone; ПКМ takes a defender back), moving and upgrading between waves, the battle report,
/// «только логика», «без визуальных эффектов» and «остановка на событии».
/// </summary>
public partial class BattleScreen
{
    /// <summary>The template picked on the placement panel (null — none).</summary>
    public string? PlaceTemplate { get; private set; }
    /// <summary>A placed defender being moved (null — none).</summary>
    public Unit? Relocating { get; private set; }
    /// <summary>The report of this battle, filled from its events (the same as the autobattle's).</summary>
    public BattleReport Report { get; private set; } = null!;
    /// <summary>Breakpoints «остановка на событии».</summary>
    public Breakpoints Breaks { get; private set; } = null!;
    /// <summary>Why the last «только логика» run stopped ("" — the battle went on to its end).</summary>
    public string StopReason { get; private set; } = "";
    /// <summary>«Без визуальных эффектов»: events are logged, nothing flies, flashes or floats.</summary>
    public bool NoEffects { get; set; }

    private void InitAutobattle()
    {
        Report = BattleReport.Attach(Sim, _scenario.DisplayName, Sim.Options.Seed);
        Breaks = Breakpoints.Attach(Sim, new List<BreakpointDef>());
        // The preparation waits for the player: the battle starts paused until «начать бой».
        if (Sim.Scenario.Preparing) Driver.Paused = true;
    }

    public void ChoosePlacement(string? template)
    {
        PlaceTemplate = template;
        Relocating = null;
        if (template != null) _hud.ShowBanner($"«{_db.Unit(template)?.DisplayName}»: клик по подсвеченному гексу — поставить, ПКМ — отмена", Ui.Text, 3);
    }

    public void StartRelocate(Unit u)
    {
        Relocating = u;
        PlaceTemplate = null;
        _hud.ShowBanner($"«{u.Name}»: клик по гексу зоны — переставить, ПКМ — отмена", Ui.Text, 3);
    }

    public void RemovePlaced(Unit u)
    {
        if (Sim.Placement.Remove(u))
        {
            if (Selected == u) Select(null, byPlayer: false);
            _hud.ShowBanner($"«{u.Name}» снят — бюджет возвращён", Ui.Text, 2);
        }
    }

    public void UpgradePlaced(Unit u)
    {
        var nu = Sim.Placement.Upgrade(u, out var why);
        if (nu == null)
        {
            _hud.ShowBanner("Улучшение невозможно: " + why, Ui.Bad);
            return;
        }
        Select(nu, byPlayer: false);
        _hud.ShowBanner($"Улучшено: «{nu.Name}»", Ui.Good, 2);
    }

    public void PlacePreset()
    {
        int n = Sim.Placement.PlacePreset(free: false);
        _hud.ShowBanner(n > 0 ? $"Расставлено по умолчанию: {n}" : "Расстановка по умолчанию не поместилась (бюджет, лимиты, занятые гексы)", n > 0 ? Ui.Good : Ui.Warn, 3);
    }

    public void ConfirmPreparation()
    {
        PlaceTemplate = null;
        Relocating = null;
        if (!Sim.Placement.Confirm())
        {
            _hud.ShowBanner("Подготовка заканчивается сама (по времени или условию)", Ui.Warn);
            return;
        }
        _hud.ShowBanner("Бой начался — волны идут по расписанию", Ui.Good, 3);
        Resume();
    }

    /// <summary>Clicks of the preparation: place the picked template, move the defender being moved. True — handled.</summary>
    private bool TryPlacementClick()
    {
        if (_hover == null) return false;
        if (PlaceTemplate != null)
        {
            var dir = FacingTowardsEnemies(_hover.Coord);
            var u = Sim.Placement.Place(PlaceTemplate, _hover.Coord, out var why, dir);
            if (u == null) _hud.ShowBanner("Нельзя поставить: " + why, Ui.Bad, 2.5f);
            else if (Sim.Placement.CanAfford(PlaceTemplate) != null) PlaceTemplate = null;
            return true;
        }
        if (Relocating != null)
        {
            if (!Sim.Placement.Move(Relocating, _hover.Coord, out var why)) _hud.ShowBanner("Нельзя переставить: " + why, Ui.Bad, 2.5f);
            Relocating = null;
            return true;
        }
        return false;
    }

    /// <summary>ПКМ in the preparation: cancel the pick, or take a placed defender back. True — handled.</summary>
    private bool TryPlacementRightClick()
    {
        if (PlaceTemplate != null || Relocating != null)
        {
            PlaceTemplate = null;
            Relocating = null;
            return true;
        }
        if (Sim.Scenario.Preparing && _hoverUnit != null && Sim.Placement.IsPlaced(_hoverUnit))
        {
            RemovePlaced(_hoverUnit);
            return true;
        }
        return false;
    }

    /// <summary>New defenders look towards the nearest spawn base.</summary>
    private HexDirection FacingTowardsEnemies(HexCoord at)
    {
        var spawn = _scenario.Objects.Where(o => o.Type == ScenarioObjectType.SpawnBase).OrderBy(o => HexCoord.Distance(o.Anchor, at)).FirstOrDefault();
        return spawn != null ? at.DirectionTo(spawn.Anchor) ?? HexDirection.W : HexDirection.W;
    }

    /// <summary>The zone, and whether the hex under the mouse takes the picked template / the moved defender.</summary>
    private void DrawPlacement(OverlayLayer o, float pulse)
    {
        bool active = PlaceTemplate != null || Relocating != null || Sim.Scenario.Preparing;
        if (!active || Sim.Scenario.Mode != BattleMode.TdPlacement) return;
        var zone = Sim.Placement.Zone;
        var tint = new Color(0.35f, 0.65f, 1f, PlaceTemplate != null || Relocating != null ? 0.22f : 0.12f);
        foreach (var c in zone) o.FillHex(c, tint, 0.92f);
        if (_hover == null) return;
        string? why = PlaceTemplate != null ? Sim.Placement.CanPlace(PlaceTemplate, _hover.Coord)
            : Relocating != null ? Sim.Placement.CanMove(Relocating, _hover.Coord) : "";
        if (why == "") return;
        o.RingHex(_hover.Coord, why == null ? new Color(0.4f, 1f, 0.5f, 0.7f + 0.3f * pulse) : new Color(1f, 0.35f, 0.3f, 0.8f), 0.1f, 0.95f, 0.05f, true);
    }

    /// <summary>
    /// «Только логика»: the rest of the battle without rendering — the same ticks as with it, to the end, a breakpoint
    /// or the limit; the report opens after.
    /// </summary>
    public void RunLogicOnly(double maxSeconds = 900)
    {
        if (Sim.Scenario.Preparing)
        {
            _hud.ShowBanner("Сначала завершите подготовку («Начать бой»)", Ui.Warn);
            return;
        }
        Driver.Paused = true;
        long limit = Sim.Tick + Sim.TicksFor(maxSeconds);
        StopReason = "";
        Breaks.Consume();
        var started = Time.GetTicksMsec();
        while (Sim.Outcome == BattleOutcome.None && Sim.Tick < limit)
        {
            Sim.Step();
            Sim.ConsumeAutopause();
            if (Breaks.Consume() is { } hit)
            {
                StopReason = "остановка на событии: " + hit;
                break;
            }
        }
        if (StopReason == "" && Sim.Outcome == BattleOutcome.None) StopReason = $"лимит {maxSeconds:0} с";
        _hud.ShowBanner($"Только логика: {(Sim.Outcome != BattleOutcome.None ? Sim.OutcomeText : StopReason)} ({Time.GetTicksMsec() - started} мс)", Ui.Text, 4);
        _hud.Report.Open();
    }
}
