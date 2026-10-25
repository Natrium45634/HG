using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

// Route graph (docs/DECISIONS_4.md §57): nodes on hexes joined by edges; an edge is the path of hexes between two
// nodes. A simple TD route is two nodes (start at the spawn base, end at the target) and one edge.

public enum RouteNodeKind
{
    [Ed("Обычный")] Normal,
    [Ed("Начало")] Start,
    [Ed("Конец")] End,
    [Ed("Развилка")] Fork,
    [Ed("Слияние")] Merge,
    [Ed("Контрольная точка")] Checkpoint,
}

/// <summary>How a fork picks the exit for an enemy (docs/DECISIONS_4.md §57).</summary>
public enum RouteForkRule
{
    [Ed("Первый доступный (по приоритету)")] FirstAvailable,
    [Ed("По весам (ГСЧ боя)")] Weights,
    [Ed("По тегам и категориям группы")] Tags,
    [Ed("По условию ребра")] Condition,
    [Ed("По загрузке (где меньше врагов)")] Load,
    [Ed("По очереди")] RoundRobin,
}

/// <summary>How strictly an enemy keeps to the drawn hexes (docs/DECISIONS_5.md §86).</summary>
public enum RouteStrictness
{
    [Ed("Коридор", "Гексы маршрута — ориентир: можно обходить толпу в пределах допуска (как в 1.0)")] Corridor,
    [Ed("Строго по гексам", "Только нарисованные гексы; сойти можно лишь реакцией на блокировку — с записью в журнал")] StrictHexes,
    [Ed("Только узлы", "Между узлами — любой путь, гексы рёбер не обязательны")] Waypoints,
}

/// <summary>What an enemy on a route does when it makes no progress (ТЗ ч. 5, 11.3; docs/DECISIONS_5.md §86).</summary>
public enum BlockedReaction
{
    [Ed("Ждать")] Wait,
    [Ed("Уступить или обойти рядом", "Шаг в свободный соседний гекс; во встречной пробке уступает юнит с большим ID")] LocalDetour,
    [Ed("Перестроить путь к узлу", "Путь прямо к концу ребра, мимо гексов линии")] Repath,
    [Ed("Атаковать преграду", "Врага или разрушаемое препятствие на пути")] AttackBlocker,
    [Ed("Запасной маршрут")] SwitchRoute,
    [Ed("Сменить цель", "Альтернативные цели сценария и профиля ИИ")] ChangeObjective,
    [Ed("Остановиться с диагностикой")] Abort,
}

/// <summary>A loop: an edge back to an earlier node (docs/DECISIONS_4.md §57).</summary>
public sealed class RouteLoopDef : ContentPart
{
    [Ed("Повторений", "Сколько раз враг проходит петлю; 0 — без ограничения (только у патруля)"), EdRange(0, 1000)] public int MaxRepeats { get; set; } = 2;
    [Ed("Условие выхода", "Выполнено — петля больше не выбирается")] public ConditionDef? ExitCondition { get; set; }
    [Ed("Шанс выйти после прохода", "После каждого прохода враг выходит с этим шансом (ГСЧ ИИ, воспроизводимо по seed); предел повторений " +
        "остаётся гарантией (docs/DECISIONS_5.md §85)"), EdRange(0, 1)] public double ExitChance { get; set; }
    [Ed("Выйти через, с", "Не дольше этого времени на петле; 0 — без предела по времени"), EdRange(0, 3600)] public double MaxSeconds { get; set; }
    [Ed("Атаковать по пути", "Нет — враг на петле идёт, не отвлекаясь на цели")] public bool Engage { get; set; } = true;

    /// <summary>The loop ends whatever happens: a limit of repeats or of time (a chance or a condition alone may never come).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Guaranteed => MaxRepeats > 0 || MaxSeconds > 0;

