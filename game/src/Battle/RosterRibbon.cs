using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// The squad in battle (ТЗ ч. 6, 3.12, 11.1–11.3; docs/DECISIONS_6.md §97). Shown only while the deployment for points is
/// on: the points, the gain and «на поле N из M» always in view; a card per fighter — the price, the state, the pause left
/// and the reason it cannot be called; the hint of the active tool; «Отозвать» for the selected fighter and «Начать бой»
/// in the preparation. Folds to its first line so it never covers the map for good.
/// </summary>
public partial class RosterRibbon : PanelContainer
{
    private readonly BattleScreen _bs;
    private readonly Label _points;
    private readonly Button _fold;
    private readonly HFlowContainer _cards;
    private readonly Label _hint;
    private readonly HBoxContainer _actions;
    private readonly List<Button> _buttons = new();
    private string _actionsKey = "";

    public RosterRibbon(BattleScreen bs)
    {
        _bs = bs;
        Name = "RosterRibbon";
        AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.05f, 0.1f, 0.11f, 0.9f), new Color(0.3f, 0.85f, 0.78f, 0.75f), 6, 1, 8));
        var v = Ui.VBox(4);
        var head = Ui.HBox(8);
        _fold = Ui.Button("▾", () => Folded = !Folded, "Свернуть / развернуть ленту отряда");
        _fold.Name = "RosterFold";
        _points = Ui.Label("", 14, new Color(0.55f, 1f, 0.9f));
        _points.Name = "RosterPoints";
        head.AddChild(_fold);
        head.AddChild(_points);
        _cards = new HFlowContainer { Name = "RosterCards" };
        _cards.AddThemeConstantOverride("h_separation", 6);
        _cards.AddThemeConstantOverride("v_separation", 4);
        _hint = Ui.Label("", 13, Ui.Text, wrap: true);
        _hint.Name = "RosterHint";
        _actions = Ui.HBox(6);
        _actions.Name = "RosterActions";
        v.AddChild(head);
        v.AddChild(_cards);
        v.AddChild(_hint);
        v.AddChild(_actions);
        AddChild(v);
        CustomMinimumSize = new Vector2(420, 0);
    }

    private Simulation Sim => _bs.Sim;

    /// <summary>Only the first line: the points and the limit (the cards hidden).</summary>
    public bool Folded { get; set; }

    public void Refresh()
    {
        var dep = Sim.Deployment;
        Visible = dep.Enabled;
        if (!Visible) return;
        var r = dep.Rules;
        string gain = r.GainAmount > 0 ? $" · +{r.GainAmount:0.#} за {r.GainInterval:0.#} с" + (dep.NextGainIn > 0 ? $" (через {dep.NextGainIn:0.0})" : " (пауза начисления)") : "";
        _points.Text = $"{dep.PointsName}: {dep.Points:0.#}" + (r.MaxPoints > 0 ? $" / {r.MaxPoints:0.#}" : "") + gain +
                       $" · на поле {dep.DeployedCount}" + (r.MaxDeployed > 0 ? $" из {r.MaxDeployed}" : "");
        _fold.Text = Folded ? "▸" : "▾";
        _cards.Visible = !Folded;
        _hint.Text = _bs.Deploying ? _bs.DeployHint : "";
        _hint.Visible = _hint.Text != "";

        var entries = Sim.Squad.Entries;
        if (_buttons.Count != entries.Count)
        {
            Ui.ClearChildren(_cards);
            _buttons.Clear();
            foreach (var e in entries)
            {
                int i = e.Index;
                var b = Ui.Button("", () => _bs.ChooseDeploy(i));
                b.Name = "RosterCard_" + i;
                b.ToggleMode = true;
                b.CustomMinimumSize = new Vector2(118, 46);
                _buttons.Add(b);
                _cards.AddChild(b);
            }
        }
        for (int i = 0; i < entries.Count; i++) UpdateCard(_buttons[i], entries[i]);

        // The actions: rebuilt only when what they offer changes.
        var sel = _bs.Selected;
        bool canStart = Sim.Scenario.Preparing && Sim.Placement.Def is { End: PreparationEnd.Confirm or PreparationEnd.ConfirmOrTimer };
        string? why = sel != null && Sim.Squad.EntryOf(sel) != null ? dep.CanRetreat(sel) : null;
        string key = $"{sel?.Id}|{why}|{(sel != null ? dep.RefundOf(sel) : 0):0.##}|{canStart}";
        if (key == _actionsKey) return;
        _actionsKey = key;
        Ui.ClearChildren(_actions);
        if (sel != null && sel.Alive && Sim.Squad.EntryOf(sel) != null)
        {
            var rb = Ui.Button($"Отозвать «{sel.Name}» (+{dep.RefundOf(sel):0.#})", () => _bs.RetreatSelected(),
                why ?? $"Убрать с поля (O): вернётся доля цены, повторный вызов — через {dep.CooldownOf(sel.Def):0.#} с");
            rb.Name = "RosterRetreat";
            rb.Disabled = why != null;
            _actions.AddChild(rb);
        }
        if (canStart)
        {
            var go = Ui.Button("▶ Начать бой", () => _bs.ConfirmPreparation(), "Враги появятся по расписанию волн с этого момента");
            go.Name = "RosterStart";
            go.AddThemeColorOverride("font_color", Ui.Good);
            _actions.AddChild(go);
        }
        _actions.Visible = _actions.GetChildCount() > 0;
    }

    private void UpdateCard(Button b, RosterEntry e)
    {
        var dep = Sim.Deployment;
        var def = Sim.Content.Unit(e.Template);
        string name = def?.DisplayName ?? e.Template;
        string? why = dep.CanDeploy(e);
        (string state, Color col) = e.State switch
        {
            RosterState.Deployed => ("на поле", Ui.Accent),
            RosterState.Defeated => ("пал", Ui.Bad),
            RosterState.Unavailable => ("недоступен", Ui.TextDim),
            RosterState.WithdrawnCooldown or RosterState.DefeatedCooldown => ($"⟳ {dep.CooldownLeft(e):0.0} с", Ui.Warn),
            _ when why == null => ("готов", Ui.Good),
            _ when why.StartsWith("не хватает") => ("мало очков", Ui.Bad),
            _ when why.StartsWith("на поле уже") => ("лимит поля", Ui.Warn),
            _ => ("нельзя", Ui.TextDim),
        };
        b.Text = $"{name}\n{dep.CostOf(e):0.#} · {state}";
        b.AddThemeColorOverride("font_color", col);
        b.AddThemeColorOverride("font_disabled_color", new Color(col, 0.55f));
        b.ButtonPressed = _bs.DeployEntry == e.Index;
        b.Disabled = why != null && e.State != RosterState.Deployed && _bs.DeployEntry != e.Index;
        b.TooltipText = e.State == RosterState.Deployed ? "На поле — клик выделяет бойца"
            : why != null ? "Вызвать нельзя: " + why
            : $"Вызвать (цена {dep.CostOf(e):0.#}): клик → гекс зоны → направление → ЛКМ/Enter";
    }
}
