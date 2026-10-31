using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>A problem of the phases of a boss: an error or a warning, the phase («» — the profile).</summary>
public sealed record PhaseProblem(bool Error, string Phase, string Message);

/// <summary>
/// The phases of a boss as states and transitions (ТЗ ч. 6, 8; docs/DECISIONS_6.md §102): their IDs, the check (an unreachable
/// phase, a ring without a way out, transitions that fire in one tick with one priority, skills that do not exist or are not
/// allowed), the scheme for the editor and «прогон фазы» — a battle on an empty field starting in that phase, with a fixed seed.
/// </summary>
public static class BossPhases
{
    /// <summary>«До первой фазы» in «из фаз».</summary>
    public const string Start = "start";
    public const string Any = "*";

    /// <summary>The ID of a phase: its own or its number from 1.</summary>
    public static string IdOf(BossDef b, int i) => i >= 0 && i < b.Phases.Count && b.Phases[i].Id != "" ? b.Phases[i].Id : (i + 1).ToString();

    /// <summary>The phases a transition into phase i may come from («start» — before the first).</summary>
    public static List<string> Sources(BossDef b, int i)
    {
        var ph = b.Phases[i];
        if (ph.From.Count == 0) return new List<string> { i == 0 ? Start : IdOf(b, i - 1) };
        if (ph.From.Contains(Any)) return new[] { Start }.Concat(Enumerable.Range(0, b.Phases.Count).Where(j => j != i).Select(j => IdOf(b, j))).ToList();
        return ph.From.Distinct().ToList();
    }

