using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>A rule of a category as it acts on a template: where it comes from and why it acts or not.</summary>
public sealed class ResolvedRule
{
    public CategoryRuleDef Rule { get; init; } = null!;
    public OriginDef From { get; init; } = null!;
    /// <summary>Depth in the tree: the root is 0, deeper is closer to the creature (stronger).</summary>
    public int Depth { get; init; }
    /// <summary>Order of the category on the template (the main one first).</summary>
    public int Order { get; init; }
    public bool Active { get; set; } = true;
    /// <summary>Why the rule does not act (overridden, excluded, yields to…) or how it acts (conditionally).</summary>
    public string Note { get; set; } = "";
    /// <summary>«Условно изменить»: the rule acting while the condition does not hold.</summary>
    public CategoryRuleDef? Otherwise { get; set; }
    public string Key { get; init; } = "";

    public override string ToString() => $"{Rule.Describe()} — «{From.DisplayName}»" + (Active ? "" : $" (не действует: {Note})");
}

/// <summary>Every category of a template with its ancestors and the rules that act (docs/DECISIONS_4.md §61).</summary>
public sealed class CategoryResolution
{
    public List<OriginDef> Assigned { get; } = new();
    /// <summary>The assigned categories and their ancestors (each once).</summary>
    public List<OriginDef> All { get; } = new();
    public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
    public List<ResolvedRule> Rules { get; } = new();
    public List<string> Problems { get; } = new();

    public IEnumerable<ResolvedRule> Acting => Rules.Where(r => r.Active);
    public IEnumerable<ResolvedRule> Of(CategoryRuleKind kind) => Acting.Where(r => r.Rule.Kind == kind);

    public IEnumerable<string> Tags => Of(CategoryRuleKind.Tag).Select(r => r.Rule.Tag).Where(t => t != "").Distinct();
    public IEnumerable<string> MoveCategories => Of(CategoryRuleKind.MoveCategory).Select(r => r.Rule.MoveCategory).Where(t => t != "").Distinct();
    public IEnumerable<ResolvedRule> Forbids => Of(CategoryRuleKind.Forbid);

    /// <summary>The AI profile by the strongest acting rule (priority, then depth).</summary>
    public string AiProfile => Of(CategoryRuleKind.AiProfile).OrderByDescending(r => r.Rule.Priority).ThenByDescending(r => r.Depth).ThenBy(r => r.Order)
        .Select(r => r.Rule.AiProfile).FirstOrDefault(p => p != "") ?? "";

    public double PowerBonus => Of(CategoryRuleKind.PowerBonus).Sum(r => r.Rule.Power);
}

public static class CategoryTree
{
    /// <summary>
    /// The category with all its ancestors, parents before children (ТЗ ч. 5, 13.2: several parents, a graph without
    /// cycles): the main parent's line first, then the others' in their order, each ancestor once; the category last. With
    /// one parent per category it is the old chain root → leaf. A cycle or a missing parent is reported and skipped.
    /// </summary>
    public static List<OriginDef> Chain(ContentDatabase db, OriginDef c, List<string>? problems = null)
    {
        var order = new List<OriginDef>();
        var state = new Dictionary<string, bool>(StringComparer.Ordinal); // false — on the way, true — done
        void Visit(OriginDef o)
        {
            state[o.Id] = false;
            foreach (var pid in o.ParentIds)
            {
                var p = db.Get<OriginDef>(pid);
                if (p == null)
                {
                    problems?.Add($"категория «{o.DisplayName}»: родитель «{pid}» не найден");
                    continue;
                }
                if (state.TryGetValue(p.Id, out var done))
                {
                    if (!done) problems?.Add($"категория «{c.DisplayName}»: цикл в дереве через «{p.DisplayName}»");
                    continue;
                }
                Visit(p);
            }
            state[o.Id] = true;
            order.Add(o);
        }
        Visit(c);
        return order;
    }

    /// <summary>Every ancestor of a category (by any parent), without itself.</summary>
    public static HashSet<string> Ancestors(ContentDatabase db, string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(id);
        while (stack.Count > 0)
            if (db.Get<OriginDef>(stack.Pop()) is { } o)
                foreach (var p in o.ParentIds)
                    if (seen.Add(p)) stack.Push(p);
        seen.Remove(id);
        return seen;
    }

    /// <summary>Would <paramref name="parent"/> as a parent of <paramref name="child"/> close a cycle? Then why.</summary>
    public static string? CycleWith(ContentDatabase db, OriginDef child, string parent)
    {
        if (parent == "") return null;
        if (parent == child.Id) return $"«{child.DisplayName}» не может быть родителем самой себя";
        if (!Ancestors(db, parent).Contains(child.Id)) return null;
        string name = db.Get<OriginDef>(parent)?.DisplayName ?? parent;
        return $"цикл: «{name}» уже наследует «{child.DisplayName}» — родитель отклонён";
    }

