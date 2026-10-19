using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Editor without programming (TZ §15, docs/DECISIONS_2.md §33): the catalog of operations and
/// conditions, durations of temporary modifiers, new conditions/actions, AI rules and the checks
/// before publication.
/// </summary>
public class EditorOperationTests
{
    // ------------------------------------------------------------------ catalog

    [Fact]
    public void Catalog_CoversEveryItemOfTheSpec_AndEveryOperationType()
    {
        var covered = OperationCatalog.All.SelectMany(e => e.Covers).ToHashSet();
        foreach (var need in OperationCatalog.RequiredOperations.Concat(OperationCatalog.RequiredConditions))
            Assert.True(covered.Contains(need), $"каталог не покрывает пункт ТЗ «{need}»");
        foreach (EffectType t in Enum.GetValues<EffectType>())
            Assert.Contains(OperationCatalog.Abilities, e => ((SkillEffectDef)e.Create()).Type == t);
        foreach (TriggerActionType t in Enum.GetValues<TriggerActionType>())
            Assert.Contains(OperationCatalog.Actions, e => ((TriggerActionDef)e.Create()).Type == t);
        foreach (ConditionType t in Enum.GetValues<ConditionType>())
            Assert.Contains(OperationCatalog.Conditions, e => ((ConditionDef)e.Create()).Type == t);
        Assert.Equal(OperationCatalog.All.Count(), OperationCatalog.All.Select(e => e.Key).Distinct().Count());
        Assert.All(OperationCatalog.All, e => Assert.IsType(e.ItemType, e.Create()));
        Assert.Same(OperationCatalog.Conditions, OperationCatalog.For(typeof(ConditionDef)));
        Assert.Null(OperationCatalog.For(typeof(StatModDef)));
    }

