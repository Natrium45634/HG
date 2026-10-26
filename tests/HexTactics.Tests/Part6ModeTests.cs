using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.1 — the rules of a mode and the squad (ТЗ ч. 6, 1–2, T01, T30; docs/DECISIONS_6.md §96).</summary>
public class Part6ModeTests
{
    private static (ContentDatabase Db, ScenarioDef Sc) Pass()
    {
        var db = TestUtil.Shipped();
        return (db, db.Get<ScenarioDef>("td_pass_defense")!);
    }

    [Fact]
    public void EffectiveRules_PresetThenScenario_EverySourceNamed_AndTheDifference()
    {
        var (db, sc) = Pass();
        var none = EffectiveGameRules.Resolve(db, sc);
        Assert.Null(none.Preset);
        Assert.True(none.Blocking);
        Assert.All(none.Values.Where(v => v.Field != "friendlyFire"), v => Assert.Equal("по умолчанию", v.Source));
        Assert.Equal("правила боя", none.Values.Single(v => v.Field == "friendlyFire").Source);

        sc.GameRules = "hybrid";
        var hybrid = EffectiveGameRules.Resolve(db, sc);
        Assert.Equal(SquadStart.StartDeployed, hybrid.SquadStart);
        Assert.Equal("пресет «Гибрид»", hybrid.Values.Single(v => v.Field == "squadStart").Source);

        sc.GameRulesOverride = new GameRulesOverrideDef { Blocking = false };
        var own = EffectiveGameRules.Resolve(db, sc);
        Assert.False(own.Blocking);
        Assert.Equal("сценарий", own.Values.Single(v => v.Field == "blocking").Source);
        Assert.Equal("пресет «Гибрид»", own.Values.Single(v => v.Field == "squadStart").Source);

        sc.GameRulesOverride = null;
        sc.GameRules = "rts_free";
        var rts = EffectiveGameRules.Resolve(db, sc);
        sc.GameRules = "td_tactical";
        var td = EffectiveGameRules.Resolve(db, sc);
        Assert.Contains("Отряд на поле в начале: Весь отряд на поле → Никто", EffectiveGameRules.Diff(rts, td));

        // A missing preset and an own value equal to the preset's are reported, the second only as a hint.
        sc.GameRules = "no_such_mode";
        Assert.Contains(EffectiveGameRules.Resolve(db, sc).Notes, n => n.Contains("не найдены"));
        sc.GameRules = "rts_free";
        sc.GameRulesOverride = new GameRulesOverrideDef { Blocking = true };
        Assert.Contains(EffectiveGameRules.Resolve(db, sc).Redundant, n => n.Contains("совпадает"));
    }

    [Fact]
    public void Switches_Blocking_AndFriendlyFire_ReachTheBattle_TheContentUntouched()
    {
        var (db, sc) = Pass();
        bool ff = db.Rules.Combat.FriendlyFire;
        sc.GameRules = "rts_free";
        sc.GameRulesOverride = new GameRulesOverrideDef { Blocking = false, FriendlyFire = !ff };
        var sim = ScenarioSystem.Create(db, sc, 1);
        Assert.False(sim.Options.Blocking);
        Assert.Equal(!ff, sim.Rules.Combat.FriendlyFire);
        Assert.Equal(ff, db.Rules.Combat.FriendlyFire);

        // Blocking off: the knight holds nobody.
        var field = TestUtil.Field(10, 5);
        foreach (var blocking in new[] { true, false })
        {
            var s = new Simulation(db, field, new SimOptions { Seed = 1, PlayerFactions = new() { "player" }, AiFactions = new(), Blocking = blocking });
            var knight = s.Spawn(db.Unit("knight")!, "player", new HexCoord(4, 2), HexDirection.E);
            var goblin = s.Spawn(db.Unit("goblin")!, "enemy", new HexCoord(5, 2), HexDirection.W);
            Assert.Null(s.Enqueue(goblin, Command.Move(new HexCoord(0, 2))));
            s.Step();
            Assert.Equal(blocking ? knight : null, s.Blocking.BlockerOf(goblin));
        }
    }

    private static ScenarioSquadDef Squad(params string[] start) => new()
    {
        Enabled = true, Pool = { "knight", "archer", "mage", "priestess", "scout" }, MaxSize = 4, StartDeployed = start.ToList(),
    };

