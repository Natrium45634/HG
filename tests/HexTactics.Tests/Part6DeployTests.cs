using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.2 — the deployment for points during the battle (ТЗ ч. 6, 3; T02–T12; docs/DECISIONS_6.md §97).</summary>
public class Part6DeployTests
{
    private static (ContentDatabase Db, ScenarioDef Sc) Scene(string rules = "td_tactical", Action<DeploymentRulesDef>? edit = null)
    {
        var db = TestUtil.Shipped();
        var sc = db.Get<ScenarioDef>("td_pass_defense")!;
        sc.Mode = BattleMode.TowerDefense; // the battle goes at once (no preparation)
        sc.GameRules = rules;
        sc.Squad = new ScenarioSquadDef { Enabled = true, Pool = { "knight", "archer", "mage", "priestess", "scout" }, MaxSize = 8, DuplicatesAllowed = true };
        var d = ContentJson.Clone(db.Get<GameRulesDef>("td_tactical")!.Deployment);
        d.GainAmount = 0; // no gain unless a test wants it
        edit?.Invoke(d);
        sc.DeploymentOverride = d;
        foreach (var (id, cost) in new[] { ("knight", 7.0), ("archer", 5.0), ("mage", 6.0), ("priestess", 4.0), ("scout", 2.0) })
            db.Unit(id)!.Deployment = new UnitDeploymentDef { Cost = cost };
        return (db, sc);
    }

    private static Simulation Battle(ContentDatabase db, ScenarioDef sc, params string[] roster) => ScenarioSystem.Create(db, sc, 9, roster: roster);

    /// <summary>The free hexes of the zone for an entry, the nearest to the base first.</summary>
    private static List<HexCoord> Spots(Simulation sim, int entry = 0) =>
        sim.Placement.Zone.Where(c => sim.Deployment.CellProblem(sim.Squad.Entries[entry], c) == null).OrderBy(c => c.R).ThenBy(c => c.Q).ToList();

    private static int OnField(Simulation sim) => sim.Units.Count(u => u.Alive && u.FactionId == "player" && !u.IsStructure);

    [Fact]
    public void T03_T04_PriceSevenWithSix_Refused_NothingSpent_WithSeven_ZeroLeft()
    {
        var (db, sc) = Scene(edit: d => d.StartingPoints = 6);
        var sim = Battle(db, sc, "knight");
        var spot = Spots(sim)[0];
        Assert.Null(sim.Input.Deploy(0, spot, HexDirection.W, out var why));
        Assert.Contains("не хватает очков: нужно 7, есть 6", why);
        Assert.Equal(6, sim.Deployment.Points);
        Assert.Equal(0, OnField(sim));
        Assert.Equal(RosterState.Ready, sim.Squad.Entries[0].State);

        var (db2, sc2) = Scene(edit: d => d.StartingPoints = 7);
        var sim2 = Battle(db2, sc2, "knight");
        var u = sim2.Input.Deploy(0, Spots(sim2)[0], HexDirection.W, out why);
        Assert.NotNull(u);
        Assert.Equal(0, sim2.Deployment.Points);
        Assert.Equal(RosterState.Deployed, sim2.Squad.Entries[0].State);
    }

    [Fact]
    public void T05_Occupied_Impassable_OutOfZone_Hazard_DifferentReasons()
    {
        var (db, sc) = Scene(edit: d => d.StartingPoints = 50);
        var sim = Battle(db, sc, "scout", "scout");
        var spot = Spots(sim)[0];
        Assert.NotNull(sim.Input.Deploy(0, spot, HexDirection.W, out _));
        var reasons = new List<string>();
        reasons.Add(sim.Deployment.CellProblem(sim.Squad.Entries[1], spot)!);
        var wall = Spots(sim, 1)[0];
        sim.Map.SetTerrain(sim.Map.Get(wall)!, db.Terrain("wall")!);
        reasons.Add(sim.Deployment.CellProblem(sim.Squad.Entries[1], wall)!);
        var outside = sim.Map.Cells.Select(c => c.Coord).First(c => !sim.Placement.Zone.Contains(c));
        reasons.Add(sim.Deployment.CellProblem(sim.Squad.Entries[1], outside)!);
        var lava = Spots(sim, 1)[0];
        sim.Map.SetTerrain(sim.Map.Get(lava)!, db.Terrain("lava")!);
        reasons.Add(sim.Deployment.CellProblem(sim.Squad.Entries[1], lava)!);
        Assert.Equal(new[] { "гекс занят", "местность непроходима для бойца", "вне зоны расстановки", "опасная клетка" }, reasons);
        Assert.Equal(48, sim.Deployment.Points);
    }

