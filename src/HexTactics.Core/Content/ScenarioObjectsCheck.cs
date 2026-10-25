using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>A problem of a scenario object, route or group, with the hex it is about (the editor shows it on the map).</summary>
public sealed record ScenarioProblem(IssueSeverity Severity, string Path, string Message, HexCoord? At = null, string Object = "");

/// <summary>
/// Checks of the TD part of a scenario (docs/DECISIONS_4.md §56–58): objects (IDs, occupied area, overlaps), routes
/// (start at a spawn base, contiguous passable hexes for the groups' movement, end at the assigned target) and groups
/// (template, base, route). Severity: error — the scenario does not start; warning — it starts but may behave not
/// as intended; information — a hint. Used by the content validator and live by the editor while a route is drawn.
/// </summary>
public static class ScenarioObjectsCheck
{
    public static List<ScenarioProblem> All(ContentDatabase db, ScenarioDef sc, MapDef map)
    {
        ScenarioObjects.ResolveMarkers(sc, map);
        var list = new List<ScenarioProblem>();
        list.AddRange(Objects(db, sc, map));
        foreach (var id in sc.Routes.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key))
            list.Add(new(IssueSeverity.Error, "Routes", $"ID маршрута «{id}» повторяется"));
        foreach (var r in sc.Routes) list.AddRange(Route(db, sc, map, r));
        list.AddRange(Groups(db, sc, map));
        return list;
    }

    private static Dictionary<HexCoord, MapCellDef> CellsOf(MapDef map)
    {
        var d = new Dictionary<HexCoord, MapCellDef>();
        foreach (var c in map.Cells) d[c.C] = c;
        return d;
    }

    public static List<ScenarioProblem> Objects(ContentDatabase db, ScenarioDef sc, MapDef map)
    {
        var list = new List<ScenarioProblem>();
        var cells = CellsOf(map);
        foreach (var g in sc.Objects.GroupBy(o => o.Id).Where(g => g.Count() > 1))
            list.Add(new(IssueSeverity.Error, "Objects", $"ID объекта «{g.Key}» повторяется", g.First().Anchor, g.Key));
        var taken = new Dictionary<HexCoord, ScenarioObjectDef>();
        foreach (var o in sc.Objects)
        {
            if (string.IsNullOrWhiteSpace(o.Id)) list.Add(new(IssueSeverity.Error, "Objects", $"У объекта «{o.DisplayName}» нет ID", o.Anchor));
            foreach (var c in o.Cells())
            {
                if (!cells.TryGetValue(c, out var cell))
                {
                    list.Add(new(IssueSeverity.Error, "Objects", $"«{o.DisplayName}»: гекс {c} вне карты", c, o.Id));
                    continue;
                }
                var t = db.Terrain(cell.T);
                if (o.Health != null && t != null && (!t.IsPassableFor("walk") || cell.Blocked == true))
                    list.Add(new(IssueSeverity.Warning, "Objects", $"«{o.DisplayName}» стоит на непроходимом гексе {c} ({t.DisplayName})", c, o.Id));
                // Structures take their whole area; other objects (points, zones) may lie on it.
                if (o.Health == null) continue;
                if (taken.TryGetValue(c, out var other))
                    list.Add(new(IssueSeverity.Error, "Objects", $"«{o.DisplayName}» и «{other.DisplayName}» занимают один гекс {c}", c, o.Id));
                else taken[c] = o;
                if (sc.Units.FirstOrDefault(u => u.C == c) is { } unit)
                    list.Add(new(IssueSeverity.Error, "Objects", $"На гексе {c} объекта «{o.DisplayName}» стоит юнит «{unit.Unit}»", c, o.Id));
            }
            if (o.Type == ScenarioObjectType.SpawnBase && o.Spawn == null)
                list.Add(new(IssueSeverity.Warning, "Objects", $"«{o.DisplayName}»: база спавна без компонента «Спавн» — враги из неё не появятся", o.Anchor, o.Id));
            if (o.Spawn != null && o.Health is { Destructible: true } && o.Spawn.OnDestroyed == SpawnerDestroyedPolicy.ContinueQueue)
                list.Add(new(IssueSeverity.Info, "Objects", $"«{o.DisplayName}»: после разрушения запланированные враги продолжат появляться на её месте — " +
                    "так задумано? Иначе выберите «остановить» или «докончить начатую порцию»", o.Anchor, o.Id));
            if (o.Spawn != null && !sc.Waves.Any(w => w.Groups.Any(g => g.SpawnBase == o.Id)))
                list.Add(new(IssueSeverity.Info, "Objects", $"Из «{o.DisplayName}» не появляется ни одна группа", o.Anchor, o.Id));
        }
        var bases = sc.Objects.Where(o => o.Type == ScenarioObjectType.PlayerBase && o.Health != null).ToList();
        var bd = sc.BaseDefeat;
        if (bd.Mode == BaseDefeatMode.MainBase && bases.Count > 0)
        {
            if (bd.MainBase != "" && bases.All(b => b.Id != bd.MainBase))
                list.Add(new(IssueSeverity.Error, "BaseDefeat", $"Главная база «{bd.MainBase}» не найдена среди баз игрока"));
            if (bd.MainBase == "" && bases.Count(b => b.Health!.Main) > 1)
                list.Add(new(IssueSeverity.Warning, "BaseDefeat", "Отмечено несколько главных баз — учитывается первая"));
        }
        if (bd.Mode == BaseDefeatMode.LostCount && bases.Count > 0 && bd.Count > bases.Count)
            list.Add(new(IssueSeverity.Warning, "BaseDefeat", $"Поражение при потере {bd.Count} баз недостижимо: баз {bases.Count}"));
        return list;
    }

    /// <summary>Movement categories of the groups that use a route (walk if none).</summary>
    public static List<string> CategoriesOf(ContentDatabase db, ScenarioDef sc, RouteDef r)
    {
        var cats = sc.Waves.SelectMany(w => w.Groups).Where(g => g.Route == r.Id).Select(g => db.Unit(g.Unit)?.MoveCategory ?? "walk").Distinct().ToList();
        if (cats.Count == 0) cats.Add("walk");
        return cats;
    }

    /// <summary>Hexes a spawn base lets enemies appear on (its cells and its area).</summary>
    public static IEnumerable<HexCoord> SpawnArea(ScenarioObjectDef b)
    {
        var cells = b.Cells();
        foreach (var c in cells) yield return c;
        int r = b.Spawn?.AreaRadius ?? 0;
        if (r > 0)
            foreach (var c in HexCoord.Spiral(b.Anchor, r))
                if (!cells.Contains(c))
                    yield return c;
    }

    private static int DistanceTo(HexCoord c, ScenarioObjectDef o) => o.Cells().Min(x => HexCoord.Distance(c, x));

    public static List<ScenarioProblem> Route(ContentDatabase db, ScenarioDef sc, MapDef map, RouteDef r, IReadOnlyList<string>? categories = null)
    {
        var list = new List<ScenarioProblem>();
        var cells = CellsOf(map);
        string name = $"Маршрут «{r.DisplayName}»";
        var path = r.MainPath();
        if (path.Count == 0)
        {
            list.Add(new(IssueSeverity.Error, "Routes", $"{name} пуст — проведите линию от базы спавна"));
            return list;
        }

        // Start: a working spawn base, the line starting on its area or next to it.
        var spawn = sc.Object(r.Spawn);
        if (r.Spawn == "") list.Add(new(IssueSeverity.Error, "Routes", $"{name}: не указана база спавна", path[0], r.Id));
        else if (spawn == null) list.Add(new(IssueSeverity.Error, "Routes", $"{name}: база спавна «{r.Spawn}» не найдена", path[0], r.Id));
        else if (spawn.Spawn == null) list.Add(new(IssueSeverity.Error, "Routes", $"{name}: «{spawn.DisplayName}» не база спавна (нет компонента «Спавн»)", path[0], r.Id));
        else if (!SpawnArea(spawn).Any(c => HexCoord.Distance(c, path[0]) <= 1))
            list.Add(new(IssueSeverity.Error, "Routes", $"{name} начинается в {path[0]}, далеко от базы спавна «{spawn.DisplayName}» — начните линию у неё", path[0], r.Id));

        // The hexes of every edge: contiguous from its node, on the map, passable for every group that uses the
        // route, not through other structures. A hex shared by edges (a merge) is checked once per edge.
        var cats = categories ?? CategoriesOf(db, sc, r);
        var structures = sc.Objects.Where(o => o.Health != null && o.Id != r.Target).ToList();
        var lines = r.Edges.Count > 0 ? r.Edges.Select(e => (Edge: e, Path: r.EdgePath(e))).ToList() : new() { (Edge: (RouteEdgeDef)null!, Path: path) };
        var reported = new HashSet<HexCoord>();
        foreach (var (edge, line) in lines)
        {
            for (int i = 0; i < line.Count; i++)
            {
                var c = line[i];
                if (i > 0 && HexCoord.Distance(line[i - 1], c) != 1)
                {
                    list.Add(new(IssueSeverity.Error, "Routes", $"{name}: разрыв между {line[i - 1]} и {c} — гексы маршрута должны быть соседними", c, r.Id));
                    continue;
                }
                if (!reported.Add(c) && i > 0) continue;
                if (!cells.TryGetValue(c, out var cell))
                {
                    list.Add(new(IssueSeverity.Error, "Routes", $"{name}: гекс {c} вне карты", c, r.Id));
                    continue;
                }
                foreach (var cat in cats)
                {
                    bool ok = i == 0 ? MapAnalysis.IsWalkable(db, cell, cat) : cells.TryGetValue(line[i - 1], out var prev) && MapAnalysis.CanStep(db, prev, cell, cat);
                    if (!ok)
                    {
                        string why = !MapAnalysis.IsWalkable(db, cell, cat) ? $"непроходим для «{cat}» ({db.Terrain(cell.T)?.DisplayName ?? cell.T})" : "слишком большой перепад высоты";
                        list.Add(new(IssueSeverity.Error, "Routes", $"{name}: гекс {c} {why}", c, r.Id));
                        break;
                    }
                }
                if (structures.FirstOrDefault(o => o.Occupies(c)) is { } s)
                    list.Add(new(IssueSeverity.Error, "Routes", $"{name} проходит через «{s.DisplayName}» в {c}", c, r.Id));
            }
            if (edge != null && r.Node(edge.To) is { } to && edge.Cells.Count > 0 && edge.Cells[^1] != to.C)
                list.Add(new(IssueSeverity.Error, "Routes", $"{name}: ребро {edge.Id} не доходит до узла {to.Id} {to.C}", edge.Cells[^1], r.Id));
        }
        foreach (var c in path.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).Take(3))
            if (!r.Edges.Any(e => e.Loop != null))
                list.Add(new(IssueSeverity.Warning, "Routes", $"{name} проходит гекс {c} дважды — для петли отметьте ребро петлёй", c, r.Id));
        list.AddRange(GraphProblems(sc, r, name));

        // End: the assigned target (or an explicit end rule).
        if (r.End == RouteEndKind.Target)
        {
            var sev = r.AllowIncomplete ? IssueSeverity.Warning : IssueSeverity.Error;
            var target = sc.Object(r.Target);
            if (r.Target == "")
                list.Add(new(sev, "Routes", $"{name} не завершён: не назначена цель (база игрока или другой объект)", path[^1], r.Id));
            else if (target == null)
                list.Add(new(IssueSeverity.Error, "Routes", $"{name}: цель «{r.Target}» не найдена", path[^1], r.Id));
            else
            {
                int d = DistanceTo(path[^1], target);
                if (d > 1)
                    list.Add(new(sev, "Routes", $"{name} не доходит до цели «{target.DisplayName}»: от конца {path[^1]} ещё {d - 1} гекс(ов) — продлите линию до базы", path[^1], r.Id));
            }
        }
        else list.Add(new(IssueSeverity.Info, "Routes", $"{name}: {(r.End == RouteEndKind.Patrol ? "бесконечный патруль" : "враги останавливаются в конце")} — цель не требуется", path[^1], r.Id));
        if (!sc.Waves.Any(w => w.Groups.Any(g => g.Route == r.Id)))
            list.Add(new(IssueSeverity.Info, "Routes", $"{name} не назначен ни одной группе", path[0], r.Id));
        return list;
    }

    /// <summary>
    /// The graph of a route (docs/DECISIONS_4.md §57): edges between existing nodes, every node reachable, no dead
    /// end before the target, forks with a working rule, loops with an exit (or an explicit patrol), the fallback
    /// route exists.
    /// </summary>
    public static List<ScenarioProblem> GraphProblems(ScenarioDef sc, RouteDef r, string name)
    {
        var list = new List<ScenarioProblem>();
        if (r.Nodes.Count == 0) return list;
        foreach (var id in r.Nodes.GroupBy(n => n.Id).Where(g => g.Count() > 1).Select(g => g.Key))
            list.Add(new(IssueSeverity.Error, "Routes", $"{name}: ID узла «{id}» повторяется", null, r.Id));
        foreach (var e in r.Edges)
            if (r.Node(e.From) == null || r.Node(e.To) == null)
                list.Add(new(IssueSeverity.Error, "Routes", $"{name}: ребро {e.Id} ссылается на несуществующий узел", e.Cells.FirstOrDefault(), r.Id));
        var reach = r.Reachable();
        foreach (var n in r.Nodes.Where(n => !reach.Contains(n.Id)))
            list.Add(new(IssueSeverity.Warning, "Routes", $"{name}: узел {(n.Name != "" ? n.Name : n.Id)} {n.C} недостижим от начала", n.C, r.Id));
        var target = sc.Object(r.Target);
        foreach (var n in r.Nodes.Where(n => reach.Contains(n.Id)))
        {
            var outs = r.Out(n.Id).ToList();
            string nn = n.Name != "" ? n.Name : n.Id;
            if (outs.Count == 0 && r.End == RouteEndKind.Target && target != null && n.C != r.MainPath().LastOrDefault()
                && target.Cells().All(c => HexCoord.Distance(c, n.C) > 1))
                list.Add(new(r.AllowIncomplete ? IssueSeverity.Warning : IssueSeverity.Error, "Routes", $"{name}: ветка обрывается в узле {nn} {n.C}, не дойдя до цели — продлите её или соедините с маршрутом", n.C, r.Id));
            if (outs.Count >= 2)
            {
                if (n.Rule == RouteForkRule.Weights && outs.All(e => e.Weight <= 0))
                    list.Add(new(IssueSeverity.Error, "Routes", $"{name}: развилка {nn} «по весам», но у всех выходов вес 0", n.C, r.Id));
                if (n.Rule == RouteForkRule.Tags && outs.All(e => e.Tags.Count > 0))
                    list.Add(new(IssueSeverity.Info, "Routes", $"{name}: развилка {nn} «по тегам» без выхода по умолчанию — враг без подходящих тегов пойдёт по первому", n.C, r.Id));
                if (n.Rule == RouteForkRule.Condition && outs.All(e => e.Condition == null))
                    list.Add(new(IssueSeverity.Warning, "Routes", $"{name}: развилка {nn} «по условию», но у выходов нет условий", n.C, r.Id));
                if (outs.All(e => e.Condition != null) && outs.All(e => e.Loop == null))
                    list.Add(new(IssueSeverity.Warning, "Routes", $"{name}: у всех выходов развилки {nn} есть условия — пока ни одно не выполнено, враг ждёт в узле", n.C, r.Id));
            }
        }
        // Loops: every edge of a cycle needs a limit or an exit; the route must still lead somewhere.
        foreach (var e in r.Edges.Where(r.IsLoopEdge))
            if (r.End != RouteEndKind.Patrol && !r.IsControlledLoop(e))
                list.Add(new(IssueSeverity.Error, "Routes", $"{name}: петля через ребро {e.Id} без числа повторений и условия выхода — отметьте «бесконечный патруль» или задайте выход", e.Cells.FirstOrDefault(), r.Id));
        // A route that must reach its end needs a guaranteed way out of each loop: a chance or a condition may never come
        // (ТЗ ч. 5, 10.9; docs/DECISIONS_5.md §85).
        if (r.End == RouteEndKind.Target)
            foreach (var e in r.Edges.Where(x => x.Loop != null && r.IsLoopEdge(x) && !x.Loop.Guaranteed && r.LoopControls(x).All(f => !f.Loop!.Guaranteed)))
                list.Add(new(e.Loop!.ExitChance > 0 && e.Loop.ExitCondition == null ? IssueSeverity.Error : IssueSeverity.Warning, "Routes",
                    $"{name}: петля через ребро {e.Id} заканчивается только {(e.Loop.ExitChance > 0 ? "по шансу" : "по условию")} — задайте предел повторений или времени, иначе враг может не дойти до цели",
                    e.Cells.FirstOrDefault(), r.Id));
        if (r.End == RouteEndKind.Target && reach.Count > 0 && !r.Nodes.Any(n => reach.Contains(n.Id) && !r.Out(n.Id).Any()))
            list.Add(new(IssueSeverity.Error, "Routes", $"{name}: из петель нет выхода к концу маршрута", r.StartNode()?.C, r.Id));
        if (r.Fallback != "" && (sc.Route(r.Fallback) == null || r.Fallback == r.Id))
            list.Add(new(IssueSeverity.Error, "Routes", $"{name}: запасной маршрут «{r.Fallback}» не найден", r.StartNode()?.C, r.Id));
        return list;
    }

    public static List<ScenarioProblem> Groups(ContentDatabase db, ScenarioDef sc, MapDef? map = null)
    {
        var list = new List<ScenarioProblem>();
        for (int wi = 0; wi < sc.Waves.Count; wi++)
        {
            var w = sc.Waves[wi];
            foreach (var g in w.Groups)
            {
                string name = $"Волна «{w.Name}», группа «{(g.Name == "" ? g.Unit : g.Name)}»";
                string path = $"Waves[{wi}].Groups[{g.Id}]";
                if (db.Unit(g.Unit) == null) list.Add(new(IssueSeverity.Error, path, $"{name}: шаблон «{g.Unit}» не найден"));
                var b = sc.Object(g.SpawnBase);
                if (g.SpawnBase == "") list.Add(new(IssueSeverity.Error, path, $"{name}: не указана база спавна"));
                else if (b == null) list.Add(new(IssueSeverity.Error, path, $"{name}: база спавна «{g.SpawnBase}» не найдена"));
                else if (b.Spawn == null) list.Add(new(IssueSeverity.Error, path, $"{name}: «{b.DisplayName}» не база спавна", b.Anchor, b.Id));
                foreach (var extra in g.ExtraBases.Where(x => x != ""))
                {
                    var eb = sc.Object(extra);
                    if (eb == null) list.Add(new(IssueSeverity.Error, path, $"{name}: доп. база спавна «{extra}» не найдена"));
                    else if (eb.Spawn == null) list.Add(new(IssueSeverity.Error, path, $"{name}: «{eb.DisplayName}» не база спавна", eb.Anchor, eb.Id));
                }
                var r = sc.Route(g.Route);
                bool mapRoute = r == null && map?.FindRoute(g.Route) != null;
                if (g.Route == "") list.Add(new(IssueSeverity.Info, path, $"{name}: маршрут не назначен — враги действуют по профилю ИИ", b?.Anchor));
                else if (r == null && !mapRoute) list.Add(new(IssueSeverity.Error, path, $"{name}: маршрут «{g.Route}» не найден", b?.Anchor));
                else if (r != null && b != null && r.Spawn != "" && r.Spawn != b.Id && g.ExtraBases.Count == 0)
                    list.Add(new(IssueSeverity.Warning, path, $"{name}: маршрут «{r.DisplayName}» начинается у «{r.Spawn}», а группа — у «{b.Id}»", b.Anchor, b.Id));
                if (g.Target != "" && sc.Object(g.Target) == null) list.Add(new(IssueSeverity.Error, path, $"{name}: цель «{g.Target}» не найдена"));
                foreach (var alt in g.AltRoutes.Where(x => x != "" && sc.Route(x) == null && map?.FindRoute(x) == null))
                    list.Add(new(IssueSeverity.Error, path, $"{name}: другой маршрут «{alt}» не найден", b?.Anchor));
                if (g.SubgroupSize > 0 && g.SubgroupSize >= g.Count && g.Repeats == 0)
                    list.Add(new(IssueSeverity.Info, path, $"{name}: подгруппа ({g.SubgroupSize}) не меньше количества ({g.Count}) — пауза не действует"));
                if (g.MaxAlive > 0 && w.MaxAlive > 0 && g.MaxAlive > w.MaxAlive)
                    list.Add(new(IssueSeverity.Info, path, $"{name}: лимит живых группы ({g.MaxAlive}) больше лимита волны ({w.MaxAlive})"));
                if (b?.Spawn is { MaxCreated: > 0 } sp && WaveSchedule.CountOf(g, w) * (g.Repeats + 1) > sp.MaxCreated)
                    list.Add(new(IssueSeverity.Warning, path, $"{name}: врагов {WaveSchedule.CountOf(g, w) * (g.Repeats + 1)}, а лимит созданных базы «{b.DisplayName}» — {sp.MaxCreated}: лишние не появятся", b.Anchor, b.Id));
            }
            foreach (var id in w.Groups.GroupBy(g => g.Id).Where(x => x.Key != "" && x.Count() > 1).Select(x => x.Key))
                list.Add(new(IssueSeverity.Error, $"Waves[{wi}].Groups", $"Волна «{w.Name}»: ID группы «{id}» повторяется"));

            // The threat budget: its limits must be satisfiable (the same pick the battle makes).
            var bd = w.Budget;
            if (!bd.Enabled) continue;
            string bp = $"Waves[{wi}].Budget";
            if (bd.MinUnits > bd.MaxUnits) list.Add(new(IssueSeverity.Error, bp, $"Волна «{w.Name}»: мин. врагов ({bd.MinUnits}) больше макс. ({bd.MaxUnits})"));
            var sb = sc.Object(bd.SpawnBase);
            if (!bd.Pinned)
            {
                if (bd.SpawnBase == "" || sb?.Spawn == null) list.Add(new(IssueSeverity.Error, bp, $"Волна «{w.Name}» по бюджету: не указана база спавна (или это не база спавна)"));
                if (bd.Route != "" && sc.Route(bd.Route) == null && map?.FindRoute(bd.Route) == null) list.Add(new(IssueSeverity.Error, bp, $"Волна «{w.Name}» по бюджету: маршрут «{bd.Route}» не найден"));
                var pick = WaveBudget.Compose(db, w, bd, WaveBudget.SeedOf(bd, sc.Seed, wi));
                foreach (var pr in pick.Problems) list.Add(new(IssueSeverity.Warning, bp, $"Волна «{w.Name}» по бюджету: {pr}"));
            }
            else if (!w.Groups.Any(g => g.FromBudget))
                list.Add(new(IssueSeverity.Warning, bp, $"Волна «{w.Name}»: бюджет отмечен закреплённым, но подобранных групп нет — нажмите «Подобрать и закрепить»"));
        }
        return list;
    }
}
