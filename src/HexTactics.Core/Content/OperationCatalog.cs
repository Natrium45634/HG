using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>Where an entry of the catalog can be used.</summary>
[Flags]
public enum CatalogUse
{
    None = 0,
    /// <summary>Skills, passives, cell effects, status ticks (SkillEffectDef).</summary>
    Ability = 1,
    /// <summary>Scenario triggers, waves, objectives, boss phases, AI rules (TriggerActionDef / ConditionDef).</summary>
    Scenario = 2,
    All = Ability | Scenario,
}

/// <summary>An operation or condition of the editor catalog (docs/DECISIONS_2.md §33).</summary>
public sealed class CatalogEntry
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; }
    public string Description { get; init; } = "";
    /// <summary>The type of definition the entry creates: SkillEffectDef, TriggerActionDef or ConditionDef.</summary>
    public required Type ItemType { get; init; }
    public required Func<object> Create { get; init; }
    /// <summary>Items of TZ §15.1/§15.2 this entry covers (for the coverage check and the help).</summary>
    public string[] Covers { get; init; } = Array.Empty<string>();
}

/// <summary>
/// The catalog of ready operations and conditions the editor assembles skills, passives, cell
/// effects, statuses, enemy behaviour and scenario events from (TZ §15). Every entry creates an
/// ordinary content object with the right type and sensible defaults — no code.
/// </summary>
public static class OperationCatalog
{
    private static CatalogEntry Op(string key, string name, string group, string desc, EffectType type, Action<SkillEffectDef>? init = null, params string[] covers) =>
        new()
        {
            Key = "op." + key, Name = name, Group = group, Description = desc, ItemType = typeof(SkillEffectDef), Covers = covers,
            Create = () =>
            {
                var e = new SkillEffectDef { Type = type };
                if (e.IsGlobal || type is EffectType.ModifyStat or EffectType.SetRoute or EffectType.SetAiProfile) e.Amount = 0;
                init?.Invoke(e);
                return e;
            },
        };

    private static CatalogEntry Act(string key, string name, string group, string desc, TriggerActionType type, Action<TriggerActionDef>? init = null, params string[] covers) =>
        new()
        {
            Key = "act." + key, Name = name, Group = group, Description = desc, ItemType = typeof(TriggerActionDef), Covers = covers,
            Create = () =>
            {
                var a = new TriggerActionDef { Type = type };
                init?.Invoke(a);
                return a;
            },
        };

    private static CatalogEntry Cond(string key, string name, string group, string desc, ConditionType type, Action<ConditionDef>? init = null, params string[] covers) =>
        new()
        {
            Key = "cond." + key, Name = name, Group = group, Description = desc, ItemType = typeof(ConditionDef), Covers = covers,
            Create = () =>
            {
                var c = new ConditionDef { Type = type };
                init?.Invoke(c);
                return c;
            },
        };

