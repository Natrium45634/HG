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
}
