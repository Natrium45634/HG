using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.4 — modifiers of a mission and variants of a scenario (ТЗ ч. 6, 5; T17–T18; docs/DECISIONS_6.md §99).</summary>
public class Part6ModifierTests
{
    /// <summary>«Осада — усложнённая» of ТЗ 5.7: two fewer on the field, enemies 20 % faster, an extra group in the third wave, healing ×0.7.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc, DifficultyVariantDef Variant) Siege()
    {
        var db = TestUtil.Shipped();
        var sc = db.Get<ScenarioDef>("td_pass_defense")!;
        sc.Mode = BattleMode.TowerDefense;
        sc.GameRules = "td_tactical";
        sc.Squad = new ScenarioSquadDef { Enabled = true, Pool = { "knight", "archer", "priestess" }, StartingRoster = { "knight", "archer", "priestess" }, Fixed = true };
        db.Add(new ModifierSetDef
        {
            Id = "test_siege", Name = "Осада (тест)",
            Modifiers =
            {
                new MissionModifierDef { Id = "fewer", Label = "Двое меньше на поле", Op = MissionModOp.Deployment, DeployField = ModDeployField.MaxDeployed, Delta = -2 },
                new MissionModifierDef { Id = "fast", Label = "Враги быстрее", Op = MissionModOp.Stat, Selector = { Faction = "enemy" }, Stat = { Stat = Stats.MoveSpeed, Mul = 0.2 } },
                new MissionModifierDef { Id = "rams", Label = "Ещё гоблины в «Таране»", Op = MissionModOp.Wave, WaveIndex = 3, ExtraUnit = "goblin", ExtraCount = 2 },
                new MissionModifierDef { Id = "heal", Label = "Лечение слабее", Op = MissionModOp.Stat, Selector = { Faction = "player" }, Stat = { Stat = "heal_received", Mul = -0.3 } },
            },
        });
        var v = new DifficultyVariantDef { Id = "td_pass_siege", Name = "Осада", Scenario = sc.Id, ModifierSets = { "test_siege" } };
        db.Add(v);
        return (db, sc, v);
    }

    [Fact]
    public void T17_AVariant_AppliesSeveralExistingThings_WithoutCopyingTheMap_OrChangingTheMission()
    {
        var (db, sc, v) = Siege();
        string before = ContentJson.Serialize(sc);
        int scenarios = db.All<ScenarioDef>().Count(), maps = db.All<MapDef>().Count();
        var (vdb, vsc) = MissionModifiers.Prepare(db, sc, v);
        Assert.Equal(before, ContentJson.Serialize(sc)); // the mission is as it was
        Assert.Equal(scenarios, db.All<ScenarioDef>().Count());
        Assert.Equal(maps, db.All<MapDef>().Count());
        Assert.Equal(sc.Map, vsc.Map);
        Assert.Equal(sc.Id, vsc.Id);
        Assert.Same(vsc, vdb.Get<ScenarioDef>(sc.Id));
        Assert.Equal("td_pass_siege", vsc.AppliedVariant);

        int baseLimit = EffectiveGameRules.Resolve(db, sc).Deployment.MaxDeployed;
        Assert.Equal(baseLimit - 2, EffectiveGameRules.Resolve(vdb, vsc).Deployment.MaxDeployed);
        Assert.Contains(vsc.Waves[2].Groups, g => g.Unit == "goblin" && g.Count == 2 && g.SpawnBase == sc.Waves[2].Groups[0].SpawnBase);
        Assert.Equal(sc.Waves[2].Groups.Count + 1, vsc.Waves[2].Groups.Count);
        Assert.Equal(2, vsc.AppliedModifiers.Count); // the two of units go to the battle, the rest are already in the copy

        // In the battle: the enemies 20 % faster through the engine of modifiers, the squad heals ×0.7.
        var normal = ScenarioSystem.Create(db, sc, 7);
        var hard = ScenarioSystem.Create(vdb, vsc, 7);
        Assert.Equal(baseLimit - 2, hard.Deployment.Rules.MaxDeployed);
        Unit? Enemy(Simulation s)
        {
            for (int i = 0; i < s.TicksFor(12); i++)
            {
                s.Step();
                if (s.Units.FirstOrDefault(u => u.Alive && u.FactionId == "enemy" && u.Def.Id == "goblin") is { } g) return g;
            }
            return null;
        }
        var g0 = Enemy(normal)!;
        var g1 = Enemy(hard)!;
        Assert.Equal(g0.Stat(Stats.MoveSpeed) * 1.2, g1.Stat(Stats.MoveSpeed), 6);
        Assert.Contains(g1.Stats.Explain(Stats.MoveSpeed).Items, c => c.C.Source.Contains("Враги быстрее"));
        var squad = hard.Units.Where(u => u.FactionId == "player" && !u.IsStructure).ToList();
        if (squad.Count == 0)
        {
            var called = hard.Input.Deploy(0, hard.Placement.Zone.First(c => hard.Deployment.CellProblem(hard.Squad.Entries[0], c) == null), HexDirection.W, out var why);
            Assert.True(called != null, why);
            hard.Step();
            squad.Add(called!);
        }
        Assert.All(squad, u => Assert.Equal(u.Stats.Base("heal_received") * 0.7, u.Stat("heal_received"), 6));
        Assert.Equal(g0.HpFraction, g1.HpFraction, 6);
    }