    [Fact]
    public void T06_TheHexChangesBetweenPreviewAndConfirm_RefusedWithoutSpending()
    {
        var (db, sc) = Scene(edit: d => d.StartingPoints = 20);
        var sim = Battle(db, sc, "knight");
        var spot = Spots(sim)[0];
        Assert.Null(sim.Deployment.CellProblem(sim.Squad.Entries[0], spot)); // the preview said yes
        sim.Spawn(db.Unit("goblin")!, "enemy", spot, HexDirection.E); // meanwhile someone stepped in
        Assert.Null(sim.Input.Deploy(0, spot, HexDirection.W, out var why));
        Assert.Equal("гекс занят", why);
        Assert.Equal(20, sim.Deployment.Points);
        Assert.Equal(RosterState.Ready, sim.Squad.Entries[0].State);
    }

    [Fact]
    public void T07_EverySixFacings_KeptAfterTheCall_AndUsedByTheBattle()
    {
        var (db, sc) = Scene(edit: d =>
        {
            d.StartingPoints = 99;
            d.MaxDeployed = 0;
        });
        var roster = Enumerable.Repeat("scout", 6).ToArray();
        var sim = Battle(db, sc, roster);
        for (int i = 0; i < 6; i++)
        {
            var dir = (HexDirection)i;
            var u = sim.Input.Deploy(i, Spots(sim, i)[0], dir, out var why);
            Assert.True(u != null, why);
            Assert.Equal(dir, u!.Facing);
        }
        // Automatic and fixed facings of the profile win over the choice.
        db.Unit("scout")!.Deployment = new UnitDeploymentDef { Cost = 2, Facing = DeployFacing.Fixed, FixedFacing = HexDirection.NE };
        var sim2 = Battle(db, sc, "scout");
        Assert.Equal(HexDirection.NE, sim2.Input.Deploy(0, Spots(sim2)[0], HexDirection.W, out _)!.Facing);
    }

    [Fact]
    public void T02_Hybrid_StartingAndPaidPlacement_Coexist_RosterIsNotTheLimit()
    {
        var (db, sc) = Scene("hybrid", d =>
        {
            d.StartingPoints = 10;
            d.MaxDeployed = 2;
            d.EnemyRadius = 0;
        });
        sc.Squad!.StartDeployed.Add("knight");
        var sim = Battle(db, sc, "knight", "scout", "scout", "archer");
        Assert.Equal(1, sim.Deployment.DeployedCount); // the knight is there for free
        Assert.Equal(10, sim.Deployment.Points);
        Assert.NotNull(sim.Input.Deploy(1, Spots(sim, 1)[0], HexDirection.W, out _));
        // Two on the field — the limit, though four in the squad and points enough.
        Assert.Contains("на поле уже 2 из 2", sim.Deployment.CanDeploy(sim.Squad.Entries[2])!);
        Assert.Equal(4, sim.Squad.Entries.Count);
    }

