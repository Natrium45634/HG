using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>How a characteristic acts (ТЗ ч. 5, 12.5).</summary>
public enum StatUse
{
    /// <summary>Read by a formula or a rule of the core.</summary>
    Core,
    /// <summary>Not read by the core, but content uses it (modifiers, conditions, formulas, skills).</summary>
    Content,
    /// <summary>A field without behaviour: nothing reads it — it promises no effect.</summary>
    None,
}

/// <summary>
/// Which characteristics the core reads and where (ТЗ ч. 5, 12.5; docs/DECISIONS_5.md §87): the editor marks every
/// characteristic «подключено к ядру», «используется контентом» or «пользовательское поле без поведения», so a custom one
/// never promises an effect «сама собой».
/// </summary>
public static class StatUsage
{
    public static readonly IReadOnlyDictionary<string, string> Core = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Stats.HpMax] = "максимум здоровья",
        [Stats.MoveSpeed] = "скорость шага",
        [Stats.ApMax] = "запас ОД",
        [Stats.ApRegen] = "восстановление ОД",
        [Stats.DetectionRange] = "дальность обнаружения",
        [Stats.Accuracy] = "формула попадания (accuracy)",
        [Stats.Evasion] = "формула попадания (evasion)",
        [Stats.BlockChance] = "шанс блока",
        [Stats.BlockReduction] = "снижение урона блоком",
        [Stats.BlockLimit] = "предел блока",
        [Stats.Armor] = "формула урона (armor)",
        [Stats.CritChance] = "шанс крита",
        [Stats.CritMult] = "множитель крита",
        [Stats.CritBonus] = "бонус крита",
        [Stats.CritResist] = "защита от критов",
        [Stats.Perception] = "восприятие (обнаружение скрытых)",
        [Stats.Stealth] = "скрытность",
        [Stats.InterruptResist] = "устойчивость к прерыванию",
        [Stats.Power] = "сила навыков",
        [Stats.MagicPower] = "сила магии",
        [Stats.HealPower] = "сила лечения",
        [Stats.HealReceived] = "получаемое лечение",
        [Stats.ShieldPower] = "сила щитов",
        [Stats.BaseDamage] = "базовый урон атак",
        [Stats.AttackRange] = "дальность атак",
        [Stats.CastSpeed] = "скорость подготовки навыков",
        [Stats.ControlResist] = "устойчивость к контролю, формула отталкивания (resist)",
        [Stats.Weight] = "масса: отталкивание (mass)",
        [Stats.Size] = "размер: узкие проходы",
        [Stats.PursuitRange] = "дальность преследования ИИ",
    };

    /// <summary>The use of a characteristic and the words for it.</summary>
    public static (StatUse Use, string Text) Of(ContentDatabase db, string statId)
    {
        if (Core.TryGetValue(statId, out var where)) return (StatUse.Core, "Подключено к ядру: " + where);
        if (statId.StartsWith("resist_", StringComparison.Ordinal) && db.DamageType(statId[7..]) != null)
            return (StatUse.Core, $"Подключено к ядру: сопротивление урону «{db.DamageType(statId[7..])!.DisplayName}» в формуле урона (resist)");
        var stat = db.Stat(statId);
        // Units only set a value; a use is a modifier, a condition, a formula, a skill reading it.
        var users = stat == null ? new List<Def>() : ContentDependencies.UsedBy(db, stat).Where(d => d is not UnitDef).ToList();
        if (users.Count > 0)
            return (StatUse.Content, $"Пользовательская: ядро её не читает; действует только через {users.Count} объект(ов) контента — " +
                                     string.Join(", ", users.Take(5).Select(u => $"«{u.DisplayName}»")) + (users.Count > 5 ? "…" : ""));
        return (StatUse.None, "Пользовательское поле без поведения: ни ядро, ни контент её не читают — само по себе значение ничего не меняет");
    }
}