    /// <summary>Operations of skills, passives, cell effects and status ticks.</summary>
    public static readonly IReadOnlyList<CatalogEntry> Abilities = new[]
    {
        Op("damage", "Нанести урон", "Урон и лечение", "Урон выбранного типа через единый расчёт (сопротивления, щиты, поглощение, отражение).", EffectType.Damage, null, "урон"),
        Op("heal", "Лечение", "Урон и лечение", "Фиксированное, % от максимума или % от недостающего здоровья.", EffectType.Heal, null, "лечение"),
        Op("resource", "Изменить ресурс", "Ресурсы", "Расход (отрицательная величина) или восстановление ресурса (мана, ярость…).", EffectType.ModifyResource, null, "ресурсы"),
        Op("ap", "Изменить ОД", "Ресурсы", "Отнять или добавить очки действия.", EffectType.ModifyAp, null, "ресурсы"),
        Op("status", "Наложить состояние", "Состояния", "Временное или накопительное состояние; длительность и режим (до конца боя, пока условие…) задаются в состоянии.", EffectType.ApplyStatus, null, "состояния", "временные модификаторы"),
        Op("unstatus", "Снять состояние", "Состояния", "Снимает все экземпляры состояния.", EffectType.RemoveStatus, null, "состояния"),
        Op("accumulate", "Изменить накопление", "Состояния", "Добавить или отнять очки накопительного состояния (стаки временного).", EffectType.ModifyAccumulation, e => e.Amount = -10, "накопление"),
        Op("stun", "Оглушение", "Контроль", "Состояние «Оглушён»: не действует, подготовка прерывается.", EffectType.ApplyStatus, e => { e.Status = "stunned"; e.Amount = 1; e.Duration = 2; }, "оглушение"),
        Op("slow", "Замедление", "Контроль", "Состояние «Замедлен»: снижает скорость.", EffectType.ApplyStatus, e => { e.Status = "slowed"; e.Amount = 1; e.Duration = 4; }, "замедление"),
        Op("root", "Обездвиживание", "Контроль", "Состояние «Обездвижен»: не может перемещаться.", EffectType.ApplyStatus, e => { e.Status = "rooted"; e.Amount = 1; e.Duration = 3; }, "обездвиживание"),
        Op("push", "Толчок", "Перемещение", "Оттолкнуть цель на N клеток (останавливается у препятствий).", EffectType.Push, null, "перемещение"),
        Op("pull", "Притяжение", "Перемещение", "Притянуть цель на N клеток.", EffectType.Pull, null, "перемещение"),
        Op("facing", "Изменить направление", "Перемещение", "Развернуть цель к источнику, от него, в обратную или заданную сторону.", EffectType.ChangeFacing, null, "направление"),
        Op("teleport", "Переместить исполнителя", "Перемещение", "Исполнитель переносится в точку навыка.", EffectType.TeleportCaster, null, "перемещение"),
        Op("stat_timed", "Изменить характеристику на время", "Характеристики", "Прибавка и/или множитель характеристики на заданное время.", EffectType.ModifyStat, e => { e.Stat = Stats.Armor; e.StatAdd = 3; e.Duration = 10; }, "временное изменение характеристик"),
        Op("stat_battle", "Изменить характеристику до конца боя", "Характеристики", "Постоянное (до конца боя) изменение, не снимается очищением.", EffectType.ModifyStat, e => { e.Stat = Stats.Power; e.StatAdd = 2; e.Duration = 0; }, "постоянное изменение характеристик"),
        Op("shield", "Создать щит", "Защита", "Щит с объёмом, длительностью, фильтром урона и правилами наложения.", EffectType.Shield, null, "щиты"),
        Op("unshield", "Снять щит", "Защита", "Снять щит по названию или все щиты.", EffectType.RemoveShield, null, "щиты"),
        Op("absorb", "Поглощение урона", "Защита", "Состояние «Барьер»: часть урона поглощается (защита уровня состояния).", EffectType.ApplyStatus, e => { e.Status = "absorb_ward"; e.Amount = 1; }, "поглощение"),
        Op("reflect", "Отражение урона", "Защита", "Состояние «Зеркальный щит»: часть урона возвращается атакующему.", EffectType.ApplyStatus, e => { e.Status = "mirror_ward"; e.Amount = 1; }, "отражение"),
        Op("convert", "Преобразование урона", "Защита", "Состояние «Заземление»: часть входящего урона меняет тип.", EffectType.ApplyStatus, e => { e.Status = "grounding"; e.Amount = 1; }, "преобразование"),
        Op("summon", "Призвать юнита", "Призыв", "Призыв на клетку навыка; длительность — время жизни.", EffectType.Summon, null, "призыв"),
        Op("cell_effect", "Создать эффект клетки", "Клетки и местность", "Огонь, яд, лёд… с уровнем и длительностью.", EffectType.AddCellEffect, null, "эффекты клеток"),
        Op("uncell_effect", "Удалить эффект клетки", "Клетки и местность", "Убирает эффект с клеток области.", EffectType.RemoveCellEffect, null, "эффекты клеток"),
        Op("obstacle", "Создать препятствие", "Клетки и местность", "Сменить местность на препятствие (не замуровывает юнитов).", EffectType.ChangeTerrain, e => e.Terrain = "rock", "препятствия"),
        Op("terrain", "Сменить местность", "Клетки и местность", "Любая местность, в том числе обрушение пола в яму.", EffectType.ChangeTerrain, null, "препятствия"),
        Op("destroy", "Разрушить препятствие", "Клетки и местность", "Урон разрушаемым препятствиям в области.", EffectType.DestroyObstacle, null, "препятствия"),
        Op("route", "Сменить маршрут", "ИИ и маршруты", "Цель пойдёт по маршруту карты.", EffectType.SetRoute, null, "маршруты"),
        Op("ai", "Сменить поведение (профиль ИИ)", "ИИ и маршруты", "Приоритеты целей, дистанция, отступление.", EffectType.SetAiProfile, null, "приоритеты"),
        Op("trigger", "Запустить триггер сценария", "Сценарий", "Выполняет действия триггера сценария по названию.", EffectType.ActivateTrigger, null, "триггеры"),
        Op("flag", "Событие (флаг) сценария", "Сценарий", "Устанавливает флаг, на который реагируют условия.", EffectType.SetFlag, null, "триггеры"),
        Op("message", "Сообщение", "Сообщения и эффекты", "Текст в журнале боя.", EffectType.Message, null, "сообщения"),
        Op("vfx", "Визуальный эффект", "Сообщения и эффекты", "Запуск визуального эффекта по ID (ресурсы — раздел «Ресурсы»).", EffectType.PlayVfx, null, "визуальные эффекты"),
        Op("sound", "Звук", "Сообщения и эффекты", "Запуск звука по ID.", EffectType.PlaySound, null, "звуковые эффекты"),
    };

