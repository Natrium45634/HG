using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 6, stage 6.9 — integration (ТЗ ч. 6, 14.1, 15, 16; T31–T32; docs/DECISIONS_6.md §107): the demo level «Два прохода», the
/// switches of the subsystems, the codes and fixes of the problems of the content, one result for the visual and the batch run.
/// </summary>
public class Part6IntegrationTests
{
    private static (ContentDatabase Db, ScenarioDef Sc) Demo()
    {
        var db = TestUtil.Shipped();
        return (db, db.Get<ScenarioDef>("two_passes")!);
    }

    private static Simulation Battle(ContentDatabase db, ScenarioDef sc, params string[] roster) => ScenarioSystem.Create(db, sc, 11, roster: roster);

    [Fact]
    public void TheDemoLevel_HasEveryPieceOfTheAcceptance()
    {
        var (db, sc) = Demo();
        var issues = ContentValidator.Validate(db);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error && (i.Source.Contains("two_passes") || i.Source.Contains("pass_warlord")));
        var mode = EffectiveGameRules.Resolve(db, sc);
        // Points for two calls, three defenders: a blocking guard, a shooter, a cheap mobile one.
        Assert.True(mode.Deployment.Enabled);
        Assert.Equal(10, mode.Deployment.StartingPoints);
        Assert.Equal(new[] { "knight", "archer", "scout" }, sc.Squad!.Pool);
        Assert.True(db.Unit("knight")!.Stats.GetValueOrDefault("block_limit") >= 2);
        Assert.Equal(3, db.Unit("scout")!.Deployment!.Cost);
        // One base, two routes, a near and a far zone.
        Assert.Single(sc.Objects, o => o.Type == ScenarioObjectType.PlayerBase);
        Assert.Equal(2, sc.Routes.Count);
        Assert.Equal(new[] { "ближняя", "дальняя" }, sc.DeploymentZones.SelectMany(z => z.Tags));
        Assert.All(sc.DeploymentZones, z => Assert.NotEmpty(z.Cells));
        // The bridge and its lever; the wave of the bypass waits for the bridge; the warlord with two phases.
        Assert.Equal("raised", sc.Objects.Single(o => o.Id == "bridge").States!.Initial);
        Assert.Equal("bridge", sc.Objects.Single(o => o.Id == "lever").Interaction!.Target);
        Assert.Equal(WaveStart.Manual, sc.Waves.Single(w => w.Name == "Обход").Start);
        Assert.Contains(sc.Triggers, t => t.Event == ScenarioEventType.ObjectStateChanged && t.EventObject == "bridge" && t.Actions.Any(a => a.Type == TriggerActionType.SpawnWave));
        Assert.Equal(2, db.Unit("pass_warlord")!.Boss.Phases.Count);
        // A harder variant and a campaign of two missions.
        Assert.Equal("two_passes", db.Get<DifficultyVariantDef>("two_passes_hard")!.Scenario);
        var c = db.Get<CampaignDef>("two_passes_campaign")!;
        Assert.Empty(Campaigns.Check(db, c).Where(p => p.Error));
        Assert.Equal(new[] { "fork" }, c.Node("hold")!.Next);
    }

    [Fact]
    public void TheDemoLevel_PointsForTwoCalls_TheCheapOneLeavesTwo()
    {
        var (db, sc) = Demo();
        var sim = Battle(db, sc, "knight", "archer", "scout");
        Assert.Equal(10, sim.Deployment.Points);
        var near = sc.DeploymentZones.Single(z => z.Id == "near").Cells.Where(c => sim.Deployment.CellProblem(sim.Squad.Entries[0], c) == null).ToList();
        Assert.NotNull(sim.Input.Deploy(0, near[0], HexDirection.W, out var why));
        Assert.NotNull(sim.Input.Deploy(1, near.First(c => sim.Deployment.CellProblem(sim.Squad.Entries[1], c) == null), HexDirection.W, out why));
        Assert.Equal(0, sim.Deployment.Points, 6);
        Assert.Null(sim.Input.Deploy(2, near.Last(), HexDirection.W, out why));
        Assert.Contains("не хватает очков", why);
    }

    [Fact]
    public void TheDemoLevel_TheBridgeStartsTheBypass_ByTheLeverOrTheSappers()
    {
        var (db, sc) = Demo();
        int bypass = sc.Waves.FindIndex(w => w.Name == "Обход");
        // The sappers lower the bridge at 40 s: the bypass starts.
        var sim = Battle(db, sc);
        var bridgeCell = sim.Map.Get(new HexCoord(1, 7))!;
        Assert.Equal("chasm", bridgeCell.Terrain.Id);
        for (int i = 0; i < sim.TicksFor(39); i++) sim.Step();
        Assert.True(sim.Scenario.Waves[bypass].StartedTick < 0, "обход не должен начаться до моста");
        for (int i = 0; i < sim.TicksFor(3); i++) sim.Step();
        Assert.NotEqual("chasm", bridgeCell.Terrain.Id);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("Южный мост опущен"));
        // The first enemy comes after the warning of the spawn.
        for (int i = 0; i < sim.TicksFor(25) && sim.Scenario.Waves[bypass].StartedTick < 0; i++) sim.Step();
        Assert.True(sim.Scenario.Waves[bypass].StartedTick >= 0, "мост опущен — обход начался");
        // Earlier by the lever (its signal goes to the bridge).
        var s2 = Battle(db, sc);
        Assert.True(s2.Interactive.SetState("bridge", "lowered", "рычаг", out _));
        // The wave is announced with its warning (≈14 s) — still long before the sappers (40 s).
        for (int i = 0; i < s2.TicksFor(25) && s2.Scenario.Waves[bypass].StartedTick < 0; i++) s2.Step();
        Assert.True(s2.Scenario.Waves[bypass].StartedTick >= 0 && s2.Tick < s2.TicksFor(30), "рычаг: обход начался раньше сапёров");
    }

    [Fact]
    public void TheDemoLevel_TheWarlord_RagesThenCalls()
    {
        var (db, sc) = Demo();
        var sim = Battle(db, sc);
        var boss = sim.Spawn(db.Unit("pass_warlord")!, "enemy", new HexCoord(8, 5), HexDirection.W);
        sim.Step();
        boss.Hp = boss.HpMax * 0.55;
        sim.Step();
        sim.Step();
        Assert.Equal(0, sim.Scenario.CurrentPhase(boss));
        Assert.Contains(boss.Statuses, s => s.Def.Id == "frenzy");
        int goblins = sim.Units.Count(u => u.Alive && u.Def.Id == "goblin");
        boss.Hp = boss.HpMax * 0.25;
        sim.Step();
        sim.Step();
        Assert.Equal(1, sim.Scenario.CurrentPhase(boss));
        Assert.Equal(goblins + 2, sim.Units.Count(u => u.Alive && u.Def.Id == "goblin"));
    }

    [Fact]
    public void TheDemoLevel_TheHardVariant_AndTheNextMission()
    {
        var (db, sc) = Demo();
        var (vdb, vsc) = MissionModifiers.Prepare(db, sc, db.Get<DifficultyVariantDef>("two_passes_hard"));
        Assert.Single(vsc.AppliedModifiers);
        var sim = ScenarioSystem.Create(vdb, vsc, 11);
        var foe = sim.Spawn(db.Unit("goblin")!, "enemy", new HexCoord(3, 3), HexDirection.E);
        for (int i = 0; i < 3; i++) sim.Step();
        Assert.Equal(db.Unit("goblin")!.Stats["hp_max"] * 1.3, foe.HpMax, 6);
        var c = db.Get<CampaignDef>("two_passes_campaign")!;
        var p = Campaigns.Start(c);
        Assert.Equal(CampaignNodeStatus.Locked, Campaigns.Status(c, p, c.Node("fork")!));
        Campaigns.Complete(db, c, p, "hold", new Dictionary<OutcomeMetric, double> { [OutcomeMetric.Victory] = 1, [OutcomeMetric.BaseHpRemaining] = 1 }, "demo-1");
        Assert.NotEqual(CampaignNodeStatus.Locked, Campaigns.Status(c, p, c.Node("fork")!));
    }

    /// <summary>T32: the same seed and configuration — the same result twice in a batch, and the same for the visual (frames) run.</summary>
    [Fact]
    public void T32_BatchTwice_AndTheVisualRun_GiveTheSameResult()
    {
        var (db, sc) = Demo();
        var opts = new AutoBattleOptions { MaxSeconds = 600 };
        var a = AutoBattle.Batch(db, sc, 2, 5, opts);
        var b = AutoBattle.Batch(db, sc, 2, 5, opts);
        Assert.Equal(a.Reports.Select(r => r.ToJson()), b.Reports.Select(r => r.ToJson()));
        var first = AutoBattle.Run(db, sc, 5, opts);
        Assert.NotEqual("не завершён", first.Outcome); // the battle ends by itself (no defenders called: the base falls)
        var logic = first.ToJson();
        var (sim, report, breaks) = AutoBattle.Prepare(db, sc, 5, opts);
        var driver = new SimDriver(sim) { Paused = false };
        driver.SetSpeedIndex(driver.Speeds.Count - 1);
        for (int f = 0; f < 60 * 400 && sim.Outcome == BattleOutcome.None && sim.Scenario.BattleTime < opts.MaxSeconds; f++)
        {
            driver.Advance(1 / 60.0);
            driver.Paused = false;
        }
        breaks.Detach();
        Assert.Equal(logic, report.Finish().ToJson());
    }

    // ------------------------------------------------------------------ ТЗ 14.1: the switches

    [Fact]
    public void EverySubsystem_SwitchesOffOnItsOwn()
    {
        var (db, sc) = Demo();
        var off = ContentJson.Clone(sc);
        off.Id = "two_passes_off";
        off.GameRulesOverride = new GameRulesOverrideDef { SquadStart = SquadStart.NoneOnField, Objects = false, Modifiers = false, Graphs = false, Scales = false, Summons = false };
        var mode = EffectiveGameRules.Resolve(db, off);
        Assert.False(mode.Objects || mode.Modifiers || mode.Graphs || mode.Scales || mode.Summons);
        Assert.Contains(mode.Values, v => v.Field == "objects" && v.Value == "выключены" && v.Source == "сценарий");
        // Objects off: the bridge does not switch; the map stays as it is.
        var sim = Battle(db, off);
        Assert.False(sim.Interactive.SetState("bridge", "lowered", "тест", out _));
        // Modifiers off: the variant changes nothing.
        db.Add(off);
        var (_, vsc) = MissionModifiers.Prepare(db, off, db.Get<DifficultyVariantDef>("two_passes_hard"));
        Assert.Empty(vsc.AppliedModifiers);
        // Scales off: a unit with a scale has none.
        db.Unit("goblin")!.Scale = new ScaleUseDef { Profile = "morale" };
        var foe = sim.Spawn(db.Unit("goblin")!, "enemy", new HexCoord(3, 3), HexDirection.E);
        Assert.Null(sim.Scales.Of(foe));
        // Summons off: a described summon is refused (the simple one of a skill is untouched).
        var bm = sim.Spawn(db.Unit("beastmaster")!, "player", new HexCoord(10, 2), HexDirection.W);
        Assert.False(sim.Summons.Summon(db.Get<SummonDef>("wolf_pack")!, bm, bm.Pos, -1, null, "тест"));
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.SummonFailed && e.Text.Contains("выключены правилами режима"));
        // Graphs off: the graphs of a scenario do not become triggers.
        var withGraph = db.All<ScenarioDef>().FirstOrDefault(s => s.Graphs.Count > 0);
        if (withGraph != null)
        {
            int on = ScenarioSystem.Create(db, withGraph, 3).Scenario.Def!.Triggers.Count;
            var g = ContentJson.Clone(withGraph);
            g.Id = withGraph.Id + "_nographs";
            g.GameRulesOverride = new GameRulesOverrideDef { Graphs = false };
            Assert.True(ScenarioSystem.Create(db, g, 3).Scenario.Def!.Triggers.Count < on);
        }
        // The same scenario with everything on still works as before.
        var full = Battle(db, sc);
        Assert.True(full.Interactive.SetState("bridge", "lowered", "тест", out _));
    }

    // ------------------------------------------------------------------ T31: codes, places, fixes

    [Fact]
    public void T31_EveryProblem_HasACode_APlace_AndAFix_SomeAreFixedByOneButton()
    {
        var db = TestUtil.Shipped();
        var skill = db.Skill("fireball")!;
        var burn = skill.Stages.SelectMany(st => st.Effects).First(e => e.Type == EffectType.ApplyStatus);
        burn.Status = "no_such_status";
        skill.Cooldown = -5;
        var sum = db.Get<SummonDef>("wolf_pack")!;
        sum.Despawn = SummonEnd.Keep;
        var issues = ContentValidator.Validate(db).Where(i => i.Severity != IssueSeverity.Info).ToList();
        Assert.NotEmpty(issues);
        Assert.All(issues, i =>
        {
            Assert.Matches(@"^[EWI]-[A-Z0-9]+-[0-9A-F]{2,4}$", IssueCatalog.CodeOf(i));
            Assert.False(string.IsNullOrWhiteSpace(i.Source));
            Assert.False(string.IsNullOrWhiteSpace(IssueCatalog.FixOf(i)));
        });
        var badRef = issues.Single(i => i.Source == "Skill:fireball" && i.Message.StartsWith("Ссылка") && i.Message.Contains("no_such_status"));
        var badRange = issues.Single(i => i.Source == "Skill:fireball" && i.Message.Contains("вне диапазона"));
        var badSummon = issues.Single(i => i.Source == "Summon:wolf_pack" && i.Message.Contains("нужно действие"));
        Assert.Equal("E-REF-01", IssueCatalog.CodeOf(badRef));
        Assert.Equal("E-RNG-01", IssueCatalog.CodeOf(badRange));
        Assert.Equal("E-SUM-01", IssueCatalog.CodeOf(badSummon));
        Assert.Contains("no_such_status", IssueCatalog.FixOf(badRef));
        // The same kind of problem — the same code, whatever the names and numbers.
        var other = new ContentIssue(IssueSeverity.Warning, "Unit:knight", "x", "Нечто странное «A» у 5 юнитов");
        Assert.Equal(IssueCatalog.CodeOf(other), IssueCatalog.CodeOf(other with { Message = "Нечто странное «B» у 7 юнитов" }));
        // One button each.
        foreach (var i in new[] { badRef, badRange, badSummon })
        {
            Assert.True(IssueCatalog.CanAutoFix(i));
            Assert.True(IssueCatalog.TryFix(db, i, out var done), done);
        }
        Assert.Equal("", burn.Status);
        Assert.Equal(0, skill.Cooldown);
        Assert.Equal(SummonEnd.Vanish, sum.Despawn);
        var after = ContentValidator.Validate(db);
        Assert.DoesNotContain(after, i => i.Source == "Skill:fireball" && (i.Message.Contains("no_such_status") || i.Message.Contains("вне диапазона")));
        Assert.DoesNotContain(after, i => i.Source == "Summon:wolf_pack" && i.Severity == IssueSeverity.Error);
        Assert.Contains("Исправление:", IssueCatalog.Describe(badRef));
    }
}
