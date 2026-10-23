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

/// <summary>How a route ends: at a target, or by a rule for patrols and guards.</summary>
public enum RouteEndKind
{
    [Ed("У цели")] Target,
    [Ed("Бесконечный патруль")] Patrol,
    [Ed("Остановиться в конце")] Hold,
}

public sealed class RouteNodeDef
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Гекс"), EdHidden] public HexCoord C { get; set; }
    [Ed("Тип узла")] public RouteNodeKind Kind { get; set; } = RouteNodeKind.Normal;
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Событие", "Триггер сценария, запускаемый при прохождении узла"), EdHidden] public string Event { get; set; } = "";

    public override string ToString() => $"{(Name == "" ? Id : Name)} {C} ({Kind})";
}

public sealed class RouteEdgeDef
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Из узла")] public string From { get; set; } = "";
    [Ed("В узел")] public string To { get; set; } = "";
    [Ed("Гексы", "Путь от узла «из» (не включая его) до узла «в» (включая его)"), EdHidden] public List<HexCoord> Cells { get; set; } = new();
    [Ed("Приоритет"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Вес выбора", "Для развилки «по весам»"), EdRange(0, 1000)] public double Weight { get; set; } = 1;
    [Ed("Теги")] public List<string> Tags { get; set; } = new();

    public override string ToString() => $"{From} → {To} ({Cells.Count} гексов)";
}

/// <summary>
/// A route of enemies: a graph from a spawn base to a target (or an end rule). Groups refer to it by ID; several
/// routes may share hexes and stay independent (a crossing is not a junction).
/// </summary>
public sealed class RouteDef
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#ff9a40";
    [Ed("База спавна", "Объект, у которого маршрут начинается")] public string Spawn { get; set; } = "";
    [Ed("Цель", "Объект в конце маршрута (база игрока, цель, контрольная точка)")] public string Target { get; set; } = "";
    [Ed("Завершение")] public RouteEndKind End { get; set; } = RouteEndKind.Target;
    [Ed("Разрешить незавершённый", "Подтверждение автора: маршрут без цели всё равно используется")] public bool AllowIncomplete { get; set; }
    [Ed("Узлы"), EdHidden] public List<RouteNodeDef> Nodes { get; set; } = new();
    [Ed("Рёбра"), EdHidden] public List<RouteEdgeDef> Edges { get; set; } = new();

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
            var e = Out(cur.Id).FirstOrDefault();
            if (e == null || !seen.Add(e.Id)) break;
            path.AddRange(e.Cells);
            cur = Node(e.To);
        }
        return path;
    }

    /// <summary>Every hex used by the route (for drawing and checks).</summary>
    public IEnumerable<HexCoord> AllCells() => Nodes.Select(n => n.C).Concat(Edges.SelectMany(e => e.Cells)).Distinct();

    public override string ToString() => $"{DisplayName} [{Id}]";
}

public static class RouteGraph
{
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