    /// <summary>Actions of triggers, waves, objectives, boss phases and AI rules.</summary>
    public static readonly IReadOnlyList<CatalogEntry> Actions = new[]
    {
        Act("effects", "Операции над юнитами", "Юниты", "Любые операции навыков (урон, лечение, ресурсы, состояния, щиты, характеристики, толчок…) для выбранных юнитов.", TriggerActionType.Effects,
            a => a.Effects.Add(new SkillEffectDef { Type = EffectType.Damage, Amount = 20 }), "урон", "лечение", "ресурсы", "щиты"),
        Act("status", "Наложить состояние", "Юниты", "", TriggerActionType.ApplyStatus, null, "состояния"),
        Act("unstatus", "Снять состояние", "Юниты", "", TriggerActionType.RemoveStatus, null, "состояния"),
        Act("heal", "Лечение, % HP", "Юниты", "", TriggerActionType.HealPercent, a => a.Amount = 25, "лечение"),
        Act("summon", "Призвать юнитов", "Юниты", "Рядом с меткой карты или владельцем.", TriggerActionType.SummonUnits, null, "призыв"),
        Act("add_skill", "Добавить навык", "Юниты", "", TriggerActionType.AddSkill),
        Act("remove_skill", "Убрать навык", "Юниты", "", TriggerActionType.RemoveSkill),
        Act("auto_skill", "Сменить навык автоатаки", "Юниты", "", TriggerActionType.SetAutoAttackSkill),
        Act("ai", "Сменить профиль ИИ", "ИИ и маршруты", "Изменение приоритетов и поведения.", TriggerActionType.SetAiProfile, null, "приоритеты"),
        Act("route", "Сменить маршрут", "ИИ и маршруты", "", TriggerActionType.SetRoute, null, "маршруты"),
        Act("cell_effect", "Эффект клетки в метке", "Карта", "", TriggerActionType.AddCellEffect, null, "эффекты клеток"),
        Act("terrain", "Сменить местность (создать препятствие)", "Карта", "", TriggerActionType.ChangeTerrain, null, "препятствия"),
        Act("destroy", "Разрушить препятствия", "Карта", "", TriggerActionType.DestroyObstacle, null, "препятствия"),
        Act("wave", "Запустить волну", "Волны и цели", "", TriggerActionType.SpawnWave),
        Act("cancel_wave", "Отменить волну", "Волны и цели", "", TriggerActionType.CancelWave),
        Act("complete", "Выполнить цель", "Волны и цели", "", TriggerActionType.CompleteObjective),
        Act("fail", "Провалить цель", "Волны и цели", "", TriggerActionType.FailObjective),
        Act("victory", "Победа", "Волны и цели", "", TriggerActionType.Victory),
        Act("defeat", "Поражение", "Волны и цели", "", TriggerActionType.Defeat),
        Act("flag", "Событие (флаг)", "События", "", TriggerActionType.SetFlag, null, "триггеры"),
        Act("environment", "Время суток / погода", "События", "Меняет переменную сценария (например time_of_day = night).", TriggerActionType.SetEnvironment,
            a => { a.Variable = "time_of_day"; a.VariableValue = "night"; }, "переменные сценария"),
        Act("trigger", "Запустить триггер", "События", "Выполняет действия другого триггера (циклы обнаруживает проверка).", TriggerActionType.ActivateTrigger, null, "триггеры"),
        Act("boss_phase", "Сменить фазу босса", "Боссы", "", TriggerActionType.SetBossPhase, null, "фаза босса"),
        Act("message", "Сообщение", "Сообщения и эффекты", "", TriggerActionType.Message, null, "сообщения"),
        Act("autopause", "Автопауза", "Сообщения и эффекты", "", TriggerActionType.Autopause),
        Act("vfx", "Визуальный эффект", "Сообщения и эффекты", "", TriggerActionType.PlayVfx, null, "визуальные эффекты"),
        Act("sound", "Звук", "Сообщения и эффекты", "", TriggerActionType.PlaySound, null, "звуковые эффекты"),
        // Part 4, stage 5 (docs/DECISIONS_4.md §68).
        Act("base_on", "Активировать базу спавна", "Базы и объекты", "Включает базу спавна (вместо её настройки «активна»).", TriggerActionType.ActivateBase, null, "активировать базу"),
        Act("base_off", "Отключить базу спавна", "Базы и объекты", "", TriggerActionType.DeactivateBase, null, "отключить базу"),
        Act("object_state", "Изменить состояние объекта", "Базы и объекты", "Включить, отключить, разрушить, восстановить.", TriggerActionType.SetObjectState, null, "состояние объекта"),
        Act("group_route", "Изменить маршрут группы", "ИИ и маршруты", "Живые враги группы и её будущие появления идут по новому маршруту.", TriggerActionType.SetGroupRoute, null, "маршрут группы"),
        Act("set_target", "Установить цель", "ИИ и маршруты", "Объект, к которому идут, или юниты (метка, тег), которых атакуют.", TriggerActionType.SetTarget, null, "установить цель"),
        Act("end", "Завершить сценарий", "Волны и цели", "Победой или поражением, с текстом.", TriggerActionType.EndScenario, null, "завершить сценарий"),
        Act("object_switch", "Перевести объект в состояние", "Базы и объекты", "Ворота, мост, турель: состояние объекта с компонентом «Состояния»; пусто — следующее.",
            TriggerActionType.SwitchObjectState, null, "состояние объекта"),
        Act("zone_open", "Открыть зону развёртывания", "Отряд и развёртывание", "В зону снова можно вызывать бойцов (часть 6).", TriggerActionType.OpenDeploymentZone, null, "открыть зону"),
        Act("zone_close", "Закрыть зону развёртывания", "Отряд и развёртывание", "Бойцы на поле остаются; новых в зону не вызвать.", TriggerActionType.CloseDeploymentZone, null, "закрыть зону"),
        Act("diagnostic", "Записать диагностическое событие", "Сообщения и эффекты", "Строка в журнале диагностики и отчёте автобоя.", TriggerActionType.LogDiagnostic, null, "диагностика"),
    };

