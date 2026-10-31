using System.Collections.Generic;
using System.Text.Json.Serialization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

// Summons of sub-units (ТЗ ч. 6, 20; docs/DECISIONS_6.md §106): a description of the library «Призывы» the effect «Призвать юнита»,
// a passive or a scenario action refers to. A summoned unit is an ordinary unit of the battle — the same movement, health, combat,
// statuses and saving — linked to its summoner; it is not the deployment of the squad for points (ТЗ 1–3).

/// <summary>Where the summoned appear (ТЗ 20.2).</summary>
public enum SummonPlacement
{
    [Ed("Соседние гексы", "Вокруг призывателя, начиная с направления взгляда")] Adjacent,
    [Ed("В радиусе", "Ближайшие к призывателю гексы в радиусе")] Radius,
    [Ed("Выбранный гекс", "Гекс навыка и ближайшие к нему")] SelectedHex,
    [Ed("У цели", "Вокруг юнита-цели навыка")] AtTarget,
    [Ed("Кольцом вокруг", "Гексы ровно на расстоянии радиуса от призывателя")] AroundCaster,
    [Ed("Заданные смещения", "Смещения от призывателя (для взгляда на восток), поворачиваются за взглядом")] Offsets,
}

/// <summary>Where the summoned look.</summary>
public enum SummonFacing
{
    [Ed("Как призыватель")] Summoner,
    [Ed("От призывателя")] Away,
    [Ed("К цели", "К гексу или юниту навыка")] Target,
}

/// <summary>The side of the summoned (ТЗ 20.2).</summary>
public enum SummonFaction
{
    [Ed("Фракция призывателя", "Текущая фракция призывателя")] Summoner,
    [Ed("Фракция владельца", "Фракция корня цепочки призывов")] Owner,
    [Ed("Заданная фракция")] Fixed,
    [Ed("Независимые нейтралы")] Neutral,
}

/// <summary>How long the summoned live (ТЗ 20.2).</summary>
public enum SummonLifetime
{
    [Ed("Без ограничения")] Unlimited,
    [Ed("Время", "Секунды игрового времени")] Duration,
    [Ed("До гибели призывателя")] UntilSummonerDeath,
    [Ed("До конца навыка", "Пока длится применение навыка-призыва (этапы, подготовка)")] UntilSkillEnd,
    [Ed("До гибели цели", "Цели навыка-призыва")] UntilTargetDies,
}

/// <summary>What happens to a summoned unit at the end of its life or at the death of its summoner (ТЗ 20.2).</summary>
public enum SummonEnd
{
    [Ed("По правилам боя", "«Призванные исчезают со смертью призывателя»: да — гибнут, нет — остаются")] ByRules,
    [Ed("Остаётся", "Без изменений: связь и поведение прежние")] Keep,
    [Ed("Исчезает", "Убирается с поля; события гибели и добыча — по флажку")] Vanish,
    [Ed("Гибнет", "Обычная гибель: события гибели, добыча, поля трупов")] Die,
    [Ed("Нейтрализуется", "Переходит к нейтралам и перестаёт сражаться")] Neutralize,
    [Ed("Становится независимым", "Связь разорвана, поведение — «после призывателя»")] BecomeIndependent,
    [Ed("Передаётся", "Призывателю призывателя или владельцу цепочки; нет живых — независимый")] TransferControl,
}

/// <summary>When some of the hexes are taken or impassable (ТЗ 20.5).</summary>
public enum SummonFailure
{
    [Ed("Отменить весь призыв", "Не хватает мест — никто не появляется, ресурс возвращается")] CancelAll,
    [Ed("Частично", "Сколько поместилось; ресурс за остальных возвращается")] SpawnPartial,
    [Ed("Искать ближайший", "Свободный гекс в радиусе поиска от занятого")] SearchNearest,
    [Ed("Ждать освобождения", "В очередь до освобождения места; по таймауту — возврат")] QueueUntilFree,
}

/// <summary>When a limit of alive summoned is reached (ТЗ 20.5: a predictable order).</summary>
public enum SummonLimitPolicy
{
    [Ed("Отказать новым")] Reject,
    [Ed("Заменить старейшего", "Самый ранний призванный этого описания у призывателя уходит по политике окончания")] ReplaceOldest,
}