    [Fact]
    public void T08_TheZoneLimitsOnlyTheCall_TheLeashLimitsTheMoving()
    {
        foreach (int leash in new[] { 0, 2 })
        {
            var (db, sc) = Scene(edit: d =>
            {
                d.StartingPoints = 20;
                d.Leash = leash;
            });
            var sim = Battle(db, sc, "scout");
            var spot = Spots(sim)[0];
            var u = sim.Input.Deploy(0, spot, HexDirection.W, out _)!;
            var far = sim.Map.Cells.Select(c => c.Coord)
                .Where(c => !sim.Placement.Zone.Contains(c) && HexCoord.Distance(c, spot) is >= 4 and <= 6 && MovementRules.Check(MoveContext.For(sim, u), null, sim.Map.Get(c), true, out _) is StepCheck.Ok or StepCheck.Leash)
                .OrderBy(c => HexCoord.Distance(c, spot)).ThenBy(c => c.R).ThenBy(c => c.Q).First();
            var err = sim.Input.Enqueue(u, Command.Move(far));
            for (int i = 0; i < sim.TicksFor(15) && u.Pos != far; i++) sim.Step();
            if (leash == 0) Assert.Equal(far, u.Pos);
            else
            {
                Assert.NotEqual(far, u.Pos);
                Assert.True(HexCoord.Distance(u.Pos, spot) <= 2, $"поводок 2, а боец в {HexCoord.Distance(u.Pos, spot)} гексах" + (err != null ? " (" + err + ")" : ""));
            }
        }
    }

    [Fact]
    public void T09_T10_Retreat_RefundsTheShare_StartsThePause_ClearsOrders_NoClone_DeathPaysNothing()
    {
        var (db, sc) = Scene(edit: d =>
        {
            d.StartingPoints = 20;
            d.RetreatRefund = 0.5;
            d.RedeployCooldown = 3;
        });
        var sim = Battle(db, sc, "knight", "scout");
        var knight = sim.Input.Deploy(0, Spots(sim)[0], HexDirection.W, out _)!;
        Assert.Equal(13, sim.Deployment.Points);
        sim.Input.Enqueue(knight, Command.Move(Spots(sim, 1)[0]));
        int units = sim.Units.Count;
        Assert.True(sim.Input.Retreat(knight, out var why), why);
        Assert.Equal(16.5, sim.Deployment.Points); // 13 + 7 × 0.5
        Assert.Equal(units - 1, sim.Units.Count);
        Assert.Null(sim.GetUnit(knight.Id));
        Assert.False(knight.Alive);
        Assert.Equal(RosterState.WithdrawnCooldown, sim.Squad.Entries[0].State);
        Assert.Empty(knight.Queue.Items.Where(c => c.Status is CommandStatus.Pending or CommandStatus.Active));
        Assert.Null(sim.Squad.EntryOf(knight));
        // A second retreat of the same unit — nothing.
        Assert.False(sim.Input.Retreat(knight, out _));
        Assert.Equal(16.5, sim.Deployment.Points);

        // T10: the scout dies; a retreat afterwards pays nothing.
        var scout = sim.Input.Deploy(1, Spots(sim, 1)[0], HexDirection.W, out _)!;
        double before = sim.Deployment.Points;
        sim.Combat.Kill(scout, null, "тест");
        Assert.False(sim.Input.Retreat(scout, out why));
        Assert.Equal(before, sim.Deployment.Points);
        Assert.Equal(RosterState.Defeated, sim.Squad.Entries[1].State);
    }

