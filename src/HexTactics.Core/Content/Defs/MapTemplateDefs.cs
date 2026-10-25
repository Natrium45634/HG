using System.Collections.Generic;
using System.Text.Json.Serialization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>Which parts of a map a template stores (the shape and size are always stored).</summary>
public sealed class TemplatePartsDef : ContentPart
{
    [Ed("Типы местности", "Иначе — пол по умолчанию")] public bool Terrain { get; set; } = true;
    [Ed("Высоты", "Иначе — высота 0")] public bool Heights { get; set; } = true;
    [Ed("Препятствия", "Клетки, местность которых — препятствие")] public bool Obstacles { get; set; } = true;
    [Ed("Точки появления", "Метки старта игрока и появления врагов/нейтралов")] public bool SpawnPoints { get; set; } = true;
    [Ed("Цели", "Метки целей, выходов и триггеров")] public bool Objectives { get; set; } = true;
    [Ed("Маршруты")] public bool Routes { get; set; } = true;
    [Ed("Прочие настройки", "Правила карты, переопределения клеток, стартовые эффекты, имена клеток")] public bool Other { get; set; } = true;

    public TemplatePartsDef Clone() => (TemplatePartsDef)MemberwiseClone();

    public static TemplatePartsDef ShapeOnly() => new()
    {
        Terrain = false, Heights = false, Obstacles = false, SpawnPoints = false, Objectives = false, Routes = false, Other = false,
    };
}

/// <summary>
/// Map template (docs/DECISIONS_2.md §18): a stored map shape plus the selected parts.
/// Maps created from a template copy its data; changing the template never changes them.
/// </summary>
public sealed class MapTemplateDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.MapTemplate;

    [Ed("Назначение", "Для чего подходит шаблон", Group = "Основное"), EdMultiline] public string Purpose { get; set; } = "";
    [Ed("Встроенный", "Шаблон из поставки (можно восстановить)", Group = "Основное"), EdReadOnly] public bool BuiltIn { get; set; }
    [Ed("Порядок в списке", "Меньше — выше", Group = "Основное")] public int SortOrder { get; set; } = 100;
    [Ed("Сохраняемые части", Group = "Содержимое")] public TemplatePartsDef Parts { get; set; } = new();
    [Ed("Правила карты", Group = "Содержимое")] public MapRulesDef Rules { get; set; } = new();
    [EdHidden] public List<MapCellDef> Cells { get; set; } = new();
    [EdHidden] public List<MapMarkerDef> Markers { get; set; } = new();
    [EdHidden] public List<MapRouteDef> Routes { get; set; } = new();

    /// <summary>Bounding size in columns × rows (offset coordinates) for the template list.</summary>
    public (int Columns, int Rows) Size()
    {
        if (Cells.Count == 0) return (0, 0);
        int minCol = int.MaxValue, maxCol = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
        foreach (var c in Cells)
        {
            var (col, row) = HexLayout.ToOffset(c.C);
            if (col < minCol) minCol = col;
            if (col > maxCol) maxCol = col;
            if (c.C.R < minRow) minRow = c.C.R;
            if (c.C.R > maxRow) maxRow = c.C.R;
        }
        return (maxCol - minCol + 1, maxRow - minRow + 1);
    }
}

/// <summary>A route on the map: ordered waypoints; the walking path between them is computed by movement rules.</summary>
public sealed class MapRouteDef : ContentPart
{
    [Ed("ID маршрута")] public string Id { get; set; } = "";
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Точки", "Первая — точка появления, последняя — цель маршрута")] public List<HexCoord> Points { get; set; } = new();

    public MapRouteDef Clone() => new() { Id = Id, Name = Name, Points = new List<HexCoord>(Points) };
}
