using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

// Autobattle and AI (docs/DECISIONS_4.md §59, §64, §68): battle modes with a preparation phase, the encounter
// behaviour of route enemies, alternative targets, scenario events with filters and limits.

/// <summary>How a scenario is played (docs/DECISIONS_4.md §64).</summary>
public enum BattleMode
{
    [Ed("Обычный бой", "Как в частях 1–3: юниты сценария расставлены заранее")] Normal,
    [Ed("TD с расстановкой", "Фаза подготовки: игрок размещает защитников, враги появляются после подтверждения")] TdPlacement,
    [Ed("Свободный бой", "Без маршрутов: юниты ищут противников по профилям ИИ")] FreeBattle,
    [Ed("Автотест", "Без игрока: защитники по профилям ИИ и заданной расстановке")] AutoTest,
}

/// <summary>When the preparation ends and the first wave may start.</summary>
public enum PreparationEnd
{
    [Ed("По подтверждению игрока")] Confirm,
    [Ed("По истечении времени")] Timer,
    [Ed("Подтверждение или время")] ConfirmOrTimer,
    [Ed("По условию")] Condition,
}

/// <summary>A template the player may place in the preparation phase.</summary>
public sealed class PlacementUnitDef : ContentPart
{
    [Ed("Шаблон"), EdRef(DefCategory.Unit, AllowEmpty = false)] public string Unit { get; set; } = "";
    [Ed("Стоимость", "0 — стоимость угрозы шаблона"), EdRange(0, 100_000)] public double Cost { get; set; }
    [Ed("Не более", "0 — без ограничения"), EdRange(0, 1000)] public int MaxCount { get; set; }
    [Ed("Профиль ИИ", "Для автотеста и ИИ-защитников; пусто — из шаблона"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "";
    [Ed("Улучшение", "Шаблон, в который можно улучшить (строительство и улучшение сооружений)"), EdRef(DefCategory.Unit)] public string UpgradeTo { get; set; } = "";
    [Ed("Стоимость улучшения", "0 — разница стоимостей"), EdRange(0, 100_000)] public double UpgradeCost { get; set; }

    public override string ToString() => Unit + (Cost > 0 ? $" ({Cost:0.#})" : "") + (MaxCount > 0 ? $" ≤{MaxCount}" : "");
}

/// <summary>The preparation phase of «TD с расстановкой» and the defenders of «Автотест» (docs/DECISIONS_4.md §64).</summary>
public sealed class PlacementDef : ContentPart
{
    [Ed("Конец подготовки")] public PreparationEnd End { get; set; } = PreparationEnd.Confirm;
    [Ed("Длительность подготовки, с"), EdRange(0, 3600), EdShowIf(nameof(End), PreparationEnd.Timer, PreparationEnd.ConfirmOrTimer)] public double Duration { get; set; } = 30;
    [Ed("Условие начала первой волны"), EdShowIf(nameof(End), PreparationEnd.Condition)] public ConditionDef? StartCondition { get; set; }
    [Ed("Бюджет размещения", "0 — без ограничения"), EdRange(0, 1_000_000)] public double Budget { get; set; } = 30;
    [Ed("Не более юнитов", "0 — без ограничения"), EdRange(0, 1000)] public int MaxUnits { get; set; }
    [Ed("Доступные юниты")] public List<PlacementUnitDef> Units { get; set; } = new();
    [Ed("Зоны: объекты", "ID объектов сценария (зоны поведения, базы): их гексы и радиус вокруг")] public List<string> ZoneObjects { get; set; } = new();
    [Ed("Радиус вокруг объектов зон"), EdRange(0, 20)] public int ZoneRadius { get; set; } = 2;
    [Ed("Зоны: гексы", "Отмечаются на карте"), EdHidden] public List<HexCoord> ZoneCells { get; set; } = new();
    [Ed("Перестановка между волнами")] public bool RelocateBetweenWaves { get; set; } = true;
    [Ed("Восстановление здоровья между волнами", "Доля недостающего здоровья"), EdRange(0, 1)] public double RestoreHealth { get; set; } = 0.25;
    [Ed("Восстановление ресурсов между волнами")] public bool RestoreResources { get; set; } = true;
    [Ed("Враги до подтверждения", "Волны идут уже во время подготовки")] public bool EnemiesDuringPreparation { get; set; }
    [Ed("Расстановка по умолчанию", "Для автотеста и кнопки «Расставить как в тесте»")] public List<ScenarioUnitDef> Preset { get; set; } = new();
    [Ed("Профиль ИИ защитников", "Автотест: профиль защитников, у которых он не задан; пусто — «Страж» (ai_guard), если он есть"), EdRef(DefCategory.AiProfile)] public string DefenderProfile { get; set; } = "";

    /// <summary>Every hex of the zones; empty — no restriction by zone.</summary>
    public HashSet<HexCoord> ZoneHexes(ScenarioDef sc)
    {
        var set = new HashSet<HexCoord>(ZoneCells);
        foreach (var id in ZoneObjects)
        {
            var o = sc.Object(id);
            if (o == null) continue;
            foreach (var c in o.Cells())
            foreach (var h in HexCoord.Spiral(c, ZoneRadius))
                set.Add(h);
        }
        return set;
    }
}

/// <summary>How a route enemy meets the defenders (docs/DECISIONS_4.md §59, TZ §5.2).</summary>
public enum EncounterMode
{
    [Ed("Бегущий к базе", "Идёт дальше; атакует только блокирующего или того, кто бьёт его вплотную")] Runner,
    [Ed("Охотник", "Сходит с маршрута ради цели, затем возвращается")] Hunter,
    [Ed("Страж стаи", "Защищает союзников и вожака: бьёт тех, кто атакует своих")] PackGuard,
    [Ed("Убийца", "Ищет слабых и изолированных")] Assassin,
    [Ed("Танк", "Продавливает путь: атакует блокирующих и препятствия")] Tank,
    [Ed("Поддержка", "Лечит и усиливает союзников, не уходя с пути без необходимости")] Support,
}

/// <summary>Leaving the route for a fight, and coming back (docs/DECISIONS_4.md §59, TZ §5.3).</summary>
public sealed class EncounterDef : ContentPart
{
    [Ed("Поведение при встрече")] public EncounterMode Mode { get; set; } = EncounterMode.Hunter;
    [Ed("Дистанция преследования", "Гексов от маршрута; −1 — характеристика «дальность преследования»"), EdRange(-1, 50)] public int PursuitRange { get; set; } = -1;
    [Ed("Время отвлечения, с", "Сколько можно быть вне маршрута; 0 — без ограничения"), EdRange(0, 600)] public double DistractionSeconds { get; set; } = 8;
    [Ed("Допустимые цели", "Теги («tag:x»), категории, ID шаблонов; пусто — любые")] public List<string> AllowedTargets { get; set; } = new();
    [Ed("Атаковать движущуюся цель")] public bool AttackMoving { get; set; } = true;
    [Ed("Преследовать сквозь препятствия", "Нет — бросает цель, если путь к ней вдвое длиннее прямой")] public bool ThroughObstacles { get; set; }
    [Ed("Приоритет базы", "0 — бьётся с любым встречным, 1 — только с блокирующими"), EdRange(0, 1)] public double BasePriority { get; set; } = 0.3;
    [Ed("Условие возврата", "Выполнено — бросает преследование и возвращается на маршрут")] public UnitConditionDef? ReturnCondition { get; set; }

    public override string ToString() => $"{Mode}, r={PursuitRange}, {DistractionSeconds:0.#} с";
}

/// <summary>What an enemy does when its target base is destroyed or unreachable (docs/DECISIONS_4.md §59, TZ §5.4).</summary>
public enum AltTargetKind
{
    [Ed("К другой базе (по важности)")] OtherBase,
    [Ed("Атаковать защитников")] AttackDefenders,
    [Ed("Захватить объект")] CaptureObject,
    [Ed("Ломать препятствие")] BreakObstacle,
    [Ed("Остановиться")] Stop,
    [Ed("Отступить к базе спавна")] Retreat,
    [Ed("Событие (флаг)")] Event,
}

public sealed class AltTargetDef : ContentPart
{
    [Ed("Что делать")] public AltTargetKind Kind { get; set; } = AltTargetKind.OtherBase;
    [Ed("Объект", "ID объекта; пусто — ближайшая цель/контрольная точка"), EdShowIf(nameof(Kind), AltTargetKind.CaptureObject)] public string Object { get; set; } = "";
    [Ed("Флаг события"), EdShowIf(nameof(Kind), AltTargetKind.Event)] public string Flag { get; set; } = "";

    public override string ToString() => Kind + (Object != "" ? $" «{Object}»" : "") + (Flag != "" ? $" «{Flag}»" : "");
}

/// <summary>The behaviour an AI rule sets while its branch holds (docs/DECISIONS_4.md §64).</summary>
public enum AiDirective
{
    [Ed("— (только действия)")] None,
    [Ed("Атаковать по приоритету профиля")] Attack,
    [Ed("Атаковать слабейшего")] AttackWeakest,
    [Ed("Атаковать опаснейшего")] AttackDangerous,
    [Ed("Атаковать угрожающих базе")] DefendBase,
    [Ed("Отступить")] Retreat,
    [Ed("Удерживать позицию")] Hold,
    [Ed("Идти к цели, не отвлекаясь")] Advance,
    [Ed("Лечить и усиливать союзников")] Support,
    [Ed("Держаться союзников")] Guard,
    [Ed("Вернуться на маршрут")] ReturnToRoute,
    [Ed("Ждать")] Wait,
}

/// <summary>Scenario events (docs/DECISIONS_4.md §68, TZ §16.2): a trigger with an event fires when it happens.</summary>
public enum ScenarioEventType
{
    [Ed("Нет — условие проверяется каждый тик")] None,
    [Ed("Начало сценария")] ScenarioStart,
    [Ed("Завершение подготовки")] PreparationDone,
    [Ed("Начало волны")] WaveStarted,
    [Ed("Завершение волны")] WaveEnded,
    [Ed("Появление юнита")] UnitSpawned,
    [Ed("Смерть юнита")] UnitDied,
    [Ed("Достижение цели")] GoalReached,
    [Ed("Уничтожение базы или сооружения")] BaseDestroyed,
    [Ed("Активация объекта")] ObjectActivated,
    [Ed("Достижение контрольной точки")] CheckpointReached,
    [Ed("Истечение таймера")] Timer,
    [Ed("Превышение лимита живых врагов")] AliveLimitExceeded,
    [Ed("Выполнение условия", "Условие стало выполненным (было не выполнено)")] ConditionMet,
    [Ed("Завершение боя")] BattleEnded,
    [Ed("Наложено состояние")] StatusApplied,
    [Ed("Применён навык")] SkillUsed,
    [Ed("Сработал пассив")] PassiveTriggered,
    [Ed("Окончено появление волны", "Волна выпустила всех (её враги могут быть ещё живы) — не путать с завершением волны")] WaveSpawnFinished,
}

/// <summary>«Изменить состояние объекта».</summary>
public enum ObjectStateChange
{
    [Ed("Включить")] Activate,
    [Ed("Отключить")] Deactivate,
    [Ed("Разрушить")] Destroy,
    [Ed("Восстановить здоровье")] Repair,
}

/// <summary>An event of the scenario as the triggers see it.</summary>
public sealed class ScenarioEvent : ContentPart
{
    public ScenarioEventType Type { get; init; }
    /// <summary>The unit of the event (the one that appeared, died, reached…): «владелец» of the actions.</summary>
    public int Unit { get; init; } = -1;
    /// <summary>Object, base, checkpoint node, status, skill or passive ID.</summary>
    public string Object { get; init; } = "";
    public int Wave { get; init; } = -1;
    public string Group { get; init; } = "";
    /// <summary>Depth of the chain of events (an event raised by a trigger's action is one deeper).</summary>
    public int Depth { get; init; }

    public override string ToString() => Type + (Object != "" ? $" «{Object}»" : "") + (Wave >= 0 ? $" (волна {Wave + 1})" : "");
}

/// <summary>A cycle of triggers found by the static check (docs/DECISIONS_4.md §68).</summary>
public sealed record TriggerCycle(List<int> Triggers, bool Limited, string Text);

/// <summary>
/// The static check of scenario events (docs/DECISIONS_4.md §68): the graph «действие порождает событие, которое
/// запускает правило». A cycle with an explicit limit (once, rate limit, maximum fires) is intended — a warning;
/// without one it is an error.
/// </summary>
public static class ScenarioEvents
{
    /// <summary>The events an action may raise.</summary>
    public static IEnumerable<ScenarioEventType> Produces(TriggerActionDef a)
    {
        switch (a.Type)
        {
            case TriggerActionType.SpawnWave:
                yield return ScenarioEventType.WaveStarted;
                yield return ScenarioEventType.UnitSpawned;
                break;
            case TriggerActionType.SummonUnits:
                yield return ScenarioEventType.UnitSpawned;
                break;
            case TriggerActionType.CancelWave:
                yield return ScenarioEventType.WaveEnded;
                break;
            case TriggerActionType.ActivateBase:
                yield return ScenarioEventType.ObjectActivated;
                yield return ScenarioEventType.UnitSpawned;
                break;
            case TriggerActionType.SetObjectState:
                if (a.ObjectState == ObjectStateChange.Activate) yield return ScenarioEventType.ObjectActivated;
                if (a.ObjectState == ObjectStateChange.Destroy) yield return ScenarioEventType.BaseDestroyed;
                break;
            case TriggerActionType.Effects:
                if (a.Effects.Any(e => e.Type == EffectType.Damage)) yield return ScenarioEventType.UnitDied;
                if (a.Effects.Any(e => e.Type == EffectType.ApplyStatus)) yield return ScenarioEventType.StatusApplied;
                if (a.Effects.Any(e => e.Type == EffectType.Summon)) yield return ScenarioEventType.UnitSpawned;
                break;
            case TriggerActionType.ApplyStatus:
                yield return ScenarioEventType.StatusApplied;
                break;
            case TriggerActionType.Victory:
            case TriggerActionType.Defeat:
            case TriggerActionType.EndScenario:
                yield return ScenarioEventType.BattleEnded;
                break;
            case TriggerActionType.SetFlag:
            case TriggerActionType.SetEnvironment:
                yield return ScenarioEventType.ConditionMet;
                break;
        }
    }

    /// <summary>A trigger limits itself: once, a rate limit or a maximum of fires.</summary>
    public static bool Limited(TriggerDef t) => t.Once || t.Cooldown > 0 || t.MaxFires > 0;

    public static List<TriggerCycle> Cycles(ScenarioDef sc)
    {
        var edges = new Dictionary<int, List<int>>();
        for (int i = 0; i < sc.Triggers.Count; i++)
        {
            var produced = sc.Triggers[i].Actions.SelectMany(Produces).ToHashSet();
            edges[i] = new List<int>();
            for (int j = 0; j < sc.Triggers.Count; j++)
            {
                var ev = sc.Triggers[j].Event;
                if (ev != ScenarioEventType.None && produced.Contains(ev)) edges[i].Add(j);
            }
        }
        var list = new List<TriggerCycle>();
        foreach (var cycle in FindCycles(edges))
        {
            bool limited = cycle.Any(i => Limited(sc.Triggers[i]));
            string path = string.Join(" → ", cycle.Append(cycle[0]).Select(i => $"«{sc.Triggers[i].Name}» ({sc.Triggers[i].Event})"));
            list.Add(new TriggerCycle(cycle, limited, path));
        }
        return list;
    }

    /// <summary>Elementary cycles, each once (by its smallest member), bounded.</summary>
    private static List<List<int>> FindCycles(Dictionary<int, List<int>> edges)
    {
        var found = new List<List<int>>();
        var seen = new HashSet<string>();
        foreach (var start in edges.Keys.OrderBy(k => k))
        {
            var stack = new List<int> { start };
            void Walk(int at)
            {
                if (found.Count >= 20) return;
                foreach (var next in edges[at])
                {
                    if (next == start)
                    {
                        var c = new List<int>(stack);
                        int min = c.IndexOf(c.Min());
                        var norm = c.Skip(min).Concat(c.Take(min)).ToList();
                        if (seen.Add(string.Join(",", norm))) found.Add(norm);
                    }
                    else if (next > start && !stack.Contains(next) && stack.Count < 12)
                    {
                        stack.Add(next);
                        Walk(next);
                        stack.RemoveAt(stack.Count - 1);
                    }
                }
            }
            Walk(start);
        }
        return found;
    }
}
