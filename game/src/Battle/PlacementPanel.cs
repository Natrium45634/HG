using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// The preparation of «TD с расстановкой» (docs/DECISIONS_4.md §64): the budget and limits, a card per available
/// template (a click picks it, a click on a hex of the zone places it), the actions on a placed defender (move,
/// take back, upgrade), «расставить как в тесте» and «начать бой». Between waves it stays for moving and upgrading.
/// </summary>
public partial class PlacementPanel : PanelContainer
{
    private readonly BattleScreen _bs;
    private readonly Label _title;
    private readonly Label _budget;
    private readonly HFlowContainer _cards;
    private readonly HBoxContainer _actions;
    private string _key = "";

    public PlacementPanel(BattleScreen bs)
    {
        _bs = bs;
        Name = "PlacementPanel";
        AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.06f, 0.09f, 0.13f, 0.92f), new Color(0.35f, 0.6f, 0.95f, 0.8f), 1, 6, 10));
        var v = Ui.VBox(6);
        _title = Ui.Label("Подготовка к бою", 17, Ui.Accent);
        _budget = Ui.Label("", 13, Ui.Text);
        _budget.Name = "PlaceBudget";
        _cards = new HFlowContainer { Name = "PlaceCards" };
        _cards.AddThemeConstantOverride("h_separation", 6);
        _cards.AddThemeConstantOverride("v_separation", 4);
        _actions = Ui.HBox(6);
        v.AddChild(_title);
        v.AddChild(_budget);
        v.AddChild(_cards);
        v.AddChild(_actions);
        AddChild(v);
        CustomMinimumSize = new Vector2(560, 0);
    }

    private Simulation Sim => _bs.Sim;

    public void Refresh()
    {
        var pl = Sim.Placement;
        var def = pl.Def;
        bool prep = Sim.Scenario.Preparing;
        bool between = !prep && Sim.Scenario.Mode == BattleMode.TdPlacement && pl.CanRelocateNow && pl.Placed.Count > 0;
        Visible = def != null && Sim.Scenario.Mode == BattleMode.TdPlacement && (prep || between) && Sim.Outcome == BattleOutcome.None;
        if (!Visible) return;
        _title.Text = prep ? "Подготовка к бою" : "Между волнами: перестановка и улучшение";
        string time = prep && def!.End is PreparationEnd.Timer or PreparationEnd.ConfirmOrTimer && def.Duration > 0
            ? $" · до начала {System.Math.Max(0, def.Duration - Sim.Scenario.PreparationTime):0} с" : "";
        int alive = pl.Placed.Count(p => Sim.GetUnit(p.Unit)?.Alive == true);
        _budget.Text = (pl.Budget > 0 ? $"Бюджет: осталось {pl.Left:0.#} из {pl.Budget:0.#}" : "Бюджет не ограничен")
                       + $" · защитников {alive}{(def!.MaxUnits > 0 ? $"/{def.MaxUnits}" : "")}" + time;

        // Rebuilt only when what it shows changes (the cards keep their pressed state between frames).
        var sel = _bs.Selected;
        string key = $"{prep}|{pl.Left:0.##}|{alive}|{_bs.PlaceTemplate}|{_bs.Relocating?.Id}|{sel?.Id}|{pl.CanRelocateNow}";
        if (key == _key) return;
        _key = key;
        Ui.ClearChildren(_cards);
        Ui.ClearChildren(_actions);
        if (prep)
            foreach (var e in def.Units)
            {
                var ud = Sim.Content.Unit(e.Unit);
                if (ud == null) continue;
                var why = pl.CanAfford(e.Unit);
                string text = $"{ud.DisplayName} — {pl.CostOf(e):0.#}" + (e.MaxCount > 0 ? $" ({pl.CountOf(e.Unit)}/{e.MaxCount})" : "");
                string unit = e.Unit;
                var b = Ui.Button(text, () => _bs.ChoosePlacement(_bs.PlaceTemplate == unit ? null : unit),
                    why ?? $"Выбрать и кликнуть по гексу зоны (подсвечена). Профиль ИИ: {(e.AiProfile != "" ? e.AiProfile : ud.AiProfile)}");
                b.Name = "PlaceCard_" + e.Unit;
                b.ToggleMode = true;
                b.ButtonPressed = _bs.PlaceTemplate == e.Unit;
                b.Disabled = why != null && _bs.PlaceTemplate != e.Unit;
                _cards.AddChild(b);
            }
        if (sel != null && sel.Alive && pl.IsPlaced(sel))
        {
            _actions.AddChild(Ui.Label(sel.Name + ":", 13, Ui.TextDim));
            if (pl.CanRelocateNow)
            {
                var mv = Ui.Button(_bs.Relocating == sel ? "Куда? (клик по гексу)" : "Переставить", () => _bs.StartRelocate(sel), "Затем клик по гексу зоны");
                mv.Name = "PlaceRelocate";
                _actions.AddChild(mv);
            }
            if (prep)
            {
                var rm = Ui.Button("Снять", () => _bs.RemovePlaced(sel), "Вернуть в бюджет (или ПКМ по юниту)");
                rm.Name = "PlaceRemove";
                _actions.AddChild(rm);
            }
            if (pl.UpgradeOf(sel) is { } up)
            {
                var ub = Ui.Button($"Улучшить → {up.To.DisplayName} (−{up.Cost:0.#})", () => _bs.UpgradePlaced(sel));
                ub.Name = "PlaceUpgrade";
                ub.Disabled = pl.Budget > 0 && up.Cost > pl.Left + 1e-9;
                _actions.AddChild(ub);
            }
            _actions.AddChild(Ui.Spacer(12));
        }
        if (prep)
        {
            if (def.Preset.Count > 0)
            {
                var pr = Ui.Button("Расставить как в тесте", () => _bs.PlacePreset(), "Расстановка по умолчанию сценария (в пределах бюджета)");
                pr.Name = "PlacePreset";
                _actions.AddChild(pr);
            }
            if (def.End is PreparationEnd.Confirm or PreparationEnd.ConfirmOrTimer)
            {
                var go = Ui.Button("▶ Начать бой", () => _bs.ConfirmPreparation(), "Враги появятся по расписанию волн с этого момента");
                go.Name = "PlaceConfirm";
                go.AddThemeColorOverride("font_color", Ui.Good);
                _actions.AddChild(go);
            }
        }
    }
}
