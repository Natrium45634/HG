using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.11 — friendly fire and the rules of applying effects (ТЗ ч. 6, 19; T41–T46; docs/DECISIONS_6.md §105).</summary>
public class Part6FriendlyFireTests
{
    /// <summary>
    /// A mage of the player at (2,2), a knight of the player at (6,2) next to a goblin at (7,2); a ball of fire (damage to everyone
    /// in radius 1, always hits) and a ball of healing (allies in radius 1). The AI and auto-attacks are off.
    /// </summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene(System.Action<ScenarioDef>? edit = null, System.Action<ContentDatabase>? dbEdit = null)
    {
        var db = TestUtil.Shipped();
        var ball = ContentJson.Clone(db.Skill("fireball")!);
        ball.Id = "ff_ball";
        ball.HitMode = HitMode.AlwaysHit;
        ball.Cooldown = 0;
        ball.ResourceCosts.Clear();
        ball.Stages[0].Effects.RemoveAll(e => e.Type != EffectType.Damage);
        ball.Stages[0].Effects[0].Amount = 20;
        ball.Stages[0].Effects[0].ScalingStat = "";
        db.Add(ball);
        var heal = ContentJson.Clone(ball);
        heal.Id = "ff_heal";
        heal.Stages[0].Effects[0] = new SkillEffectDef { Type = EffectType.Heal, Amount = 30, Filter = TargetFilterDef.AlliesAndSelf(), RequiresHit = false };
        db.Add(heal);
        var mage = db.Unit("mage")!;
        mage.Skills.Add("ff_ball");
        mage.Skills.Add("ff_heal");
        dbEdit?.Invoke(db);
        var map = TestUtil.Field(12, 6);
        map.Id = "ff_field";
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "ff_test", Name = "Огонь по своим", Map = map.Id,
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 9999 } },
            Defeat = new List<ConditionDef>(),
            Units =
            {
                new ScenarioUnitDef { Unit = "mage", Faction = "player", C = new HexCoord(2, 2), Facing = HexDirection.E, Tag = "mage" },
                new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(6, 2), Facing = HexDirection.E, Tag = "ally" },
                new ScenarioUnitDef { Unit = "goblin", Faction = "enemy", C = new HexCoord(7, 2), Facing = HexDirection.W, Tag = "foe" },
            },
        };
        edit?.Invoke(sc);
        db.Add(sc);
        return (db, sc);
    }

    private static Simulation Start(ContentDatabase db, ScenarioDef sc)
    {
        var sim = ScenarioSystem.Create(db, sc, 4);
        sim.Ai.Enabled = false;
        foreach (var u in sim.Units) sim.AutoAttacks.DisabledUnits.Add(u.Id);
        sim.Step();
        return sim;
    }

    private static Unit U(Simulation sim, string tag) => sim.Units.First(u => u.Tag == tag);

    /// <summary>The mage casts a skill at a hex and the battle runs until it is released.</summary>
    private static void Cast(Simulation sim, string skill, HexCoord at)
    {
        var mage = U(sim, "mage");
        Assert.Null(sim.Enqueue(mage, Command.Skill(skill, at)));
        for (int i = 0; i < sim.TicksFor(4); i++) sim.Step();
    }

    private static FriendlyFireRulesDef Rules(FfKind kind, FfTargets t) => kind switch
    {
        FfKind.Area => new FriendlyFireRulesDef { Area = new FriendlyFireDef { Targets = t } },
        FfKind.Field => new FriendlyFireRulesDef { Fields = new FriendlyFireDef { Targets = t } },
        _ => new FriendlyFireRulesDef { Direct = new FriendlyFireDef { Targets = t } },
    };

    [Fact]
    public void T41_FriendlyFireOff_TheAllyTakesNoDamageFromAnArea()
    {
        // The old switch of the scenario…
        var (db, sc) = Scene(s => s.GameRulesOverride = new GameRulesOverrideDef { FriendlyFire = false });
        var sim = Start(db, sc);
        double ally = U(sim, "ally").Hp, foe = U(sim, "foe").Hp;
        Cast(sim, "ff_ball", new HexCoord(7, 2));
        Assert.Equal(ally, U(sim, "ally").Hp, 6);
        Assert.True(U(sim, "foe").Hp < foe);

        // …and the new rule «только враждебные» for areas give the same.
        var (db2, sc2) = Scene(s => s.GameRulesOverride = new GameRulesOverrideDef { FriendlyFireRules = Rules(FfKind.Area, FfTargets.HostileOnly) });
        var s2 = Start(db2, sc2);
        double a2 = U(s2, "ally").Hp;
        Cast(s2, "ff_ball", new HexCoord(7, 2));
        Assert.Equal(a2, U(s2, "ally").Hp, 6);
        Assert.Equal(0, BattleReport.Attach(s2).FriendlyDamage);

        // On: the ally is hit too, and the report counts it.
        var (db3, sc3) = Scene();
        var s3 = Start(db3, sc3);
        var rep = BattleReport.Attach(s3);
        double a3 = U(s3, "ally").Hp;
        Cast(s3, "ff_ball", new HexCoord(7, 2));
        Assert.True(U(s3, "ally").Hp < a3);
        Assert.True(rep.Fill().FriendlyDamage > 0);
    }

    [Fact]
    public void T42_FriendlyFireOnForAField_TheAllyGetsOnEnterAndOnTick_TheEnemyByTheSameFilter()
    {
        foreach (var on in new[] { true, false })
        {
            var (db, sc) = Scene(s => s.GameRulesOverride = new GameRulesOverrideDef
            {
                FriendlyFireRules = Rules(FfKind.Field, on ? FfTargets.AllIncludingSource : FfTargets.HostileOnly),
            });
            var sim = Start(db, sc);
            var mage = U(sim, "mage");
            var ally = U(sim, "ally");
            var foe = U(sim, "foe");
            var fire = db.Get<CellEffectDef>("fire")!;
            sim.CellEffects.Add(sim.Map.Get(new HexCoord(5, 2))!, fire, 1, 10, mage.Id, 0, 0);
            sim.CellEffects.Add(sim.Map.Get(foe.Pos)!, fire, 1, 10, mage.Id, 0, 0);
            double hp = ally.Hp;
            Assert.Null(sim.Enqueue(ally, Command.Move(new HexCoord(5, 2))));
            for (int i = 0; i < sim.TicksFor(3); i++) sim.Step();
            Assert.Equal(new HexCoord(5, 2), ally.Pos);
            if (on) Assert.True(ally.Hp < hp, "огонь по своим включён — союзник получает урон");
            else Assert.Equal(hp, ally.Hp, 6);
            Assert.True(foe.Hp < foe.HpMax, "враг получает урон поля в любом случае");
        }
    }

    [Fact]
    public void T43_OneSkill_HasItsOwnRule_OverThePresetOfTheScenario()
    {
        var (db, sc) = Scene(s => s.GameRulesOverride = new GameRulesOverrideDef { FriendlyFireRules = Rules(FfKind.Area, FfTargets.HostileOnly) },
            d =>
            {
                var own = ContentJson.Clone(d.Skill("ff_ball")!);
                own.Id = "ff_ball_wild";
                own.FriendlyFire = new FriendlyFireDef { Targets = FfTargets.AllExceptSource };
                d.Add(own);
                d.Unit("mage")!.Skills.Add("ff_ball_wild");
            });
        var sim = Start(db, sc);
        var ally = U(sim, "ally");
        double hp = ally.Hp;
        Cast(sim, "ff_ball", new HexCoord(7, 2));
        Assert.Equal(hp, ally.Hp, 6); // the scenario: hostile only
        Cast(sim, "ff_ball_wild", new HexCoord(7, 2));
        Assert.True(ally.Hp < hp);    // the skill's own rule is more specific

        // «Попутный урон»: a ball that picks enemies only still hits the ally in its area when the rule says so.
        var (db2, sc2) = Scene(s => s.GameRulesOverride = new GameRulesOverrideDef
        {
            FriendlyFireRules = new FriendlyFireRulesDef { Area = new FriendlyFireDef { Targets = FfTargets.AllExceptSource, Collateral = true } },
        }, d => d.Skill("ff_ball")!.Stages[0].Effects[0].Filter = TargetFilterDef.EnemiesOnly());
        var s2 = Start(db2, sc2);
        double h2 = U(s2, "ally").Hp;
        Cast(s2, "ff_ball", new HexCoord(7, 2));
        Assert.True(U(s2, "ally").Hp < h2);
        var (db3, sc3) = Scene(null, d => d.Skill("ff_ball")!.Stages[0].Effects[0].Filter = TargetFilterDef.EnemiesOnly());
        var s3 = Start(db3, sc3);
        double h3 = U(s3, "ally").Hp;
        Cast(s3, "ff_ball", new HexCoord(7, 2));
        Assert.Equal(h3, U(s3, "ally").Hp, 6); // without the mode the filter of the skill decides (ТЗ 19.4)
    }

    [Fact]
    public void T44_AChangeOfFaction_TheFieldTakesItsRelationsByItsRule()
    {
        foreach (var at in new[] { FfRelationAt.Live, FfRelationAt.Creation })
        {
            var (db, sc) = Scene(s => s.GameRulesOverride = new GameRulesOverrideDef
            {
                FriendlyFireRules = new FriendlyFireRulesDef { Fields = new FriendlyFireDef { Targets = FfTargets.HostileOnly, RelationAt = at } },
            });
            var sim = Start(db, sc);
            var mage = U(sim, "mage");
            var ally = U(sim, "ally");
            sim.CellEffects.Add(sim.Map.Get(ally.Pos)!, db.Get<CellEffectDef>("fire")!, 1, 20, mage.Id, 0, 0);
            for (int i = 0; i < sim.TicksFor(2); i++) sim.Step();
            double hp = ally.Hp;
            Assert.Equal(ally.HpMax, hp, 6); // an ally of the creator: spared
            mage.FactionId = "enemy";          // charmed: the creator now fights for the other side
            for (int i = 0; i < sim.TicksFor(3); i++) sim.Step();
            if (at == FfRelationAt.Live) Assert.True(ally.Hp < hp, "при срабатывании: создатель теперь враг — поле жжёт");
            else Assert.Equal(hp, ally.Hp, 6);
        }
    }

    [Fact]
    public void T45_TwoFieldsOverlap_ByTheStackingPolicy_AndAnEntryIsNotRepeated()
    {
        var (db, sc) = Scene(dbEdit: d =>
        {
            foreach (var id in new[] { "mark_a", "mark_b" })
                d.Add(new CellEffectDef
                {
                    Id = id, Name = id, Duration = 0, TickInterval = 1, StackPolicy = CellStackPolicy.Refresh,
                    OnEnter = { new SkillEffectDef { Type = EffectType.ModifyResource, Resource = "rage", Amount = 1, Filter = TargetFilterDef.Everyone(), RequiresHit = false } },
                });
        });
        var sim = Start(db, sc);
        var ally = U(sim, "ally");
        var cell = sim.Map.Get(new HexCoord(5, 2))!;
        foreach (var id in new[] { "mark_a", "mark_b", "mark_a" }) // the second «mark_a» refreshes the first — one instance
            sim.CellEffects.Add(cell, db.Get<CellEffectDef>(id)!, 1, 0, -1, 0, 0);
        Assert.Equal(2, cell.Effects.Count);
        Assert.Null(sim.Enqueue(ally, Command.Move(new HexCoord(5, 2))));
        for (int i = 0; i < sim.TicksFor(6); i++) sim.Step(); // arrives, then stands — many ticks, many frames
        Assert.Equal(new HexCoord(5, 2), ally.Pos);
        // One entry, two fields — once each (counted in the log: rage also grows by itself).
        Assert.Single(sim.Log.Entries, e => e.Type == SimEventType.ResourceChanged && e.TargetUnit == ally.Id && e.Text.Contains("mark_a"));
        Assert.Single(sim.Log.Entries, e => e.Type == SimEventType.ResourceChanged && e.TargetUnit == ally.Id && e.Text.Contains("mark_b"));
    }

    [Fact]
    public void T46_HealingOfAllies_IsNotBlockedByTheSwitchOfDamage()
    {
        var (db, sc) = Scene(s => s.GameRulesOverride = new GameRulesOverrideDef { FriendlyFire = false, FriendlyFireRules = Rules(FfKind.Area, FfTargets.HostileOnly) });
        var sim = Start(db, sc);
        var ally = U(sim, "ally");
        ally.Hp = ally.HpMax - 50;
        var rep = BattleReport.Attach(sim);
        Cast(sim, "ff_heal", new HexCoord(6, 2));
        Assert.True(ally.Hp > ally.HpMax - 50);
        Assert.True(rep.Fill().AlliedHealing > 0);
    }

    [Fact]
    public void TheLevels_Inherit_TheEnvironment_SelfHarm_AndThePreview()
    {
        var db = TestUtil.Shipped();
        var rules = ContentJson.Clone(db.Rules);
        // Nothing set: areas by the old switch, the rest by the filters.
        Assert.Equal(FfTargets.AllIncludingSource, FriendlyFireSystem.Resolve(rules, null, null, FfKind.Area).Rule.Targets);
        rules.Combat.FriendlyFire = false;
        Assert.Same(FriendlyFireSystem.LegacyNoAllyDamage, FriendlyFireSystem.Resolve(rules, null, null, FfKind.Area).Rule);
        // «Наследовать» on a level takes the next one; the most specific explicit one wins.
        var preset = Rules(FfKind.Area, FfTargets.HostileAndNeutral);
        var scen = new FriendlyFireRulesDef { Area = new FriendlyFireDef { Targets = FfTargets.Inherit } };
        var p = FriendlyFireSystem.Resolve(rules, preset, scen, FfKind.Area, (new FriendlyFireDef(), "навык"));
        Assert.Equal((FfTargets.HostileAndNeutral, "правила режима"), (p.Rule.Targets, p.Source));
        Assert.Equal("навык", FriendlyFireSystem.Resolve(rules, preset, scen, FfKind.Area, (new FriendlyFireDef { Targets = FfTargets.HostileOnly }, "навык")).Source);
        Assert.Equal(FfTargets.AllIncludingSource, FriendlyFireSystem.Resolve(rules, preset, scen, FfKind.Direct).Rule.Targets); // a direct attack: its own kind

        // Self-harm only when explicit; healing is never harm; the environment belongs to nobody.
        var probe = new Unit(1, db.Unit("knight")!, db);
        var custom = new FriendlyFireDef { Targets = FfTargets.Custom };
        Assert.False(FriendlyFireSystem.Permits(custom, FfCategory.Damage, FfRelation.Self, probe));
        Assert.True(FriendlyFireSystem.Permits(new FriendlyFireDef { Targets = FfTargets.Custom, Self = true }, FfCategory.Damage, FfRelation.Self, probe));
        Assert.True(FriendlyFireSystem.Permits(new FriendlyFireDef { Targets = FfTargets.HostileOnly }, FfCategory.Healing, FfRelation.Ally, probe));
        Assert.True(FriendlyFireSystem.Permits(new FriendlyFireDef { Targets = FfTargets.HostileOnly }, FfCategory.Damage, FfRelation.Environment, probe));
        Assert.Equal(FfCategory.Control, FriendlyFireSystem.CategoryOf(new SkillEffectDef { Type = EffectType.Push }, db));
        Assert.Equal(FfCategory.Buff, FriendlyFireSystem.CategoryOf(new SkillEffectDef { Type = EffectType.ApplyStatus, Status = "steadfast" }, db));
        Assert.Equal(FfCategory.Control, FriendlyFireSystem.CategoryOf(new SkillEffectDef { Type = EffectType.ApplyStatus, Status = "panic" }, db));

        // The preview of the editor: who gets what and why.
        var sc = new ScenarioDef { GameRulesOverride = new GameRulesOverrideDef { FriendlyFireRules = Rules(FfKind.Area, FfTargets.HostileOnly) } };
        var lines = FriendlyFireSystem.Preview(db, sc, FfKind.Area, db.Skill("fireball")!.Stages[0].Effects);
        Assert.StartsWith("Правило (область): только враждебные — сценарий", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("союзник: урон — нет (правило)"));
        Assert.Contains(lines, l => l.StartsWith("враг: урон — да"));
    }
}