/// <summary>The simple AI of a summoned unit (ТЗ 20.3).</summary>
public enum SummonBehavior
{
    [Ed("Следовать за призывателем", "На дистанции; клетку призывателя не занимает; бьёт тех, кто рядом")] FollowSummoner,
    [Ed("Охранять призывателя", "Держится в поводке, перехватывает атакующих призывателя, возвращается")] GuardSummoner,
    [Ed("Атаковать цель призывателя", "Цель призывателя; её нет — запасное поведение")] AttackSummonerTarget,
    [Ed("Атаковать ближайшего врага", "Досягаемость, затем расстояние, затем номер")] AttackNearestEnemy,
    [Ed("Держать позицию", "Защищает гекс появления, не преследует дальше поводка")] HoldPosition,
    [Ed("Патрулировать", "Между гексами смещений от места появления")] PatrolSimple,
    [Ed("Идти к точке", "К гексу навыка, затем держит его")] MoveToPoint,
    [Ed("Ручное управление", "Приказы игрока, если призыватель его и сценарий разрешает; иначе — запасное поведение")] ManualSimple,
}

/// <summary>Who gives orders to a summoned unit of the player (ТЗ 20.4).</summary>
public enum SummonControl
{
    [Ed("Только ИИ")] AutoOnly,
    [Ed("Приказы владельца важнее", "Приказ игрока выполняется, потом снова ИИ")] OwnerPriorityOrders,
    [Ed("Полное управление", "Как обычный боец игрока")] FullControl,
}

/// <summary>A share of a characteristic of the summoner the summoned gets (ТЗ 20.4: only what is named).</summary>
public sealed class SummonInheritDef : ContentPart
{
    [Ed("Характеристика"), EdRef(DefCategory.Stat, AllowEmpty = false)] public string Stat { get; set; } = "";
    [Ed("Доля", "0.25 — четверть значения призывателя прибавляется"), EdRange(0, 10)] public double Share { get; set; } = 0.25;
}

