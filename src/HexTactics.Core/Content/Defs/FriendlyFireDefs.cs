using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

// Friendly fire (ТЗ ч. 6, 19; docs/DECISIONS_6.md §105): a rule on top of the filter of an effect, per kind of impact, on the levels
// «правила боя → правила режима → сценарий → навык или эффект клетки → объект»; «наследовать» takes the level above.

/// <summary>The kinds of impact the rules are told apart by (ТЗ 19.4: an area and a direct attack may differ).</summary>
public enum FfKind
{
    [Ed("Прямая атака")] Direct,
    [Ed("Область")] Area,
    [Ed("Поля на гексах")] Field,
    [Ed("Объекты")] Object,
}

/// <summary>Who a harmful effect may reach (ТЗ 19.2).</summary>
public enum FfTargets
{
    [Ed("Наследовать")] Inherit,
    [Ed("Только враждебные")] HostileOnly,
    [Ed("Враждебные и нейтральные")] HostileAndNeutral,
    [Ed("Все, кроме источника")] AllExceptSource,
    [Ed("Все, включая источник")] AllIncludingSource,
    [Ed("Своё правило", "По отношениям, видам эффектов и тегам")] Custom,
}

/// <summary>When the relations of a field are taken (ТЗ 19.2, 19.4).</summary>
public enum FfRelationAt
{
    [Ed("Наследовать")] Inherit,
    [Ed("При срабатывании", "Фракция создателя сейчас (погиб — сохранённая при создании)")] Live,
    [Ed("При создании", "Фракция создателя, сохранённая при создании поля")] Creation,
}

/// <summary>A rule of friendly fire of one level.</summary>
public sealed class FriendlyFireDef : ContentPart
{
    [Ed("Кого задевает вредное", "Урон, ослабления, контроль; лечение и усиления правило урона не блокирует")] public FfTargets Targets { get; set; } = FfTargets.Inherit;
    [Ed("Нейтралов"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool Neutrals { get; set; } = true;
    [Ed("Себя", "Самоповреждение — только явно"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool Self { get; set; }
    [Ed("Союзников уроном"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool AlliesDamage { get; set; }
    [Ed("Союзников лечением"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool AlliesHealing { get; set; } = true;
    [Ed("Союзников усилениями"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool AlliesBuffs { get; set; } = true;
    [Ed("Союзников ослаблениями"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool AlliesDebuffs { get; set; }
    [Ed("Союзников контролем", "Толчок, притяжение, состояния контроля"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool AlliesControl { get; set; }
    [Ed("Своих призванных"), EdShowIf(nameof(Targets), FfTargets.Custom)] public bool OwnSummons { get; set; }
    [Ed("Не задевать с тегами", "Вредное не действует на юнитов с этими тегами"), EdShowIf(nameof(Targets), FfTargets.Custom)] public List<string> IgnoreTags { get; set; } = new();
    [Ed("Задевать в области всех", "Режим «попутный урон»: вредное в области — и тем, кого фильтр эффекта не выбирает")] public bool Collateral { get; set; }
    [Ed("Отношения поля", "Полям на гексах: на момент срабатывания или создания")] public FfRelationAt RelationAt { get; set; } = FfRelationAt.Inherit;

    public string Describe() => Targets == FfTargets.Custom
        ? "своё: " + string.Join(", ", new[]
        {
            Neutrals ? "нейтралы" : "", Self ? "себя" : "", AlliesDamage ? "союзников уроном" : "", AlliesDebuffs ? "союзников ослаблением" : "",
            AlliesControl ? "союзников контролем" : "", OwnSummons ? "своих призванных" : "", !AlliesHealing ? "без лечения союзников" : "", !AlliesBuffs ? "без усилений союзников" : "",
        }.Where(x => x != "").DefaultIfEmpty("только враждебные"))
        : EdUtil.EnumLabel(Targets).ToLowerInvariant();

    public override string ToString() => Describe() + (Collateral ? ", попутный урон" : "");
}

/// <summary>Rules of friendly fire per kind of impact (a level: battle rules, rules of the mode, scenario).</summary>
public sealed class FriendlyFireRulesDef : ContentPart
{
    [Ed("Прямая атака")] public FriendlyFireDef? Direct { get; set; }
    [Ed("Область")] public FriendlyFireDef? Area { get; set; }
    [Ed("Поля на гексах", "Огонь, яд, ловушки")] public FriendlyFireDef? Fields { get; set; }
    [Ed("Объекты", "Турели и опасности объектов")] public FriendlyFireDef? Objects { get; set; }

    public FriendlyFireDef? Of(FfKind k) => k switch
    {
        FfKind.Direct => Direct,
        FfKind.Area => Area,
        FfKind.Field => Fields,
        _ => Objects,
    };
}

/// <summary>Moving through fields by a teleport (ТЗ 19.3).</summary>
public enum TeleportFields
{
    [Ed("Только гекс прибытия")] DestinationOnly,
    [Ed("Пройденные гексы", "Вход и выход полей по линии перемещения")] Traversed,
}