    [Fact]
    public void T11_TheSecondCall_RefusedDuringThePause_AllowedAfter_WithPoints()
    {
        var (db, sc) = Scene(edit: d =>
        {
            d.StartingPoints = 20;
            d.RedeployCooldown = 2;
            d.RetreatRefund = 0;
        });
        var sim = Battle(db, sc, "archer");
        var a = sim.Input.Deploy(0, Spots(sim)[0], HexDirection.W, out _)!;
        Assert.True(sim.Input.Retreat(a, out _));
        Assert.Contains("перерыв после отзыва", sim.Deployment.CanDeploy(sim.Squad.Entries[0])!);
        Assert.Null(sim.Input.Deploy(0, Spots(sim)[0], HexDirection.W, out _));
        for (int i = 0; i < sim.TicksFor(2) + 1; i++) sim.Step();
        Assert.Equal(RosterState.Ready, sim.Squad.Entries[0].State);
        Assert.NotNull(sim.Input.Deploy(0, Spots(sim)[0], HexDirection.W, out var why));
        Assert.Equal(10, sim.Deployment.Points);
        // No points — no call even after the pause: 20 − 5 − 5 − 5 − 5 = 0.
        for (int round = 0; round < 2; round++)
        {
            Assert.True(sim.Input.Retreat(sim.GetUnit(sim.Squad.Entries[0].Unit)!, out _));
            for (int i = 0; i < sim.TicksFor(2) + 1; i++) sim.Step();
            Assert.NotNull(sim.Input.Deploy(0, Spots(sim)[0], HexDirection.W, out _));
        }
        Assert.Equal(0, sim.Deployment.Points);
        Assert.True(sim.Input.Retreat(sim.GetUnit(sim.Squad.Entries[0].Unit)!, out _));
        for (int i = 0; i < sim.TicksFor(2) + 1; i++) sim.Step();
        Assert.Contains("не хватает очков", sim.Deployment.CanDeploy(sim.Squad.Entries[0])!);
    }

    [Fact]
    public void Gain_ByTime_PerKill_Capped_PausesDoNotCount_AndAfterDeath_Comeback()
    {
        var (db, sc) = Scene(edit: d =>
        {
            d.StartingPoints = 0;
            d.GainAmount = 1;
            d.GainInterval = 0.5;
            d.MaxPoints = 5;
            d.GainPerKill = 2;
            d.AfterDeath = AfterDeathPolicy.Cooldown;
            d.DeathCooldown = 1;
        });
        var sim = Battle(db, sc, "scout");
        for (int i = 0; i < sim.TicksFor(2); i++) sim.Step();
        Assert.Equal(4, sim.Deployment.Points);
        for (int i = 0; i < sim.TicksFor(5); i++) sim.Step();
        Assert.Equal(5, sim.Deployment.Points); // the cap
        var s = sim.Input.Deploy(0, Spots(sim)[0], HexDirection.W, out _)!;
        Assert.Equal(3, sim.Deployment.Points);
        var gob = sim.Spawn(db.Unit("goblin")!, "enemy", sim.Map.Cells.Select(c => c.Coord).First(c => sim.Map.Get(c)!.Occupants.Count == 0 && !sim.Placement.Zone.Contains(c)), HexDirection.E);
        sim.Combat.Kill(gob, s, "тест");
        Assert.Equal(5, sim.Deployment.Points); // +2 per kill
        sim.Combat.Kill(s, null, "тест");
        Assert.Equal(RosterState.DefeatedCooldown, sim.Squad.Entries[0].State);
        for (int i = 0; i < sim.TicksFor(1) + 1; i++) sim.Step();
        Assert.Equal(RosterState.Ready, sim.Squad.Entries[0].State);
    }

