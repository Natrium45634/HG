using System.Diagnostics;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;
using Xunit.Abstractions;

namespace HexTactics.Tests;

public class TimeTests
{
    [Fact]
    public void Pause_DoesNotAdvance()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var drv = new SimDriver(sim) { Paused = true };
        for (int i = 0; i < 100; i++) drv.Advance(1 / 60.0);
        Assert.Equal(0, sim.Tick);
    }

    [Theory]
    [InlineData(0, 0.25)]
    [InlineData(1, 0.5)]
    [InlineData(2, 1.0)]
    [InlineData(3, 2.0)]
    [InlineData(4, 4.0)]
    public void Speeds_ScaleTickCount(int index, double speed)
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var drv = new SimDriver(sim) { Paused = false };
        drv.SetSpeedIndex(index);
        Assert.Equal(speed, drv.Speed);
        for (int i = 0; i < 600; i++) drv.Advance(1 / 60.0); // 10 real seconds
        Assert.InRange(sim.Tick, (long)(200 * speed) - 1, (long)(200 * speed) + 1);
    }

    /// <summary>Arena battle where both sides are AI-driven and start by marching to the centre.</summary>
    public static Simulation Battle(ulong seed)
    {
        var db = TestUtil.Shipped();
        db.Rules.Autopause = new AutopauseRules
        {
            EnemySpotted = false, AllyCriticalDamage = false, UnitDied = false, PathUnavailable = false, WaveStarted = false,
        };
        var sc = db.Get<ScenarioDef>("arena_duel")!;
        var sim = new Simulation(db, db.Get<MapDef>(sc.Map)!, new SimOptions
        {
            Seed = seed, PlayerFactions = new() { "player" }, AiFactions = new() { "player", "enemy" }, FogOfWar = true,
        });
        sim.Scenario.Load(sc);
        foreach (var u in sim.Units) sim.Enqueue(u, Command.Move(new HexCoord(u.FactionId == "player" ? -1 : 1, 0)));
        return sim;
    }

    [Fact]
    public void Results_DoNotDependOnFrameRate()
    {
        // Same seed, different frame rates (30 vs 144 FPS) → identical state at the same tick.
        var a = Battle(99);
        var b = Battle(99);
        var da = new SimDriver(a) { Paused = false };
        var db = new SimDriver(b) { Paused = false };
        da.SetSpeedIndex(4);
        db.SetSpeedIndex(4);
        while (a.Tick < 400) da.Advance(1 / 30.0);
        while (b.Tick < a.Tick) db.Advance(1 / 144.0);
        while (b.Tick > a.Tick) a.Step();
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void SameSeed_SameResult_DifferentSeed_DifferentResult()
    {
        var a = Battle(5);
        var b = Battle(5);
        var c = Battle(6);
        a.Run(1200);
        b.Run(1200);
        c.Run(1200);
        Assert.True(a.Rng.Draws > 0);
        Assert.Equal(a.StateHash(), b.StateHash());
        Assert.NotEqual(a.StateHash(), c.StateHash());
    }

    [Fact]
    public void EffectTicks_AreSameAtAnySpeed()
    {
        int Ticks(int speedIndex)
        {
            var db = TestUtil.Minimal();
            db.Add(new CellEffectDef { Id = "f", Name = "f", Duration = 0, TickInterval = 0.5, OnTick = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 1, Filter = TargetFilterDef.Everyone() } } });
            db.Unit("soldier")!.Stats[Stats.HpMax] = 1000;
            var sim = TestUtil.Sim(db, TestUtil.Field());
            var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 1), HexDirection.E);
            sim.CellEffects.Add(sim.Map.Get(u.Pos)!, db.CellEffect("f")!, 1, 0, -1, 0, 0);
            var drv = new SimDriver(sim) { Paused = false };
            drv.SetSpeedIndex(speedIndex);
            while (sim.Tick < 200 - 8) drv.Advance(1 / 60.0);
            while (sim.Tick < 200) sim.Step();
            return (int)(u.HpMax - u.Hp);
        }
        int slow = Ticks(0), normal = Ticks(2), fast = Ticks(4);
        Assert.Equal(20, normal);
        Assert.Equal(normal, slow);
        Assert.Equal(normal, fast);
    }
}