    public override string ToString() => (MaxRepeats > 0 ? $"×{MaxRepeats}" : "без ограничения") + (MaxSeconds > 0 ? $", ≤ {MaxSeconds:0.#} с" : "") +
                                         (ExitChance > 0 ? $", выход {ExitChance:P0}" : "") + (ExitCondition != null ? ", с условием выхода" : "");
}

/// <summary>How a route ends: at a target, or by a rule for patrols and guards.</summary>
public enum RouteEndKind
{
    [Ed("У цели")] Target,
    [Ed("Бесконечный патруль")] Patrol,
    [Ed("Остановиться в конце")] Hold,
}

public sealed class RouteNodeDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Гекс"), EdHidden] public HexCoord C { get; set; }
    [Ed("Тип узла")] public RouteNodeKind Kind { get; set; } = RouteNodeKind.Normal;
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Флаг события", "Устанавливается, когда враг проходит узел (для триггеров и условий)")] public string Event { get; set; } = "";
    [Ed("Правило развилки", "Какой выход выбирает враг, если выходов несколько")] public RouteForkRule Rule { get; set; } = RouteForkRule.FirstAvailable;
    [Ed("Ожидание в узле, с", "Враг стоит в узле перед продолжением (пост, точка петли)"), EdRange(0, 3600)] public double DwellSeconds { get; set; }

    public override string ToString() => $"{(Name == "" ? Id : Name)} {C} ({Kind})";
}

