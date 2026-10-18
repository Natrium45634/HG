using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

public enum MapShape
{
    [Ed("Прямоугольник (ряды)")] Rectangle,
    [Ed("Ромб")] Rhombus,
    [Ed("Шестиугольник")] Hexagon,
}

/// <summary>
/// Built-in map templates and the template ↔ map operations (docs/DECISIONS_2.md §18).
/// Templates and maps never share objects: every operation copies.
/// </summary>
public static class MapTemplates
{
    public const string StandardId = "standard_10x8";
    public const string DefaultFloor = "floor";
    public const string BuiltInGroup = "Встроенные";
    public const string UserGroup = "Пользовательские";

    public static readonly string[] BuiltInIds = { StandardId, "small_arena", "wide_16x8", "defense_12x8", "boss_arena", "empty" };

    public static bool IsSpawnMarker(MarkerType t) => t is MarkerType.PlayerStart or MarkerType.EnemySpawn or MarkerType.NeutralSpawn;

    public static bool IsObjectiveMarker(MarkerType t) => t is MarkerType.Objective or MarkerType.Exit or MarkerType.Trigger;

    /// <summary>Templates in display order: the standard 10 × 8 first, then by sort order and name.</summary>
    public static List<MapTemplateDef> Ordered(ContentDatabase db) =>
        db.All<MapTemplateDef>()
            .OrderBy(t => t.Id == StandardId ? 0 : 1)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.DisplayName, StringComparer.CurrentCulture)
            .ToList();

    private static string FloorOf(ContentDatabase? db)
    {
        if (db == null || db.Terrain(DefaultFloor) != null) return DefaultFloor;
        return db.All<TerrainDef>().FirstOrDefault(t => t.Kind == TerrainKind.Floor)?.Id ?? db.Ids(DefCategory.Terrain).FirstOrDefault() ?? DefaultFloor;
    }

    private static string TerrainOr(ContentDatabase? db, string id, string fallback) => db == null || db.Terrain(id) != null ? id : fallback;

    /// <summary>Cells of a generated shape (custom size generator of the "new map" dialog).</summary>
    public static List<MapCellDef> Shape(MapShape shape, int width, int height, string terrain)
    {
        var cells = new List<MapCellDef>();
        switch (shape)
        {
            case MapShape.Rectangle:
                for (int row = 0; row < height; row++)
                for (int col = 0; col < width; col++)
                    cells.Add(new MapCellDef { C = HexLayout.FromOffset(col, row), T = terrain });
                break;
            case MapShape.Rhombus:
                for (int r = 0; r < height; r++)
                for (int q = 0; q < width; q++)
                    cells.Add(new MapCellDef { C = new HexCoord(q, r), T = terrain });
                break;
            case MapShape.Hexagon:
                foreach (var c in HexCoord.Spiral(HexCoord.Zero, Math.Max(0, width)))
                    cells.Add(new MapCellDef { C = c, T = terrain });
                break;
        }
        return cells;
    }

    public static MapDef Generate(string id, MapShape shape, int width, int height, string terrain)
    {
        var m = new MapDef { Id = id, Name = id };
        m.Cells.AddRange(Shape(shape, width, height, terrain));
        return m;
    }

    // ------------------------------------------------------------------ built-in templates

    public static List<MapTemplateDef> CreateAllBuiltIn(ContentDatabase? db = null) => BuiltInIds.Select(id => CreateBuiltIn(id, db)!).ToList();

    /// <summary>Recreates a built-in template from code (used by «Восстановить встроенные»).</summary>
    public static MapTemplateDef? CreateBuiltIn(string id, ContentDatabase? db = null)
    {
        string floor = FloorOf(db);
        string raised = TerrainOr(db, "platform", floor);
        string wall = TerrainOr(db, "wall", floor);
        var t = new MapTemplateDef { Id = id, BuiltIn = true, Group = BuiltInGroup };
        static HexCoord Off(int col, int row) => HexLayout.FromOffset(col, row);
        switch (id)
        {
            case StandardId:
                t.Name = "Стандартная карта 10 × 8";
                t.Description = "10 столбцов × 8 рядов, ровный пол.";
                t.Purpose = "Основной шаблон: вписывается в экран 1920 × 1080 целиком, крупные гексы.";
                t.SortOrder = 0;
                t.Tags.Add("стандарт");
                t.Cells.AddRange(Shape(MapShape.Rectangle, 10, 8, floor));
                t.Markers.Add(new MapMarkerDef { Id = "player", Type = MarkerType.PlayerStart, C = Off(1, 4), Label = "Отряд", Radius = 1 });
                t.Markers.Add(new MapMarkerDef { Id = "enemy", Type = MarkerType.EnemySpawn, C = Off(8, 3), Label = "Противник", Radius = 1 });
                break;
            case "small_arena":
                t.Name = "Маленькая арена";
                t.Description = "Шестиугольник радиуса 3 (37 клеток) с платформой в центре.";
                t.Purpose = "Короткие стычки 2–3 на 2–3.";
                t.SortOrder = 10;
                t.Cells.AddRange(Shape(MapShape.Hexagon, 3, 0, floor));
                t.Cells.First(c => c.C == HexCoord.Zero).T = raised;
                t.Cells.First(c => c.C == HexCoord.Zero).H = 1;
                t.Markers.Add(new MapMarkerDef { Id = "player", Type = MarkerType.PlayerStart, C = new HexCoord(-3, 1), Label = "Отряд", Radius = 1 });
                t.Markers.Add(new MapMarkerDef { Id = "enemy", Type = MarkerType.EnemySpawn, C = new HexCoord(3, -1), Label = "Противник", Radius = 1 });
                break;
            case "wide_16x8":
                t.Name = "Широкая карта 16 × 8";
                t.Description = "16 столбцов × 8 рядов.";
                t.Purpose = "Фланговые манёвры, несколько направлений атаки.";
                t.SortOrder = 20;
                t.Cells.AddRange(Shape(MapShape.Rectangle, 16, 8, floor));
                t.Markers.Add(new MapMarkerDef { Id = "player", Type = MarkerType.PlayerStart, C = Off(1, 4), Label = "Отряд", Radius = 1 });
                t.Markers.Add(new MapMarkerDef { Id = "enemy", Type = MarkerType.EnemySpawn, C = Off(14, 3), Label = "Противник", Radius = 1 });
                break;
            case "defense_12x8":
            {
                t.Name = "Карта обороны 12 × 8";
                t.Description = "Коридор от входа справа к защищаемой зоне слева, маршрут врагов.";
                t.Purpose = "Оборона гекса/объекта, отражение волн, блокирование.";
                t.SortOrder = 30;
                t.Cells.AddRange(Shape(MapShape.Rectangle, 12, 8, floor));
                // Two wall bands leave a winding corridor.
                foreach (var c in t.Cells)
                {
                    var (col, row) = HexLayout.ToOffset(c.C);
                    bool upper = row <= 1 && col >= 3 && col <= 9;
                    bool lower = row >= 6 && col >= 2 && col <= 8;
                    bool pillar = (col == 6 && row >= 2 && row <= 3) || (col == 4 && row >= 4 && row <= 5);
                    if (upper || lower || pillar) c.T = wall;
                }
                t.Markers.Add(new MapMarkerDef { Id = "gate", Type = MarkerType.EnemySpawn, C = Off(11, 4), Label = "Вход", Radius = 0 });
                t.Markers.Add(new MapMarkerDef { Id = "core", Type = MarkerType.Objective, C = Off(0, 3), Label = "Защищаемая зона", Radius = 1 });
                t.Markers.Add(new MapMarkerDef { Id = "player", Type = MarkerType.PlayerStart, C = Off(2, 3), Label = "Отряд", Radius = 1 });
                t.Routes.Add(new MapRouteDef { Id = "main", Name = "Основной", Points = { Off(11, 4), Off(8, 4), Off(5, 2), Off(3, 4), Off(0, 3) } });
                break;
            }
            case "boss_arena":
                t.Name = "Арена босса";
                t.Description = "Шестиугольник радиуса 5: приподнятый центр для босса и кольцо платформ.";
                t.Purpose = "Бой с боссом: фазы, призыв подкреплений, изменение местности.";
                t.SortOrder = 40;
                t.Cells.AddRange(Shape(MapShape.Hexagon, 5, 0, floor));
                foreach (var c in t.Cells)
                {
                    int d = HexCoord.Distance(c.C, HexCoord.Zero);
                    if (d <= 1)
                    {
                        c.T = raised;
                        c.H = 1;
                    }
                    else if (d == 3 && (c.C.Q + 2 * c.C.R) % 3 == 0)
                    {
                        c.T = raised;
                        c.H = 1;
                    }
                }
                t.Markers.Add(new MapMarkerDef { Id = "player", Type = MarkerType.PlayerStart, C = new HexCoord(-5, 2), Label = "Отряд", Radius = 1 });
                t.Markers.Add(new MapMarkerDef { Id = "boss", Type = MarkerType.EnemySpawn, C = HexCoord.Zero, Label = "Босс", Radius = 0 });
                t.Markers.Add(new MapMarkerDef { Id = "adds", Type = MarkerType.EnemySpawn, C = new HexCoord(5, -2), Label = "Подкрепления", Radius = 1 });
                break;
            case "empty":
                t.Name = "Пустая карта";
                t.Description = "Ни одной клетки — гексы добавляются кистью «Добавить гекс».";
                t.Purpose = "Карты неправильной формы с нуля.";
                t.SortOrder = 50;
                t.Parts = TemplatePartsDef.ShapeOnly();
                break;
            default:
                return null;
        }
        return t;
    }

    // ------------------------------------------------------------------ copying

    private sealed class MapData
    {
        public List<MapCellDef> Cells = new();
        public List<MapMarkerDef> Markers = new();
        public List<MapRouteDef> Routes = new();
        public MapRulesDef Rules = new();
    }

    /// <summary>Copies the selected parts; parts not selected get neutral values (floor, height 0, nothing).</summary>
    private static MapData CopyParts(IEnumerable<MapCellDef> cells, IEnumerable<MapMarkerDef> markers, IEnumerable<MapRouteDef> routes, MapRulesDef rules,
        TemplatePartsDef parts, ContentDatabase? db)
    {
        string floor = FloorOf(db);
        var data = new MapData();
        var seen = new HashSet<HexCoord>();
        foreach (var src in cells)
        {
            if (!seen.Add(src.C)) continue;
            var obstacle = db?.Terrain(src.T)?.IsObstacle == true;
            var c = new MapCellDef { C = src.C, T = floor };
            if (obstacle ? parts.Obstacles : parts.Terrain) c.T = src.T;
            if (parts.Heights) c.H = src.H;
            if (parts.Other)
            {
                var o = src.Clone();
                c.MoveCost = o.MoveCost;
                c.Blocked = o.Blocked;
                c.BlocksLoS = o.BlocksLoS;
                c.Cover = o.Cover;
                c.AllowPlacement = o.AllowPlacement;
                c.ObstacleHp = o.ObstacleHp;
                c.AllowedEffects = o.AllowedEffects;
                c.Effects = o.Effects;
                c.Tags = o.Tags;
                c.Name = o.Name;
                c.Description = o.Description;
            }
            data.Cells.Add(c);
        }
        foreach (var m in markers)
        {
            bool keep = IsSpawnMarker(m.Type) ? parts.SpawnPoints : IsObjectiveMarker(m.Type) && parts.Objectives;
            if (keep) data.Markers.Add(ContentJson.Clone(m));
        }
        if (parts.Routes) data.Routes.AddRange(routes.Select(r => r.Clone()));
        data.Rules = parts.Other ? ContentJson.Clone(rules) : new MapRulesDef();
        return data;
    }

    /// <summary>Creates a new map from a template (independent copy; provenance stored).</summary>
    public static MapDef CreateMap(MapTemplateDef template, string mapId, ContentDatabase? db, string? name = null)
    {
        var d = CopyParts(template.Cells, template.Markers, template.Routes, template.Rules, template.Parts, db);
        return new MapDef
        {
            Id = mapId,
            Name = name ?? template.DisplayName,
            Description = template.Description,
            Tags = new List<string>(template.Tags),
            Cells = d.Cells,
            Markers = d.Markers,
            Routes = d.Routes,
            Rules = d.Rules,
            TemplateId = template.Id,
            TemplateVersion = template.Version,
        };
    }

    /// <summary>Saves a map as a template with the chosen parts.</summary>
    public static MapTemplateDef FromMap(MapDef map, string templateId, TemplatePartsDef parts, ContentDatabase? db, string? name = null)
    {
        var d = CopyParts(map.Cells, map.Markers, map.Routes, map.Rules, parts, db);
        return new MapTemplateDef
        {
            Id = templateId,
            Name = name ?? map.DisplayName,
            Description = map.Description,
            Group = UserGroup,
            Tags = new List<string>(map.Tags),
            Parts = parts.Clone(),
            Cells = d.Cells,
            Markers = d.Markers,
            Routes = d.Routes,
            Rules = d.Rules,
            SortOrder = 100,
        };
    }

    /// <summary>A new template based on an existing one (copy with a new id; not built-in).</summary>
    public static MapTemplateDef Derive(MapTemplateDef source, string newId)
    {
        var t = ContentJson.Clone(source);
        t.Id = newId;
        t.BuiltIn = false;
        t.Group = UserGroup;
        t.Version = 1;
        t.Name = source.DisplayName + " (копия)";
        return t;
    }

    /// <summary>
    /// Explicit update of a map created from a template: the template's stored parts replace the
    /// corresponding parts of the map (shape always). Id, name, description of the map stay.
    /// </summary>
    public static void ApplyToMap(MapTemplateDef template, MapDef map, ContentDatabase? db)
    {
        var p = template.Parts;
        var d = CopyParts(template.Cells, template.Markers, template.Routes, template.Rules, new TemplatePartsDef(), db);
        var old = map.Cells.ToDictionary(c => c.C);
        var cells = new List<MapCellDef>();
        foreach (var tc in d.Cells)
        {
            old.TryGetValue(tc.C, out var mc);
            var c = mc?.Clone() ?? new MapCellDef { C = tc.C, T = tc.T };
            bool tObstacle = db?.Terrain(tc.T)?.IsObstacle == true;
            if (tObstacle ? p.Obstacles : p.Terrain) c.T = tc.T;
            if (p.Heights) c.H = tc.H;
            if (p.Other)
            {
                c.MoveCost = tc.MoveCost;
                c.Blocked = tc.Blocked;
                c.BlocksLoS = tc.BlocksLoS;
                c.Cover = tc.Cover;
                c.AllowPlacement = tc.AllowPlacement;
                c.ObstacleHp = tc.ObstacleHp;
                c.AllowedEffects = tc.AllowedEffects;
                c.Effects = tc.Effects;
                c.Tags = tc.Tags;
                c.Name = tc.Name;
                c.Description = tc.Description;
            }
            cells.Add(c);
        }
        map.Cells = cells;
        if (p.SpawnPoints)
        {
            map.Markers.RemoveAll(m => IsSpawnMarker(m.Type));
            map.Markers.AddRange(d.Markers.Where(m => IsSpawnMarker(m.Type)));
        }
        if (p.Objectives)
        {
            map.Markers.RemoveAll(m => IsObjectiveMarker(m.Type));
            map.Markers.AddRange(d.Markers.Where(m => IsObjectiveMarker(m.Type)));
        }
        if (p.Routes) map.Routes = d.Routes;
        if (p.Other) map.Rules = d.Rules;
        map.TemplateId = template.Id;
        map.TemplateVersion = template.Version;
    }

    /// <summary>Maps created from the template (for the «обновить карты» dialog), with "outdated" flag.</summary>
    public static List<(MapDef Map, bool Outdated)> MapsFrom(ContentDatabase db, MapTemplateDef template) =>
        db.All<MapDef>().Where(m => m.TemplateId == template.Id).Select(m => (m, m.TemplateVersion < template.Version)).ToList();
}
