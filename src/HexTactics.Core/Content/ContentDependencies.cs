using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>A reference of a definition to another one (path inside the owner, pinned version if any).</summary>
public sealed record DependencyInfo(DefCategory Category, string Reference, string Path)
{
    public string Id => ContentRef.IdOf(Reference);
    public int? PinnedVersion => ContentRef.Parse(Reference).Version;
}

/// <summary>
/// Dependencies between definitions (docs/DECISIONS_2.md §31): «использует» (what a definition refers
/// to, transitively) and «используется в» (who refers to it). Used by the library view, the
/// incomplete-draft check and the package export.
/// </summary>
public static class ContentDependencies
{
    /// <summary>Direct references of a definition (tags and movement categories are not definitions).</summary>
    public static List<DependencyInfo> Uses(Def d)
    {
        var list = new List<DependencyInfo>();
        ReferenceWalker.WalkDef(d, (owner, path, cat, value, _, attr) =>
        {
            if (string.IsNullOrEmpty(value) || cat == DefCategory.MoveCategory) return;
            if (attr != null && attr.AllowTags && value.StartsWith("tag:", StringComparison.Ordinal)) return;
            if (owner == d && cat == d.Category && value == d.Id && path == d.Id + ".Id") return;
            list.Add(new DependencyInfo(cat, value, path));
        });
        return list;
    }

    /// <summary>Distinct definitions directly used by a definition (resolved, pinned versions included).</summary>
    public static List<Def> UsedDefs(ContentDatabase db, Def d)
    {
        var result = new List<Def>();
        foreach (var dep in Uses(d))
        {
            var target = db.Resolve(dep.Category, dep.Reference);
            if (target != null && target != d && !result.Contains(target)) result.Add(target);
        }
        return result;
    }

    /// <summary>Everything the roots need, transitively (the roots included), in a stable order.</summary>
    public static List<Def> Closure(ContentDatabase db, IEnumerable<Def> roots)
    {
        var seen = new HashSet<Def>();
        var order = new List<Def>();
        var queue = new Queue<Def>();
        foreach (var r in roots)
            if (seen.Add(r))
            {
                order.Add(r);
                queue.Enqueue(r);
            }
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            foreach (var u in UsedDefs(db, d))
                if (seen.Add(u))
                {
                    order.Add(u);
                    queue.Enqueue(u);
                }
        }
        return order;
    }

    /// <summary>References that cannot be resolved (missing objects or missing pinned versions).</summary>
    public static List<DependencyInfo> Missing(ContentDatabase db, Def d) =>
        Uses(d).Where(dep => !db.Exists(dep.Category, dep.Reference)).ToList();

    /// <summary>Definitions that refer to the given one (directly).</summary>
    public static List<Def> UsedBy(ContentDatabase db, Def d) =>
        db.FindReferences(d.Category, d.Id).Where(r => r.Owner != null && r.Owner != d).Select(r => r.Owner!).Distinct().ToList();

    /// <summary>
    /// What the references to an object amount to, by kind (ТЗ ч. 5, 3.5): «используется в 3 волнах (2 сценария),
    /// 1 пассиве и 2 юнитах». Waves are counted by wave, other owners by object.
    /// </summary>
    public static string Summary(IReadOnlyCollection<ReferenceInfo> refs)
    {
        if (refs.Count == 0) return "нигде не используется";
        var parts = new List<string>();
        var waves = refs.Where(r => r.Owner is ScenarioDef && r.Path.Contains(".Waves[", StringComparison.Ordinal))
            .Select(r => (Sc: r.Owner!.Id, Wave: WaveOf(r.Path))).Distinct().ToList();
        if (waves.Count > 0)
        {
            int scen = waves.Select(w => w.Sc).Distinct().Count();
            parts.Add($"{waves.Count} {Plural(waves.Count, "волне", "волнах")} ({scen} {Plural(scen, "сценарий", "сценария", "сценариев")})");
        }
        var others = refs.Where(r => !(r.Owner is ScenarioDef && r.Path.Contains(".Waves[", StringComparison.Ordinal)))
            .GroupBy(r => r.Owner?.Category).OrderBy(g => g.Key.HasValue ? (int)g.Key.Value : -1);
        foreach (var g in others)
        {
            int n = g.Select(r => r.Owner?.Id ?? "rules").Distinct().Count();
            parts.Add(g.Key switch
            {
                null => "правилах боя",
                DefCategory c => $"{n} {Locative(c, n)}",
            });
        }
        return "используется в " + (parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " и " + parts[^1]);
    }

    private static string WaveOf(string path)
    {
        int i = path.IndexOf(".Waves[", StringComparison.Ordinal);
        int j = path.IndexOf(']', i);
        return j > i ? path[i..(j + 1)] : path;
    }

    private static string Plural(int n, string one, string many) => n % 10 == 1 && n % 100 != 11 ? one : many;

    private static string Plural(int n, string one, string few, string many) =>
        n % 10 == 1 && n % 100 != 11 ? one : n % 10 is >= 2 and <= 4 && (n % 100 < 12 || n % 100 > 14) ? few : many;

    private static string Locative(DefCategory c, int n)
    {
        var (one, many) = c switch
        {
            DefCategory.Unit => ("юните", "юнитах"),
            DefCategory.Skill => ("навыке", "навыках"),
            DefCategory.Passive => ("пассиве", "пассивах"),
            DefCategory.Origin => ("категории существ", "категориях существ"),
            DefCategory.Scenario => ("сценарии", "сценариях"),
            DefCategory.Map => ("карте", "картах"),
            DefCategory.MapTemplate => ("шаблоне карты", "шаблонах карт"),
            DefCategory.Status => ("состоянии", "состояниях"),
            DefCategory.Element => ("элементе", "элементах"),
            DefCategory.DamageType => ("типе урона", "типах урона"),
            DefCategory.Terrain => ("типе местности", "типах местности"),
            DefCategory.CellEffect => ("эффекте клетки", "эффектах клеток"),
            DefCategory.Synergy => ("синергии", "синергиях"),
            DefCategory.GroupPreset => ("пресете группы", "пресетах групп"),
            DefCategory.GameRules => ("правилах режима", "правилах режимов"),
            DefCategory.SquadPreset => ("пресете отряда", "пресетах отряда"),
            DefCategory.ZonePreset => ("пресете зоны", "пресетах зон"),
            DefCategory.ModifierSet => ("наборе модификаторов", "наборах модификаторов"),
            DefCategory.DifficultyVariant => ("варианте сценария", "вариантах сценариев"),
            DefCategory.ObjectPreset => ("пресете объекта", "пресетах объектов"),
            DefCategory.Campaign => ("кампании", "кампаниях"),
            DefCategory.RewardSet => ("наборе наград", "наборах наград"),
            DefCategory.Faction => ("фракции", "фракциях"),
            DefCategory.AiProfile => ("профиле ИИ", "профилях ИИ"),
            DefCategory.Asset => ("ресурсе", "ресурсах"),
            DefCategory.Vfx => ("визуальном эффекте", "визуальных эффектах"),
            _ => ("объекте «" + c + "»", "объектах «" + c + "»"),
        };
        return Plural(n, one, many);
    }
}