    /// <summary>The parents a category may take now (no cycle).</summary>
    public static IEnumerable<OriginDef> ParentChoices(ContentDatabase db, OriginDef child) =>
        db.All<OriginDef>().Where(o => CycleWith(db, child, o.Id) == null);

    /// <summary>
    /// Sets the parents if they make no cycle (T05: the attempt to close a cycle is rejected); false — nothing changed,
    /// <paramref name="why"/> says which parent closes it.
    /// </summary>
    public static bool TrySetParents(ContentDatabase db, OriginDef child, string parent, IEnumerable<string> extra, out string why)
    {
        var extras = extra.Where(x => x != "").Distinct(StringComparer.Ordinal).ToList();
        // A cycle through the child needs a parent that already reaches the child: each is checked on its own.
        foreach (var p in extras.Prepend(parent))
            if (CycleWith(db, child, p) is { } cycle)
            {
                why = cycle;
                return false;
            }
        child.Parent = parent;
        child.ExtraParents = extras.Where(x => x != parent).ToList();
        why = "";
        return true;
    }

    /// <summary>The categories of a template: the main one, the additional ones (origins), those assigned by tags.</summary>
    public static List<OriginDef> AssignedOf(ContentDatabase db, UnitDef def, string unitTag = "")
    {
        var list = new List<OriginDef>();
        void Add(OriginDef? o)
        {
            if (o != null && !list.Contains(o)) list.Add(o);
        }
        if (def.MainCategory != "") Add(db.Get<OriginDef>(def.MainCategory));
        foreach (var id in def.Origins) Add(db.Get<OriginDef>(id));
        foreach (var o in db.All<OriginDef>())
            if (o.AutoTags.Count > 0 && o.AutoTags.Any(t => def.Tags.Contains(t) || (unitTag != "" && unitTag == t)))
                Add(o);
        return list;
    }

    public static CategoryResolution Resolve(ContentDatabase db, UnitDef def, string unitTag = "")
    {
        var res = new CategoryResolution();
        res.Assigned.AddRange(AssignedOf(db, def, unitTag));
        var all = new List<ResolvedRule>();
        for (int order = 0; order < res.Assigned.Count; order++)
        {
            var chain = Chain(db, res.Assigned[order], res.Problems);
            foreach (var c in chain)
                if (res.Ids.Add(c.Id))
                    res.All.Add(c);
            all.AddRange(ResolveChain(db, chain, order, res.Problems));
        }
        // The same rule reached through two chains (a shared ancestor) acts once.
        var unique = new List<ResolvedRule>();
        foreach (var r in all)
        {
            var same = unique.FirstOrDefault(x => x.Rule == r.Rule && x.From == r.From);
            if (same == null) unique.Add(r);
            // Overridden in one chain only: the copy that still acts competes by key below (the deeper rule wins there).
            else if (r.Active && !same.Active) unique[unique.IndexOf(same)] = r;
        }
        // One key from different additional categories: priority, then depth, then the order on the template.
        foreach (var g in unique.Where(r => r.Active).GroupBy(r => r.Key).Where(g => g.Count() > 1))
        {
            var ordered = g.OrderByDescending(r => r.Rule.Priority).ThenByDescending(r => r.Depth).ThenBy(r => r.Order).ToList();
            foreach (var loser in ordered.Skip(1))
            {
                if (loser.Rule.Kind != ordered[0].Rule.Kind || loser.Rule == ordered[0].Rule) continue;
                loser.Active = false;
                loser.Note = $"уступает правилу «{ordered[0].From.DisplayName}» (приоритет, глубина, порядок)";
            }
        }
        // A forbid and an allow of one action: the stronger wins; equal priority — a warning, then depth and order.
        var acting = unique.Where(r => r.Active && r.Rule.Kind is CategoryRuleKind.Forbid or CategoryRuleKind.Allow).ToList();
        foreach (var g in acting.GroupBy(r => r.Rule.ActionKey))
        {
            var forbids = g.Where(r => r.Rule.Kind == CategoryRuleKind.Forbid).ToList();
            var allows = g.Where(r => r.Rule.Kind == CategoryRuleKind.Allow).ToList();
            if (forbids.Count == 0 || allows.Count == 0) continue;
            var best = g.OrderByDescending(r => r.Rule.Priority).ThenByDescending(r => r.Depth).ThenBy(r => r.Order).First();
            int top = best.Rule.Priority;
            if (forbids.Any(f => f.Rule.Priority == top) && allows.Any(a => a.Rule.Priority == top))
                res.Problems.Add($"противоречие: запрет и разрешение «{best.Rule.Describe().Split(": ").Last()}» с одинаковым приоритетом — действует «{best.From.DisplayName}» ({(best.Rule.Kind == CategoryRuleKind.Forbid ? "запрет" : "разрешение")}, ближе к существу или раньше); задайте приоритет или режим");
            foreach (var r in g.Where(r => r.Rule.Kind != best.Rule.Kind))
            {
                r.Active = false;
                r.Note = $"{(best.Rule.Kind == CategoryRuleKind.Forbid ? "запрет" : "разрешение")} из «{best.From.DisplayName}» сильнее";
            }
        }
        res.Rules.AddRange(unique);
        return res;
    }