    /// <summary>Conditions (combined with И / ИЛИ / НЕ).</summary>
    public static readonly IReadOnlyList<CatalogEntry> Conditions = new[]
    {
        Cond("and", "И (все вложенные)", "Логика", "", ConditionType.All, null, "AND"),
        Cond("or", "ИЛИ (хотя бы одно)", "Логика", "", ConditionType.Any, null, "OR"),
        Cond("not", "НЕ", "Логика", "", ConditionType.Not, null, "NOT"),
        Cond("order", "По порядку", "Логика", "", ConditionType.InOrder),
        Cond("always", "Всегда", "Логика", "", ConditionType.Always),
        Cond("hp", "Здоровье ниже порога", "Юниты", "Без метки — сам владелец.", ConditionType.HpBelow, null, "здоровье ниже порога"),
        Cond("range", "Противник в дальности", "Юниты", "", ConditionType.TargetInRange, c => c.Count = 2, "цель в дальности"),
        Cond("spotted", "Обнаружен противник", "Юниты", "", ConditionType.EnemySpotted, null, "обнаружен противник"),
        Cond("enter", "Юнит в области", "Юниты", "Вошёл в метку карты (и пробыл N с).", ConditionType.UnitInArea, c => c.Seconds = 0, "вход в область"),
        Cond("leave", "Юнит покинул область", "Юниты", "", ConditionType.UnitLeftArea, null, "выход из области"),
        Cond("resource", "Ресурс достиг значения", "Юниты", "", ConditionType.ResourceReached, c => c.Value = 50, "ресурс"),
        Cond("status", "Наложено состояние", "Юниты", "", ConditionType.HasStatus, null, "состояние"),
        Cond("dead", "Юнит с меткой погиб", "Юниты", "", ConditionType.UnitDead, null, "объект уничтожен"),
        Cond("alive", "Юнит с меткой жив", "Юниты", "", ConditionType.UnitAlive),
        Cond("reach", "Юнит игрока в метке", "Юниты", "", ConditionType.ReachMarker),
        Cond("time", "Прошло N с", "Время", "", ConditionType.TimeElapsed, null, "таймер"),
        Cond("survive", "Продержаться N с", "Время", "", ConditionType.SurviveTime, null, "таймер"),
        Cond("flag_timer", "N с после события", "Время", "", ConditionType.FlagTimer, c => c.Seconds = 5, "таймер"),
        Cond("all_dead", "Все враги уничтожены", "Бой", "", ConditionType.AllEnemiesDead),
        Cond("player_dead", "Все юниты игрока погибли", "Бой", "", ConditionType.AllPlayerUnitsDead),
        Cond("kills", "Уничтожено N врагов", "Бой", "", ConditionType.KillCount),
        Cond("obstacle", "Препятствие разрушено", "Бой", "", ConditionType.ObstacleDestroyed, c => c.Count = 1, "объект уничтожен"),
        Cond("synergy", "Произошла комбинация эффектов", "Бой", "", ConditionType.SynergyOccurred, c => c.Count = 1, "комбинация эффектов"),
        Cond("boss_phase", "Босс сменил фазу", "Бой", "", ConditionType.BossPhase, c => c.Count = 2, "фаза босса"),
        Cond("wave", "Волна началась", "Сценарий", "", ConditionType.WaveSpawned),
        Cond("wave_cleared", "Волна зачищена", "Сценарий", "", ConditionType.WaveCleared),
        Cond("waves", "Зачищено N волн", "Сценарий", "", ConditionType.WavesCleared),
        Cond("objective", "Цель выполнена", "Сценарий", "", ConditionType.ObjectiveCompleted, null, "цель выполнена"),
        Cond("objective_failed", "Цель провалена", "Сценарий", "", ConditionType.ObjectiveFailed),
        Cond("flag", "Событие (флаг)", "Сценарий", "", ConditionType.FlagSet),
        Cond("environment", "Время суток / погода", "Сценарий", "Переменная сценария равна значению.", ConditionType.Environment,
            c => { c.Variable = "time_of_day"; c.VariableValue = "night"; }, "переменные сценария"),
        Cond("leaks", "Пропущено N врагов", "Сценарий", "", ConditionType.EnemiesLeaked),
        Cond("object_state", "Объект в состоянии", "Сценарий", "Ворота закрыты, мост опущен…", ConditionType.ObjectInState),
    };

