using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Босс и фазы» of the unit editor (ТЗ ч. 6, 8.6; docs/DECISIONS_6.md §102): the phases as a table of states, the problems of
/// the check, the scheme of the transitions (the canvas of the graphs, read-only) and «▶ Прогон фазы» — a battle on an empty
/// field starting in the chosen phase, with a fixed seed. The fields themselves are edited in the grid below.
/// </summary>
public partial class BossPhasesView : VBoxContainer
{
    private readonly EditorContext _ctx;
    private readonly UnitDef _u;
    private readonly Label _head;
    private readonly VBoxContainer _problems;
    private readonly GridContainer _table;
    private readonly RuleGraphCanvas _scheme;
    private readonly OptionButton _phase;
    private readonly SpinBox _seed;

    public BossPhasesView(EditorContext ctx, UnitDef u)
    {
        _ctx = ctx;
        _u = u;
        Name = "BossPhasesView";
        AddThemeConstantOverride("separation", 6);
        _head = Ui.Label("", 15, Ui.Accent, wrap: true);
        AddChild(_head);
        _problems = Ui.VBox(2);
        _problems.Name = "BossPhaseProblems";
        AddChild(_problems);
        _table = new GridContainer { Name = "BossPhaseTable", Columns = 6 };
        _table.AddThemeConstantOverride("h_separation", 10);
        AddChild(_table);
        _scheme = new RuleGraphCanvas { Name = "BossPhaseScheme", ReadOnly = true, CustomMinimumSize = new Vector2(0, 260), MinimapEnabled = false };
        _scheme.SizeFlagsVertical = SizeFlags.Fill;
        AddChild(_scheme);
        var run = new HFlowContainer();
        run.AddChild(Ui.Label("Прогон фазы:", 13, Ui.TextDim));
        _phase = new OptionButton { Name = "BossPhaseRunChoice", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(180, 0) };
        run.AddChild(_phase);
        run.AddChild(Ui.Label("seed", 13, Ui.TextDim));
        _seed = new SpinBox { Name = "BossPhaseSeed", MinValue = 1, MaxValue = 999999, Value = 7, Step = 1 };
        run.AddChild(_seed);
        var go = Ui.Button("▶ Прогон фазы", Run, "Бой на пустой карте: босс сразу в выбранной фазе и два рыцаря; контент проекта не меняется");
        go.Name = "BossPhaseRun";
        run.AddChild(go);
        AddChild(run);
        AddChild(Ui.Sep());
        Refresh();
    }

    /// <summary>The phase chosen for the run (an index of the list).</summary>
    public int RunPhase => _phase.Selected;
    public int TableRows => _u.Boss.Phases.Count;
    public RuleGraphCanvas Scheme => _scheme;

    public void Refresh()
    {
        if (!IsInstanceValid(_head)) return;
        var b = _u.Boss;
        Visible = true;
        _head.Text = !b.IsBoss ? "Не босс: включите «Это босс» ниже — появятся фазы." : $"Фазы босса: {b.Phases.Count}" + (b.Title != "" ? $" · «{b.Title}»" : "");
        Ui.ClearChildren(_problems);
        var problems = BossPhases.Check(_ctx.Db, b);
        if (b.IsBoss)
        {
            var ok = Ui.Label(problems.Count == 0 ? "✓ Фазы без ошибок" : $"Ошибок: {problems.Count(p => p.Error)}, предупреждений: {problems.Count(p => !p.Error)}", 13,
                problems.Count == 0 ? Ui.Good : problems.Any(p => p.Error) ? Ui.Bad : Ui.Warn);
            ok.Name = "BossPhaseCheck";
            _problems.AddChild(ok);
        }
        foreach (var p in problems)
            _problems.AddChild(Ui.Label((p.Error ? "⛔ " : "⚠ ") + p.Message, 12, p.Error ? Ui.Bad : Ui.Warn, wrap: true));

        Ui.ClearChildren(_table);
        if (b.IsBoss && b.Phases.Count > 0)
        {
            foreach (var h in new[] { "№ · ID", "Фаза", "Из фаз", "Условие", "Приоритет", "Повтор · сброс" })
                _table.AddChild(Ui.Label(h, 12, Ui.TextDim));
            for (int i = 0; i < b.Phases.Count; i++)
            {
                var ph = b.Phases[i];
                string resets = string.Join(", ", new[]
                {
                    ph.ResetCooldowns ? "перезарядки" : "", ph.RefillAp ? "ОД" : "", ph.Heal > 0 ? $"лечение {ph.Heal:P0}" : "",
                    ph.ClearStatuses.Count > 0 ? "состояния" : "",
                }.Where(x => x != ""));
                _table.AddChild(Ui.Label($"{i + 1} · {BossPhases.IdOf(b, i)}", 13));
                _table.AddChild(Ui.Label(ph.Name, 13, Ui.Text));
                _table.AddChild(Ui.Label(string.Join(", ", BossPhases.Sources(b, i).Select(x => x == BossPhases.Start ? "начало" : x)), 13));
                var when = Ui.Label(ph.When.Describe(), 13);
                when.CustomMinimumSize = new Vector2(160, 0);
                when.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _table.AddChild(when);
                _table.AddChild(Ui.Label(ph.Priority.ToString(), 13));
                _table.AddChild(Ui.Label((ph.CanRepeat ? "да" : "один раз") + (resets != "" ? " · " + resets : " · всё сохраняется"), 13));
            }
        }
        _scheme.Visible = b.IsBoss && b.Phases.Count > 0;
        if (_scheme.Visible) _scheme.Show(BossPhases.Scheme(b));
        int keep = _phase.Selected;
        _phase.Clear();
        for (int i = 0; i < b.Phases.Count; i++) _phase.AddItem($"{i + 1}. {b.Phases[i].Name}");
        if (_phase.ItemCount > 0) _phase.Select(keep >= 0 && keep < _phase.ItemCount ? keep : 0);
        _phase.GetParent<Control>().Visible = b.IsBoss && b.Phases.Count > 0;
    }

    private void Run()
    {
        if (_phase.Selected < 0) return;
        _ctx.Validate();
        var (db, sc) = BossPhases.Sandbox(_ctx.Db, _u, _phase.Selected, (ulong)_seed.Value);
        App.Main.Instance.ShowBattle(sc, db, admin: true, onExit: () => App.Main.Instance.ShowEditor());
    }
}