    [Fact]
    public void T18_IncompatibleSets_AreReportedBeforeTheStart_AndTheEffectiveValueShowsWhereItCameFrom()
    {
        var (db, sc, v) = Siege();
        db.Add(new ModifierSetDef
        {
            Id = "calm", Name = "Спокойная", IncompatibleWith = { "test_siege" },
            Modifiers = { new MissionModifierDef { Id = "slow", Label = "Враги медленнее", Op = MissionModOp.Stat, Selector = { Faction = "enemy" }, Stat = { Stat = Stats.MoveSpeed, Mul = -0.1 } } },
        });
        v.ModifierSets.Add("calm");
        var problems = MissionModifiers.Check(db, sc, v);
        Assert.Contains(problems, p => p.Error && p.Message.Contains("несовместимы"));
        Assert.Contains(problems, p => !p.Error && p.Message.Contains("одно поле меняют 2"));
        Assert.Contains(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Message.Contains("Вариант «Осада»") && i.Message.Contains("несовместимы"));

        // The effective value of the speed of a goblin: base → +20 % −10 %, both sources named.
        var rows = MissionModifiers.Preview(db, sc, v);
        double baseSpeed = db.Unit("goblin")!.Stats.GetValueOrDefault(Stats.MoveSpeed, db.StatDefault(Stats.MoveSpeed));
        var speed = rows.First(r => r.Field.Contains("Гоблин") && r.Field.StartsWith(db.Stat(Stats.MoveSpeed)!.DisplayName));
        Assert.Equal(baseSpeed.ToString("0.##"), speed.Base);
        Assert.Equal((baseSpeed * 1.1).ToString("0.##"), speed.Effective);
        Assert.Contains(speed.Sources, s => s.Contains("Враги быстрее"));
        Assert.Contains(speed.Sources, s => s.Contains("Враги медленнее"));
        Assert.Contains(rows, r => r.Field.StartsWith("Волна 3") && r.Effective.Contains("групп 2"));

        // «Последний заменяет»: only the later one of the field acts.
        db.Get<ModifierSetDef>("calm")!.Modifiers[0].Stacking = ModStacking.Replace;
        var rows2 = MissionModifiers.Preview(db, sc, v);
        var speed2 = rows2.First(r => r.Field.Contains("Гоблин") && r.Field.StartsWith(db.Stat(Stats.MoveSpeed)!.DisplayName));
        Assert.Equal((baseSpeed * 0.9).ToString("0.##"), speed2.Effective);
    }