    /// <summary>Entries that create items of the given list type (null — no catalog for it).</summary>
    public static IReadOnlyList<CatalogEntry>? For(Type itemType) =>
        itemType == typeof(SkillEffectDef) ? Abilities : itemType == typeof(TriggerActionDef) ? Actions : itemType == typeof(ConditionDef) ? Conditions : null;

    public static IEnumerable<CatalogEntry> All => Abilities.Concat(Actions).Concat(Conditions);

    /// <summary>Items of TZ §15.1 (operations) and §15.2 (conditions) the catalog must cover.</summary>
    public static readonly string[] RequiredOperations =
    {
        "урон", "лечение", "ресурсы", "состояния", "накопление", "эффекты клеток", "перемещение", "направление", "оглушение", "замедление",
        "обездвиживание", "временное изменение характеристик", "постоянное изменение характеристик", "щиты", "поглощение", "отражение",
        "преобразование", "призыв", "маршруты", "приоритеты", "препятствия", "триггеры", "визуальные эффекты", "звуковые эффекты", "фаза босса", "сообщения",
    };

    public static readonly string[] RequiredConditions =
    {
        "здоровье ниже порога", "цель в дальности", "обнаружен противник", "вход в область", "выход из области", "ресурс", "состояние", "таймер",
        "объект уничтожен", "цель выполнена", "фаза босса", "комбинация эффектов", "AND", "OR", "NOT",
    };
}
