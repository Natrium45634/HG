using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Tools of the project in the TD scene (docs/DECISIONS_4.md §69–72): the wave report, the performance budget and a
/// profiled run, local overrides of a group turned into a template, the groups that walk a route, the comparison of
/// a batch with the previous one.
/// </summary>
public partial class TdScenePanel
{
    public const string WaveReportTitle = "Отчёт по волнам";

    /// <summary>The reports of the last autotest or batch (the wave report takes what happened from them).</summary>
    public List<BattleReport> LastReports { get; private set; } = new();
    private BatchReport? _prevBatch;
    private Task<PerformanceReport>? _profile;
    public PerformanceReport? LastProfile { get; private set; }

    /// <summary>The wave report: the plan and, after tests, what the waves did; a jump of difficulty highlighted.</summary>
    public WaveReport ShowWaveReport()
    {
        var rep = WaveReports.Build(Ctx.Db, _sc!, LastReports, Ctx.Db.Rules.Performance.WaveJump);
        var v = Ui.VBox(4);
        v.AddChild(Ui.Label(rep.Rows.Any(r => r.Runs > 0)
            ? $"По плану и по {rep.Rows.Max(r => r.Runs)} последним прогонам автотеста."
            : "Только план (автотест ещё не запускался — «Тестирование»: дошедшие до цели и урон по базе появятся после прогонов).", 13, Ui.TextDim, wrap: true).MinSize(900));
        var grid = new GridContainer { Columns = 11, Name = "WaveReportGrid" };
        grid.AddThemeConstantOverride("h_separation", 14);
        foreach (var h in new[] { "№", "Волна", "Начало", "Бюджет", "Стоимость", "Врагов", "Уровень ср./макс.", "Ожид., с", "Дошли", "Урон по базе", "К пред." })
            grid.AddChild(Ui.Label(h, 12, Ui.Accent));
        foreach (var r in rep.Rows)
        {
            var col = r.Jump ? Ui.Warn : Ui.Text;
            void Cell(string t, string name = "")
            {
                var l = Ui.Label(t, 12, col);
                if (name != "") l.Name = name;
                grid.AddChild(l);
            }
            Cell((r.Index + 1).ToString());
            Cell(r.Name);
            Cell(r.Start);
            Cell(r.Budget > 0 ? $"{r.Budget:0.#}" : "—");
            Cell($"{r.Cost:0.#}" + (r.ByBudget ? "*" : ""));
            Cell(r.Count.ToString());
            Cell($"{r.AvgLevel:0.0} / {r.MaxLevel:0.0}");
            Cell($"{r.ExpectedSeconds:0}" + (r.Seconds is { } s ? $" ({s:0})" : ""));
            Cell(r.ReachedGoal is { } g ? $"{g:0.#}" : "—");
            Cell(r.BaseDamage is { } d ? $"{d:0}" : "—");
            Cell(r.Index == 0 ? "—" : $"×{r.Relative:0.00}" + (r.Jump ? " ⚠ скачок" : ""), "WaveJump_" + r.Index);
        }
        v.AddChild(grid);
        v.AddChild(Ui.Label("* состав подбирается по бюджету при запуске (показан предпросмотр с seed сцены). В скобках — средняя длительность волны в прогонах. " +
                            $"Скачок — волна дороже предыдущей в {rep.JumpRatio:0.##}× и более (правила → «Производительность»).", 12, Ui.TextDim, wrap: true).MinSize(900));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(940, 360) };
        scroll.AddChild(v);
        Dialogs.Custom(this, WaveReportTitle, scroll, "Закрыть", () => true, new Vector2I(980, 480));
        return rep;
    }

    /// <summary>«⚙ Переопределения» of a group and «Превратить в шаблон».</summary>
    private void AddGroupOverrides(SpawnGroupDef g)
    {
        if (!GroupOverrides.Has(g)) return;
        var l = Ui.Label("⚙ Переопределения группы: " + string.Join(", ", GroupOverrides.Describe(Ctx.Db, g)), 13, Ui.Warn, wrap: true);
        l.Name = "TdOverrides";
        _props.AddChild(l);
        var b = Ui.Button("Превратить в шаблон", () => GroupToTemplate(g), "Новый шаблон с этими изменениями; группа переключается на него, переопределения очищаются (фракция остаётся у группы)");
        b.Name = "TdToTemplate";
        _props.AddChild(b);
    }

    public UnitDef GroupToTemplate(SpawnGroupDef g)
    {
        Ctx.Commit();
        var u = GroupOverrides.ToTemplate(Ctx.Db, g);
        Ctx.MarkDirty();
        Ctx.Commit($"Превратить в шаблон: «{u.DisplayName}»");
        _status.Text = $"Создан шаблон «{u.DisplayName}» [{u.Id}]; группа переключена на него";
        RefreshLibrary();
        RefreshProps();
        RunCheck(false);
        return u;
    }

    private void AddRouteUsage(RouteDef r)
    {
        var uses = ChangeImpact.OfRoute(_sc!, r.Id);
        var l = Ui.Label(uses.Count == 0 ? "Маршрут не используют группы волн." : $"Используют маршрут ({uses.Count}): " + string.Join("; ", uses.Select(u => u.Text)) + ". Изменение маршрута меняет путь этих групп.",
            12, uses.Count == 0 ? Ui.TextDim : Ui.Text, wrap: true);
        l.Name = "TdRouteUsage";
        _props.AddChild(l);
    }

    /// <summary>The performance budget of the scene and the profiled run (in the background).</summary>
    private void AddPerformance()
    {
        _props.AddChild(Ui.Sep());
        _props.AddChild(Ui.Label("Производительность", 15, Ui.Accent));
        var est = LastProfile ?? PerformanceBudget.Estimate(Ctx.Db, _sc!);
        var l = Ui.Label(est.ToText(), 12, est.OverBudget ? Ui.Warn : Ui.Text, wrap: true);
        l.Name = "TdPerfText";
        _props.AddChild(l);
        var prof = Ui.Button(_profile != null ? "Профиль прогона: идёт…" : "Профиль прогона (60 с боя)", () =>
        {
            if (_profile != null || !CanTest()) return;
            var db = Ctx.Db.Clone();
            var sc = db.Get<ScenarioDef>(_sc!.Id) ?? ContentJson.Clone(_sc);
            _profile = Task.Run(() => PerformanceBudget.Profile(db, sc, 60, _seed == 0 ? 1 : _seed));
            _status.Text = "Профиль прогона: 60 с боя без отрисовки…";
            RefreshProps();
        }, "Пробный прогон без отрисовки: доля времени каждой подсистемы (ИИ, юниты и состояния, эффекты клеток…)");
        prof.Name = "TdPerfProfile";
        _props.AddChild(prof);
        var waves = Ui.Button("📊 Отчёт по волнам", () => ShowWaveReport(), "Бюджет, стоимость, число и уровни врагов, ожидаемая длительность, итоги прогонов, скачки сложности");
        waves.Name = "TdWaveReport";
        _props.AddChild(waves);
    }

    private void PollProfile()
    {
        if (_profile == null || !_profile.IsCompleted) return;
        var t = _profile;
        _profile = null;
        if (t.IsFaulted) _status.Text = "Профиль прогона: ошибка — " + t.Exception?.GetBaseException().Message;
        else
        {
            LastProfile = t.Result;
            _status.Text = "Профиль прогона: " + string.Join(", ", t.Result.Profile.Take(4).Select(p => $"{p.Section} {p.Share:P0}"));
        }
        if (Mode == TdMode.Test) RefreshProps();
    }

    /// <summary>A batch against the previous one of the same scene: wins, waves, bases, duration.</summary>
    private string CompareBatches(BatchReport cur)
    {
        var prev = _prevBatch;
        _prevBatch = cur;
        if (prev == null || prev.Scenario != cur.Scenario) return "";
        static string D(double a, double b, string f, string unit = "") => $"{a.ToString(f)}{unit} → {b.ToString(f)}{unit}" + (Math.Abs(b - a) > 1e-9 ? $" ({(b > a ? "+" : "−")}{Math.Abs(b - a).ToString(f)}{unit})" : "");
        return "\nСравнение с предыдущим пакетом: победы " + D(prev.WinRate * 100, cur.WinRate * 100, "0", " %") + "; волн " + D(prev.MeanWaves, cur.MeanWaves, "0.0") +
               "; здоровье баз " + D(prev.MeanBaseHealth * 100, cur.MeanBaseHealth * 100, "0", " %") + "; длительность " + D(prev.MeanSeconds, cur.MeanSeconds, "0", " с");
    }
}