    /// <summary>The check of the phases (ТЗ 8.6).</summary>
    public static List<PhaseProblem> Check(ContentDatabase db, BossDef b)
    {
        var list = new List<PhaseProblem>();
        if (!b.IsBoss || b.Phases.Count == 0) return list;
        var ids = Enumerable.Range(0, b.Phases.Count).Select(i => IdOf(b, i)).ToList();
        foreach (var dup in ids.GroupBy(x => x).Where(g => g.Count() > 1))
            list.Add(new(true, dup.Key, $"ID фазы «{dup.Key}» повторяется"));
        if (ids.Contains(Start) || ids.Contains(Any)) list.Add(new(true, Start, "ID фазы не может быть «start» или «*»"));
        var known = new HashSet<string>(ids) { Start, Any };
        for (int i = 0; i < b.Phases.Count; i++)
            foreach (var f in b.Phases[i].From.Where(f => !known.Contains(f)))
                list.Add(new(true, ids[i], $"«{b.Phases[i].Name}»: переход из неизвестной фазы «{f}»"));

        // Reachable from the start.
        var edges = Enumerable.Range(0, b.Phases.Count).SelectMany(i => Sources(b, i).Select(src => (From: src, To: ids[i], Index: i))).ToList();
        var reached = new HashSet<string> { Start };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var e in edges)
                if (reached.Contains(e.From) && reached.Add(e.To))
                    grew = true;
        }
        for (int i = 0; i < b.Phases.Count; i++)
            if (!reached.Contains(ids[i]))
                list.Add(new(true, ids[i], $"фаза «{b.Phases[i].Name}» недостижима: в неё не ведёт ни один переход от начала боя"));

        // Rings without a way out: phases that pass to one another for ever (each may repeat) and lead nowhere else.
        foreach (var ring in Rings(b, ids, edges))
        {
            bool repeat = ring.All(id => b.Phases[ids.IndexOf(id)].CanRepeat);
            bool exit = edges.Any(e => ring.Contains(e.From) && !ring.Contains(e.To));
            if (repeat && !exit)
                list.Add(new(false, ring[0], "цикл фаз без выхода: " + string.Join(" → ", ring.Append(ring[0]).Select(id => $"«{b.Phases[ids.IndexOf(id)].Name}»")) +
                                             " — босс будет переходить по кругу до конца боя"));
        }

        // Transitions from the same phase with the same priority and the same kind of condition: they may fire in one tick.
        var groups = edges.GroupBy(e => e.From);
        foreach (var g in groups)
        {
            var arr = g.ToList();
            for (int x = 0; x < arr.Count; x++)
                for (int y = x + 1; y < arr.Count; y++)
                {
                    var a = b.Phases[arr[x].Index];
                    var c = b.Phases[arr[y].Index];
                    if (a.Priority != c.Priority || a.When.Type != c.When.Type || a.When.UnitTag != c.When.UnitTag) continue;
                    list.Add(new(false, arr[y].To, $"конфликт переходов из «{(g.Key == Start ? "начала" : g.Key)}»: «{a.Name}» и «{c.Name}» могут сработать в один тик " +
                                                   $"с одним приоритетом ({a.Priority}) — решит порядок списка; задайте приоритет"));
                }
        }

        // Skills (ТЗ 8.3): existing, and allowed explicitly.
        for (int i = 0; i < b.Phases.Count; i++)
            foreach (var act in b.Phases[i].Actions.Where(x => x.Type is TriggerActionType.AddSkill or TriggerActionType.SetAutoAttackSkill or TriggerActionType.RemoveSkill))
            {
                if (act.Skill == "") continue;
                if (db.Skill(act.Skill) == null)
                    list.Add(new(true, ids[i], $"«{b.Phases[i].Name}»: навыка «{act.Skill}» нет"));
                else if (act.Type != TriggerActionType.RemoveSkill && !b.AllowedSkills.Contains(act.Skill))
                    list.Add(new(false, ids[i], $"«{b.Phases[i].Name}»: навык «{act.Skill}» не в списке разрешённых навыков фаз"));
            }
        foreach (var s in b.AllowedSkills.Where(s => db.Skill(s) == null))
            list.Add(new(true, "", $"разрешённого навыка «{s}» нет"));
        return list;
    }

    /// <summary>The rings of the transitions (each once, from its smallest member).</summary>
    private static List<List<string>> Rings(BossDef b, List<string> ids, List<(string From, string To, int Index)> edges)
    {
        var next = ids.Distinct().ToDictionary(id => id, id => edges.Where(e => e.From == id).Select(e => e.To).Distinct().ToList());
        var rings = new List<List<string>>();
        var seen = new HashSet<string>();
        foreach (var start in next.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var path = new List<string>();
            void Walk(string id)
            {
                if (id == start && path.Count > 0)
                {
                    if (seen.Add(string.Join(">", path.OrderBy(x => x, StringComparer.Ordinal)))) rings.Add(path.ToList());
                    return;
                }
                if (path.Contains(id) || string.CompareOrdinal(id, start) < 0 || path.Count > 32 || !next.ContainsKey(id)) return;
                path.Add(id);
                foreach (var n in next[id]) Walk(n);
                path.RemoveAt(path.Count - 1);
            }
            Walk(start);
        }
        return rings;
    }

    /// <summary>
    /// The scheme of the phases as a graph to show (read-only): «Начало боя», a node per phase with its condition and rules,
    /// a link per transition. The same canvas as the graphs of events draws it.
    /// </summary>
    public static RuleGraphDef Scheme(BossDef b)
    {
        var g = new RuleGraphDef { Id = "phases", Name = "Фазы босса" };
        g.Nodes.Add(new RuleNodeDef { Id = Start, Kind = RuleNodeKind.Event, Label = "Начало боя", X = 0, Y = 0 });
        for (int i = 0; i < b.Phases.Count; i++)
        {
            var ph = b.Phases[i];
            var rules = new List<string>();
            if (ph.Priority != 0) rules.Add($"приоритет {ph.Priority}");
            if (ph.CanRepeat) rules.Add("может повториться");
            if (ph.ResetCooldowns) rules.Add("сброс перезарядок");
            if (ph.RefillAp) rules.Add("ОД полные");
            if (ph.Heal > 0) rules.Add($"лечение {ph.Heal:P0}");
            if (ph.ClearStatuses.Count > 0) rules.Add("снять: " + string.Join(", ", ph.ClearStatuses));
            if (ph.Actions.Count > 0) rules.Add($"действий {ph.Actions.Count}");
            g.Nodes.Add(new RuleNodeDef
            {
                Id = "p" + i, Kind = RuleNodeKind.Sequence, X = 260 * (i + 1), Y = i % 2 * 150,
                Label = $"{ph.Name} [{IdOf(b, i)}]\n{ph.When.Describe()}" + (rules.Count > 0 ? "\n" + string.Join(" · ", rules) : ""),
            });
        }
        var nodeOf = Enumerable.Range(0, b.Phases.Count).ToDictionary(i => IdOf(b, i), i => "p" + i);
        nodeOf[Start] = Start;
        for (int i = 0; i < b.Phases.Count; i++)
            foreach (var src in Sources(b, i))
                if (nodeOf.TryGetValue(src, out var from) && from != "p" + i)
                    g.Links.Add(new RuleLinkDef { From = from, To = "p" + i });
        return g;
    }

    /// <summary>
    /// «Прогон фазы»: a copy of the content with a field 10×7, the boss (with its phases) at the middle and two knights of the
    /// player next to it; the phase starts at the first tick (action «Сменить фазу босса»), the seed is the one given.
    /// </summary>
    public static (ContentDatabase Db, ScenarioDef Scenario) Sandbox(ContentDatabase db, UnitDef boss, int phase, ulong seed)
    {
        var test = db.Clone();
        var map = MapDef.CreateRhombus("phase_sandbox", 10, 7, "grass");
        map.Name = "Пустая карта (прогон фазы)";
        test.Table(DefCategory.Map).Replace(map);
        var sc = new ScenarioDef
        {
            Id = "phase_sandbox", Name = $"Прогон фазы {phase + 1} «{(phase >= 0 && phase < boss.Boss.Phases.Count ? boss.Boss.Phases[phase].Name : "")}» — {boss.DisplayName}",
            Map = map.Id, Seed = seed, Description = "Пустая карта: босс в выбранной фазе и два бойца. Контент проекта не меняется.",
            Victory = new List<ConditionDef> { new() { Type = ConditionType.AllEnemiesDead } },
            Defeat = new List<ConditionDef> { new() { Type = ConditionType.AllPlayerUnitsDead } },
            Units = { new ScenarioUnitDef { Unit = boss.Id, Faction = "enemy", C = new HexCoord(6, 3), Facing = HexDirection.W, Tag = "boss" } },
        };
        var fighter = db.Unit("knight") ?? db.All<UnitDef>().FirstOrDefault(u => u.Faction == "player");
        if (fighter != null)
        {
            // Next to the boss: the phase is seen in a fight at once.
            sc.Units.Add(new ScenarioUnitDef { Unit = fighter.Id, Faction = "player", C = new HexCoord(5, 3), Facing = HexDirection.E });
            sc.Units.Add(new ScenarioUnitDef { Unit = fighter.Id, Faction = "player", C = new HexCoord(5, 4), Facing = HexDirection.E });
        }
        if (phase >= 0)
            sc.Triggers.Add(new TriggerDef
            {
                Name = "Прогон: фаза", Event = ScenarioEventType.ScenarioStart, Once = true, Condition = new ConditionDef { Type = ConditionType.Always },
                Actions = { new TriggerActionDef { Type = TriggerActionType.SetBossPhase, Phase = phase + 1, Target = ActionTarget.UnitTag, UnitTag = "boss" } },
            });
        test.Table(DefCategory.Scenario).Replace(sc);
        return (test, sc);
    }
}
