using System.Collections.Generic;
using System.Text.Json.Serialization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

// Group orders and formations (docs/DECISIONS_3.md §44).

/// <summary>Role of a unit in group orders (formation slots, group attack).</summary>
public enum UnitRole
{
    [Ed("Авто (по обычной атаке)")] Auto,
    [Ed("Ближний бой")] Melee,
    [Ed("Дальний бой")] Ranged,
    [Ed("Маг")] Caster,
    [Ed("Поддержка")] Support,
    [Ed("Танк")] Tank,
}

public enum FormationKind
{
    [Ed("Слоты (свои позиции)")] Slots,
    [Ed("Свободное построение")] Free,
    [Ed("Окружение цели")] Surround,
    [Ed("Оборонительный периметр")] Perimeter,
}

public sealed class FormationSlotDef : ContentPart
{
    [Ed("Смещение", "Относительно точки приказа при движении на восток")] public HexCoord Offset { get; set; }
    [Ed("Роль", "Авто — любой юнит")] public UnitRole Role { get; set; } = UnitRole.Auto;

    public override string ToString() => $"{Offset}" + (Role != UnitRole.Auto ? $" {Role}" : "");
}

/// <summary>
/// A formation: preferred positions relative to the order's point (or target), rotated with the direction
/// of the group's movement. Occupied, impassable or wrong-height slots are replaced by the nearest valid hex.
/// </summary>
public sealed class FormationDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Formation;

    [Ed("Вид")] public FormationKind Kind { get; set; } = FormationKind.Slots;
    [Ed("Слоты", "Позиции по порядку заполнения; первая — у точки приказа"), EdShowIf(nameof(Kind), FormationKind.Slots)]
    public List<FormationSlotDef> Slots { get; set; } = new();
    [Ed("Радиус", "Окружение и периметр: расстояние от цели/точки"), EdRange(1, 6), EdShowIf(nameof(Kind), FormationKind.Surround, FormationKind.Perimeter)]
    public int Radius { get; set; } = 1;
    [Ed("Поворачивать по движению", "Слоты поворачиваются в сторону движения группы")] public bool RotateWithDirection { get; set; } = true;
    [Ed("Взгляд наружу", "Периметр: каждый юнит смотрит от центра")] public bool FaceOutward { get; set; }
}

public enum GroupQueueMode
{
    [Ed("Заменить очередь")] Replace,
    [Ed("Добавить в конец")] Append,
    [Ed("После текущего действия")] AfterCurrent,
}

public enum TargetDeathBehavior
{
    [Ed("Остановиться")] Stop,
    [Ed("Следующая цель")] NextTarget,
    [Ed("Продолжить к последней точке")] ContinueToPoint,
    [Ed("Цель по приоритету")] PriorityTarget,
}

public enum NoSpaceBehavior
{
    [Ed("Занять доступные, сообщить о частичном выполнении")] Partial,
    [Ed("Ждать освобождения мест")] Wait,
}