    [Fact]
    public void InvalidResults_AndMissingReferences_AreErrorsBeforeTheBattle()
    {
        var (db, sc, v) = Siege();
        var set = db.Get<ModifierSetDef>("test_siege")!;
        set.Modifiers.Add(new MissionModifierDef { Label = "Без лимита", Op = MissionModOp.Deployment, DeployField = ModDeployField.MaxDeployed, Delta = -20 });
        set.Modifiers.Add(new MissionModifierDef { Label = "Стоп", Op = MissionModOp.Stat, Selector = { Units = { "wolf" } }, Stat = { Stat = Stats.MoveSpeed, Mul = -2 } });
        set.Modifiers.Add(new MissionModifierDef { Label = "Девятая волна", Op = MissionModOp.Wave, WaveIndex = 9, CountAdd = 2 });
        set.Modifiers.Add(new MissionModifierDef { Label = "Нет группы", Op = MissionModOp.Status, Status = "stun", Selector = { Group = "nowhere" } });
        set.Modifiers.Add(new MissionModifierDef { Label = "Отряд мал", Op = MissionModOp.Squad, SquadField = ModSquadField.MaxSize, Delta = -5 });
        v.ModifierSets.Add("missing_set");
        var p = MissionModifiers.Check(db, sc, v).Where(x => x.Error).Select(x => x.Message).ToList();
        Assert.Contains(p, m => m.Contains("«на поле одновременно» меньше 0"));
        Assert.Contains(p, m => m.Contains("не сможет действовать") && m.Contains("Волк"));
        Assert.Contains(p, m => m.Contains("волны 9 в сценарии нет"));
        Assert.Contains(p, m => m.Contains("группы «nowhere» в волнах нет"));
        Assert.Contains(p, m => m.Contains("меньше обязательных бойцов"));
        Assert.Contains(p, m => m.Contains("набора модификаторов «missing_set» нет"));
    }

    [Fact]
    public void ATimedModifier_ActsOnlyInItsWindow_AndAVariantBattle_ReplaysTheSame()
    {
        var (db, sc, v) = Siege();
        sc.GameRules = "rts_free"; // the whole squad on the field from the start
        var set = db.Get<ModifierSetDef>("test_siege")!;
        set.Modifiers.Add(new MissionModifierDef { Id = "rally", Label = "Подкрепление духа", Op = MissionModOp.Stat, Selector = { Faction = "player" }, Stat = { Stat = Stats.Armor, Add = 5 }, ActiveFrom = 2, ActiveUntil = 4 });
        var (vdb, vsc) = MissionModifiers.Prepare(db, sc, v);
        var (snap, sim) = BattleSnapshot.Begin(vdb, vsc, 11);
        var rec = BattleReplay.Record(snap, sim, withContent: true);
        var knight = sim.Units.First(u => u.Def.Id == "knight");
        double armor = knight.Stats.Base(Stats.Armor);
        double At(double s)
        {
            while (sim.Scenario.BattleTime < s) sim.Step();
            return knight.Stat(Stats.Armor);
        }
        Assert.Equal(armor, At(1), 6);
        Assert.Equal(armor + 5, At(3), 6);
        Assert.Equal(armor, At(5), 6);
        rec.Finish(sim);
        var play = BattleReplay.FromJson(rec.ToJson())!.Play();
        Assert.True(play.Matches, play.Text);
        Assert.Equal("td_pass_siege", play.Sim!.Mission.Variant);
    }

    [Fact]
    public void NoVariant_NoModifiers_TheBattleIsAsBefore()
    {
        var db = TestUtil.Shipped();
        var sc = db.Get<ScenarioDef>("valley_ambush")!;
        var (vdb, vsc) = MissionModifiers.Prepare(db, sc, null);
        var a = ScenarioSystem.Create(db, sc, 3);
        var b = ScenarioSystem.Create(vdb, vsc, 3);
        for (int i = 0; i < 200; i++)
        {
            a.Step();
            b.Step();
        }
        Assert.Equal(a.Digest().Hash, b.Digest().Hash);
        Assert.Empty(MissionModifiers.Preview(db, sc, null));
    }
}
