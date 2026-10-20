using System;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Серия» tab of the skill editor (docs/DECISIONS_3.md §45): the hits of the sequence in order with their
/// moments, multipliers, conditions and costs; templates; warnings; and a run on a test map (an isolated
/// simulation with dummies) with the breakdown of every hit.
/// </summary>
public partial class SkillEditorPanel
{
    private VBoxContainer _seriesTab = null!;
    private readonly SkillTestOptions _testOptions = new();
    private SkillTestResult? _lastRun;

    private void RefreshSeriesTab()
    {
        if (!IsInstanceValid(_seriesTab) || _skill == null) return;
        Ui.ClearChildren(_seriesTab);
        var s = _skill;
        int hits = SkillText.TotalHits(s);
        _seriesTab.AddChild(Ui.Label($"Ударов: {hits} · длительность серии {SkillText.Duration(s):0.##} с (после подготовки {s.PrepTime:0.##} с)", 15, Ui.Accent, wrap: true));
        var summary = SkillText.Summary(s);
        if (summary != "") _seriesTab.AddChild(Ui.Label(summary, 13, Ui.Text, wrap: true));
        _seriesTab.AddChild(Ui.Label("Каждый этап — один удар (или повторяется N раз). Параметры удара, условие, стоимость и движение — на вкладке «Этап»; " +
                                     "правила серии (крит, гибель цели, смена цели, нехватка ресурса) — на вкладке «Навык», раздел «Серия».", 12, Ui.TextDim, wrap: true));

        var lines = SkillText.StageLines(s);
        for (int i = 0; i < s.Stages.Count; i++)
        {
            int idx = i;
            var row = Ui.HBox(4);
            var pick = Ui.Button((i == _stage ? "▶ " : "") + lines[i], () =>
            {
                _stage = idx;
                _selected = null;
                RefreshAll();
                _tabs.CurrentTab = 1;
            }, "Открыть этап");
            pick.Name = "SeriesStage_" + i;
            pick.Alignment = HorizontalAlignment.Left;
            pick.ClipText = true;
            pick.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(pick);
            row.AddChild(Ui.Button("↑", () => MoveStage(idx, -1), "Раньше в списке (момент задаёт задержка)"));
            row.AddChild(Ui.Button("↓", () => MoveStage(idx, 1), "Позже в списке"));
            row.AddChild(Ui.Button("⧉", () => DuplicateStage(idx), "Копия этапа (задержка +0,35 с)"));
            _seriesTab.AddChild(row);
        }

        var bar = new HFlowContainer();
        bar.AddChild(Ui.Button("+ удар", AddHit, "Новый удар: копия последнего удара, позже на 0,35 с"));
        bar.AddChild(Ui.Button("Шаблон «Три удара»", ThreeHitsTemplate, "Три удара по 70 %, 85 % и 120 % с эффектами урона первого этапа"));
        bar.AddChild(Ui.Button("Шаблон «Серия N»", RepeatTemplate, "Один этап, повторённый 5 раз через 0,25 с (серия стрел)"));
        _seriesTab.AddChild(bar);

        foreach (var i in Ctx.IssuesOf(s).Where(i => i.Path.StartsWith("Stages", StringComparison.Ordinal) || i.Path is "OnResourceOut" or "FreeIf" or "RetargetOnDeath" || i.Path.StartsWith("Defense", StringComparison.Ordinal)))
            _seriesTab.AddChild(Ui.Label($"{(i.Severity == IssueSeverity.Error ? "✗" : "⚠")} {i.Message}", 13, i.Severity == IssueSeverity.Error ? Ui.Bad : Ui.Warn, wrap: true));
        if (s.Defense.Enabled) _seriesTab.AddChild(Ui.Label($"Защита ({Names.Of(s.Defense.Kind)}): {s.Defense.Describe()}", 13, Ui.Accent, wrap: true));

        // ---- test run
        _seriesTab.AddChild(Ui.Sep());
        _seriesTab.AddChild(Ui.Label("Проверка на тестовой карте", 15, Ui.Accent));
        _seriesTab.AddChild(Ui.Label("Отдельная симуляция в памяти: исполнитель, манекены, одно применение. Контент и бой не меняются; один и тот же номер прогона даёт тот же результат.", 12, Ui.TextDim, wrap: true));
        var opts = new GridContainer { Columns = 4 };
        opts.AddThemeConstantOverride("h_separation", 8);
        Spin(opts, "Манекенов", 0, 6, 1, _testOptions.Targets, v => _testOptions.Targets = (int)v);
        Spin(opts, "Здоровье", 1, 100000, 10, _testOptions.TargetHp, v => _testOptions.TargetHp = v);
        Spin(opts, "Броня", 0, 1000, 1, _testOptions.TargetArmor, v => _testOptions.TargetArmor = v);
        Spin(opts, "Уклонение", 0, 1, 0.05, _testOptions.TargetEvasion, v => _testOptions.TargetEvasion = v);
        Spin(opts, "Номер прогона", 1, 1_000_000, 1, _testOptions.Seed, v => _testOptions.Seed = (ulong)v);
        var away = new CheckBox { Text = "Спиной к исполнителю", ButtonPressed = _testOptions.TargetsFacingAway, FocusMode = FocusModeEnum.None };
        away.Toggled += on => _testOptions.TargetsFacingAway = on;
        opts.AddChild(away);
        _seriesTab.AddChild(opts);
        var casters = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Чьи характеристики у исполнителя" };
        casters.AddItem("Исполнитель: автоматически");
        var units = Ctx.Db.All<UnitDef>().OrderBy(u => u.DisplayName).ToList();
        foreach (var u in units) casters.AddItem(u.DisplayName + (u.Skills.Contains(s.Id) || u.DefenseSkill == s.Id || u.BasicAttack == s.Id ? " ✓" : ""));
        casters.Selected = Math.Max(0, units.FindIndex(u => u.Id == _testOptions.CasterUnit) + 1);
        casters.ItemSelected += i => _testOptions.CasterUnit = i <= 0 ? "" : units[(int)i - 1].Id;
        _seriesTab.AddChild(casters);
        var run = Ui.Button("▶ Прогнать на тестовой карте", () =>
        {
            Ctx.Validate();
            _lastRun = SkillTestRun.Run(Ctx.Db, s, _testOptions);
            RefreshSeriesTab();
        }, "Применить навык к манекенам и показать разбор каждого удара");
        run.Name = "SeriesTestRun";
        _seriesTab.AddChild(run.MinSize(0, 36));
        if (_lastRun != null) ShowRun(_lastRun);
    }

