using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>Well-known layer ids (more can be added as data).</summary>
public static class Layers
{
    public const string Ground = "ground";
    public const string Air = "air";
    public const string Underground = "underground";
}

/// <summary>
/// Logical position layer of creatures (docs/DECISIONS_2.md §22): affects movement, collisions,
/// detection, targeting, blocking and terrain interaction.
/// </summary>
public sealed class LayerDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Layer;

    [Ed("Высота отображения", "Смещение фишки над поверхностью, уровней высоты (воздух — выше, подземный — ниже)", Group = "Вид"), EdRange(-5, 10)]
    public double DisplayHeight { get; set; }
    [Ed("Полупрозрачный", "Подземные существа видны как тень", Group = "Вид")] public bool Translucent { get; set; }

    [Ed("Игнорирует стоимость местности", "Каждая клетка стоит 1", Group = "Движение")] public bool IgnoresTerrainCost { get; set; }
    [Ed("Игнорирует правила высоты", "Подъём и спуск без ограничений и доп. стоимости", Group = "Движение")] public bool IgnoresHeight { get; set; }
    [Ed("Преодолевает препятствия до высоты", "Уровней: воздушный пролетает над стенами ниже этого значения; 0 — не преодолевает", Group = "Движение"), EdRange(0, 100)]
    public double PassesObstaclesUpTo { get; set; }
    [Ed("Иммунитет к ямам", Group = "Движение")] public bool ImmuneToPits { get; set; }
    [Ed("Сталкивается со слоями", "С существами каких слоёв нельзя делить клетку (пусто — только со своим)", Group = "Движение"), EdRef(DefCategory.Layer)]
    public List<string> CollidesWith { get; set; } = new();

    [Ed("Скрыт", "Виден только наблюдателю с характеристикой обнаружения слоя или вплотную", Group = "Обнаружение")] public bool Hidden { get; set; }
    [Ed("Характеристика обнаружения", "Наблюдатель видит существ слоя, если значение > 0", Group = "Обнаружение"), EdRef(DefCategory.Stat), EdShowIf(nameof(Hidden), true)]
    public string DetectionStat { get; set; } = "";
    [Ed("Дальность раскрытия", "На этой дистанции существо видно всегда", Group = "Обнаружение"), EdRange(0, 20), EdShowIf(nameof(Hidden), true)]
    public int RevealRange { get; set; } = 1;

    [Ed("Блокируется по умолчанию", "Существа слоя могут быть заблокированы", Group = "Бой")] public bool Blockable { get; set; } = true;
}

public sealed class LayerRules : ContentPart
{
    [Ed("Слой по умолчанию"), EdRef(DefCategory.Layer, AllowEmpty = false)] public string Default { get; set; } = Layers.Ground;
    [Ed("Цели ближнего боя", "Если у навыка не задано: ближний бой (дальность ≤ 1, не дистанционный)"), EdRef(DefCategory.Layer)]
    public List<string> MeleeTargets { get; set; } = new() { Layers.Ground };
    [Ed("Цели дальнего боя", "Если у навыка не задано: дистанционные атаки"), EdRef(DefCategory.Layer)]
    public List<string> RangedTargets { get; set; } = new() { Layers.Ground, Layers.Air };
    [Ed("Цели заклинаний и поддержки", "Если у навыка не задано: заклинания, поддержка, утилиты"), EdRef(DefCategory.Layer)]
    public List<string> SpellTargets { get; set; } = new() { Layers.Ground, Layers.Air };
    [Ed("Слои эффектов клеток", "Если у эффекта не задано"), EdRef(DefCategory.Layer)]
    public List<string> CellEffectLayers { get; set; } = new() { Layers.Ground };
}

public enum StackingMode
{
    [Ed("По настройкам типов юнитов")] ByUnitType,
    [Ed("Запрещено всем")] ForbidAll,
    [Ed("Разрешено всем")] AllowAll,
}

public enum StackLayout
{
    [Ed("Кучка")] Cluster,
    [Ed("Ряд")] Row,
    [Ed("Кольцо")] Ring,
}

/// <summary>Sharing a hex with other creatures (docs/DECISIONS_2.md §26).</summary>
public sealed class StackingDef : ContentPart
{
    [Ed("Можно делить клетку", "Совместное нахождение с другими существами (никогда — с враждебными)")] public bool CanShare { get; set; }
    [Ed("Предел на гексе", "Сколько существ всего может стоять на клетке"), EdRange(1, 12), EdShowIf(nameof(CanShare), true)]
    public int MaxPerHex { get; set; } = 3;
    [Ed("С кем можно", "Категории существ или ID типов; пусто — с любыми, кто тоже может делить клетку"), EdShowIf(nameof(CanShare), true)]
    public List<string> ShareWith { get; set; } = new();
    [Ed("Доп. стоимость движения за соседа", "Вход в клетку дороже на это значение за каждого, кто уже там"), EdRange(0, 10), EdShowIf(nameof(CanShare), true)]
    public double CrowdMoveCost { get; set; } = 0.5;
    [Ed("Штраф точности за соседа", "Каждый сосед по клетке снижает точность и уклонение"), EdRange(0, 1), EdShowIf(nameof(CanShare), true)]
    public double CrowdAccuracyPenalty { get; set; } = 0.05;
    [Ed("Раскладка группы"), EdShowIf(nameof(CanShare), true)] public StackLayout Layout { get; set; } = StackLayout.Cluster;

    private static bool Accepts(UnitDef a, UnitDef b)
    {
        var w = a.Stacking.ShareWith;
        if (w.Count == 0) return true;
        foreach (var x in w)
        {
            if (x == b.Id || x == b.CreatureCategory) return true;
            if (x.StartsWith("tag:", System.StringComparison.Ordinal) && b.Tags.Contains(x.Substring(4))) return true;
        }
        return false;
    }

    /// <summary>Both types allow sharing a hex with each other (relation and layer are checked by the caller).</summary>
    public static bool Mutual(UnitDef a, UnitDef b) => a.Stacking.CanShare && b.Stacking.CanShare && Accepts(a, b) && Accepts(b, a);
}
