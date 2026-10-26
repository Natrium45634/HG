using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>A place a template, a category or a route is used: the scenario, the wave, the group.</summary>
public sealed record UsagePlace(ScenarioDef Scenario, int Wave, string Group, string Text);

/// <summary>
/// What a change touches (docs/DECISIONS_4.md §71, TZ §19.3): a category — its descendant categories and the templates
/// whose chains go through it; a route — the groups and budgets of waves that walk it; a template — the groups,
/// waves, units and placements of the scenarios that use it.
/// </summary>
public static class ChangeImpact
{
    /// <summary>The categories below <paramref name="id"/> (any depth) and the templates whose categories go through it.</summary>
    public static (List<OriginDef> Children, List<UnitDef> Templates) OfCategory(ContentDatabase db, string id)
    {
        var children = new List<OriginDef>();
        var frontier = new Queue<string>();
        frontier.Enqueue(id);
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        while (frontier.Count > 0)
        {
            var cur = frontier.Dequeue();
            foreach (var c in db.All<OriginDef>().Where(o => o.ParentIds.Contains(cur) && seen.Add(o.Id)))
            {
                children.Add(c);
                frontier.Enqueue(c.Id);
            }
        }
        var templates = db.All<UnitDef>().Where(u => CategoryTree.AssignedOf(db, u).Any(a => seen.Contains(a.Id) || CategoryTree.Chain(db, a).Any(x => x.Id == id))).ToList();
        return (children, templates);
    }

    /// <summary>The groups and the budgets that walk a route of the scenario.</summary>
    public static List<UsagePlace> OfRoute(ScenarioDef sc, string routeId)
    {
        var list = new List<UsagePlace>();
        for (int wi = 0; wi < sc.Waves.Count; wi++)
        {
            var w = sc.Waves[wi];
            foreach (var g in w.Groups.Where(g => g.Route == routeId || g.AltRoutes.Contains(routeId)))
                list.Add(new UsagePlace(sc, wi, g.Id, $"волна «{w.Name}», группа «{(g.Name != "" ? g.Name : g.Unit)}»" + (g.Route == routeId ? "" : " (другой маршрут)")));
            if (w.Budget.Enabled && w.Budget.Route == routeId) list.Add(new UsagePlace(sc, wi, "", $"волна «{w.Name}», подбор по бюджету"));
            if (w.Route == routeId) list.Add(new UsagePlace(sc, wi, "", $"волна «{w.Name}» (маршрут волны)"));
        }
        foreach (var r in sc.Routes.Where(r => r.Fallback == routeId)) list.Add(new UsagePlace(sc, -1, "", $"запасной для маршрута «{r.DisplayName}»"));
        return list;
    }

    /// <summary>Every use of a template in the scenarios (live or pinned «id@N» references).</summary>
    public static List<UsagePlace> OfTemplate(ContentDatabase db, string unitId)
    {
        var list = new List<UsagePlace>();
        foreach (var sc in db.All<ScenarioDef>())
        {
            for (int wi = 0; wi < sc.Waves.Count; wi++)
            {
                var w = sc.Waves[wi];
                foreach (var g in w.Groups.Where(g => ContentRef.IdOf(g.Unit) == unitId))
                    list.Add(new UsagePlace(sc, wi, g.Id, $"«{sc.DisplayName}»: волна «{w.Name}», группа «{(g.Name != "" ? g.Name : g.Id)}» ×{g.Count}" +
                                                          (ContentRef.Parse(g.Unit).Version is { } v ? $" (закреплена v{v})" : "") +
                                                          (GroupOverrides.Has(g) ? " (есть переопределения)" : "")));
                if (w.Units.Any(u => ContentRef.IdOf(u.Unit) == unitId)) list.Add(new UsagePlace(sc, wi, "", $"«{sc.DisplayName}»: волна «{w.Name}»"));
            }
            int units = sc.Units.Count(u => ContentRef.IdOf(u.Unit) == unitId);
            if (units > 0) list.Add(new UsagePlace(sc, -1, "", $"«{sc.DisplayName}»: юнитов сценария ×{units}"));
            if (sc.Placement.Units.Any(u => ContentRef.IdOf(u.Unit) == unitId) || sc.Placement.Preset.Any(u => ContentRef.IdOf(u.Unit) == unitId))
                list.Add(new UsagePlace(sc, -1, "", $"«{sc.DisplayName}»: расстановка"));
        }
        return list;
    }
}