    private void ShowRun(SkillTestResult r)
    {
        if (!r.Started)
        {
            _seriesTab.AddChild(Ui.Label("✗ Навык не сработал: " + r.Problem, 13, Ui.Bad, wrap: true));
            return;
        }
        _seriesTab.AddChild(Ui.Label($"Исполнитель: {r.CasterName} · ударов по целям: {r.Hits.Count} · урон всего {r.TotalDamage:0}" +
                                     (r.StopReason != "" ? $" · серия прекращена: {r.StopReason}" : ""), 13, Ui.Good, wrap: true));
        foreach (var h in r.Hits)
            _seriesTab.AddChild(Ui.Label($"Удар {h.HitNumber} → {h.TargetName}: " + (h.Hit ? (h.Crit ? "крит, " : "попадание, ") + $"{h.Damage:0} урона" : "промах") +
                                         (h.Killed ? ", погиб" : "") + (h.Note != "" ? $" · {h.Note}" : ""), 13, h.Hit ? Ui.Text : Ui.TextDim, wrap: true));
        foreach (var t in r.Targets)
            _seriesTab.AddChild(Ui.Label($"{t.Name}: {t.HpBefore:0} → {t.HpAfter:0}{(t.Alive ? "" : " (погиб)")}", 12, Ui.TextDim));
        var details = Ui.Label(string.Join("\n", r.Log.Concat(new[] { "", "Расчёт урона:" }).Concat(r.Breakdowns)), 12, Ui.Text, wrap: true);
        details.Visible = false;
        var toggle = Ui.Button($"Журнал ({r.Log.Count}) и расчёт урона ({r.Breakdowns.Count}) ▾", () => details.Visible = !details.Visible);
        _seriesTab.AddChild(toggle);
        _seriesTab.AddChild(details);
    }

