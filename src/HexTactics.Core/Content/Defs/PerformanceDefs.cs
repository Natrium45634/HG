namespace HexTactics.Core.Content;

/// <summary>
/// «Производительность» of the project (docs/DECISIONS_4.md §72, TZ §20.2): thresholds of warnings, not limits of the
/// architecture — a scene over them is reported and still runs.
/// </summary>
public sealed class PerformanceRulesDef : ContentPart
{
    [Ed("Макс. живых юнитов", "Одновременно на поле: враги, защитники, сооружения"), EdRange(1, 100_000)] public int MaxAliveUnits { get; set; } = 150;
    [Ed("Макс. групп в волне"), EdRange(1, 10_000)] public int MaxGroupsPerWave { get; set; } = 16;
    [Ed("Макс. узлов маршрута", "У одного маршрута"), EdRange(1, 100_000)] public int MaxRouteNodes { get; set; } = 48;
    [Ed("Макс. рёбер графа маршрутов", "У всех маршрутов сцены"), EdRange(1, 100_000)] public int MaxRouteEdges { get; set; } = 120;
    [Ed("Макс. правил пассивов", "Одновременно действующих модификаторов пассивов у всех юнитов"), EdRange(1, 1_000_000)] public int MaxPassiveRules { get; set; } = 800;
    [Ed("Макс. одновременных эффектов", "Состояния и эффекты клеток"), EdRange(1, 1_000_000)] public int MaxEffects { get; set; } = 400;
    [Ed("Макс. решений ИИ в секунду", "Всех юнитов вместе"), EdRange(1, 1_000_000)] public double MaxAiDecisionsPerSecond { get; set; } = 300;
    [Ed("Мин. интервал решений ИИ, с", "Профиль с интервалом меньше — предупреждение"), EdRange(0.05, 10)] public double MinAiDecisionInterval { get; set; } = 0.2;
    [Ed("Параллельных тестовых прогонов", "Пакетные прогоны; 0 — по числу ядер"), EdRange(0, 256)] public int ParallelRuns { get; set; }
    [Ed("Скачок сложности волны", "Волна дороже предыдущей во столько раз — выделяется в отчёте по волнам"), EdRange(1.05, 10)] public double WaveJump { get; set; } = 1.5;
}
