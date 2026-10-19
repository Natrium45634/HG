namespace HexTactics.Core.Content;

// Player control (docs/DECISIONS_3.md §41): routing modes and weights of the player's routes.

public enum RouteMode
{
    [Ed("Минимальная стоимость")] MinCost,
    [Ed("Минимальное расстояние")] MinDistance,
    [Ed("Избегать опасности")] AvoidDanger,
    [Ed("Свой приоритет")] Custom,
}

/// <summary>Weights of a route edge: w·cost + w·1 (step) + w·turn + w·hazard + w·threat.</summary>
public sealed class RouteWeights
{
    [Ed("Стоимость движения"), EdRange(0, 100)] public double Cost { get; set; } = 1;
    [Ed("Число шагов"), EdRange(0, 100)] public double Steps { get; set; }
    [Ed("Повороты"), EdRange(0, 100)] public double Turns { get; set; }
    [Ed("Опасные клетки", "Огонь, яд, лава и другие опасные эффекты"), EdRange(0, 100)] public double Hazard { get; set; } = 3;
    [Ed("Угрозы", "Зоны обнаружения и автоатак видимых врагов"), EdRange(0, 100)] public double Threat { get; set; } = 2;

    public RouteWeights Clone() => (RouteWeights)MemberwiseClone();

    /// <summary>Weights of a mode; Custom uses the given weights (or the defaults).</summary>
    public static RouteWeights For(RouteMode mode, RouteWeights? custom) => mode switch
    {
        RouteMode.MinDistance => new RouteWeights { Cost = 0.001, Steps = 1, Hazard = 0, Threat = 0 },
        RouteMode.AvoidDanger => new RouteWeights { Cost = 1, Hazard = 5, Threat = 3 },
        RouteMode.Custom => custom?.Clone() ?? new RouteWeights(),
        _ => new RouteWeights { Cost = 1, Hazard = 0, Threat = 0 },
    };

    public override string ToString() => $"стоимость×{Cost:0.##}, шаги×{Steps:0.##}, повороты×{Turns:0.##}, опасность×{Hazard:0.##}, угрозы×{Threat:0.##}";
}