    /// <summary>One chain root → leaf: the modes decide what the rule with the same ID becomes.</summary>
    private static List<ResolvedRule> ResolveChain(ContentDatabase db, List<OriginDef> chain, int order, List<string> problems)
    {
        var list = new List<ResolvedRule>();
        var byKey = new Dictionary<string, ResolvedRule>(StringComparer.Ordinal);
        int auto = 0;
        for (int depth = 0; depth < chain.Count; depth++)
        {
            var c = chain[depth];
            // Passives of part 3 are rules «Дополнить» with their own keys.
            var rules = c.Passives.Select(p => new CategoryRuleDef { Id = "passive:" + p, Kind = CategoryRuleKind.Passive, Passive = p, Mode = CategoryInherit.Add }).Concat(c.Rules);
            foreach (var rule in rules)
            {
                string key = rule.Id != "" ? rule.Id : $"#{c.Id}#{auto++}";
                byKey.TryGetValue(key, out var parent);
                // «Дополнить» keeps its own key: both rules act and do not compete below.
                var rr = new ResolvedRule { Rule = rule, From = c, Depth = depth, Order = order, Key = rule.Mode == CategoryInherit.Add && parent != null ? key + "+" + c.Id : key };
                switch (rule.Mode)
                {
                    case CategoryInherit.Inherit when parent != null && parent.From != c:
                        rr.Active = false;
                        rr.Note = $"совпадает по ID с правилом «{parent.From.DisplayName}» — при «наследовать» действует родительское";
                        problems.Add($"«{c.DisplayName}»: правило «{rule.Id}» с режимом «наследовать» совпадает с правилом «{parent.From.DisplayName}» — выберите «дополнить», «переопределить» или «исключить»");
                        list.Add(rr);
                        continue;
                    case CategoryInherit.Add when parent != null:
                        // Both act: the child's under its own key so later modes still find the parent's.
                        list.Add(rr);
                        byKey[key + "+" + c.Id] = rr;
                        continue;
                    case CategoryInherit.Override when parent != null:
                    case CategoryInherit.Conditional when parent != null:
                        parent.Active = false;
                        parent.Note = rule.Mode == CategoryInherit.Override ? $"переопределено в «{c.DisplayName}»" : $"условно заменено в «{c.DisplayName}»";
                        if (rule.Mode == CategoryInherit.Conditional)
                        {
                            rr.Otherwise = parent.Rule;
                            rr.Note = "условно: " + (rule.Condition?.Describe() ?? "всегда") + $", иначе — правило «{parent.From.DisplayName}»";
                        }
                        break;
                    case CategoryInherit.Exclude:
                        rr.Active = false;
                        rr.Note = "исключает правило родителя";
                        if (parent != null)
                        {
                            parent.Active = false;
                            parent.Note = $"исключено в «{c.DisplayName}»";
                        }
                        else problems.Add($"«{c.DisplayName}»: правило «{rule.Id}» исключает то, чего нет у родителей");
                        list.Add(rr);
                        byKey.Remove(key);
                        continue;
                    case CategoryInherit.Override:
                    case CategoryInherit.Conditional:
                        if (rule.Mode == CategoryInherit.Conditional) rr.Note = "условно: " + (rule.Condition?.Describe() ?? "всегда");
                        break;
                }
                list.Add(rr);
                byKey[key] = rr;
            }
        }
        return list;
    }

    /// <summary>The tags of a template with those its categories give (filters of budgets and libraries).</summary>
    public static HashSet<string> TagsOf(ContentDatabase db, UnitDef def)
    {
        var tags = new HashSet<string>(def.Tags, StringComparer.Ordinal);
        foreach (var t in Resolve(db, def).Tags) tags.Add(t);
        return tags;
    }

    /// <summary>
    /// A template matches a type: its ID, creature category, a category of the tree (ancestors included), «tag:x»
    /// with the tags of the categories, «cat:x».
    /// </summary>
    public static bool Matches(ContentDatabase db, UnitDef def, string type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        if (type == def.Id || type == def.CreatureCategory) return true;
        var res = Resolve(db, def);
        if (type.StartsWith("tag:", StringComparison.Ordinal)) return def.Tags.Contains(type[4..]) || res.Tags.Contains(type[4..]);
        if (type.StartsWith("cat:", StringComparison.Ordinal)) return res.Ids.Contains(type[4..]);
        return res.Ids.Contains(type);
    }
}
