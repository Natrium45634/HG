using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage C — the modes of a scenario and their fields (docs/DECISIONS_5.md §86): T27, «Штурм», «Смешанный».</summary>
public class Part5ModeTests
{
    private static (ContentDatabase Db, ScenarioDef Sc) Scene(BattleMode mode, bool withBase = true)
    {
        var db = TestUtil.Minimal();
        db.Rules.Time.SimVersion = 2;
        // Not distracted on the route: only the mode (or the group's «охотится») sends them after the player.
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегущий", RouteFocus = true });
        db.Add(new UnitDef
        {
            Id = "wolf", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "runner",
            Stats = { [Stats.HpMax] = 30, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 4, [Stats.BaseDamage] = 3 },
        });
        var map = TestUtil.Field(20, 12);
        map.Id = "p5mode";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5mode", Name = "Режимы", Map = map.Id, Seed = 3, Mode = mode, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 2)));
        if (withBase)
        {
            var b = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(18, 2));
            b.Health!.MaxHp = 100000;
            sc.Objects.Add(b);
        }
        var cells = HexCoord.Line(new HexCoord(0, 2), new HexCoord(17, 2)).ToList();
        sc.Routes.Add(RouteGraph.FromCells("r1", cells, "spawn_1", withBase ? "base_1" : "", "Дорога"));
        sc.Units.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(6, 10), Tag = "hero" });
        sc.Waves.Add(new WaveDef
        {
            Name = "Волна", Start = WaveStart.Time, Time = 0,
            Groups =
            {
                new SpawnGroupDef { Id = "runners", Unit = "wolf", Count = 2, Interval = 1, SpawnBase = "spawn_1", Route = "r1" },
                new SpawnGroupDef { Id = "hunters", Unit = "wolf", Count = 2, Interval = 1, SpawnBase = "spawn_1", Route = "r1", Hunt = true },
            },
        });
        db.Add(sc);
        return (db, sc);
    }

    private static string[] ModeErrors(ContentDatabase db, ScenarioDef sc) =>
        ScenarioObjectsCheck.All(db, sc, db.Get<MapDef>(sc.Map)!).Where(p => p.Path == "Mode" && p.Severity == IssueSeverity.Error).Select(p => p.Message).ToArray();

    [Fact]
    public void T27_FreeBattleWithoutABase_IsValid_TowerDefenseWithoutABase_IsAnError()
    {
        var (db, free) = Scene(BattleMode.FreeBattle, withBase: false);
        Assert.Empty(ModeErrors(db, free));
        // Routes are ignored by a free battle: a warning, not an error.
        Assert.Contains(ScenarioModes.Check(db, free), p => p.Severity == IssueSeverity.Warning && p.Message.Contains("маршруты"));

        foreach (var mode in new[] { BattleMode.TowerDefense, BattleMode.TdPlacement })
        {
            var (db2, td) = Scene(mode, withBase: false);
            Assert.Contains(ModeErrors(db2, td), m => m.Contains("обязательно — базы игрока"));
            // And it does not start: the check the launch uses says so.
            Assert.Contains(ScenarioObjectsCheck.All(db2, td, db2.Get<MapDef>(td.Map)!), p => p.Severity == IssueSeverity.Error && p.Path == "Mode");
        }
        var (db3, withBase) = Scene(BattleMode.TowerDefense);
        Assert.Empty(ModeErrors(db3, withBase));
    }

    [Fact]
    public void EveryMode_HasItsTable_AndTheRequiredFieldsAreChecked()
    {
        foreach (var mode in System.Enum.GetValues<BattleMode>())
        {
            var fields = ScenarioModes.Fields(mode);
            Assert.Equal(fields.Count, fields.Select(f => f.Key).Distinct().Count());
            Assert.NotEmpty(ScenarioModes.Summary(mode));
        }
        Assert.All(ScenarioModes.Fields(BattleMode.Sandbox), f => Assert.Equal(FieldUse.Allowed, f.Use));
        // «Смешанный» without hunting groups is just TD: an error.
        var (db, mixed) = Scene(BattleMode.Mixed);
        Assert.Empty(ModeErrors(db, mixed));
        mixed.Waves[0].Groups[1].Hunt = false;
        Assert.Contains(ModeErrors(db, mixed), m => m.Contains("охотится"));
        // «Сопровождение» needs a finish.
        var (db2, escort) = Scene(BattleMode.Escort);
        Assert.Contains(ModeErrors(db2, escort), m => m.Contains("сопровождение или финиш"));
        escort.Victory.Add(new ConditionDef { Type = ConditionType.ReachMarker, Marker = "exit" });
        Assert.Empty(ModeErrors(db2, escort));
        // «Штурм» needs someone to hunt.
        var (db3, assault) = Scene(BattleMode.Assault);
        Assert.Empty(ModeErrors(db3, assault));
        assault.Units.Clear();
        Assert.Contains(ModeErrors(db3, assault), m => m.Contains("юниты игрока"));
        // The ignored hunting flag outside «Смешанный» is a warning.
        var (db4, td) = Scene(BattleMode.TowerDefense);
        Assert.Contains(ScenarioModes.Check(db4, td), p => p.Severity == IssueSeverity.Warning && p.Message.Contains("охотится"));
    }

    private static (double Hunters, double Runners) Distances(ContentDatabase db, ScenarioDef sc)
    {
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(6);
        var hero = sim.Units.Single(u => u.Tag == "hero");
        double Avg(string g) => sim.Units.Where(u => u.Group == g && u.Alive).Select(u => (double)HexCoord.Distance(u.Pos, hero.Pos)).DefaultIfEmpty(0).Average();
        return (Avg("hunters"), Avg("runners"));
    }

    [Fact]
    public void Mixed_HuntersGoForThePlayer_TheOthersKeepTheRoute_Assault_EveryoneHunts()
    {
        var (db, mixed) = Scene(BattleMode.Mixed);
        var (h, r) = Distances(db, mixed);
        Assert.True(h + 3 < r, $"охотники ({h:0.#}) должны быть ближе к герою, чем идущие по маршруту ({r:0.#})");

        var (db2, assault) = Scene(BattleMode.Assault);
        var (h2, r2) = Distances(db2, assault);
        Assert.True(h2 < 5 && r2 < 5, $"в штурме все ищут героя: {h2:0.#} и {r2:0.#}");

        // The same groups in TD: everyone keeps the route, the flag is ignored.
        var (db3, td) = Scene(BattleMode.TowerDefense);
        var (h3, r3) = Distances(db3, td);
        Assert.True(h3 > 5 && r3 > 5, $"в TD охоты нет: {h3:0.#} и {r3:0.#}");
    }
}