    [Fact]
    public void T12_TheSameAtOneAndFourTimesSpeed_AndTheReplayRepeatsCallsAndRetreats()
    {
        var (db, sc) = Scene(edit: d =>
        {
            d.StartingPoints = 6;
            d.GainAmount = 1;
            d.GainInterval = 0.35;
            d.RedeployCooldown = 1;
        });
        // Points are a function of the tick only — whatever the frames.
        foreach (int speed in new[] { 1, 4 })
        {
            var sim = Battle(db, sc, "knight");
            var driver = new SimDriver(sim) { Paused = false };
            driver.SetSpeedIndex(driver.Speeds.IndexOf(speed));
            for (int f = 0; f < 60 * 3; f++)
            {
                driver.Advance(1 / 60.0);
                driver.Paused = false;
            }
            long gains = sim.Tick / sim.TicksFor(0.35);
            Assert.Equal(Math.Min(99, 6 + gains), sim.Deployment.Points);
        }
        // A battle with calls and a retreat at 4× replays tick by tick to the same end.
        var (snap, s4) = BattleSnapshot.Begin(db, sc, 9, roster: new[] { "knight", "scout" });
        var rec = BattleReplay.Record(snap, s4, withContent: true);
        var drv = new SimDriver(s4) { Paused = false };
        drv.SetSpeedIndex(drv.Speeds.IndexOf(4));
        Unit? k = null;
        for (int f = 0; f < 60 * 8; f++)
        {
            if (k == null && s4.Tick > 30) k = s4.Input.Deploy(0, Spots(s4)[0], HexDirection.W, out _);
            if (k != null && k.Alive && s4.Tick > 90 && s4.Squad.Entries[0].State == RosterState.Deployed) s4.Input.Retreat(k, out _);
            if (s4.Tick > 120 && s4.Squad.Entries[1].State == RosterState.Ready) s4.Input.Deploy(1, Spots(s4, 1)[0], HexDirection.NW, out _);
            drv.Advance(1 / 60.0);
            drv.Paused = false;
        }
        rec.Finish(s4);
        Assert.Contains(rec.Entries, e => e.Op == ReplayOp.Deploy);
        Assert.Contains(rec.Entries, e => e.Op == ReplayOp.Retreat);
        var play = BattleReplay.FromJson(rec.ToJson())!.Play();
        Assert.True(play.Matches, string.Join("; ", play.Problems));
    }

