using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace HexTactics.Core.Content;

/// <summary>How the renderer draws a terrain type (purely visual).</summary>
public enum TerrainVisual
{
    Ground,
    Grass,
    Sand,
    Stone,
    Water,
    Pit,
    Wall,
    Rock,
    Crate,
    Vegetation,
    Hazard,
    Special,
    Ice,
    Rubble,
}

public sealed class TerrainDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Terrain;

    [Ed("Цвет", Group = "Вид"), EdColor]
    public string Color { get; set; } = "#8a9a6a";

    [Ed("Стиль отрисовки", Group = "Вид")]
    public TerrainVisual Visual { get; set; } = TerrainVisual.Ground;

    [Ed("Стоимость перемещения", "Стоимость входа в клетку (до учёта высоты и эффектов)", Group = "Движение"), EdRange(0.1, 100)]
    public double MoveCost { get; set; } = 1.0;

    [Ed("Проходима для", "Категории движения, которые могут войти в клетку", Group = "Движение"), EdRef(DefCategory.MoveCategory)]
    public List<string> PassableFor { get; set; } = new() { "walk", "fly" };

    [Ed("Стоимость по категориям", "Переопределение стоимости для категории движения", Group = "Движение"), EdKeyRef(DefCategory.MoveCategory)]
    public Dictionary<string, double> MoveCostByCategory { get; set; } = new();

    [Ed("Высота по умолчанию", "Высота при рисовании кистью (возвышенность +1, низина -1)", Group = "Движение"), EdRange(-5, 10)]
    public int DefaultHeight { get; set; }

    [Ed("Блокирует обзор", Group = "Обзор и укрытие")]
    public bool BlocksLoS { get; set; }

    [Ed("Высота препятствия", "В уровнях высоты, над поверхностью клетки (для линии видимости)", Group = "Обзор и укрытие"), EdRange(0, 20)]
    public double ObstacleHeight { get; set; }

    [Ed("Укрытие", "0..1, штраф к точности дистанционных атак по юниту в клетке", Group = "Обзор и укрытие"), EdRange(0, 1)]
    public double Cover { get; set; }

    [Ed("Можно размещать юнитов", Group = "Прочее")]
    public bool AllowsPlacement { get; set; } = true;

    [Ed("Опасная", "ИИ и маршрутизатор избегают клетку, интерфейс предупреждает", Group = "Прочее")]
    public bool Hazard { get; set; }

    [Ed("Прочность препятствия", "0 = неразрушимо", Group = "Разрушение"), EdRange(0, 100000)]
    public int DestructibleHp { get; set; }

    [Ed("Становится после разрушения", Group = "Разрушение"), EdRef(DefCategory.Terrain)]
    public string DestroyedTerrain { get; set; } = "";

    [Ed("Постоянные эффекты", "Эффекты клеток, всегда действующие на этой местности", Group = "Прочее"), EdRef(DefCategory.CellEffect)]
    public List<string> IntrinsicEffects { get; set; } = new();

    public bool IsPassableFor(string moveCategory) => PassableFor.Contains(moveCategory);

    public double CostFor(string moveCategory) =>
        MoveCostByCategory.TryGetValue(moveCategory, out var c) ? c : MoveCost;
}