public sealed class RouteEdgeDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Из узла")] public string From { get; set; } = "";
    [Ed("В узел")] public string To { get; set; } = "";
    [Ed("Гексы", "Путь от узла «из» (не включая его) до узла «в» (включая его)"), EdHidden] public List<HexCoord> Cells { get; set; } = new();
    [Ed("Приоритет"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Вес выбора", "Для развилки «по весам»"), EdRange(0, 1000)] public double Weight { get; set; } = 1;
    [Ed("Теги", "Для развилки «по тегам»: ребро выбирают враги с такими тегами, категорией, меткой или группой")] public List<string> Tags { get; set; } = new();
    [Ed("Условие использования", "Ребро доступно, пока выполнено")] public ConditionDef? Condition { get; set; }
    [Ed("Петля", "Ребро ведёт назад: число повторений и выход")] public RouteLoopDef? Loop { get; set; }

    public override string ToString() => $"{From} → {To} ({Cells.Count} гексов)" + (Loop != null ? " ↺" : "");
}

/// <summary>
/// A route of enemies: a graph from a spawn base to a target (or an end rule). Groups refer to it by ID; several
/// routes may share hexes and stay independent (a crossing is not a junction).
/// </summary>
public sealed class RouteDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#ff9a40";
    [Ed("База спавна", "Объект, у которого маршрут начинается")] public string Spawn { get; set; } = "";
    [Ed("Цель", "Объект в конце маршрута (база игрока, цель, контрольная точка)")] public string Target { get; set; } = "";
    [Ed("Завершение")] public RouteEndKind End { get; set; } = RouteEndKind.Target;
    [Ed("Разрешить незавершённый", "Подтверждение автора: маршрут без цели всё равно используется")] public bool AllowIncomplete { get; set; }
    [Ed("Запасной маршрут", "Выход из узла заблокирован — враг переходит на этот маршрут")] public string Fallback { get; set; } = "";
    [Ed("Вес", "Для выбора группой «случайно по весам»"), EdRange(0, 1000)] public double Weight { get; set; } = 1;
    [Ed("Строгость следования", "Как враг держится нарисованных гексов (ТЗ ч. 5, 11.2; docs/DECISIONS_5.md §86)")]
    public RouteStrictness Strictness { get; set; } = RouteStrictness.Corridor;
    [Ed("Допуск коридора, гексов", "Насколько можно отойти от линии маршрута"), EdRange(1, 6), EdShowIf(nameof(Strictness), RouteStrictness.Corridor)]
    public int Tolerance { get; set; } = 1;
    [Ed("Если путь заблокирован", "Реакции по порядку: каждая следующая — когда предыдущая не помогла за время «нет продвижения» " +
        "(правила → «Застревание»); пусто — по умолчанию: ждать, уступить или обойти, перестроить путь, запасной маршрут, другая цель, остановиться")]
    public List<BlockedReaction> OnBlocked { get; set; } = new();
    [Ed("Узлы"), EdHidden] public List<RouteNodeDef> Nodes { get; set; } = new();
    [Ed("Рёбра"), EdHidden] public List<RouteEdgeDef> Edges { get; set; } = new();

    /// <summary>The reactions in force: the route's own list or the default one.</summary>
    [JsonIgnore] public IReadOnlyList<BlockedReaction> Reactions => OnBlocked.Count > 0 ? OnBlocked : DefaultReactions;

    public static readonly BlockedReaction[] DefaultReactions =
    {
        BlockedReaction.Wait, BlockedReaction.LocalDetour, BlockedReaction.Repath, BlockedReaction.SwitchRoute, BlockedReaction.ChangeObjective, BlockedReaction.Abort,
    };

    [JsonIgnore] public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

    public RouteNodeDef? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public RouteNodeDef? StartNode() => Nodes.FirstOrDefault(n => n.Kind == RouteNodeKind.Start) ?? Nodes.FirstOrDefault();

    public IEnumerable<RouteEdgeDef> Out(string nodeId) => Edges.Where(e => e.From == nodeId).OrderByDescending(e => e.Priority);

    /// <summary>
    /// The hexes of the main line from the start: the first exit of every node, stopping at an end, a dead end or a
    /// repeated edge. The start hex is included.
    /// </summary>
    public List<HexCoord> MainPath()
    {
        var start = StartNode();
        var path = new List<HexCoord>();
        if (start == null) return path;
        path.Add(start.C);
        var seen = new HashSet<string>();
        var cur = start;
        while (cur != null && cur.Kind != RouteNodeKind.End)
        {
            // The way on, not around a loop: exits outside cycles first.
            var e = Out(cur.Id).OrderBy(x => IsLoopEdge(x) ? 1 : 0).FirstOrDefault(x => !seen.Contains(x.Id));
            if (e == null || !seen.Add(e.Id)) break;
            path.AddRange(e.Cells);
            cur = Node(e.To);
        }
        return path;
    }

    /// <summary>Every hex used by the route (for drawing and checks).</summary>
    public IEnumerable<HexCoord> AllCells() => Nodes.Select(n => n.C).Concat(Edges.SelectMany(e => e.Cells)).Distinct();

    public IEnumerable<RouteEdgeDef> In(string nodeId) => Edges.Where(e => e.To == nodeId);

    public RouteEdgeDef? Edge(string id) => Edges.FirstOrDefault(e => e.Id == id);

    /// <summary>The hexes of an edge with its first node: from the «from» hex to the «to» hex.</summary>
    public List<HexCoord> EdgePath(RouteEdgeDef e)
    {
        var list = new List<HexCoord>();
        if (Node(e.From) is { } f) list.Add(f.C);
        list.AddRange(e.Cells);
        return list;
    }

    /// <summary>Nodes reachable from the start along the edges.</summary>
    public HashSet<string> Reachable(string? from = null)
    {
        var seen = new HashSet<string>();
        var start = from ?? StartNode()?.Id;
        if (start == null) return seen;
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n)) continue;
            foreach (var e in Out(n)) stack.Push(e.To);
        }
        return seen;
    }

    /// <summary>An edge of a cycle: its «to» node leads back to its «from» node.</summary>
    public bool IsLoopEdge(RouteEdgeDef e) => e.From == e.To || Reachable(e.To).Contains(e.From);

    /// <summary>
    /// A cycle edge without its own loop settings is controlled when an edge of the same cycle has them (a limit or
    /// an exit): that edge stops the cycle.
    /// </summary>
    public bool IsControlledLoop(RouteEdgeDef e) => LoopControls(e).Any();

    /// <summary>The edges with loop settings (a limit or an exit) on a cycle through this edge (itself included).</summary>
    public IEnumerable<RouteEdgeDef> LoopControls(RouteEdgeDef e)
    {
        var fromTo = Reachable(e.To);
        return Edges.Where(f => (f.Loop is { MaxRepeats: > 0 } or { MaxSeconds: > 0 } or { ExitChance: > 0 } || f.Loop?.ExitCondition != null)
                                && (f == e || fromTo.Contains(f.From) && Reachable(f.To).Contains(e.From)));
    }

    /// <summary>A node that ends the route: the end kind, or no exit at all.</summary>
    public bool IsEnd(RouteNodeDef n) => n.Kind == RouteNodeKind.End || !Edges.Any(e => e.From == n.Id);

    public override string ToString() => $"{DisplayName} [{Id}]";
}