    [Fact]
    public void Zones_PriorityPhaseTagsAndClosed_DecideTheHex()
    {
        var (db, sc) = Scene(edit: d => d.StartingPoints = 50);
        var cells = new List<HexCoord> { new(11, 3), new(11, 4), new(10, 4) };
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "rear", Name = "Тыл", Cells = cells.ToList(), Tags = { "rear" }, Priority = 0 });
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "guard_only", Name = "Пост", Cells = { new(11, 4) }, UnitTags = { "knight" }, Priority = 5 });
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "later", Name = "Позже", Cells = { new(10, 3) }, Phase = ZonePhase.Preparation });
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "shut", Name = "Закрыта", Cells = { new(9, 4) }, Active = false });
        db.Unit("archer")!.Deployment!.ZoneTags.Add("high");
        var sim = Battle(db, sc, "scout", "knight", "archer");
        var scout = sim.Squad.Entries[0];
        Assert.Null(sim.Deployment.CellProblem(scout, new HexCoord(11, 3)));
        Assert.Contains("этого бойца нельзя", sim.Deployment.CellProblem(scout, new HexCoord(11, 4))); // the higher zone decides
        Assert.Null(sim.Deployment.CellProblem(sim.Squad.Entries[1], new HexCoord(11, 4)));
        Assert.Contains("только в подготовке", sim.Deployment.CellProblem(scout, new HexCoord(10, 3)));
        Assert.Contains("закрыта", sim.Deployment.CellProblem(scout, new HexCoord(9, 4)));
        Assert.Equal("вне зон развёртывания", sim.Deployment.CellProblem(scout, new HexCoord(5, 5)));
        Assert.Contains("только в зоны: high", sim.Deployment.CellProblem(sim.Squad.Entries[2], new HexCoord(11, 3)));
        // An event opens the closed zone and closes the rear one; the fighters already on the field stay.
        var on = sim.Input.Deploy(0, new HexCoord(11, 3), HexDirection.W, out var why);
        Assert.True(on != null, why);
        sim.Scenario.SetZoneOpen("shut", true, "тест");
        sim.Scenario.SetZoneOpen("rear", false, "тест");
        Assert.DoesNotContain("закрыта", sim.Deployment.CellProblem(sim.Squad.Entries[1], new HexCoord(9, 4)) ?? "");
        Assert.Contains("закрыта", sim.Deployment.CellProblem(sim.Squad.Entries[1], new HexCoord(10, 4)));
        Assert.True(on!.Alive);
        Assert.Contains("a zone:rear=False".Replace(" ", ""), string.Join("|", sim.Scenario.SwitchedObjects.Select(x => "a" + x)));
    }

    [Fact]
    public void ZoneActions_OpenAndClose_FromATrigger_AndTheCheckFindsAMissingZone()
    {
        var (db, sc) = Scene(edit: d => d.StartingPoints = 50);
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "gate", Name = "Ворота", Cells = { new(11, 3) }, Active = false });
        sc.Triggers.Add(new TriggerDef
        {
            Name = "Открыть ворота", Once = true, Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 1 },
            Actions = { new TriggerActionDef { Type = TriggerActionType.OpenDeploymentZone, Object = "gate" } },
        });
        var sim = Battle(db, sc, "scout");
        Assert.Contains("закрыта", sim.Deployment.CellProblem(sim.Squad.Entries[0], new HexCoord(11, 3)));
        for (int i = 0; i < sim.TicksFor(1.5); i++) sim.Step();
        Assert.Null(sim.Deployment.CellProblem(sim.Squad.Entries[0], new HexCoord(11, 3)));
        sc.Triggers[^1].Actions[0].Object = "nowhere";
        Assert.Contains(ContentValidator.Validate(db), i => i.Message.Contains("зоны развёртывания «nowhere» нет"));
    }

    [Fact]
    public void RulesOff_NothingOfTheDeploymentActs_AndTheEffectiveRulesSayIt()
    {
        var (db, sc) = Scene("rts_free");
        sc.DeploymentOverride = null;
        var sim = Battle(db, sc, "knight");
        Assert.False(sim.Deployment.Enabled);
        Assert.Contains("выключен", sim.Deployment.CanDeploy(sim.Squad.Entries[0])!);
        sc.GameRules = "td_tactical";
        var eff = EffectiveGameRules.Resolve(db, sc);
        Assert.True(eff.Deployment.Enabled);
        Assert.StartsWith("старт 10", eff.Values.Single(v => v.Field == "deployment").Value);
    }

    [Theory]
    [InlineData("titan_lair")]
    [InlineData("valley_ambush")]
    public void T28_SavedInTheMiddle_LoadsToTheSameState_FiresNothingAgain_AndGoesOnTheSame(string id)
    {
        var db = TestUtil.Shipped();
        var (snap, sim) = BattleSnapshot.Begin(db, db.Get<ScenarioDef>(id)!, 5, autoPlayer: true);
        var rec = BattleReplay.Record(snap, sim);
        for (int i = 0; i < sim.TicksFor(45) && sim.Outcome == BattleOutcome.None; i++) sim.Step();
        var save = BattleReplay.FromJson(rec.SaveAt(snap, sim).ToJson())!;
        var fired = sim.Scenario.TriggerFires.ToList();
        var phases = sim.Scenario.BossPhasesEntered.ToList();
        Assert.True(fired.Count + phases.Count > 0, "до сохранения не сработало ни одного триггера и фазы — тест ничего не проверяет");
        var res = save.Resume();
        Assert.True(res.Matches, res.Text);
        var loaded = res.Sim!;
        Assert.Equal(sim.Tick, loaded.Tick);
        Assert.Equal(fired, loaded.Scenario.TriggerFires.ToList());
        Assert.Equal(phases, loaded.Scenario.BossPhasesEntered.ToList());
        for (int i = 0; i < sim.TicksFor(30); i++)
        {
            sim.Step();
            loaded.Step();
        }
        Assert.Equal(sim.Digest().Hash, loaded.Digest().Hash);
        Assert.Equal(sim.Scenario.TriggerFires.ToList(), loaded.Scenario.TriggerFires.ToList());
    }

    [Fact]
    public void T28_Deployment_PointsCooldownsAndStates_SurviveSaveAndLoad_AndTheRecordGoesOn()
    {
        var (db, sc) = Scene(edit: d =>
        {
            d.StartingPoints = 12;
            d.GainAmount = 1;
            d.GainInterval = 0.7;
            d.RedeployCooldown = 6;
        });
        var (snap, sim) = BattleSnapshot.Begin(db, sc, 9, roster: new[] { "archer", "scout", "knight" });
        var rec = BattleReplay.Record(snap, sim);
        for (int i = 0; i < 20; i++) sim.Step();
        var a = sim.Input.Deploy(0, Spots(sim)[0], HexDirection.W, out _)!;
        sim.Input.Deploy(1, Spots(sim, 1)[0], HexDirection.SW, out _);
        for (int i = 0; i < 30; i++) sim.Step();
        Assert.True(sim.Input.Retreat(a, out _));
        for (int i = 0; i < 25; i++) sim.Step();
        var save = BattleReplay.FromJson(rec.SaveAt(snap, sim).ToJson())!;
        var res = save.Resume();
        Assert.True(res.Matches, res.Text);
        var loaded = res.Sim!;
        Assert.Equal(sim.Deployment.Points, loaded.Deployment.Points);
        Assert.Equal(RosterState.WithdrawnCooldown, loaded.Squad.Entries[0].State);
        Assert.Equal(sim.Squad.Entries[0].ReadyAt, loaded.Squad.Entries[0].ReadyAt);
        Assert.Equal(sim.Deployment.CooldownLeft(sim.Squad.Entries[0]), loaded.Deployment.CooldownLeft(loaded.Squad.Entries[0]));
        // The same call after the load — the same battle; the loaded record writes it on.
        int before = save.Entries.Count;
        foreach (var s in new[] { sim, loaded })
        {
            Assert.NotNull(s.Input.Deploy(2, Spots(s, 2)[0], HexDirection.W, out var why));
            for (int i = 0; i < s.TicksFor(8); i++) s.Step();
        }
        Assert.Equal(sim.Digest().Hash, loaded.Digest().Hash);
        Assert.Equal(before + 1, save.Entries.Count);
        // A save of the loaded battle loads again.
        var again = save.SaveAt(BattleSnapshot.FromReplay(save, res.Content!), loaded).Resume();
        Assert.True(again.Matches, again.Text);
    }

    [Fact]
    public void EditorCheck_HexesForAChosenFighter_AndAZoneWalledOff_IsReported()
    {
        var (db, sc) = Scene(edit: d => d.StartingPoints = 50);
        var pocket = new HexCoord(11, 3);
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "front", Name = "Фронт", Cells = { new(11, 4), new(10, 4) } });
        var sim = DeploymentCheck.Battle(db, sc)!;
        var check = DeploymentCheck.For(sim, sc, db.Unit("knight")!);
        Assert.Equal(2, check.Count);
        Assert.All(check.Values, Assert.Null);
        Assert.Empty(DeploymentCheck.Unreachable(db, sc));

        // A zone closed in by walls: nobody walks out of it to a target.
        var map = db.Get<MapDef>(sc.Map)!;
        foreach (var n in pocket.Neighbors())
            if (map.Cells.FirstOrDefault(c => c.C == n) is { } mc) mc.T = "wall";
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "pocket", Name = "Карман", Cells = { pocket } });
        var problems = DeploymentCheck.Unreachable(db, sc);
        Assert.Single(problems);
        Assert.Contains("Карман", problems[0]);
        Assert.Contains(ContentValidator.Validate(db), i => i.Message.Contains("из зоны «Карман»"));
        var wallHex = pocket.Neighbors().First(n => map.Cells.Any(c => c.C == n));
        sc.DeploymentZones.Add(new DeploymentZoneDef { Id = "wall", Name = "Стена", Cells = { wallHex } });
        var sim2 = DeploymentCheck.Battle(db, sc)!;
        Assert.Equal("местность непроходима для бойца", DeploymentCheck.For(sim2, sc, db.Unit("knight")!)[wallHex]);
        Assert.Contains(ContentValidator.Validate(db), i => i.Message.Contains("Зона «Стена»: все гексы — препятствия"));
    }
}
