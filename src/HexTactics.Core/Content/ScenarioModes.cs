using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>How a mode treats a part of the scenario (ТЗ ч. 5, 18.5).</summary>
public enum FieldUse
{
    /// <summary>Without it the scenario does not start.</summary>
    Required,
    /// <summary>May be there or not.</summary>
    Allowed,
    /// <summary>The mode ignores it: a warning, the scenario starts.</summary>
    Ignored,
}

/// <summary>A part of a scenario a mode requires, allows or ignores.</summary>
public sealed record ModeField(string Key, string Label, FieldUse Use, string Why = "");

/// <summary>
/// The modes of a scenario and their fields (ТЗ ч. 5, 18; docs/DECISIONS_5.md §86): for each mode — what is required,
/// what is allowed and what the mode ignores. «Свободный бой» without a base is valid; «Tower Defense» without one is an
/// error (T27). The editor shows the same table next to the mode.
/// </summary>
public static class ScenarioModes
{
    public const string Bases = "bases";
    public const string Spawners = "spawners";
    public const string Waves = "waves";
    public const string Routes = "routes";
    public const string Placement = "placement";
    public const string Sides = "sides";
    public const string PlayerUnits = "player_units";
    public const string Hunters = "hunters";
    public const string Finish = "finish";

    private static readonly Dictionary<string, string> Labels = new()
    {
        [Bases] = "базы игрока",
        [Spawners] = "базы спавна",
        [Waves] = "волны",
        [Routes] = "маршруты",
        [Placement] = "расстановка игроком",
        [Sides] = "две стороны с юнитами",
        [PlayerUnits] = "юниты игрока",
        [Hunters] = "группы «охотится»",
        [Finish] = "сопровождение или финиш",
    };

    public static IReadOnlyList<ModeField> Fields(BattleMode mode)
    {
        ModeField F(string key, FieldUse use, string why = "") => new(key, Labels[key], use, why);
        return mode switch
        {
            BattleMode.TowerDefense => new[]
            {
                F(Bases, FieldUse.Required, "враги идут к базе"), F(Spawners, FieldUse.Required), F(Waves, FieldUse.Required), F(Routes, FieldUse.Required),
                F(PlayerUnits, FieldUse.Allowed, "фиксированная расстановка"), F(Placement, FieldUse.Ignored, "для расстановки игроком — режим «TD с расстановкой»"),
                F(Hunters, FieldUse.Ignored, "в TD все идут по маршрутам"), F(Finish, FieldUse.Allowed),
            },
            BattleMode.TdPlacement => new[]
            {
                F(Bases, FieldUse.Required, "враги идут к базе"), F(Spawners, FieldUse.Required), F(Waves, FieldUse.Required), F(Routes, FieldUse.Required),
                F(Placement, FieldUse.Required, "доступные для расстановки юниты"), F(PlayerUnits, FieldUse.Allowed),
                F(Hunters, FieldUse.Ignored, "в TD все идут по маршрутам"), F(Finish, FieldUse.Allowed),
            },
            BattleMode.FreeBattle => new[]
            {
                F(Sides, FieldUse.Required, "отряды двух фракций"), F(Bases, FieldUse.Allowed), F(Spawners, FieldUse.Allowed), F(Waves, FieldUse.Allowed),
                F(Routes, FieldUse.Ignored, "в свободном бою все ищут противника"), F(Placement, FieldUse.Ignored), F(Hunters, FieldUse.Ignored, "охотятся все"),
                F(Finish, FieldUse.Allowed),
            },
            BattleMode.Assault => new[]
            {
                F(PlayerUnits, FieldUse.Required, "враги ищут юнитов игрока"), F(Waves, FieldUse.Allowed), F(Spawners, FieldUse.Allowed), F(Bases, FieldUse.Allowed),
                F(Routes, FieldUse.Allowed, "путь подхода, пока противник не найден"), F(Hunters, FieldUse.Ignored, "в штурме охотятся все"),
                F(Placement, FieldUse.Ignored), F(Finish, FieldUse.Allowed),
            },
            BattleMode.Mixed => new[]
            {
                F(Waves, FieldUse.Required), F(Spawners, FieldUse.Required), F(Routes, FieldUse.Required, "для групп без «охотится»"),
                F(Hunters, FieldUse.Required, "хотя бы одна группа ищет юнитов игрока"), F(Bases, FieldUse.Allowed), F(PlayerUnits, FieldUse.Allowed),
                F(Placement, FieldUse.Ignored), F(Finish, FieldUse.Allowed),
            },
            BattleMode.Escort => new[]
            {
                F(Finish, FieldUse.Required, "цель «сопровождение» или условие победы «дойти до метки»"), F(PlayerUnits, FieldUse.Required),
                F(Waves, FieldUse.Allowed), F(Spawners, FieldUse.Allowed), F(Routes, FieldUse.Allowed), F(Bases, FieldUse.Allowed),
                F(Placement, FieldUse.Ignored), F(Hunters, FieldUse.Ignored),
            },
            BattleMode.AutoTest => new[]
            {
                F(PlayerUnits, FieldUse.Allowed, "или расстановка по умолчанию"), F(Bases, FieldUse.Allowed), F(Spawners, FieldUse.Allowed), F(Waves, FieldUse.Allowed),
                F(Routes, FieldUse.Allowed), F(Placement, FieldUse.Allowed), F(Hunters, FieldUse.Ignored), F(Finish, FieldUse.Allowed),
            },
            BattleMode.Sandbox => new[]
            {
                F(PlayerUnits, FieldUse.Allowed), F(Bases, FieldUse.Allowed), F(Spawners, FieldUse.Allowed), F(Waves, FieldUse.Allowed), F(Routes, FieldUse.Allowed),
                F(Placement, FieldUse.Allowed), F(Hunters, FieldUse.Allowed), F(Finish, FieldUse.Allowed),
            },
            _ => new[]
            {
                F(PlayerUnits, FieldUse.Allowed), F(Bases, FieldUse.Allowed), F(Spawners, FieldUse.Allowed), F(Waves, FieldUse.Allowed), F(Routes, FieldUse.Allowed),
                F(Placement, FieldUse.Ignored, "для расстановки игроком — режим «TD с расстановкой»"), F(Hunters, FieldUse.Ignored, "только в «Штурме» и «Смешанном»"),
                F(Finish, FieldUse.Allowed),
            },
        };
    }