    [Fact]
    public void SquadRules_SayWhyNot_AndCheckTheWhole()
    {
        var (db, sc) = Pass();
        sc.Squad = Squad();
        sc.Squad.ForbiddenTags.Add("mage");
        sc.Squad.Limits.Add(new SquadLimitDef { Match = "archer", Max = 1 });
        sc.Squad.RequiredTags.Add("priestess");
        sc.Squad.StartingRoster.Add("knight");
        var list = new List<string> { "knight", "archer" };
        Assert.Null(SquadRules.WhyNot(db, sc, list, "scout"));
        Assert.Contains("запрещён тег", SquadRules.WhyNot(db, sc, list, "mage"));
        Assert.Contains("уже в отряде", SquadRules.WhyNot(db, sc, list, "knight"));
        Assert.Contains("нет среди доступных", SquadRules.WhyNot(db, sc, list, "goblin"));
        sc.Squad.DuplicatesAllowed = true;
        Assert.Contains("не более 1", SquadRules.WhyNot(db, sc, list, "archer"));
        list.AddRange(new[] { "scout", "scout" });
        Assert.Contains("отряд полон", SquadRules.WhyNot(db, sc, list, "priestess"));
        // The whole: the required tag is missing; the required fighter cannot be taken out.
        Assert.Contains(SquadRules.Check(db, sc, list), p => p.Contains("нужен боец с тегом «priestess»"));
        Assert.Equal("обязательный боец сценария", SquadRules.WhyKeep(sc, list, 0));
        Assert.Null(SquadRules.WhyKeep(sc, list, 1));
        Assert.Contains(SquadRules.Check(db, sc, new List<string> { "archer" }), p => p.Contains("нет обязательного бойца"));
        Assert.Empty(SquadRules.Check(db, sc, new List<string> { "knight", "archer", "priestess" }));
        // The default squad: the required ones, then the preset — what fits.
        sc.Squad.DefaultPreset = "squad_vanguard";
        Assert.Equal(new[] { "knight", "archer", "priestess" }, SquadRules.DefaultRoster(db, sc));
        // The content check of the rules themselves.
        sc.Squad.MinSize = 5;
        sc.Squad.RequiredTags.Add("dragon");
        var problems = SquadRules.Validate(db, sc);
        Assert.Contains(problems, p => p.Contains("больше «Не больше»"));
        Assert.Contains(problems, p => p.Contains("«dragon»"));
    }

    [Fact]
    public void Squad_AllOnField_StandsInTheZone_FacingTheEnemy_Deterministically()
    {
        var (db, sc) = Pass();
        sc.Mode = BattleMode.TowerDefense;
        sc.GameRules = "rts_free";
        sc.Squad = Squad();
        var roster = new[] { "knight", "archer", "priestess" };
        var sim = ScenarioSystem.Create(db, sc, 7, roster: roster);
        Assert.True(sim.Squad.Enabled);
        Assert.Equal(3, sim.Squad.Count(RosterState.Deployed));
        var zone = sim.Placement.Zone;
        var units = sim.Squad.Entries.Select(e => sim.GetUnit(e.Unit)!).ToList();
        Assert.All(units, u => Assert.Contains(u.Pos, zone));
        Assert.All(units, u => Assert.Equal("player", u.FactionId));
        // Facing the nearest cave (the enemy's spawn base lies west).
        Assert.All(units, u => Assert.Contains(u.Facing, new[] { HexDirection.W, HexDirection.NW, HexDirection.SW }));
        // The same squad stands the same way every time.
        var again = ScenarioSystem.Create(db, sc, 7, roster: roster);
        Assert.Equal(units.Select(u => u.Pos), again.Squad.Entries.Select(e => again.GetUnit(e.Unit)!.Pos));
    }