public class AiAndScenarioTests
{
    [Fact]
    public void Ai_ApproachesAndAttacks_UsingSameRules()
    {
        var db = TestUtil.Minimal();
        db.Rules.Combat.MinHit = 1;
        var sim = TestUtil.Sim(db, TestUtil.Field(12, 6), ai: true);
        var player = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(1, 2), HexDirection.E);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(7, 2), HexDirection.W);
        Assert.True(enemy.AiControlled);
        sim.RunSeconds(8);
        Assert.True(player.Hp < player.HpMax, enemy.Ai.Explanation);
        Assert.False(string.IsNullOrEmpty(enemy.Ai.Explanation));
        Assert.True(HexCoord.Distance(enemy.Pos, player.Pos) <= 1);
    }

    [Fact]
    public void Ai_AdvanceWhenIdle_MovesTowardPlayerStart()
    {
        var db = TestUtil.Minimal();
        var map = TestUtil.Field(30, 3);
        map.Markers.Add(new MapMarkerDef { Type = MarkerType.PlayerStart, C = new HexCoord(1, 1) });
        var sim = TestUtil.Sim(db, map, ai: true);
        var enemy = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(28, 1), HexDirection.W);
        sim.RunSeconds(4);
        Assert.Equal(new HexCoord(28, 1), enemy.Pos); // default profile waits

        db.Get<AiProfileDef>(enemy.Def.AiProfile)!.AdvanceWhenIdle = true;
        sim.RunSeconds(6);
        Assert.True(HexCoord.Distance(enemy.Pos, new HexCoord(1, 1)) < 27, enemy.Ai.Explanation);
        Assert.Contains("наступает", enemy.Ai.Explanation);
    }

    [Fact]
    public void Scenario_Waves_And_Victory()
    {
        var db = TestUtil.Shipped();
        var sc = db.Get<ScenarioDef>("valley_ambush")!;
        var sim = ScenarioSystem.Create(db, sc, 1);
        // Remove the initial enemies so the passive player squad survives until the first wave.
        foreach (var u in sim.Units.Where(u => u.FactionId == "enemy").ToList()) sim.Combat.Kill(u, null, "тест");
        int enemiesBefore = sim.Units.Count(u => u.FactionId == "enemy" && u.Alive);
        sim.RunSeconds(sc.Waves[0].Time + 0.1);
        Assert.Equal(BattleOutcome.None, sim.Outcome);
        Assert.Contains(0, sim.Scenario.SpawnedWaves);
        Assert.True(sim.Units.Count(u => u.FactionId == "enemy" && u.Alive) > enemiesBefore);
        // Kill every enemy → victory once all waves have spawned.
        sim.Scenario.SpawnWave(1);
        foreach (var u in sim.Units.Where(u => u.FactionId == "enemy").ToList()) sim.Combat.Kill(u, null, "тест");
        sim.Step();
        Assert.Equal(BattleOutcome.Victory, sim.Outcome);
    }

    [Fact]
    public void Scenario_Defeat_WhenHeroDies()
    {
        var db = TestUtil.Shipped();
        var sim = ScenarioSystem.Create(db, db.Get<ScenarioDef>("valley_ambush")!, 1);
        var hero = sim.Units.First(u => u.Tag == "hero");
        sim.Combat.Kill(hero, null, "тест");
        sim.Step();
        Assert.Equal(BattleOutcome.Defeat, sim.Outcome);
    }

    [Fact]
    public void FogOfWar_HidesUndetectedEnemies()
    {
        var db = TestUtil.Shipped();
        var sim = ScenarioSystem.Create(db, db.Get<ScenarioDef>("valley_ambush")!, 1);
        var orc = sim.Units.First(u => u.Def.Id == "orc_brute");
        Assert.False(sim.Vision.IsVisibleToPlayer(orc));
        var knight = sim.Units.First(u => u.Def.Id == "knight");
        Assert.True(sim.Vision.IsVisibleToPlayer(knight));
    }
}

/// <summary>Timing budgets are measured alone: parallel tests would compete for the CPU.</summary>
[CollectionDefinition("Performance", DisableParallelization = true)]
public class PerformanceCollection
{
}