public static class RouteGraph
{
    public static string NewNodeId(RouteDef r)
    {
        for (int i = r.Nodes.Count + 1; ; i++)
            if (r.Nodes.All(n => n.Id != $"n{i}")) return $"n{i}";
    }

    public static string NewEdgeId(RouteDef r)
    {
        for (int i = r.Edges.Count + 1; ; i++)
            if (r.Edges.All(e => e.Id != $"e{i}")) return $"e{i}";
    }

    public static RouteNodeDef? NodeAt(RouteDef r, HexCoord c) => r.Nodes.FirstOrDefault(n => n.C == c);

    /// <summary>An edge passing a hex between its nodes: the edge and the index of the hex in its cells.</summary>
    public static (RouteEdgeDef Edge, int Index)? EdgeAt(RouteDef r, HexCoord c)
    {
        foreach (var e in r.Edges)
        {
            int i = e.Cells.IndexOf(c);
            if (i >= 0 && i < e.Cells.Count - 1) return (e, i);
        }
        return null;
    }

    /// <summary>Splits an edge at one of its inner hexes: a new node there, the edge becomes two.</summary>
    public static RouteNodeDef Split(RouteDef r, RouteEdgeDef e, int index)
    {
        var at = e.Cells[index];
        var node = new RouteNodeDef { Id = NewNodeId(r), C = at, Kind = RouteNodeKind.Normal };
        r.Nodes.Add(node);
        var tail = new RouteEdgeDef
        {
            Id = NewEdgeId(r), From = node.Id, To = e.To, Cells = e.Cells.Skip(index + 1).ToList(),
            Priority = e.Priority, Weight = e.Weight, Tags = new List<string>(e.Tags), Condition = e.Condition, Loop = e.Loop,
        };
        e.Cells = e.Cells.Take(index + 1).ToList();
        e.To = node.Id;
        e.Loop = null;
        r.Edges.Add(tail);
        return node;
    }

    /// <summary>
    /// The node of a route at a hex: an existing node, or a new one splitting the edge that passes there; null — the
    /// hex is not on the route.
    /// </summary>
    public static RouteNodeDef? NodeOn(RouteDef r, HexCoord c)
    {
        if (NodeAt(r, c) is { } n) return n;
        return EdgeAt(r, c) is { } hit ? Split(r, hit.Edge, hit.Index) : null;
    }

    /// <summary>
    /// A new edge from a node along drawn hexes (the first is the node's hex). It ends at a node of the route there
    /// (a merge or a loop) or at a new node. Kinds are recomputed.
    /// </summary>
    public static RouteEdgeDef AddBranch(RouteDef r, RouteNodeDef from, IReadOnlyList<HexCoord> cells)
    {
        var to = cells.Count > 1 ? NodeOn(r, cells[^1]) : null;
        if (to == null)
        {
            to = new RouteNodeDef { Id = NewNodeId(r), C = cells[^1], Kind = RouteNodeKind.Normal };
            r.Nodes.Add(to);
        }
        var e = new RouteEdgeDef { Id = NewEdgeId(r), From = from.Id, To = to.Id, Cells = cells.Skip(1).ToList() };
        if (r.IsLoopEdge(e) || e.From == e.To) e.Loop = new RouteLoopDef();
        r.Edges.Add(e);
        UpdateKinds(r);
        return e;
    }

