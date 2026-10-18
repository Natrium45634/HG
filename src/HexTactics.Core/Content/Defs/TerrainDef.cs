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
    [Ed("Технопол")] TechFloor,
    [Ed("Платформа")] Platform,
    [Ed("Лава")] Lava,
    [Ed("Кислота")] Acid,
    [Ed("Барьер")] Barrier,
    [Ed("Ловушка")] Trap,
    [Ed("Пропасть (без дна)")] Chasm,
    [Ed("Колонны")] Pillars,
    [Ed("Ограждение")] Railing,
    [Ed("Терминал")] Terminal,
}

public enum TerrainMaterial
{
    [Ed("Матовый")] Matte,
    [Ed("Металл")] Metal,
    [Ed("Глянец")] Glossy,
    [Ed("Стекло")] Glass,
    [Ed("Жидкость")] Liquid,
    [Ed("Свечение")] Emissive,
    [Ed("Голограмма")] Hologram,
}

public enum TerrainPattern
{
    [Ed("Нет")] None,
    [Ed("Полосы")] Stripes,
    [Ed("Сетка")] Grid,
    [Ed("Шевроны")] Chevrons,
    [Ed("Точки")] Dots,
    [Ed("Крест")] Cross,
    [Ed("Предупреждение (жёлто-чёрные полосы)")] Hazard,
    [Ed("Кольцо")] Ring,
}

public enum TerrainAnimation
{
    [Ed("Нет")] None,
    [Ed("Течение")] Flow,
    [Ed("Пульсация")] Pulse,
    [Ed("Блики")] Shimmer,
}

/// <summary>
/// Visual parameters of a terrain type (docs/DECISIONS_2.md §20). Purely presentational: the
/// simulation never reads them. Colour is never the only cue — patterns, symbols, outline, depth.
/// </summary>
public sealed class TerrainLookDef
{
    [Ed("Материал")] public TerrainMaterial Material { get; set; } = TerrainMaterial.Matte;
    [Ed("Цвет боковых граней", "Пусто — темнее верха"), EdColor] public string SideColor { get; set; } = "";
    [Ed("Акцентный цвет", "Кромка, свечение, узор"), EdColor] public string AccentColor { get; set; } = "#5fd4ff";
    [Ed("Доп. высота плиты", "Уровней высоты над поверхностью (стены, платформы)"), EdRange(0, 10)] public double ExtraHeight { get; set; }
    [Ed("Фаска", "Доля радиуса гекса"), EdRange(0, 0.3)] public double Bevel { get; set; } = 0.06;
    [Ed("Прозрачность"), EdRange(0, 0.95)] public double Transparency { get; set; }
    [Ed("Свечение", "Сила свечения акцентным цветом (0 — нет)"), EdRange(0, 8)] public double Glow { get; set; }
    [Ed("Отбрасывает тень")] public bool CastShadow { get; set; } = true;
    [Ed("Цвет контура", "Обводка верхней грани; пусто — нет"), EdColor] public string OutlineColor { get; set; } = "";
    [Ed("Толщина контура"), EdRange(0, 0.2)] public double OutlineWidth { get; set; } = 0.035;
    [Ed("Узор", "Дополнительный признак помимо цвета")] public TerrainPattern Pattern { get; set; } = TerrainPattern.None;
    [Ed("Символ", "1–2 знака поверх клетки (⚠, ☣, ✕…)")] public string Symbol { get; set; } = "";
    [Ed("Глубина", "Ямы и жидкости: глубина в уровнях высоты"), EdRange(0, 30)] public double Depth { get; set; }
    [Ed("Анимация")] public TerrainAnimation Animation { get; set; } = TerrainAnimation.None;
}

public enum FallConsequence
{
    [Ed("Гибель")] Death,
    [Ed("Урон и возврат на безопасную клетку")] DamageAndReturn,
    [Ed("Убрать из боя (без гибели)")] Remove,
}

public enum FallAnimation
{
    [Ed("Падение вниз")] Drop,
    [Ed("Растворение")] Fade,
    [Ed("Уменьшение")] Shrink,
    [Ed("Без анимации")] None,
}

/// <summary>Pit / chasm behaviour (docs/DECISIONS_2.md §21).</summary>
public sealed class PitRulesDef
{
    [Ed("Можно войти", "Существо может оказаться в клетке (и упасть); иначе клетка просто непроходима")] public bool Enterable { get; set; } = true;
    [Ed("Последствие падения")] public FallConsequence Consequence { get; set; } = FallConsequence.Death;
    [Ed("Урон при падении", "Для «урон и возврат»"), EdRange(0, 100000), EdShowIf(nameof(Consequence), FallConsequence.DamageAndReturn)]
    public double FallDamage { get; set; } = 30;
    [Ed("Тип урона падения"), EdRef(DefCategory.DamageType), EdShowIf(nameof(Consequence), FallConsequence.DamageAndReturn)]
    public string DamageType { get; set; } = "true";
    [Ed("Действует на слои", "Пусто — только наземный"), EdRef(DefCategory.Layer)] public List<string> AffectedLayers { get; set; } = new();
    [Ed("Иммунные категории/теги", "Категории существ или tag:тег юнита")] public List<string> Immune { get; set; } = new();
    [Ed("Спасающие состояния", "Пока наложено — не падает (парение, крюк…)"), EdRef(DefCategory.Status)] public List<string> RescueStatuses { get; set; } = new();
    [Ed("Анимация падения")] public FallAnimation Animation { get; set; } = FallAnimation.Drop;
    [Ed("Длительность анимации, с"), EdRange(0, 10)] public double AnimationSeconds { get; set; } = 0.8;
}

/// <summary>Logical class of a terrain type (library filters, templates, editor brushes).</summary>
public enum TerrainKind
{
    [Ed("Пол")] Floor,
    [Ed("Возвышенность")] Elevation,
    [Ed("Препятствие")] Obstacle,
    [Ed("Жидкость")] Liquid,
    [Ed("Яма / провал")] Pit,
    [Ed("Опасная поверхность")] Hazard,
    [Ed("Особая")] Special,
}

public sealed class TerrainDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Terrain;

    [Ed("Вид местности", "Пол, препятствие, жидкость, яма… — для библиотеки, шаблонов и кистей", Group = "Основное")]
    public TerrainKind Kind { get; set; } = TerrainKind.Floor;

    [JsonIgnore] public bool IsObstacle => Kind == TerrainKind.Obstacle;

    [Ed("Цвет", Group = "Вид"), EdColor]
    public string Color { get; set; } = "#8a9a6a";

    [Ed("Стиль отрисовки", "Форма и декор (камни, контейнеры, колонны…)", Group = "Вид")]
    public TerrainVisual Visual { get; set; } = TerrainVisual.Ground;

    [Ed("Внешний вид", "Материал, высота, фаска, прозрачность, свечение, тень, контур, узор, символ", Group = "Вид")]
    public TerrainLookDef Look { get; set; } = new();

    [Ed("Яма", "Поведение при падении", Group = "Яма"), EdShowIf(nameof(Kind), TerrainKind.Pit)]
    public PitRulesDef Pit { get; set; } = new();

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