    [Fact]
    public void T30_OneMap_RtsOrTd_ByTheScenarioSettingsOnly_AndThePlacementTakesTheSquad()
    {
        var (db, sc) = Pass();
        sc.Squad = Squad("knight");
        var roster = new[] { "knight", "archer", "archer" };
        sc.Squad.DuplicatesAllowed = true;

        sc.GameRules = "rts_free";
        var rts = ScenarioSystem.Create(db, sc, 3, roster: roster);
        Assert.Equal(3, rts.Units.Count(u => u.FactionId == "player" && !u.IsStructure));

        sc.GameRules = "hybrid";
        var hybrid = ScenarioSystem.Create(db, sc, 3, roster: roster);
        Assert.Equal(new[] { "knight" }, hybrid.Units.Where(u => u.FactionId == "player" && !u.IsStructure).Select(u => u.Def.Id));
        Assert.Equal(2, hybrid.Squad.Count(RosterState.Ready));

        sc.GameRules = "td_tactical";
        var td = ScenarioSystem.Create(db, sc, 3, roster: roster);
        Assert.Empty(td.Units.Where(u => u.FactionId == "player" && !u.IsStructure));
        Assert.True(td.Scenario.Preparing);
        // The preparation places the squad: two archers, then no third; a mage is not in the squad.
        var spots = td.Placement.Zone.Where(c => td.Placement.CanPlace("archer", c) == null).Take(3).ToList();
        Assert.NotNull(td.Input.Place("archer", spots[0], out _));
        Assert.NotNull(td.Input.Place("archer", spots[1], out _));
        Assert.Null(td.Input.Place("archer", spots[2], out var why));
        Assert.Contains("уже на поле", why);
        Assert.Contains("нет в отряде", td.Placement.CanPlace("mage", spots[2])!);
        Assert.Equal(2, td.Squad.Count(RosterState.Deployed));
        // Taken back while preparing — waiting in the squad again.
        var first = td.GetUnit(td.Squad.Entries.First(e => e.State == RosterState.Deployed).Unit)!;
        Assert.True(td.Input.Unplace(first));
        Assert.Equal(2, td.Squad.Count(RosterState.Ready));
    }

    [Fact]
    public void T01_WithoutASquad_TheBattleIsAsBefore_AndTheSquadIsInTheReplay()
    {
        var (db, sc) = Pass();
        var before = ScenarioSystem.Create(db, sc, 11);
        sc.GameRules = "rts_free";
        var after = ScenarioSystem.Create(db, sc, 11);
        for (int i = 0; i < 200; i++)
        {
            before.Step();
            after.Step();
        }
        Assert.Equal(before.Digest().Hash, after.Digest().Hash);
        Assert.False(after.Squad.Enabled);

        // A battle with a chosen squad replays with it.
        sc.Squad = Squad();
        sc.Mode = BattleMode.TowerDefense;
        var (snap, sim) = BattleSnapshot.Begin(db, sc, 5, roster: new[] { "scout", "mage" });
        var rec = BattleReplay.Record(snap, sim, withContent: true);
        for (int i = 0; i < 150; i++) sim.Step();
        rec.Finish(sim);
        var back = BattleReplay.FromJson(rec.ToJson())!;
        Assert.Equal(new[] { "scout", "mage" }, back.Roster);
        Assert.True(back.Play().Matches);
        var restored = snap.Restore();
        Assert.Equal(new[] { "scout", "mage" }, restored.Squad.Entries.Select(e => e.Template));
    }

    [Fact]
    public void FallenFighter_IsDefeated_InTheSquad()
    {
        var (db, sc) = Pass();
        sc.Mode = BattleMode.TowerDefense;
        sc.GameRules = "rts_free";
        sc.Squad = Squad();
        var sim = ScenarioSystem.Create(db, sc, 2, roster: new[] { "scout" });
        var u = sim.GetUnit(sim.Squad.Entries[0].Unit)!;
        sim.Combat.Kill(u, null, "тест");
        Assert.Equal(RosterState.Defeated, sim.Squad.Entries[0].State);
        Assert.Equal(-1, sim.Squad.Entries[0].Unit);
    }

    [Fact]
    public void ContentCheck_ReportsTheSquadAndTheMode()
    {
        var (db, sc) = Pass();
        sc.GameRules = "missing_mode";
        sc.Squad = Squad();
        sc.Squad.MinSize = 9;
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Source.Contains("td_pass_defense") && i.Message.StartsWith("Правила режима:"));
        Assert.Contains(issues, i => i.Source.Contains("td_pass_defense") && i.Severity == IssueSeverity.Error && i.Message.StartsWith("Отряд:"));
        // The shipped content has no such problems.
        Assert.DoesNotContain(ContentValidator.Validate(TestUtil.Shipped()), i => i.Message.StartsWith("Отряд:") || i.Message.StartsWith("Правила режима:"));
    }
}