[Collection("Performance")]
public class StressTests
{
    private readonly ITestOutputHelper _out;

    public StressTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public void ManyUnits_BigMap_FrequentEffects()
    {
        var db = TestUtil.Shipped();
        db.Rules.Autopause = new AutopauseRules { EnemySpotted = false, AllyCriticalDamage = false, UnitDied = false, PathUnavailable = false, WaveStarted = false };
        var map = MapDef.CreateRectangle("big", 60, 60, "grass");
        var rng = new SimRng(3);
        foreach (var c in map.Cells)
        {
            double r = rng.NextDouble();
            if (r < 0.05) c.T = "rock";
            else if (r < 0.10) c.T = "shallow_water";
            else if (r < 0.14) c.T = "bushes";
        }
        var sim = new Simulation(db, map, new SimOptions { Seed = 11, PlayerFactions = new() { "player" }, AiFactions = new() { "enemy", "player" }, FogOfWar = true });
        string[] players = { "knight", "archer", "mage", "priestess", "scout" };
        string[] enemies = { "goblin", "goblin_archer", "orc_brute", "shaman", "wolf" };
        int spawned = 0;
        foreach (var c in map.Cells)
        {
            if (spawned >= 200) break;
            var (col, row) = (c.C.Q + c.C.R / 2, c.C.R);
            if (row % 3 != 0 || col % 3 != 0 || c.T != "grass") continue;
            bool west = col < 30;
            var id = west ? players[spawned % 5] : enemies[spawned % 5];
            sim.Spawn(db.Unit(id)!, west ? "player" : "enemy", c.C, west ? HexDirection.E : HexDirection.W);
            spawned++;
        }
        // Frequent effects: fire and poison scattered around.
        for (int i = 0; i < 150; i++)
        {
            var cell = sim.Map.Cells[rng.NextInt(0, sim.Map.Count)];
            sim.CellEffects.Add(cell, db.CellEffect(i % 2 == 0 ? "fire" : "poison_cloud")!, 1, 0, -1, 0, 0);
        }
        var sw = Stopwatch.StartNew();
        int ticks = 600; // 30 s of simulation
        sim.Run(ticks);
        sw.Stop();
        double msPerTick = sw.Elapsed.TotalMilliseconds / ticks;
        _out.WriteLine($"units {spawned}, cells {sim.Map.Count}, alive {sim.AliveUnits.Count()}, ms/tick {msPerTick:0.00}, " +
                       $"path searches {sim.Pathfinder.TotalSearches}, LoS checks {sim.Los.Checks}, vision recomputes {sim.Vision.Recomputes}, " +
                       $"synergies {sim.CellEffects.SynergiesTriggered}, log {sim.Log.TotalCount}");
        foreach (var (sec, total, per) in sim.Profiler.Totals()) _out.WriteLine($"  {sec}: {per:0.000} мс/тик");
        Assert.Equal(200, spawned);
        // 20 ticks per second at 4× speed = 80 ticks per real second → budget 12.5 ms per tick for the
        // optimised build (CI and the game run Release); unoptimised Debug code gets twice the budget.
#if DEBUG
        const double budget = 25;
#else
        const double budget = 12.5;
#endif
        Assert.True(msPerTick < budget, $"слишком медленно: {msPerTick:0.00} мс/тик (бюджет {budget} мс)");
    }

    [Fact]
    public void LongChainOfSynergies_StaysWithinLimits()
    {
        var db = TestUtil.Shipped();
        var map = MapDef.CreateRectangle("gas", 30, 30, "grass");
        var sim = new Simulation(db, map, new SimOptions { Seed = 1, FogOfWar = false });
        foreach (var c in sim.Map.Cells) sim.CellEffects.Add(c, db.CellEffect("poison_cloud")!, 1, 0, -1, 0, 0);
        var sw = Stopwatch.StartNew();
        sim.CellEffects.Add(sim.Map.Get(map.Cells[400].C)!, db.CellEffect("fire")!, 1, 0, -1, 0, 0);
        sim.Run(100);
        sw.Stop();
        _out.WriteLine($"synergies {sim.CellEffects.SynergiesTriggered}, {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }
}
