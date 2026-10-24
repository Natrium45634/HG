using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>
/// «Проверить сценарий» (docs/DECISIONS_4.md §70, TZ §18.1): bases, routes and groups (the TD check), every problem the
/// validator reports for the scenario (objectives, triggers, cycles of events), the templates it uses (their
/// characteristics, the «чрезмерно сильный» warnings, conflicts of categories), the skills of those templates, the
/// counts and intervals of the groups, the performance budget and the jumps of difficulty between waves. Each problem
/// names the object to go to: a scenario object or route by its ID, a template as «unit:ID», a skill as «skill:ID», a
/// wave as «wave:N».
/// </summary>
public static class ScenarioChecks
{
    public static List<ScenarioProblem> Run(ContentDatabase db, ScenarioDef sc, IReadOnlyList<ContentIssue>? issues = null)
    {
        var list = new List<ScenarioProblem>();
        var map = db.Get<MapDef>(sc.Map);
        if (map == null) list.Add(new(IssueSeverity.Error, "Map", $"Карта «{sc.Map}» не найдена"));
        else list.AddRange(ScenarioObjectsCheck.All(db, sc, map));
        issues ??= ContentValidator.Validate(db);

        // The validator about the scenario itself (without what the TD check already said).
        string src = $"{DefCategory.Scenario}:{sc.Id}";
        var seen = list.Select(p => p.Message).ToHashSet(StringComparer.Ordinal);
        foreach (var i in issues.Where(i => i.Source == src && i.Severity != IssueSeverity.Info && !seen.Contains(i.Message)))
            list.Add(new(i.Severity, i.Path, i.Message, null, WaveOf(i.Path)));

        // The templates it uses and their skills.
        var units = UsedTemplates(db, sc);
        foreach (var u in units)
        {
            foreach (var i in issues.Where(i => i.Source == $"{DefCategory.Unit}:{u.Id}" && i.Severity != IssueSeverity.Info))
                list.Add(new(i.Severity, i.Path, $"Шаблон «{u.DisplayName}»: {i.Message}", null, "unit:" + u.Id));
            foreach (var p in CategoryTree.Resolve(db, u).Problems.Distinct())
                list.Add(new(IssueSeverity.Warning, "Categories", $"Шаблон «{u.DisplayName}», категории: {p}", null, "unit:" + u.Id));
        }
        foreach (var s in units.SelectMany(u => u.Skills.Append(u.BasicAttack)).Distinct().Select(id => db.Skill(id)).Where(s => s != null).Select(s => s!))
            foreach (var i in issues.Where(i => i.Source == $"{DefCategory.Skill}:{s.Id}" && i.Severity != IssueSeverity.Info))
                list.Add(new(i.Severity, i.Path, $"Навык «{s.DisplayName}»: {i.Message}", null, "skill:" + s.Id));

        // Counts and intervals of the groups.
        for (int wi = 0; wi < sc.Waves.Count; wi++)
        {
            var w = sc.Waves[wi];
            foreach (var g in w.Groups.Where(g => g.Enabled))
            {
                string name = $"Волна «{w.Name}», группа «{(g.Name == "" ? g.Unit : g.Name)}»";
                int n = WaveSchedule.CountOf(g, w);
                var b = sc.Object(g.SpawnBase);
                int area = b == null ? 0 : ScenarioObjectsCheck.SpawnArea(b).Count();
                if (g.Interval <= 0 && g.SubgroupSize <= 0 && area > 0 && n > area)
                    list.Add(new(IssueSeverity.Warning, $"Waves[{wi}].Groups[{g.Id}]", $"{name}: {n} врагов сразу (интервал 0), а у базы «{b!.DisplayName}» {area} мест появления — остальные будут ждать",
                        b.Anchor, "wave:" + wi));
                double spawnSeconds = (n - 1) * g.Interval + (g.SubgroupSize > 0 ? (n - 1) / g.SubgroupSize * g.SubgroupPause : 0);
                if (g.Repeats > 0 && g.RepeatEvery < spawnSeconds)
                    list.Add(new(IssueSeverity.Warning, $"Waves[{wi}].Groups[{g.Id}]", $"{name}: повтор через {g.RepeatEvery:0.#} с, а выход группы длится {spawnSeconds:0.#} с — выходы наложатся",
                        b?.Anchor, "wave:" + wi));
                if (n * (g.Repeats + 1) > db.Rules.Performance.MaxAliveUnits)
                    list.Add(new(IssueSeverity.Warning, $"Waves[{wi}].Groups[{g.Id}]", $"{name}: {n * (g.Repeats + 1)} врагов — больше порога живых юнитов проекта ({db.Rules.Performance.MaxAliveUnits})",
                        b?.Anchor, "wave:" + wi));
            }
        }

        // The performance budget and the jumps of difficulty.
        var perf = PerformanceBudget.Estimate(db, sc);
        foreach (var l in perf.Lines.Where(l => l.Over))
            list.Add(new(IssueSeverity.Warning, "Performance", $"Производительность: {l.Label} {l.Value:0.#} — больше порога {l.Limit:0.#}" + (l.Detail != "" ? $" ({l.Detail})" : "")));
        foreach (var r in WaveReports.Build(db, sc, null, db.Rules.Performance.WaveJump).Rows.Where(r => r.Jump))
            list.Add(new(IssueSeverity.Warning, $"Waves[{r.Index}]", $"Скачок сложности: волна «{r.Name}» дороже предыдущей в {r.Relative:0.0} раза (стоимость {r.Cost:0.#})", null, "wave:" + r.Index));
        return list;
    }

    /// <summary>The templates of the scenario: its units, groups and old-style waves, the placement, a previewed budget.</summary>
    public static List<UnitDef> UsedTemplates(ContentDatabase db, ScenarioDef sc)
    {
        var ids = new List<string>();
        ids.AddRange(sc.Units.Select(u => u.Unit));
        ids.AddRange(sc.Waves.SelectMany(w => w.Groups.Select(g => g.Unit).Concat(w.Units.Select(u => u.Unit)).Append(w.Budget.Enabled ? w.Budget.Leader : "")));
        ids.AddRange(sc.Placement.Units.Select(u => u.Unit));
        ids.AddRange(sc.Placement.Preset.Select(u => u.Unit));
        var list = ids.Where(id => id != "").Select(id => db.Unit(id)).Where(u => u != null).Select(u => u!).Distinct().ToList();
        foreach (var p in WavePlan.Of(db, sc).Where(p => p.ByBudget))
            foreach (var s in p.Spawns)
                if (!list.Contains(s.Unit)) list.Add(s.Unit);
        return list;
    }

    private static string WaveOf(string path)
    {
        if (!path.StartsWith("Waves[", StringComparison.Ordinal)) return "";
        int end = path.IndexOf(']');
        return end > 6 && int.TryParse(path[6..end], out var i) ? "wave:" + i : "";
    }
}
