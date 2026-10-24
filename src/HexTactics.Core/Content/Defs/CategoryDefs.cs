using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

// Category tree (docs/DECISIONS_4.md §61): origins of part 3 become categories with a parent and rules; a rule is
// inherited, added to, overridden, excluded or changed under a condition down the tree.

/// <summary>What a rule of a category gives.</summary>
public enum CategoryRuleKind
{
    [Ed("Пассивное умение")] Passive,
    [Ed("Тег по умолчанию")] Tag,
    [Ed("Допустимый тип перемещения")] MoveCategory,
    [Ed("Запрет действия")] Forbid,
    [Ed("Разрешение действия", "Снимает запрет того же действия")] Allow,
    [Ed("Профиль ИИ по умолчанию", "Если у шаблона профиль не задан")] AiProfile,
    [Ed("Ручной вклад в оценку силы")] PowerBonus,
}

/// <summary>How a rule meets the rule with the same ID higher in the tree.</summary>
public enum CategoryInherit
{
    [Ed("Наследовать", "Передаётся потомкам без изменений")] Inherit,
    [Ed("Дополнить", "Своё вместе с правилом родителя")] Add,
    [Ed("Переопределить", "Заменяет правило родителя с тем же ID")] Override,
    [Ed("Исключить", "Отключает правило родителя с этим ID")] Exclude,
    [Ed("Условно изменить", "Пока условие выполнено — это правило вместо родительского")] Conditional,
}

/// <summary>The action a «запрет» or «разрешение» is about.</summary>
public enum ForbidWhat
{
    [Ed("Стандартная атака")] BasicAttack,
    [Ed("Навыки с тегом")] SkillsWithTag,
    [Ed("Все навыки")] AllSkills,
    [Ed("Движение")] Movement,
}