    [Fact]
    public void CatalogPresets_ReferenceShippedContent()
    {
        var db = TestUtil.Shipped();
        foreach (var e in OperationCatalog.Abilities)
        {
            var op = (SkillEffectDef)e.Create();
            if (!string.IsNullOrEmpty(op.Status)) Assert.True(db.Status(op.Status) != null, $"{e.Name}: нет состояния {op.Status}");
            if (!string.IsNullOrEmpty(op.Terrain)) Assert.True(db.Terrain(op.Terrain) != null, $"{e.Name}: нет местности {op.Terrain}");
            if (!string.IsNullOrEmpty(op.Stat)) Assert.True(db.Stat(op.Stat) != null, $"{e.Name}: нет характеристики {op.Stat}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static (Simulation Sim, Unit Hero, Unit Foe) Duel(ContentDatabase? db = null, Action<ContentDatabase, ScenarioDef>? setup = null)
    {
        db ??= ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario();
        sc.Units.Add(new ScenarioUnitDef { Unit = "dummy", C = new HexCoord(3, 0), Facing = HexDirection.W, Tag = "foe" });
        setup?.Invoke(db, sc);
        var sim = ScenarioSetup.Sim(db, sc);
        return (sim, sim.Units.First(u => u.Tag == "hero"), sim.Units.First(u => u.Tag == "foe"));
    }

    private static void Apply(Simulation sim, Unit source, Unit target, params SkillEffectDef[] ops) =>
        sim.Combat.ApplyDirect(source, target, ops, 1.0, "тест", DamageSource.Other);

    private static void Run(Simulation sim, double seconds)
    {
        for (long i = 0, n = sim.TicksFor(seconds); i < n; i++) sim.Step();
    }

    // ------------------------------------------------------------------ operations

    [Fact]
    public void ModifyStat_ForATime_AndUntilTheEndOfTheBattle()
    {
        var (sim, hero, _) = Duel();
        double armor = hero.Stat(Stats.Armor);
        Apply(sim, hero, hero, new SkillEffectDef { Type = EffectType.ModifyStat, Stat = Stats.Armor, StatAdd = 4, Duration = 1 });
        Apply(sim, hero, hero, new SkillEffectDef { Type = EffectType.ModifyStat, Stat = Stats.Armor, StatAdd = 1, Duration = 0 });
        Assert.Equal(armor + 5, hero.Stat(Stats.Armor), 6);
        Run(sim, 1.2);
        Assert.Equal(armor + 1, hero.Stat(Stats.Armor), 6);
        // «До конца боя» is not a status: removing statuses does not touch it.
        foreach (var s in hero.Statuses.ToList()) sim.Statuses.Remove(hero, s, "очищение");
        sim.Statuses.RecalculateModifiers(hero);
        Assert.Equal(armor + 1, hero.Stat(Stats.Armor), 6);
    }

    [Fact]
    public void ModifyAccumulation_RaisesAndLowersPoints()
    {
        var db = ScenarioSetup.Db();
        db.Add(new StatusDef
        {
            Id = "heat", Name = "Жар", Kind = StatusKind.Accumulating, MaxPoints = 100,
            Levels = { new StatusLevelDef { Threshold = 10, Name = "Тепло" }, new StatusLevelDef { Threshold = 50, Name = "Перегрев" } },
        });
        var (sim, hero, foe) = Duel(db);
        Apply(sim, hero, foe, new SkillEffectDef { Type = EffectType.ModifyAccumulation, Status = "heat", Amount = 60 });
        Assert.Equal(1, foe.FindStatus("heat")!.LevelIndex);
        Apply(sim, hero, foe, new SkillEffectDef { Type = EffectType.ModifyAccumulation, Status = "heat", Amount = -30 });
        Assert.Equal(30, foe.FindStatus("heat")!.Points, 6);
        Assert.Equal(0, foe.FindStatus("heat")!.LevelIndex);
        Apply(sim, hero, foe, new SkillEffectDef { Type = EffectType.ModifyAccumulation, Status = "heat", Amount = -100 });
        Assert.Null(foe.FindStatus("heat"));
    }

    [Fact]
    public void RouteProfileMessageVfxSound_FlagAndTrigger()
    {
        var (sim, hero, foe) = Duel(setup: (db, sc) =>
        {
            db.Add(new AiProfileDef { Id = "coward", Name = "Трус", RetreatHpFraction = 1 });
            sc.Triggers.Add(new TriggerDef
            {
                Name = "Тревога", Once = true, Condition = new ConditionDef { Type = ConditionType.Not, Children = { new ConditionDef { Type = ConditionType.Always } } },
                Actions = { new TriggerActionDef { Type = TriggerActionType.Message, Text = "Тревога запущена" } },
            });
        });
        Apply(sim, hero, foe,
            new SkillEffectDef { Type = EffectType.SetRoute, Route = "main" },
            new SkillEffectDef { Type = EffectType.SetAiProfile, AiProfile = "coward" },
            new SkillEffectDef { Type = EffectType.Message, Text = "Сообщение навыка" },
            new SkillEffectDef { Type = EffectType.PlayVfx, Text = "spark_burst" },
            new SkillEffectDef { Type = EffectType.PlaySound, Text = "zap" },
            new SkillEffectDef { Type = EffectType.SetFlag, Text = "alarm" },
            new SkillEffectDef { Type = EffectType.ActivateTrigger, Text = "Тревога" });
        Assert.Equal(new HexCoord(9, 2), foe.Route![0]);
        Assert.Equal("coward", foe.AiProfile!.Id);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Message && e.Text == "Сообщение навыка");
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Vfx && e.Text == "spark_burst" && e.Cell == foe.Pos);
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.Sound && e.Text == "zap");
        Assert.Contains("alarm", sim.Scenario.Flags);
        Assert.Contains(sim.Log.Entries, e => e.Text == "Тревога запущена");
        // A once-only trigger does not run again.
        Assert.False(sim.Scenario.ActivateTrigger("Тревога", null));
    }

    // ------------------------------------------------------------------ durations of temporary modifiers