    /// <summary>One line per kind: «Обязательно: …», «Можно: …», «Не используется: …».</summary>
    public static List<string> Summary(BattleMode mode)
    {
        var f = Fields(mode);
        var lines = new List<string>();
        void Line(FieldUse use, string title)
        {
            var items = f.Where(x => x.Use == use).Select(x => x.Why != "" ? $"{x.Label} ({x.Why})" : x.Label).ToList();
            if (items.Count > 0) lines.Add($"{title}: {string.Join(", ", items)}");
        }
        Line(FieldUse.Required, "Обязательно");
        Line(FieldUse.Allowed, "Можно");
        Line(FieldUse.Ignored, "Не используется");
        return lines;
    }

    /// <summary>Whether the scenario has the part.</summary>
    public static bool Has(ContentDatabase db, ScenarioDef sc, string key)
    {
        var player = sc.Sides.Where(s => s.Controller == SideController.Player).Select(s => s.Faction).ToHashSet();
        string FactionOf(ScenarioUnitDef u) => u.Faction != "" ? u.Faction : db.Unit(u.Unit)?.Faction ?? "";
        return key switch
        {
            Bases => sc.Objects.Any(o => o.Type == ScenarioObjectType.PlayerBase),
            Spawners => sc.Objects.Any(o => o.Spawn != null),
            Waves => sc.Waves.Any(w => w.Groups.Any(g => g.Enabled) || w.Units.Count > 0 || w.Budget.Enabled),
            Routes => sc.Routes.Count > 0,
            Placement => sc.Placement.Units.Count > 0,
            PlayerUnits => sc.Units.Any(u => player.Contains(FactionOf(u))) || sc.Placement.Preset.Count > 0 || sc.Placement.Units.Count > 0,
            Sides => sc.Units.Select(FactionOf).Concat(sc.Waves.Where(w => w.Groups.Any(g => g.Enabled) || w.Units.Count > 0).Select(w => w.Faction))
                .Concat(sc.Waves.SelectMany(w => w.Groups.Where(g => g.Enabled && g.Faction != "").Select(g => g.Faction)))
                .Where(f => f != "").Distinct().Count() >= 2,
            Hunters => sc.Waves.Any(w => w.Groups.Any(g => g.Enabled && g.Hunt)),
            Finish => sc.Objectives.Any(o => o.Kind == ObjectiveKind.Escort) || sc.Victory.Any(c => ContentChecks.Flatten(c).Any(x => x.Type == ConditionType.ReachMarker)),
            _ => false,
        };
    }

    /// <summary>Missing required parts — errors (the scenario does not start); ignored parts that are there — warnings.</summary>
    public static List<ScenarioProblem> Check(ContentDatabase db, ScenarioDef sc)
    {
        var list = new List<ScenarioProblem>();
        string mode = EdUtil.EnumLabel(sc.Mode);
        foreach (var f in Fields(sc.Mode))
        {
            bool has = Has(db, sc, f.Key);
            if (f.Use == FieldUse.Required && !has)
                list.Add(new(IssueSeverity.Error, "Mode", $"Режим «{mode}»: обязательно — {f.Label}" + (f.Why != "" ? $" ({f.Why})" : "")));
            else if (f.Use == FieldUse.Ignored && has)
                list.Add(new(IssueSeverity.Warning, "Mode", $"Режим «{mode}» не использует: {f.Label}" + (f.Why != "" ? $" — {f.Why}" : "")));
        }
        // A hunting group whose profile is «только маршрут» does not hunt (the profile wins, ТЗ ч. 5, 17.7).
        if (sc.Mode is BattleMode.Assault or BattleMode.Mixed)
            foreach (var w in sc.Waves)
                foreach (var g in w.Groups.Where(g => g.Enabled && (sc.Mode == BattleMode.Assault || g.Hunt)))
                {
                    string profile = g.AiProfile != "" ? g.AiProfile : db.Unit(g.Unit)?.AiProfile ?? "";
                    if (db.Get<AiProfileDef>(profile) is { RouteOnly: true } ap)
                        list.Add(new(IssueSeverity.Warning, "Mode", $"Волна «{w.Name}», группа «{g.Id}»: профиль «{ap.DisplayName}» — «только маршрут», охотиться не будет"));
                }
        return list;
    }
}