    private static void Spin(GridContainer g, string label, double min, double max, double step, double value, Action<double> set)
    {
        g.AddChild(Ui.Label(label, 12, Ui.TextDim));
        var sb = new SpinBox { MinValue = min, MaxValue = max, Step = step, Value = value, CustomMinimumSize = new Vector2(90, 0) };
        sb.ValueChanged += v => set(v);
        g.AddChild(sb);
    }

    private void MoveStage(int i, int dir)
    {
        if (_skill == null) return;
        int j = i + dir;
        if (j < 0 || j >= _skill.Stages.Count) return;
        (_skill.Stages[i], _skill.Stages[j]) = (_skill.Stages[j], _skill.Stages[i]);
        _stage = j;
        OnEdited(_skill);
        RefreshAll();
    }

    private void DuplicateStage(int i)
    {
        if (_skill == null) return;
        var copy = ContentJson.Clone(_skill.Stages[i]);
        copy.Delay += 0.35;
        copy.Final = false;
        _skill.Stages.Insert(i + 1, copy);
        _stage = i + 1;
        OnEdited(_skill);
        RefreshAll();
    }

    private void AddHit()
    {
        if (_skill == null) return;
        var last = _skill.Stages.LastOrDefault(st => st.HasDamage) ?? _skill.Stages.LastOrDefault();
        var hit = last != null ? ContentJson.Clone(last) : new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } } };
        hit.Delay = (_skill.Stages.Count == 0 ? 0 : _skill.Stages.Max(st => st.Delay)) + 0.35;
        hit.Name = $"Удар {SkillText.TotalHits(_skill) + 1}";
        hit.Final = false;
        hit.Repeat = 1;
        _skill.Stages.Add(hit);
        _stage = _skill.Stages.Count - 1;
        OnEdited(_skill);
        RefreshAll();
    }

    private void ThreeHitsTemplate()
    {
        if (_skill == null) return;
        var baseStage = _skill.Stages.FirstOrDefault(st => st.HasDamage) ?? new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } } };
        string[] names = { "Первый удар", "Второй удар", "Третий удар" };
        double[] mult = { 0.7, 0.85, 1.2 };
        _skill.Stages.Clear();
        for (int i = 0; i < 3; i++)
        {
            var st = ContentJson.Clone(baseStage);
            st.Name = names[i];
            st.Delay = i * 0.35;
            st.DamageMultiplier = mult[i];
            st.Repeat = 1;
            if (i == 2) st.Tag = "finisher";
            _skill.Stages.Add(st);
        }
        _skill.HoldDuringSeries = true;
        _skill.StopOnTargetDeath = true;
        _stage = 0;
        OnEdited(_skill);
        RefreshAll();
    }

    private void RepeatTemplate()
    {
        if (_skill == null) return;
        var baseStage = _skill.Stages.FirstOrDefault(st => st.HasDamage) ?? new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } } };
        var st = ContentJson.Clone(baseStage);
        st.Delay = 0;
        st.Repeat = 5;
        st.RepeatInterval = 0.25;
        st.DamageMultiplier = 0.5;
        _skill.Stages.Clear();
        _skill.Stages.Add(st);
        _skill.HoldDuringSeries = true;
        _stage = 0;
        OnEdited(_skill);
        RefreshAll();
    }
}