    private static ContentDatabase DurationDb()
    {
        var db = ScenarioSetup.Db();
        db.Add(new StatusDef { Id = "battle_rage", Name = "До конца боя", Negative = false, Lasts = StatusLasts.UntilBattleEnd, Duration = 1, Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new StatusDef
        {
            Id = "desperate", Name = "Пока ранен", Negative = false, Lasts = StatusLasts.WhileCondition,
            WhileCondition = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.5 },
            Levels = { new StatusLevelDef { Threshold = 1, StatMods = { new StatModDef { Stat = Stats.Power, Add = 10 } } } },
        });
        db.Add(new StatusDef { Id = "entrenched", Name = "Окопался", Negative = false, Lasts = StatusLasts.WhileOnCell, Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new StatusDef { Id = "aura", Name = "Аура", Negative = false, Duration = 2, Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new StatusDef { Id = "aura_echo", Name = "Пока аура", Negative = false, Lasts = StatusLasts.WhileEffectActive, WhileStatus = "aura", Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new CellEffectDef { Id = "holy_ground", Name = "Святая земля", Duration = 1 });
        db.Add(new StatusDef { Id = "blessed", Name = "Пока на святой земле", Negative = false, Lasts = StatusLasts.WhileEffectActive, WhileCellEffect = "holy_ground", Levels = { new StatusLevelDef { Threshold = 1 } } });
        return db;
    }

    [Fact]
    public void Durations_UntilBattleEnd_WhileCondition()
    {
        var (sim, hero, _) = Duel(DurationDb());
        sim.Statuses.Apply(hero, sim.Content.Status("battle_rage")!, 1, 0, hero.Id);
        sim.Statuses.Apply(hero, sim.Content.Status("desperate")!, 1, 0, hero.Id);
        hero.Hp = hero.HpMax * 0.3;
        Run(sim, 2);
        Assert.NotNull(hero.FindStatus("battle_rage")); // its 1 s duration is ignored
        Assert.NotNull(hero.FindStatus("desperate"));
        hero.Hp = hero.HpMax * 0.9;
        sim.Step();
        Assert.Null(hero.FindStatus("desperate"));
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.StatusRemoved && e.Text.Contains("условие"));
    }

    [Fact]
    public void Durations_WhileOnCell_WhileOtherEffect()
    {
        var (sim, hero, _) = Duel(DurationDb());
        sim.Statuses.Apply(hero, sim.Content.Status("entrenched")!, 1, 0, hero.Id);
        sim.Statuses.Apply(hero, sim.Content.Status("aura")!, 1, 0, hero.Id);
        sim.Statuses.Apply(hero, sim.Content.Status("aura_echo")!, 1, 0, hero.Id);
        var cell = sim.Map.Get(hero.Pos)!;
        sim.CellEffects.Add(cell, sim.Content.CellEffect("holy_ground")!, 1, 0, -1, 0, 0);
        sim.Statuses.Apply(hero, sim.Content.Status("blessed")!, 1, 0, hero.Id);
        Run(sim, 0.5);
        Assert.NotNull(hero.FindStatus("entrenched"));
        Assert.NotNull(hero.FindStatus("aura_echo"));
        Assert.NotNull(hero.FindStatus("blessed"));
        Run(sim, 1.2); // holy ground (1 s) is gone
        Assert.Null(hero.FindStatus("blessed"));
        Run(sim, 1); // aura (2 s) is gone → its echo too
        Assert.Null(hero.FindStatus("aura"));
        Assert.Null(hero.FindStatus("aura_echo"));
        Assert.NotNull(hero.FindStatus("entrenched"));
        Assert.Null(sim.Enqueue(hero, Command.Move(hero.Pos + new HexCoord(0, 1))));
        Run(sim, 3);
        Assert.Null(hero.FindStatus("entrenched"));
    }

    // ------------------------------------------------------------------ conditions

    [Fact]
    public void Conditions_RangeResourceStatusFlagTimer_AndLogic()
    {
        var db = ScenarioSetup.Db();
        db.Unit("soldier")!.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 100, Start = 40 });
        var (sim, hero, foe) = Duel(db);
        var sc = sim.Scenario;
        Assert.True(sc.Evaluate(new ConditionDef { Type = ConditionType.TargetInRange, Count = 3 }, hero));
        Assert.False(sc.Evaluate(new ConditionDef { Type = ConditionType.TargetInRange, Count = 2 }, hero));
        Assert.True(sc.Evaluate(new ConditionDef { Type = ConditionType.TargetInRange, Count = 3, UnitTag = "foe" }));
        var mana = new ConditionDef { Type = ConditionType.ResourceReached, Resource = "mana", Value = 50 };
        Assert.False(sc.Evaluate(mana, hero));
        hero.Resources["mana"].Current = 55;
        Assert.True(sc.Evaluate(mana, hero));
        Assert.True(sc.Evaluate(new ConditionDef { Type = ConditionType.ResourceReached, Resource = "mana", Value = 60, Compare = CompareMode.AtMost }, hero));
        var status = new ConditionDef { Type = ConditionType.HasStatus, Status = "poison", UnitTag = "foe" };
        Assert.False(sc.Evaluate(status));
        db.Add(new StatusDef { Id = "poison", Name = "Яд", Kind = StatusKind.Accumulating, MaxPoints = 100, Levels = { new StatusLevelDef { Threshold = 1 } } });
        sim.Statuses.Apply(foe, db.Status("poison")!, 30, 0, hero.Id);
        Assert.True(sc.Evaluate(status));
        Assert.False(sc.Evaluate(new ConditionDef { Type = ConditionType.HasStatus, Status = "poison", UnitTag = "foe", Value = 40 }));

        var timer = new ConditionDef { Type = ConditionType.FlagTimer, Flag = "go", Seconds = 1 };
        Assert.False(sc.Evaluate(timer));
        sc.SetFlag("go");
        Run(sim, 0.5);
        Assert.False(sc.Evaluate(timer));
        Run(sim, 0.6);
        Assert.True(sc.Evaluate(timer));

        // AND / OR / NOT over the new leaves.
        var and = new ConditionDef { Type = ConditionType.All, Children = { mana, timer } };
        var or = new ConditionDef { Type = ConditionType.Any, Children = { new ConditionDef { Type = ConditionType.FlagSet, Flag = "nope" }, timer } };
        var not = new ConditionDef { Type = ConditionType.Not, Children = { mana } };
        Assert.True(sc.Evaluate(and, hero));
        Assert.True(sc.Evaluate(or, hero));
        Assert.False(sc.Evaluate(not, hero));
    }