public sealed class CategoryRuleDef
{
    [Ed("ID правила", "Одинаковый ID у потомка — то же правило (переопределить, исключить, условно изменить)")] public string Id { get; set; } = "";
    [Ed("Что задаёт")] public CategoryRuleKind Kind { get; set; } = CategoryRuleKind.Passive;
    [Ed("Режим наследования")] public CategoryInherit Mode { get; set; } = CategoryInherit.Inherit;
    [Ed("Пассив"), EdRef(DefCategory.Passive), EdShowIf(nameof(Kind), CategoryRuleKind.Passive)] public string Passive { get; set; } = "";
    [Ed("Тег"), EdShowIf(nameof(Kind), CategoryRuleKind.Tag)] public string Tag { get; set; } = "";
    [Ed("Тип перемещения", "walk, fly, swim, burrow, jump…"), EdShowIf(nameof(Kind), CategoryRuleKind.MoveCategory)] public string MoveCategory { get; set; } = "";
    [Ed("Действие"), EdShowIf(nameof(Kind), CategoryRuleKind.Forbid, CategoryRuleKind.Allow)] public ForbidWhat What { get; set; } = ForbidWhat.BasicAttack;
    [Ed("Тег навыков"), EdShowIf(nameof(What), ForbidWhat.SkillsWithTag)] public string SkillTag { get; set; } = "";
    [Ed("Профиль ИИ"), EdRef(DefCategory.AiProfile), EdShowIf(nameof(Kind), CategoryRuleKind.AiProfile)] public string AiProfile { get; set; } = "";
    [Ed("Вклад в силу", "Ручная поправка оценки силы (этап 6), в процентах"), EdRange(-100, 1000), EdShowIf(nameof(Kind), CategoryRuleKind.PowerBonus)] public double Power { get; set; }
    [Ed("Приоритет", "Между разными доп. категориями: больше — сильнее"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Условие", "Для «условно изменить»: пока выполнено — действует это правило"), EdShowIf(nameof(Mode), CategoryInherit.Conditional)] public UnitConditionDef? Condition { get; set; }

    /// <summary>The action key of a forbid/allow (conflicts are found by it).</summary>
    public string ActionKey => What == ForbidWhat.SkillsWithTag ? "skills:" + SkillTag : What.ToString();

    public string Describe(ContentDatabase? db = null) => Kind switch
    {
        CategoryRuleKind.Passive => $"пассив «{db?.Get<PassiveDef>(Passive)?.DisplayName ?? Passive}»",
        CategoryRuleKind.Tag => $"тег «{Tag}»",
        CategoryRuleKind.MoveCategory => $"перемещение «{MoveCategory}»",
        CategoryRuleKind.Forbid => "запрет: " + ActionText(),
        CategoryRuleKind.Allow => "разрешение: " + ActionText(),
        CategoryRuleKind.AiProfile => $"профиль ИИ «{db?.Get<AiProfileDef>(AiProfile)?.DisplayName ?? AiProfile}»",
        CategoryRuleKind.PowerBonus => $"вклад в силу {Power:+0;−0} %",
        _ => Kind.ToString(),
    };

    private string ActionText() => What switch
    {
        ForbidWhat.BasicAttack => "стандартная атака",
        ForbidWhat.SkillsWithTag => $"навыки с тегом «{SkillTag}»",
        ForbidWhat.AllSkills => "все навыки",
        _ => "движение",
    };

    public override string ToString() => $"{(Id == "" ? "" : Id + ": ")}{Describe()} ({Mode})";
}

/// <summary>An extra way of moving (docs/DECISIONS_4.md §60): acts while its condition holds.</summary>
public sealed class MoveTypeDef
{
    [Ed("Тип перемещения", "walk, fly, swim, burrow, jump…")] public string Category { get; set; } = "swim";
    [Ed("Условие", "Пусто — всегда (например, «есть состояние Полёт»)")] public UnitConditionDef? Condition { get; set; }

    public override string ToString() => Category + (Condition != null ? " (условно)" : "");
}

/// <summary>Which side the units counted by «за каждого» are on (relative to the owner).</summary>
public enum CountSide
{
    [Ed("Союзники")] Allies,
    [Ed("Противники")] Enemies,
    [Ed("Все")] All,
}

/// <summary>
/// «За каждого» (docs/DECISIONS_4.md §62): the modifier's value is multiplied by the number of counted units — by
/// tags and categories, side, radius; summoned, dead and outside-the-aura units only when allowed — and capped.
/// </summary>
public sealed class PerEachDef
{
    [Ed("Теги или категории", "Учитываемые: «tag:стая», ID категории, категория существа, ID шаблона; пусто — любые")] public List<string> Types { get; set; } = new();
    [Ed("Кто")] public CountSide Side { get; set; } = CountSide.Allies;
    [Ed("Радиус", "0 — вся карта"), EdRange(0, 50)] public int Radius { get; set; } = 3;
    [Ed("Учитывать себя")] public bool IncludeSelf { get; set; }
    [Ed("Учитывать призванных")] public bool IncludeSummoned { get; set; } = true;
    [Ed("Учитывать погибших", "Погибшие за бой (вне радиуса тоже)")] public bool IncludeDead { get; set; }
    [Ed("Учитывать вне радиуса", "Все подходящие на карте")] public bool IncludeOutside { get; set; }
    [Ed("Предел величины", "Наибольшая итоговая величина модификатора (по модулю); 0 — без предела"), EdRange(0, 100_000)] public double MaxValue { get; set; }
    [Ed("Предел количества", "0 — без предела"), EdRange(0, 1000)] public int MaxCount { get; set; }

    public override string ToString() => $"за каждого ({(Types.Count == 0 ? "любого" : string.Join(", ", Types))}, {Side}, r={Radius})" + (MaxValue > 0 ? $", не более {MaxValue:0.##}" : "");
}

/// <summary>A variable of the scenario (docs/DECISIONS_4.md §62): time of day, weather… changed by events.</summary>
public sealed class EnvironmentVarDef
{
    [Ed("Имя", "Например time_of_day, weather")] public string Name { get; set; } = "time_of_day";
    [Ed("Значение в начале")] public string Value { get; set; } = "day";
    [Ed("Подпись в бою", "Пусто — не показывать")] public string Caption { get; set; } = "";

    public override string ToString() => $"{Name} = {Value}";
}