/// <summary>A description of a summon (ТЗ 20.2): who, how many, where, whose, how long, how it ends, limits, AI.</summary>
public sealed class SummonDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Summon;

    [Ed("Юнит", "Шаблон призываемого", Group = "Кто"), EdRef(DefCategory.Unit, AllowEmpty = false)] public string Unit { get; set; } = "";
    [Ed("Количество", Group = "Кто"), EdRange(1, 20)] public int Count { get; set; } = 1;
    [Ed("Задержка, с", "Через сколько появляются после применения", Group = "Кто"), EdRange(0, 60)] public double Delay { get; set; }
    [Ed("Цена в очках развёртывания", "0 — только ресурс и перезарядка навыка", Group = "Кто"), EdRange(0, 1000)] public double DeployCost { get; set; }

    [Ed("Расстановка", Group = "Где")] public SummonPlacement Placement { get; set; } = SummonPlacement.Adjacent;
    [Ed("Радиус", Group = "Где"), EdRange(1, 10), EdShowIf(nameof(Placement), SummonPlacement.Radius, SummonPlacement.AroundCaster)] public int Radius { get; set; } = 2;
    [Ed("Смещения", "От призывателя, для взгляда на восток", Group = "Где"), EdShowIf(nameof(Placement), SummonPlacement.Offsets)] public List<HexCoord> Offsets { get; set; } = new();
    [Ed("Допустимые местности", "Пусто — любая проходимая для призванного", Group = "Где"), EdRef(DefCategory.Terrain)] public List<string> Terrains { get; set; } = new();
    [Ed("Взгляд", Group = "Где")] public SummonFacing Facing { get; set; } = SummonFacing.Summoner;
    [Ed("Место занято", Group = "Где")] public SummonFailure OnBlocked { get; set; } = SummonFailure.SpawnPartial;
    [Ed("Радиус поиска", Group = "Где"), EdRange(1, 10), EdShowIf(nameof(OnBlocked), SummonFailure.SearchNearest)] public int SearchRadius { get; set; } = 2;
    [Ed("Ждать не дольше, с", Group = "Где"), EdRange(0.5, 120), EdShowIf(nameof(OnBlocked), SummonFailure.QueueUntilFree)] public double QueueTimeout { get; set; } = 5;
    [Ed("Возвращать ресурс", "За тех, кто не появился: доля цены навыка", Group = "Где")] public bool Refund { get; set; } = true;

    [Ed("Сторона", Group = "Чей")] public SummonFaction Faction { get; set; } = SummonFaction.Summoner;
    [Ed("Фракция", Group = "Чей"), EdRef(DefCategory.Faction), EdShowIf(nameof(Faction), SummonFaction.Fixed)] public string FixedFaction { get; set; } = "";
    [Ed("Следует за сменой фракции", "Призыватель перешёл на другую сторону — призванные тоже", Group = "Чей")] public bool FollowFactionChange { get; set; } = true;
    [Ed("Наследует характеристики", "Только перечисленные — доля значения призывателя", Group = "Чей")] public List<SummonInheritDef> InheritStats { get; set; } = new();
    [Ed("Наследует теги", "Теги призывателя из списка, если они у него есть", Group = "Чей")] public List<string> InheritTags { get; set; } = new();

    [Ed("Время жизни", Group = "Сколько живёт")] public SummonLifetime Lifetime { get; set; } = SummonLifetime.Duration;
    [Ed("Длительность, с", Group = "Сколько живёт"), EdRange(0.5, 3600), EdShowIf(nameof(Lifetime), SummonLifetime.Duration)] public double Duration { get; set; } = 30;
    [Ed("По окончании", Group = "Сколько живёт")] public SummonEnd Despawn { get; set; } = SummonEnd.Vanish;
    [Ed("При гибели призывателя", Group = "Сколько живёт")] public SummonEnd OnSummonerDeath { get; set; } = SummonEnd.ByRules;
    [Ed("Исчезновение — как гибель", "События гибели и добыча при «исчезает»", Group = "Сколько живёт")] public bool VanishFiresDeath { get; set; }

    [Ed("Живых у призывателя", "0 — без лимита", Group = "Лимиты"), EdRange(0, 100)] public int MaxPerSummoner { get; set; } = 4;
    [Ed("Живых у стороны", "0 — без лимита", Group = "Лимиты"), EdRange(0, 500)] public int MaxPerTeam { get; set; }
    [Ed("Живых в сценарии", "0 — без лимита", Group = "Лимиты"), EdRange(0, 1000)] public int MaxPerScenario { get; set; }
    [Ed("Всего за бой", "0 — без лимита", Group = "Лимиты"), EdRange(0, 10000)] public int MaxTotal { get; set; }
    [Ed("Лимит достигнут", Group = "Лимиты")] public SummonLimitPolicy OnLimit { get; set; } = SummonLimitPolicy.Reject;
    [Ed("Призванные могут призывать", Group = "Лимиты")] public bool AllowSubSummon { get; set; }
    [Ed("Глубина цепочки", "1 — призванный не призывает", Group = "Лимиты"), EdRange(1, 5)] public int MaxDepth { get; set; } = 1;

    [Ed("Поведение", Group = "ИИ")] public SummonBehavior Behavior { get; set; } = SummonBehavior.GuardSummoner;
    [Ed("Запасное поведение", "Нет цели, путь потерян, ручное управление недоступно", Group = "ИИ")] public SummonBehavior Fallback { get; set; } = SummonBehavior.FollowSummoner;
    [Ed("После призывателя", "Если призыватель погиб, а призванный остался или стал независимым", Group = "ИИ")] public SummonBehavior AfterSummoner { get; set; } = SummonBehavior.AttackNearestEnemy;
    [Ed("Профиль ИИ", "Выбор навыков в бою; пусто — профиль юнита", Group = "ИИ"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "";
    [Ed("Дистанция", "Следование: держится не дальше", Group = "ИИ"), EdRange(1, 10)] public int Distance { get; set; } = 2;
    [Ed("Поводок", "Охрана, позиция, патруль: дальше не уходит", Group = "ИИ"), EdRange(1, 20)] public int Leash { get; set; } = 3;
    [Ed("Радиус охраны", "На кого реагирует вокруг охраняемого", Group = "ИИ"), EdRange(1, 20)] public int GuardRadius { get; set; } = 4;
    [Ed("Точки патруля", "Смещения от места появления", Group = "ИИ"), EdShowIf(nameof(Behavior), SummonBehavior.PatrolSimple)] public List<HexCoord> Patrol { get; set; } = new();
    [Ed("Управление", "Призванные бойца игрока", Group = "ИИ")] public SummonControl Control { get; set; } = SummonControl.AutoOnly;
}

/// <summary>Summons in a scenario (ТЗ 20.5, 20.6): the global limits and whether the summoned take places of the deployment.</summary>
public sealed class SummonRulesDef : ContentPart
{
    [Ed("Живых призванных всего", "0 — без лимита; сверх — отказ новым по порядку запросов")] [EdRange(0, 1000)] public int MaxAlive { get; set; }
    [Ed("Призывов за бой", "0 — без лимита")] [EdRange(0, 100000)] public int MaxTotal { get; set; }
    [Ed("Глубина цепочки", "0 — по описаниям призывов")] [EdRange(0, 5)] public int MaxDepth { get; set; }
    [Ed("Занимают места на поле", "Призванные игрока входят в лимит «На поле одновременно» развёртывания")] public bool CountTowardsDeploy { get; set; }
    [Ed("Ручное управление", "Разрешить поведение «Ручное управление» призванным игрока")] public bool AllowManual { get; set; } = true;
}
