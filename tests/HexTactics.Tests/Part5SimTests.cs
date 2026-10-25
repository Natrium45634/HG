using System;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage C — the simulator (docs/DECISIONS_5.md §86): reset to the snapshot (T26), the replay, the unsafe test.</summary>
public class Part5SimTests
{
    private static (ContentDatabase Db, ScenarioDef Sc) Battle(ulong seed = 7)
    {
        var db = TestUtil.Minimal();
        db.Rules.Time.SimVersion = 2;
        db.Add(new StatusDef { Id = "haste", Name = "Ускорение", Duration = 30, Negative = false });
        db.Add(new UnitDef
        {
            Id = "wolf", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "aggressive",
            Stats = { [Stats.HpMax] = 30, [Stats.ApMax] = 10, [Stats.ApRegen] = 2, [Stats.MoveSpeed] = 3, [Stats.BaseDamage] = 3 },
        });
        var map = TestUtil.Field(16, 9);
        map.Id = "p5sim";
        db.Add(map);
        var sc = new ScenarioDef { Id = "p5sim", Name = "Снимок", Map = map.Id, Seed = seed, Victory = new(), Defeat = new() };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 4)));
        var b = ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(15, 4));
        b.Health!.MaxHp = 500;
        sc.Objects.Add(b);
        sc.Routes.Add(RouteGraph.FromCells("r1", HexCoord.Line(new HexCoord(0, 4), new HexCoord(14, 4)).ToList(), "spawn_1", b.Id, "Дорога"));
        sc.Units.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(10, 2), Tag = "hero" });
        sc.Units.Add(new ScenarioUnitDef { Unit = "soldier", C = new HexCoord(10, 6), Tag = "hero2" });
        sc.Waves.Add(new WaveDef
        {
            Name = "Волна", Start = WaveStart.Time, Time = 0,
            Groups = { new SpawnGroupDef { Id = "g", Unit = "wolf", Count = 5, Interval = 1.5, SpawnBase = "spawn_1", Route = "r1", RouteSelect = GroupRouteSelect.Random } },
        });
        db.Add(sc);
        return (db, sc);
    }

    /// <summary>The player gives orders now and then; the AI and the random streams do the rest.</summary>
    private static void Play(Simulation sim, double seconds)
    {
        var hero = sim.Units.Single(u => u.Tag == "hero");
        var hero2 = sim.Units.Single(u => u.Tag == "hero2");
        for (long i = 0; i < sim.TicksFor(seconds); i++)
        {
            if (i == 5) sim.Input.Enqueue(hero, Command.Move(new HexCoord(8, 4)));
            if (i == 40) sim.Input.Enqueue(hero2, Command.Move(new HexCoord(9, 5)));
            if (i == 60) sim.Input.Enqueue(hero2, Command.Wait(2), insertIndex: 0);
            if (i == 90 && hero2.Queue.Items.FirstOrDefault() is { } c) sim.Input.RemoveCommand(hero2, c.Id);
            if (i == 120 && sim.Units.FirstOrDefault(u => u.Group == "g" && u.Alive) is { } wolf) sim.Input.Enqueue(hero, Command.Attack(wolf.Id));
            if (i == 150) sim.Input.IssueGroupOrder(new[] { hero, hero2 }, new GroupOrder { Command = GroupCommand.Move, Point = new HexCoord(11, 4) });
            sim.Step();
        }
    }

    [Fact]
    public void T26_InterruptAndReset_StateRngBuffsAndQueuesAreBackToTheSnapshot()
    {
        var (db, sc) = Battle();
        var (snap, sim) = BattleSnapshot.Begin(db, sc);
        Assert.Empty(snap.Check(sim));
        Play(sim, 12);
        // A temporary buff and a queue in the middle of the battle.
        var hero = sim.Units.First(u => u.Alive && !u.IsStructure);
        Assert.True(sim.Statuses.Apply(hero, db.Get<StatusDef>("haste")!, 1, 30, hero.Id));
        sim.Input.Enqueue(hero, Command.Move(new HexCoord(3, 1)));
        sim.Input.Enqueue(hero, Command.Wait(5));
        var broken = snap.Check(sim);
        Assert.Contains("время", broken);
        Assert.Contains("ГСЧ", broken);
        Assert.Contains("юниты", broken);
        Assert.Contains("очереди", broken);
        Assert.Contains("состояния", broken);

        // Edits of the content after the start do not leak into the reset.
        db.Unit("wolf")!.Stats[Stats.HpMax] = 999;
        db.Get<ScenarioDef>(sc.Id)!.Waves[0].Groups[0].Count = 1;
        var again = snap.Restore();
        Assert.Empty(snap.Check(again));
        Assert.Equal(snap.Start.Hash, again.Digest().Hash);
        Assert.All(again.Units, u => Assert.Empty(u.Statuses));
        Assert.True(again.Units.All(u => u.Queue.Items.Count == 0));
        // And it runs the same way again: the same seed, the same calls — the same state.
        Play(again, 12);
        var third = snap.Restore();
        Play(third, 12);
        Assert.Equal(again.Digest().Hash, third.Digest().Hash);
    }

    [Fact]
    public void Snapshot_OfARandomSeedScenario_KeepsTheSeed()
    {
        var (db, sc) = Battle(seed: 0);
        var (snap, sim) = BattleSnapshot.Begin(db, sc);
        Assert.NotEqual(0UL, snap.Seed);
        Play(sim, 6);
        var a = sim.Digest().Hash;
        var again = snap.Restore();
        Play(again, 6);
        Assert.Equal(a, again.Digest().Hash);
    }

    [Fact]
    public void Replay_RepeatsTheBattle_ReportsChangedContent_AndSurvivesEditsWithTheSavedContent()
    {
        var (db, sc) = Battle();
        var (snap, sim) = BattleSnapshot.Begin(db, sc);
        var rec = BattleReplay.Record(snap, sim, withContent: true);
        Play(sim, 15);
        rec.Finish(sim);
        Assert.True(rec.Entries.Count >= 5);
        Assert.Contains(rec.Entries, e => e.Op == ReplayOp.Group);
        // The AI's own orders are not in the journal.
        Assert.All(rec.Entries, e => Assert.True(e.Unit < 0 || sim.GetUnit(e.Unit)!.FactionId == "player"));

        var back = BattleReplay.FromJson(rec.ToJson())!;
        Assert.Equal(rec.EndHash, back.EndHash);
        var play = back.Play(db);
        Assert.True(play.Matches, play.Text);
        Assert.Empty(play.ContentChanges);

        // The content changed after the record: the replay says what, and without the saved copy it diverges.
        var wolf = db.Unit("wolf")!;
        wolf.Stats[Stats.MoveSpeed] = 5;
        wolf.Version++;
        var changed = back.Play(db);
        Assert.Contains("Unit:wolf: версия 1 → 2", changed.ContentChanges);
        Assert.False(changed.Matches);
        Assert.Contains("Контент изменён", changed.Text);
        // With the content saved in the replay it reproduces anyway.
        var saved = back.Play();
        Assert.True(saved.Matches, saved.Text);
        // An edit without a new version is noticed too.
        wolf.Version--;
        Assert.Contains(back.Changes(db), c => c.Contains("без смены версий"));
    }

    [Fact]
    public void UnsafeTest_RunsAScenarioWithErrors_ListsThem_AndNeverThrows()
    {
        var (db, sc) = Battle();
        sc.Mode = BattleMode.TowerDefense;
        sc.Objects.RemoveAll(o => o.Type == ScenarioObjectType.PlayerBase);
        var errors = UnsafeTest.Errors(db, sc);
        Assert.Contains(errors, p => p.Message.Contains("обязательно — базы игрока"));
        var res = UnsafeTest.Run(db, sc, 20);
        Assert.True(res.Started);
        Assert.NotNull(res.Report);
        Assert.Contains("НЕБЕЗОПАСНЫЙ ТЕСТ", res.Text);
        Assert.Contains("обязательно — базы игрока", res.Text);

        // No map at all: the battle cannot be created — written down, not thrown.
        sc.Map = "нет_такой";
        var none = UnsafeTest.Run(db, sc, 5);
        Assert.False(none.Started);
        Assert.Contains(none.Exceptions, e => e.Contains("создание боя"));
    }

    [Fact]
    public void Driver_CatchingErrors_PausesTheBattleInsteadOfBreakingIt()
    {
        var (db, sc) = Battle();
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.Stepped += () =>
        {
            if (sim.Tick == 10) throw new InvalidOperationException("проба");
        };
        var driver = new SimDriver(sim) { CatchErrors = true };
        string? failed = null;
        driver.Failed += e => failed = e;
        for (int i = 0; i < 30 && driver.Error == null; i++)
        {
            driver.Paused = false; // past the autopauses
            driver.Advance(sim.Dt);
        }
        Assert.True(driver.Paused);
        Assert.Contains("проба", driver.Error);
        Assert.Equal(driver.Error, failed);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("Небезопасный тест: ошибка симуляции"));
        // Without the flag the exception goes up as before.
        var sim2 = ScenarioSystem.Create(db, sc, 1);
        sim2.Stepped += () =>
        {
            if (sim2.Tick == 3) throw new InvalidOperationException("проба");
        };
        var plain = new SimDriver(sim2);
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                plain.Paused = false;
                plain.Advance(sim2.Dt);
            }
        });
    }
}