    /// <summary>Fork/merge/end/normal from the edges; the start, checkpoints and the author's kinds stay.</summary>
    public static void UpdateKinds(RouteDef r)
    {
        foreach (var n in r.Nodes)
        {
            if (n.Kind is RouteNodeKind.Start or RouteNodeKind.Checkpoint) continue;
            int outs = r.Edges.Count(e => e.From == n.Id), ins = r.Edges.Count(e => e.To == n.Id);
            n.Kind = outs == 0 ? RouteNodeKind.End : outs >= 2 ? RouteNodeKind.Fork : ins >= 2 ? RouteNodeKind.Merge : RouteNodeKind.Normal;
        }
    }

    /// <summary>
    /// «Соединить маршруты»: the part of route b reachable from its node nearest to a's end is copied into a and the
    /// end of a is joined to it by the given hexes (from a's end node to the b node, both included). a takes b's
    /// target and end rule. Returns the copied node standing for the b node.
    /// </summary>
    public static RouteNodeDef? Join(RouteDef a, RouteDef b, RouteNodeDef aEnd, RouteNodeDef bNode, IReadOnlyList<HexCoord> link)
    {
        var map = new Dictionary<string, RouteNodeDef>();
        foreach (var id in b.Reachable(bNode.Id))
        {
            var src = b.Node(id)!;
            var copy = new RouteNodeDef { Id = NewNodeId(a), C = src.C, Kind = src.Kind == RouteNodeKind.Start ? RouteNodeKind.Normal : src.Kind, Name = src.Name, Event = src.Event, Rule = src.Rule, DwellSeconds = src.DwellSeconds };
            a.Nodes.Add(copy);
            map[id] = copy;
        }
        foreach (var e in b.Edges.Where(e => map.ContainsKey(e.From) && map.ContainsKey(e.To)))
            a.Edges.Add(new RouteEdgeDef
            {
                Id = NewEdgeId(a), From = map[e.From].Id, To = map[e.To].Id, Cells = new List<HexCoord>(e.Cells), Priority = e.Priority, Weight = e.Weight,
                Tags = new List<string>(e.Tags), Condition = e.Condition, Loop = e.Loop,
            });
        var joined = map[bNode.Id];
        if (aEnd.Kind == RouteNodeKind.End) aEnd.Kind = RouteNodeKind.Normal;
        a.Edges.Add(new RouteEdgeDef { Id = NewEdgeId(a), From = aEnd.Id, To = joined.Id, Cells = link.Skip(1).ToList() });
        a.Target = b.Target;
        a.End = b.End;
        UpdateKinds(a);
        return joined;
    }

    public static string NewId(ScenarioDef sc)
    {
        for (int i = 1; ; i++)
        {
            string id = $"route_{i}";
            if (sc.Routes.All(r => r.Id != id)) return id;
        }
    }

    /// <summary>A simple route from drawn hexes: a start node, an end node and the edge between them.</summary>
    public static RouteDef FromCells(string id, IReadOnlyList<HexCoord> cells, string spawn, string target, string name = "")
    {
        var r = new RouteDef { Id = id, Name = name, Spawn = spawn, Target = target };
        if (cells.Count == 0) return r;
        r.Nodes.Add(new RouteNodeDef { Id = "n1", C = cells[0], Kind = RouteNodeKind.Start });
        if (cells.Count == 1) return r;
        r.Nodes.Add(new RouteNodeDef { Id = "n2", C = cells[^1], Kind = RouteNodeKind.End });
        r.Edges.Add(new RouteEdgeDef { Id = "e1", From = "n1", To = "n2", Cells = cells.Skip(1).ToList() });
        return r;
    }
}