    [Fact]
    public void Conditions_SpottedLeftAreaObstacleSynergyBossPhase()
    {
        var db = ScenarioSetup.Db();
        db.Add(new TerrainDef { Id = "crate2", Name = "Ящик", PassableFor = new(), DestructibleHp = 5, DestroyedTerrain = "grass" });
        db.Add(new CellEffectDef { Id = "oil", Name = "Масло", Duration = 0 });
        db.Add(new CellEffectDef { Id = "flame", Name = "Пламя", Duration = 0 });
        db.Add(new SynergyDef { Id = "oil_fire", Name = "Вспышка", A = new SynergyOperandDef { Value = "oil" }, B = new SynergyOperandDef { Value = "flame" } });
        db.Add(new UnitDef
        {
            Id = "boss", Name = "Босс", Faction = "enemy", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
            Boss = new BossDef { IsBoss = true, Phases = { new BossPhaseDef { Name = "Первая", When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.9 } }, new BossPhaseDef { Name = "Вторая", When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.5 } } } },
        });
        var (sim, hero, foe) = Duel(db, (d, sc) =>
        {
            d.Get<MapDef>("corridor")!.Cells.First(c => c.C == new HexCoord(6, 2)).T = "crate2";
            sc.Units.Add(new ScenarioUnitDef { Unit = "boss", C = new HexCoord(10, 4), Facing = HexDirection.W, Tag = "boss" });
        });
        var sc = sim.Scenario;
        Assert.True(sc.Evaluate(new ConditionDef { Type = ConditionType.EnemySpotted }));

        // Leaving an area is a one-tick event.
        var left = new ConditionDef { Type = ConditionType.UnitLeftArea, Marker = "gate", Side = AreaSide.Player };
        sim.Relocate(hero, new HexCoord(3, 0), null, "тест");
        Assert.False(sc.Evaluate(left));
        sim.Relocate(hero, new HexCoord(0, 2), null, "тест");
        Assert.True(sc.Evaluate(left));
        Assert.False(sc.Evaluate(left));

        var destroyed = new ConditionDef { Type = ConditionType.ObstacleDestroyed, Count = 1 };
        Assert.False(sc.Evaluate(destroyed));
        sim.CellEffects.DamageObstacle(sim.Map.Get(new HexCoord(6, 2))!, 10, hero, "тест");
        Assert.True(sc.Evaluate(destroyed));

        var synergy = new ConditionDef { Type = ConditionType.SynergyOccurred, Synergy = "oil_fire", Count = 1 };
        Assert.False(sc.Evaluate(synergy));
        var cell = sim.Map.Get(new HexCoord(5, 4))!;
        sim.CellEffects.Add(cell, db.CellEffect("oil")!, 1, 0, -1, 0, 0);
        sim.CellEffects.Add(cell, db.CellEffect("flame")!, 1, 0, -1, 0, 0);
        Assert.True(sc.Evaluate(synergy));

        var boss = sim.Units.First(u => u.Tag == "boss");
        var phase2 = new ConditionDef { Type = ConditionType.BossPhase, Count = 2 };
        boss.Hp = 80;
        sim.Step();
        Assert.True(sc.Evaluate(new ConditionDef { Type = ConditionType.BossPhase, Count = 1 }));
        Assert.False(sc.Evaluate(phase2));
        boss.Hp = 40;
        sim.Step();
        Assert.True(sc.Evaluate(phase2));
    }

    // ------------------------------------------------------------------ actions, triggers, passives, AI rules

    [Fact]
    public void TriggerActions_Effects_DestroyObstacle_BossPhase_TriggerChains()
    {
        var db = ScenarioSetup.Db();
        db.Add(new TerrainDef { Id = "crate2", Name = "Ящик", PassableFor = new(), DestructibleHp = 50, DestroyedTerrain = "grass" });
        db.Add(new UnitDef
        {
            Id = "boss", Name = "Босс", Faction = "enemy", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
            Boss = new BossDef { IsBoss = true, Phases = { new BossPhaseDef { Name = "А", When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.1 } }, new BossPhaseDef { Name = "Ярость", When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.05 }, Message = "Ярость!" } } },
        });
        var (sim, hero, foe) = Duel(db, (d, sc) =>
        {
            d.Get<MapDef>("corridor")!.Cells.First(c => c.C == new HexCoord(3, 0)).T = "crate2";
            sc.Units.First(u => u.Tag == "foe").C = new HexCoord(5, 0);
            sc.Units.Add(new ScenarioUnitDef { Unit = "boss", C = new HexCoord(10, 4), Facing = HexDirection.W, Tag = "boss" });
            // A cycle of triggers: A → B → A (cut at runtime by the depth limit, reported by the checks).
            sc.Triggers.Add(new TriggerDef { Name = "A", Once = false, Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "never" }, Actions = { new TriggerActionDef { Type = TriggerActionType.ActivateTrigger, Text = "B" } } });
            sc.Triggers.Add(new TriggerDef { Name = "B", Once = false, Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "never" }, Actions = { new TriggerActionDef { Type = TriggerActionType.ActivateTrigger, Text = "A" } } });
        });
        var scn = sim.Scenario;
        double hp = foe.Hp;
        scn.Execute(new TriggerActionDef
        {
            Type = TriggerActionType.Effects, Target = ActionTarget.UnitTag, UnitTag = "foe",
            Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 15 }, new SkillEffectDef { Type = EffectType.Message, Text = "Залп" } },
        }, "тест", null);
        Assert.True(foe.Hp < hp);
        Assert.Contains(sim.Log.Entries, e => e.Text == "Залп");

        scn.Execute(new TriggerActionDef { Type = TriggerActionType.DestroyObstacle, Marker = "gate", Radius = 0 }, "тест", null);
        Assert.Equal("grass", sim.Map.Get(new HexCoord(3, 0))!.Terrain.Id);

        var boss = sim.Units.First(u => u.Tag == "boss");
        scn.Execute(new TriggerActionDef { Type = TriggerActionType.SetBossPhase, Target = ActionTarget.UnitTag, UnitTag = "boss", Phase = 2 }, "тест", null);
        Assert.Equal("Ярость", scn.PhaseOf(boss));
        Assert.Contains(sim.Log.Entries, e => e.Text == "Ярость!");

        Assert.True(scn.ActivateTrigger("A", null));
        Assert.True(scn.TriggerCycleCuts > 0);
    }

    [Fact]
    public void PassiveCondition_GatesTheReaction()
    {
        var db = ScenarioSetup.Db();
        db.Add(new PassiveDef
        {
            Id = "last_stand", Name = "Последний рубеж", Trigger = PassiveTrigger.OnDamaged, Action = PassiveAction.EffectsOnSelf,
            Condition = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.5 },
            Effects = { new SkillEffectDef { Type = EffectType.ModifyStat, Stat = Stats.Armor, StatAdd = 5, Filter = TargetFilterDef.AlliesAndSelf() } },
        });
        db.Unit("dummy")!.Passives.Add("last_stand");
        var (sim, hero, foe) = Duel(db);
        double armor = foe.Stat(Stats.Armor);
        sim.Combat.DealDamage(hero, foe, 10, "physical", null, "тест", null, DamageSource.Other);
        sim.Reactions.Process();
        Assert.Equal(armor, foe.Stat(Stats.Armor), 6);
        foe.Hp = foe.HpMax * 0.4;
        sim.Combat.DealDamage(hero, foe, 5, "physical", null, "тест", null, DamageSource.Other);
        sim.Reactions.Process();
        Assert.Equal(armor + 5, foe.Stat(Stats.Armor), 6);
    }

    [Fact]
    public void AiRules_OnceAndWithCooldown()
    {
        var db = ScenarioSetup.Db();
        db.Add(new StatusDef { Id = "alert", Name = "Настороже", Negative = false, Duration = 0.5, Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new AiProfileDef
        {
            Id = "ruled", Name = "С правилами",
            Rules =
            {
                new AiRuleDef { Name = "Крик", When = new ConditionDef { Type = ConditionType.TargetInRange, Count = 5 }, Once = true, Actions = { new TriggerActionDef { Type = TriggerActionType.Message, Text = "Враг!" } } },
                new AiRuleDef { Name = "Настороже", When = new ConditionDef { Type = ConditionType.TargetInRange, Count = 5 }, Once = false, Cooldown = 1,
                    Actions = { new TriggerActionDef { Type = TriggerActionType.ApplyStatus, Status = "alert", Target = ActionTarget.Self } } },
            },
        });
        db.Unit("dummy")!.AiProfile = "ruled";
        var (sim, _, foe) = Duel(db);
        Run(sim, 2.5);
        Assert.Single(sim.Log.Entries, e => e.Text == "Враг!");
        int applied = sim.Log.Entries.Count(e => e.Type == SimEventType.StatusApplied && e.TargetUnit == foe.Id && e.Text.Contains("Настороже"));
        Assert.InRange(applied, 2, 4); // every second (the status lasts 0.5 s), not every tick
    }

    // ------------------------------------------------------------------ checks before publication

    private static List<ContentIssue> Check(ContentDatabase db) => ContentValidator.Validate(db);

    [Fact]
    public void Checks_CyclicTriggers_AndEndlessScenarioReactions()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario();
        sc.Triggers.Add(new TriggerDef { Name = "A", Actions = { new TriggerActionDef { Type = TriggerActionType.ActivateTrigger, Text = "B" } } });
        sc.Triggers.Add(new TriggerDef { Name = "B", Actions = { new TriggerActionDef { Type = TriggerActionType.Effects, Effects = { new SkillEffectDef { Type = EffectType.ActivateTrigger, Text = "A" } } } } });
        sc.Triggers.Add(new TriggerDef { Name = "C", Actions = { new TriggerActionDef { Type = TriggerActionType.ActivateTrigger, Text = "нет такого" } } });
        sc.Triggers.Add(new TriggerDef { Name = "Каждый тик", Once = false, Condition = new ConditionDef { Type = ConditionType.Always }, Actions = { new TriggerActionDef { Type = TriggerActionType.Message, Text = "!" } } });
        sc.Triggers.Add(new TriggerDef
        {
            Name = "Сам себе", Once = false, Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "loop" },
            Actions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "loop" } },
        });
        sc.Triggers.Add(new TriggerDef { Name = "Ждёт", Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "ghost" } });
        db.Add(sc);
        var issues = Check(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.StartsWith("Циклические триггеры") && i.Message.Contains("«A»") && i.Message.Contains("«B»"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("«нет такого» не найден"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("каждый тик") && i.Path.Contains("Каждый тик"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("сам устанавливает флаг"));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("«ghost» нигде не устанавливается"));
    }

    [Fact]
    public void Checks_IncompatibleEffects_StatusModes_Operations()
    {
        var db = ScenarioSetup.Db();
        db.Add(new SkillDef
        {
            Id = "tug", Name = "Туда-сюда", Targeting = TargetingMode.Unit,
            Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Push }, new SkillEffectDef { Type = EffectType.Pull },
                new SkillEffectDef { Type = EffectType.ApplyStatus, Status = "x" }, new SkillEffectDef { Type = EffectType.RemoveStatus, Status = "x" },
                new SkillEffectDef { Type = EffectType.ModifyStat }, new SkillEffectDef { Type = EffectType.Message, Text = " " } } } },
        });
        db.Add(new StatusDef { Id = "x", Name = "X", Levels = { new StatusLevelDef { Threshold = 1 } } });
        db.Add(new StatusDef { Id = "w1", Name = "Без условия", Lasts = StatusLasts.WhileCondition });
        db.Add(new StatusDef { Id = "w2", Name = "Сам по себе", Lasts = StatusLasts.WhileEffectActive, WhileStatus = "w2" });
        db.Add(new StatusDef { Id = "w3", Name = "Пусто", Lasts = StatusLasts.WhileEffectActive });
        var issues = Check(db);
        Assert.Contains(issues, i => i.Source == "Skill:tug" && i.Severity == IssueSeverity.Warning && i.Message.Contains("толчок и притяжение"));
        Assert.Contains(issues, i => i.Source == "Skill:tug" && i.Severity == IssueSeverity.Warning && i.Message.Contains("и накладывается, и снимается"));
        Assert.Contains(issues, i => i.Source == "Skill:tug" && i.Severity == IssueSeverity.Error && i.Message.Contains("не выбрана характеристика"));
        Assert.Contains(issues, i => i.Source == "Skill:tug" && i.Severity == IssueSeverity.Error && i.Message.Contains("не задан текст"));
        Assert.Contains(issues, i => i.Source == "Status:w1" && i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Source == "Status:w2" && i.Severity == IssueSeverity.Error && i.Message.Contains("никогда не закончится"));
        Assert.Contains(issues, i => i.Source == "Status:w3" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Checks_MissingResources_EndlessReactions_SynergyCycles()
    {
        var db = ScenarioSetup.Db();
        db.Add(new SkillDef { Id = "bolt", Name = "Разряд", Targeting = TargetingMode.Unit, ResourceCosts = { new ResourceCostDef { Resource = "mana", Amount = 5 } } });
        db.Unit("soldier")!.Skills.Add("bolt");
        db.Add(new PassiveDef { Id = "thorns", Name = "Шипы", Trigger = PassiveTrigger.OnDamaged, Action = PassiveAction.EffectsOnSource, Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 3 } } });
        db.Add(new CellEffectDef { Id = "spark", Name = "Искра" });
        db.Add(new CellEffectDef { Id = "gas", Name = "Газ" });
        // spark + gas → spark + gas: reproduces both inputs — endless.
        db.Add(new SynergyDef { Id = "loop", Name = "Вечная вспышка", A = new SynergyOperandDef { Value = "spark" }, B = new SynergyOperandDef { Value = "gas" }, RadiusCellEffects = { "spark", "gas" }, Radius = 1 });
        // fire + oil → fire: oil is used up — the chain ends by itself.
        db.Add(new CellEffectDef { Id = "fire2", Name = "Огонь" });
        db.Add(new CellEffectDef { Id = "oil2", Name = "Масло" });
        db.Add(new SynergyDef { Id = "spread", Name = "Растекание", A = new SynergyOperandDef { Value = "fire2" }, B = new SynergyOperandDef { Value = "oil2" }, RadiusCellEffects = { "fire2" }, Radius = 1 });
        var issues = Check(db);
        Assert.Contains(issues, i => i.Source == "Unit:soldier" && i.Message.StartsWith("Отсутствующий ресурс"));
        Assert.Contains(issues, i => i.Source == "Passive:thorns" && i.Severity == IssueSeverity.Warning && i.Message.Contains("без конца"));
        Assert.Contains(issues, i => i.Source == "Synergy:loop" && i.Message.StartsWith("Цикл синергий"));
        Assert.DoesNotContain(issues, i => i.Source == "Synergy:spread" && i.Message.StartsWith("Цикл синергий"));
    }

    [Fact]
    public void Checks_UnreachableObjective_BlocksPublication()
    {
        var db = ScenarioSetup.Db();
        var map = db.Get<MapDef>("corridor")!;
        // A wall across the corridor: the right half is cut off.
        foreach (var c in map.Cells.Where(c => c.C.Q + c.C.R / 2 == 7)) c.T = "wall";
        var sc = ScenarioSetup.Scenario(new ObjectiveDef { Id = "cap", Name = "Захват", Kind = ObjectiveKind.Capture, Cells = { new HexCoord(10, 2) }, Required = true });
        db.Add(sc);
        var issues = Check(db);
        var err = Assert.Single(issues, i => i.Source == "Scenario:test" && i.Message.Contains("недостижима"));
        Assert.Equal(IssueSeverity.Error, err.Severity);
        var gate = ContentChecks.PublishGate(db, issues);
        Assert.Contains(gate.Blocking, i => i.Message.Contains("недостижима"));

        // An incomplete draft does not block publication with its own errors.
        sc.Incomplete = true;
        var gate2 = ContentChecks.PublishGate(db, Check(db));
        Assert.DoesNotContain(gate2.Blocking, i => i.Source == "Scenario:test");
        Assert.Contains(gate2.DraftErrors, i => i.Source == "Scenario:test");
    }

    [Fact]
    public void ShippedContent_PassesThePublicationGate()
    {
        var db = TestUtil.Shipped();
        var gate = ContentChecks.PublishGate(db, ContentValidator.Validate(db));
        Assert.Empty(gate.Blocking);
        // The sample AI profile with rules works in a battle.
        Assert.NotEmpty(db.Get<AiProfileDef>("wary_guard")!.Rules);
    }
}
