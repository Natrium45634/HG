using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;
using Xunit.Abstractions;

namespace HexTactics.Tests;

/// <summary>
/// Part 3, stage 8 (TZ §22.8): the systems of part 3 together — large groups through narrow passes, routes with nodes,
/// multi-hit series under pause and every speed, performance with passives and auras, recovery from broken content.
/// </summary>
public class Part3IntegrationTests
{
    private readonly ITestOutputHelper _out;

    public Part3IntegrationTests(ITestOutputHelper output)
    {
        _out = output;
    }

#if DEBUG
    private const double Slack = 2.0;
#else
    private const double Slack = 1.0;
#endif

    /// <summary>A 30 × 18 field cut by two walls with narrow passes.</summary>
    private static MapDef Maze()
    {
        var map = TestUtil.Field(30, 18);
        foreach (var c in map.Cells)
        {
            if (c.C.Q == 10 && c.C.R is not (3 or 4 or 12 or 13)) c.T = "wall";
            if (c.C.Q == 18 && c.C.R is not (7 or 8 or 15)) c.T = "wall";
        }
        return map;
    }

    [Fact]
    public void BigGroup_ThroughNarrowPasses_DistinctEnds_NoOneSharesAHex_NoPermanentJam()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, Maze());
        var units = new List<Unit>();
        for (int q = 0; q < 5; q++)
        for (int r = 1; r < 17 && units.Count < 40; r += 2)
            units.Add(sim.Spawn(db.Unit("soldier")!, null, new HexCoord(q, r), HexDirection.E));
        Assert.Equal(40, units.Count);
        var point = new HexCoord(25, 9);
        var plan = sim.Groups.AssignMove(units, point);
        Assert.Equal(40, plan.Cells.Values.Distinct().Count());
        Assert.All(plan.Cells.Values, c => Assert.NotEqual("wall", sim.Map.Get(c)!.Terrain.Id));

        var r1 = sim.IssueGroupOrder(units, new GroupOrder { Command = GroupCommand.Move, Point = point, Facing = HexDirection.E });
        Assert.Equal(40, r1.Ordered.Count);
        var best = units.ToDictionary(u => u.Id, u => HexCoord.Distance(u.Pos, plan.Cells[u.Id]));
        var progressAt = units.ToDictionary(u => u.Id, _ => 0L);
        long longestStall = 0;
        var sw = Stopwatch.StartNew();
        long ticks = 0;
        for (; ticks < sim.TicksFor(240) && !units.All(u => u.Pos == plan.Cells[u.Id]); ticks++)
        {
            sim.Step();
            var cells = units.Select(u => u.Pos).ToList();
            Assert.Equal(cells.Count, cells.Distinct().Count()); // allies never stand on one hex
            foreach (var u in units)
            {
                int d = HexCoord.Distance(u.Pos, plan.Cells[u.Id]);
                if (d < best[u.Id] || d == 0)
                {
                    best[u.Id] = d;
                    progressAt[u.Id] = sim.Tick;
                }
                longestStall = Math.Max(longestStall, sim.Tick - progressAt[u.Id]);
            }
        }
        sw.Stop();
        int arrived = units.Count(u => u.Pos == plan.Cells[u.Id]);
        _out.WriteLine($"группа 40: дошли {arrived} за {ticks * sim.Dt:0.#} с игрового времени, самый долгий простой {longestStall * sim.Dt:0.#} с, " +
                       $"{sw.Elapsed.TotalMilliseconds / Math.Max(1, ticks):0.00} мс/тик");
        foreach (var u in units.Where(u => u.Pos != plan.Cells[u.Id]))
            _out.WriteLine($"  {u.Name}#{u.Id} at {u.Pos} → {plan.Cells[u.Id]}, очередь: {string.Join(", ", u.Queue.Items.Select(c => c.Type + ":" + c.Status + ":" + c.TargetCell))}; " +
                           string.Join(" | ", sim.Log.Entries.Where(e => e.SourceUnit == u.Id).TakeLast(4).Select(e => e.Text)));
        Assert.Equal(40, arrived);
        Assert.True(longestStall * sim.Dt < 60, $"кто-то простаивал {longestStall * sim.Dt:0.#} с — затор не разрешился");
        Assert.All(sim.Map.Cells, c => Assert.Equal(-1, c.DestinationOf));
    }

    [Fact]
    public void Route_WithNodes_ThroughBothPasses_KeepsOrder_AndEndFacing()
    {
        var db = TestUtil.Minimal();
        var sim = TestUtil.Sim(db, Maze());
        var u = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 9), HexDirection.E);
        var a = new HexCoord(11, 3);   // behind the first pass (north)
        var b = new HexCoord(19, 15);  // behind the second pass (south)
        var end = Command.Move(new HexCoord(26, 2));
        end.ArrivalFacing = HexDirection.SW;
        Assert.Null(sim.Enqueue(u, Command.Move(a)));
        Assert.Null(sim.Enqueue(u, Command.Move(b)));
        Assert.Null(sim.Enqueue(u, end));
        var projected = sim.Planner.ProjectQueue(u);
        Assert.Equal(3, projected.Count);
        Assert.Equal(HexDirection.SW, projected[2].EndFacing);
        bool sawA = false, sawB = false;
        for (long i = 0; i < sim.TicksFor(200) && u.Pos != new HexCoord(26, 2); i++)
        {
            sim.Step();
            Assert.NotEqual("wall", sim.Map.Get(u.Pos)!.Terrain.Id);
            if (u.Pos == a) sawA = true;
            if (u.Pos == b)
            {
                Assert.True(sawA, "узел маршрута B пройден раньше A");
                sawB = true;
            }
        }
        Assert.True(sawA && sawB, "оба узла должны быть пройдены");
        Assert.Equal(new HexCoord(26, 2), u.Pos);
        Assert.Equal(HexDirection.SW, u.Facing);
    }

    private sealed record Ev(long Tick, SimEventType Type, int Source, int Target, string Text, double Value);

    private static List<Ev> Snap(IEnumerable<SimEvent> events) => events.Select(e => new Ev(e.Tick, e.Type, e.SourceUnit, e.TargetUnit, e.Text, Math.Round(e.Value, 6))).ToList();

    /// <summary>A shipped battle with orders of series skills for the player side and AI for both sides.</summary>
    private static Simulation SeriesBattle(ContentDatabase db)
    {
        var sim = ScenarioSystem.Create(db, db.Get<ScenarioDef>("valley_ambush")!, 7);
        foreach (var u in sim.Units) u.AiControlled = true;
        var knight = sim.Units.First(u => u.Def.Id == "knight");
        knight.AiControlled = false;
        var foe = sim.Units.Where(x => x.Alive && sim.IsHostile(knight, x)).OrderBy(x => HexCoord.Distance(x.Pos, knight.Pos)).First();
        var order = Command.Skill("triple_strike", null, foe.Id);
        order.Approach = true;
        sim.Enqueue(knight, order);
        return sim;
    }

    [Fact]
    public void PauseAndEverySpeed_GiveTheSameEvents_NoneLost()
    {
        var db = TestUtil.Shipped();
        const double seconds = 40;

        // Reference: tick by tick.
        var a = SeriesBattle(db);
        var refEvents = new List<SimEvent>();
        for (long i = 0; i < a.TicksFor(seconds); i++)
        {
            a.Step();
            refEvents.AddRange(a.DrainEvents());
        }

        // The same battle through the driver: pauses, every speed, uneven frames, step-by-step.
        var b = SeriesBattle(db);
        var driver = new SimDriver(b) { Paused = false };
        var frames = new[] { 0.016, 0.033, 0.1, 0.25, 0.5, 0.007 };
        var got = new List<SimEvent>();
        int frame = 0, pauses = 0;
        while (b.Tick < a.Tick)
        {
            frame++;
            if (frame % 37 == 0)
            {
                driver.Paused = true;
                pauses++;
                long before = b.Tick;
                for (int k = 0; k < 5; k++) driver.Advance(0.1);
                Assert.Equal(before, b.Tick); // a pause runs no ticks
                if (frame % 74 == 0) driver.StepOnce(); // step-by-step while paused
                driver.Paused = false;
            }
            driver.SetSpeedIndex(frame % driver.Speeds.Count);
            driver.Advance(frames[frame % frames.Length]);
            if (driver.Paused) driver.Paused = false; // an autopause: the player continues
            got.AddRange(b.DrainEvents());
            Assert.True(frame < 200_000, "прогон не продвигается");
        }
        var x = Snap(refEvents.Where(e => e.Tick <= a.Tick));
        var y = Snap(got.Where(e => e.Tick <= a.Tick));
        Assert.True(pauses > 0 && driver.Speeds.Count > 1);
        Assert.Equal(x.Count, y.Count);
        for (int i = 0; i < x.Count; i++) Assert.Equal(x[i], y[i]);
        // Nothing lost between the log and the presentation stream.
        Assert.Equal(b.Log.TotalCount, got.Count);
        // The series was really played: three hits of «Три удара мечом» (or the target died earlier).
        var hits = y.Where(e => e.Type == SimEventType.SkillHit && e.Text.Contains("Три удара мечом")).ToList();
        Assert.True(hits.Count >= 1, "серия ударов не выполнялась");
        Assert.Contains(y, e => e.Type == SimEventType.StageResolved);
        _out.WriteLine($"событий {y.Count}, кадров {frame}, пауз {pauses}, отброшено тиков по времени {driver.DroppedTicks}, ударов серии {hits.Count}");
    }

    [Fact]
    public void BigBattle_PassivesAurasSeriesAndGroups_WithinBudget()
    {
        var db = TestUtil.Shipped();
        db.Rules.Autopause = new AutopauseRules { EnemySpotted = false, AllyCriticalDamage = false, UnitDied = false, PathUnavailable = false, WaveStarted = false };
        var map = MapDef.CreateRectangle("p3_load", 32, 20, "floor");
        db.Add(map);
        var sc = new ScenarioDef { Id = "p3_load", Name = "Нагрузка ч. 3", Map = map.Id, Seed = 11, Victory = new(), Defeat = new() };
        string[] allies = { "knight", "archer", "mage", "priestess", "elf_ranger", "scout" };
        string[] foes = { "goblin", "orc_brute", "slime", "wolf", "goblin_archer" };
        var cells = map.Cells.Select(c => c.C).ToList();
        int n = 0;
        foreach (var c in cells.Where(c => c.Q + c.R / 2 is >= 1 and <= 5 && c.R is >= 1 and <= 18).Take(48))
            sc.Units.Add(new ScenarioUnitDef { Unit = allies[n++ % allies.Length], C = c, Facing = HexDirection.E });
        n = 0;
        foreach (var c in cells.Where(c => c.Q + c.R / 2 is >= 25 and <= 29 && c.R is >= 1 and <= 18).Take(48))
            sc.Units.Add(new ScenarioUnitDef { Unit = foes[n++ % foes.Length], C = c, Facing = HexDirection.W });
        db.Add(sc);
        var sim = ScenarioSystem.Create(db, sc, 11);
        var players = sim.Units.Where(u => sim.IsPlayerFaction(u.FactionId)).ToList();
        foreach (var u in sim.Units.Where(u => !sim.IsPlayerFaction(u.FactionId))) u.AiControlled = true;
        // The player's group: an attack order on the enemy line, then the AI takes over.
        var target = sim.Units.Where(u => !sim.IsPlayerFaction(u.FactionId)).OrderBy(u => u.Pos.Q).First();
        var res = sim.IssueGroupOrder(players, new GroupOrder { Command = GroupCommand.Attack, TargetUnitId = target.Id });
        Assert.True(res.Ordered.Count > 0);
        long ticks = sim.TicksFor(45);
        int passives = sim.Units.Sum(u => u.PassiveInstances.Count);
        var sw = Stopwatch.StartNew();
        for (long i = 0; i < ticks; i++)
        {
            sim.Step();
            if (i == sim.TicksFor(10)) foreach (var u in players) u.AiControlled = true;
            sim.DrainEvents();
        }
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds / ticks;
        int series = sim.Log.Entries.Count(e => e.Type == SimEventType.SkillHit);
        int fired = sim.Units.Sum(u => u.PassiveFired.Values.Sum());
        _out.WriteLine($"юнитов {sim.Units.Count}, пассивов {passives}, срабатываний пассивов {fired}, ударов серий {series}, погибло {sim.Units.Count(u => !u.Alive)}, " +
                       $"событий {sim.Log.TotalCount}, мс/тик {ms:0.00}");
        foreach (var (sec, _, per) in sim.Profiler.Totals()) _out.WriteLine($"  {sec}: {per:0.000} мс/тик");
        Assert.True(passives > 0 && sim.Units.Count(u => !u.Alive) > 0, "бой должен идти, пассивы — действовать");
        Assert.True(ms < 12.5 * Slack, $"слишком медленно: {ms:0.00} мс/тик");
    }

    [Fact]
    public void BrokenReferences_AreReported_AndTheBattleStillRuns()
    {
        var db = TestUtil.Shipped();
        var knight = db.Unit("knight")!;
        knight.Passives.Add("no_such_passive");
        knight.Origins.Add("no_such_origin");
        knight.Visual.Model = "no_such_model";
        knight.Visual.FallbackModel = "no_such_fallback";
        db.Skill("fireball")!.Stages[0].Projectile = new ProjectileDef { Enabled = true, Model = "no_such_arrow", HitVfx = "no_such_vfx" };
        db.Skill("triple_strike")!.IconAsset = "no_such_icon";
        db.Status(db.All<StatusDef>().First().Id)!.IconAsset = "no_such_status_icon";
        db.Get<TerrainDef>("grass")!.Look.TopTexture = "no_such_texture";
        db.Get<TerrainDef>("grass")!.Look.PersistentVfx = "no_such_loop";
        db.CellEffect("fire")!.LoopVfx = "no_such_fire";
        db.Get<PassiveDef>(db.All<PassiveDef>().First().Id)!.Description += " ";
        var issues = ContentValidator.Validate(db);
        foreach (var id in new[] { "no_such_passive", "no_such_origin", "no_such_model", "no_such_arrow", "no_such_icon", "no_such_texture", "no_such_fire" })
            Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains(id));
        var report = AssetReport.Build(db, _ => null);
        Assert.Contains(report.MissingRefs, m => m.Contains("no_such_arrow"));

        // The battle runs anyway: unknown passives and origins are skipped, visuals are not part of the battle.
        foreach (var scId in new[] { "valley_ambush", "sandbox" })
        {
            var sim = ScenarioSystem.Create(db, db.Get<ScenarioDef>(scId)!, 3);
            foreach (var u in sim.Units) u.AiControlled = true;
            for (long i = 0; i < sim.TicksFor(30); i++) sim.Step();
            Assert.True(sim.Log.TotalCount > 0);
        }

        // Damaged files of the library: reported, never thrown.
        var mem = new MemoryStorage();
        new ContentLibrary(mem).Publish(TestUtil.Shipped(), null);
        mem.WriteText("skills.json", "{ \"formatVersion\": 4, \"kind\": \"skills\", \"items\": [ { \"id\": \"broken\", ");
        var loaded = ContentIO.Load(mem);
        Assert.True(loaded.HasErrors);
        Assert.Contains(loaded.Issues, i => i.Severity == IssueSeverity.Error && i.Source.Contains("skills"));
    }
}
