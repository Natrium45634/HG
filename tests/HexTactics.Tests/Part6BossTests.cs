using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.7 — the phases of a boss as rules, the telegraphs of attacks (ТЗ ч. 6, 8; T24–T25; docs/DECISIONS_6.md §102).</summary>
public class Part6BossTests
{
    /// <summary>A field with an orc made a boss (its phases given), a knight far away; the AI is off — nobody fights by itself.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene(BossDef boss, bool onTemplate = true)
    {
        var db = TestUtil.Shipped();
        var map = TestUtil.Field(12, 6);
        map.Id = "boss_field";
        db.Add(map);
        if (onTemplate) db.Unit("orc_brute")!.Boss = boss;
        var sc = new ScenarioDef
        {
            Id = "boss_test", Name = "Фазы", Map = map.Id,
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 9999 } },
            Defeat = new List<ConditionDef>(),
            Units =
            {
                new ScenarioUnitDef { Unit = "orc_brute", Faction = "enemy", C = new HexCoord(10, 2), Facing = HexDirection.W, Tag = "boss1", Boss = onTemplate ? null : boss },
                new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(0, 2), Facing = HexDirection.E },
            },
        };
        db.Add(sc);
        return (db, sc);
    }

    private static Simulation Start(ContentDatabase db, ScenarioDef sc)
    {
        var sim = ScenarioSystem.Create(db, sc, 4);
        sim.Ai.Enabled = false;
        sim.Step();
        return sim;
    }

    private static BossPhaseDef Phase(string id, string name, double hpBelow) => new()
    {
        Id = id, Name = name, When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = hpBelow },
    };

    [Fact]
    public void T24_BelowTheThreshold_TheSecondPhaseOnce_HpApCooldownsKept()
    {
        var boss = new BossDef { IsBoss = true, Title = "Тест", Phases = { Phase("one", "Начало", 1.01), Phase("two", "Вторая", 0.5) } };
        boss.Phases[1].Actions.Add(new TriggerActionDef { Type = TriggerActionType.ApplyStatus, Status = "burning", Target = ActionTarget.Self });
        var (db, sc) = Scene(boss);
        Assert.Empty(BossPhases.Check(db, boss).Where(p => p.Error));
        var sim = Start(db, sc);
        var u = sim.Units.First(x => x.Tag == "boss1");
        Assert.Equal("Начало", sim.Scenario.PhaseOf(u));

        u.CooldownUntil["quake"] = sim.Tick + 1000;
        u.Ap = 1.5;
        u.Hp = u.HpMax * 0.4;
        double hp = u.Hp, ap = u.Ap;
        sim.Step();
        Assert.Equal("Вторая", sim.Scenario.PhaseOf(u));
        Assert.Equal(2, sim.Scenario.PhaseEntries(u));
        Assert.True(u.CooldownUntil.ContainsKey("quake"));      // cooldowns kept
        Assert.True(u.Ap >= ap - 1e-9 && u.Ap < u.ApMax);        // AP kept (only its own regeneration)
        Assert.True(u.Hp <= hp + 1e-9);                          // no heal (burning may hurt)
        Assert.NotNull(u.FindStatus("burning"));
        Assert.Contains(sim.Log.Entries, e => e.Text.StartsWith("фаза «Вторая» (Орк")); // the action, with the phase as its source

        // Once: healed above the threshold and wounded again — no second time.
        u.Hp = u.HpMax;
        for (int i = 0; i < 5; i++) sim.Step();
        u.Hp = u.HpMax * 0.3;
        for (int i = 0; i < 5; i++) sim.Step();
        Assert.Equal(2, sim.Scenario.PhaseEntries(u));
        Assert.Equal(2, sim.Scenario.PhaseNumberOf(u));
    }

    [Fact]
    public void ExplicitRules_ResetCooldowns_RefillAp_Heal_ClearStatuses()
    {
        var two = Phase("two", "Вторая", 0.5);
        two.ResetCooldowns = true;
        two.RefillAp = true;
        two.Heal = 0.25;
        two.ClearStatuses.Add("burning");
        var (db, sc) = Scene(new BossDef { IsBoss = true, Phases = { two } });
        var sim = Start(db, sc);
        var u = sim.Units.First(x => x.Tag == "boss1");
        sim.Statuses.Apply(u, db.Get<StatusDef>("burning")!, 10, 30, -1);
        u.CooldownUntil["quake"] = sim.Tick + 1000;
        u.Ap = 0;
        u.Hp = u.HpMax * 0.4;
        sim.Step();
        Assert.Equal("Вторая", sim.Scenario.PhaseOf(u));
        Assert.Empty(u.CooldownUntil);
        Assert.Equal(u.ApMax, u.Ap, 6);
        Assert.True(u.Hp > u.HpMax * 0.6);
        Assert.Null(u.FindStatus("burning"));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("перезарядки сброшены — фаза «Вторая»"));
    }

    [Fact]
    public void Priority_From_AndARepeatablePairOfStances()
    {
        // A big hit passes 50 % and 30 % in one tick: the higher priority goes first, then the next transition.
        var a = Phase("a", "Ярость", 0.5);
        var b = Phase("b", "Отчаяние", 0.3);
        a.From.Add(BossPhases.Start);
        b.From.Add(BossPhases.Start);
        b.From.Add("a");
        b.Priority = 5;
        var (db, sc) = Scene(new BossDef { IsBoss = true, Phases = { a, b } });
        var sim = Start(db, sc);
        var u = sim.Units.First(x => x.Tag == "boss1");
        u.Hp = u.HpMax * 0.2;
        sim.Step();
        Assert.Equal("Отчаяние", sim.Scenario.PhaseOf(u)); // priority 5 first; «Ярость» cannot follow (it comes only from the start)
        Assert.Equal(1, sim.Scenario.PhaseEntries(u));

        // Stances: «Щит» while the flag is set, «Меч» while it is not; each may repeat.
        var shield = new BossPhaseDef { Id = "shield", Name = "Щит", CanRepeat = true, From = { BossPhases.Start, "sword" }, When = new ConditionDef { Type = ConditionType.FlagSet, Flag = "stance" } };
        var sword = new BossPhaseDef { Id = "sword", Name = "Меч", CanRepeat = true, From = { "shield" }, When = new ConditionDef { Type = ConditionType.Not, Children = { new ConditionDef { Type = ConditionType.FlagSet, Flag = "stance" } } } };
        var (db2, sc2) = Scene(new BossDef { IsBoss = true, Phases = { shield, sword } });
        sc2.Triggers.Add(new TriggerDef { Name = "on", Event = ScenarioEventType.Timer, Interval = 1, Once = false, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "stance" } } });
        sc2.Triggers.Add(new TriggerDef { Name = "off", Event = ScenarioEventType.Timer, Interval = 2, Once = false, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.ClearFlag, Flag = "stance" } } });
        var sim2 = Start(db2, sc2);
        var u2 = sim2.Units.First(x => x.Tag == "boss1");
        while (sim2.Scenario.BattleTime < 8.5) sim2.Step();
        Assert.True(sim2.Scenario.PhaseEntries(u2) >= 4, $"переходов {sim2.Scenario.PhaseEntries(u2)}");
    }

    [Fact]
    public void TheCheck_Unreachable_UnknownFrom_RingWithoutExit_Conflict_Skills()
    {
        var db = TestUtil.Shipped();
        var b = new BossDef
        {
            IsBoss = true,
            Phases =
            {
                Phase("one", "Первая", 0.8),
                new BossPhaseDef { Id = "x", Name = "Остров А", From = { "y" } },
                new BossPhaseDef { Id = "y", Name = "Остров Б", From = { "x" } },
                new BossPhaseDef { Id = "z", Name = "Ошибка", From = { "nope" } },
            },
        };
        var problems = BossPhases.Check(db, b);
        Assert.Contains(problems, p => p.Error && p.Phase == "x" && p.Message.Contains("недостижима"));
        Assert.Contains(problems, p => p.Error && p.Phase == "y" && p.Message.Contains("недостижима"));
        Assert.Contains(problems, p => p.Error && p.Message.Contains("неизвестной фазы «nope»"));

        // Two stances passing to each other for ever, nothing leads out — a warning; with a way out — none.
        var ring = new BossDef
        {
            IsBoss = true,
            Phases =
            {
                new BossPhaseDef { Id = "a", Name = "А", CanRepeat = true, From = { BossPhases.Start, "b" } },
                new BossPhaseDef { Id = "b", Name = "Б", CanRepeat = true, From = { "a" } },
            },
        };
        Assert.Contains(BossPhases.Check(db, ring), p => !p.Error && p.Message.StartsWith("цикл фаз без выхода"));
        ring.Phases.Add(new BossPhaseDef { Id = "end", Name = "Конец", From = { "b" }, Priority = 1, When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.2 } });
        Assert.DoesNotContain(BossPhases.Check(db, ring), p => p.Message.StartsWith("цикл фаз без выхода"));

        // Two transitions from the start with one priority and the same kind of condition — a conflict; a priority settles it.
        var conflict = new BossDef { IsBoss = true, Phases = { Phase("p", "П", 0.5), Phase("q", "Q", 0.3) } };
        conflict.Phases[0].From.Add(BossPhases.Start);
        conflict.Phases[1].From.Add(BossPhases.Start);
        Assert.Contains(BossPhases.Check(db, conflict), p => p.Message.StartsWith("конфликт переходов"));
        conflict.Phases[1].Priority = 2;
        Assert.DoesNotContain(BossPhases.Check(db, conflict), p => p.Message.StartsWith("конфликт переходов"));

        // Skills: unknown — an error; not allowed — a warning; allowed — nothing.
        var sk = new BossDef { IsBoss = true, Phases = { Phase("s", "С", 0.5) } };
        sk.Phases[0].Actions.Add(new TriggerActionDef { Type = TriggerActionType.AddSkill, Skill = "meteor" });
        sk.Phases[0].Actions.Add(new TriggerActionDef { Type = TriggerActionType.AddSkill, Skill = "no_such_skill" });
        var sp = BossPhases.Check(db, sk);
        Assert.Contains(sp, p => p.Error && p.Message.Contains("навыка «no_such_skill» нет"));
        Assert.Contains(sp, p => !p.Error && p.Message.Contains("«meteor» не в списке разрешённых"));
        sk.AllowedSkills.Add("meteor");
        Assert.DoesNotContain(BossPhases.Check(db, sk), p => p.Message.Contains("«meteor»"));

        // The shipped titan is clean.
        Assert.Empty(BossPhases.Check(db, db.Unit("titan")!.Boss));
    }

    [Fact]
    public void AProfileOnAUnitOfTheScenario_TheScheme_AndTheRunOfAPhaseWithASeed()
    {
        var boss = new BossDef { IsBoss = true, Phases = { Phase("one", "Своя", 0.9) } };
        var (db, sc) = Scene(boss, onTemplate: false);
        Assert.False(db.Unit("orc_brute")!.Boss.IsBoss); // the template stays as it was
        var sim = Start(db, sc);
        var u = sim.Units.First(x => x.Tag == "boss1");
        Assert.True(u.Boss.IsBoss);
        u.Hp = u.HpMax * 0.5;
        sim.Step();
        Assert.Equal("Своя", sim.Scenario.PhaseOf(u));
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Path.StartsWith("Units[0].Boss"));

        var titan = db.Unit("titan")!;
        var scheme = BossPhases.Scheme(titan.Boss);
        Assert.Equal(titan.Boss.Phases.Count + 1, scheme.Nodes.Count);
        Assert.Equal(titan.Boss.Phases.Count, scheme.Links.Count);

        // «Прогон фазы»: the titan starts in «Ярость» at once; the same seed — the same battle.
        var (tdb, tsc) = BossPhases.Sandbox(db, titan, 1, 77);
        Assert.Null(db.Get<ScenarioDef>("phase_sandbox"));
        string Run()
        {
            var s = ScenarioSystem.Create(tdb, tsc, tsc.Seed);
            for (int i = 0; i < 60; i++) s.Step();
            Assert.Equal("Ярость", s.Scenario.PhaseOf(s.Units.First(x => x.Tag == "boss")));
            return SimDigest.Of(s).Hash;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void T25_AnInterruptedTelegraphedAttack_TakesItsWarningAway_AndHitsNobody()
    {
        var db = TestUtil.Shipped();
        var map = TestUtil.Field(10, 5);
        map.Id = "tele_field";
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "tele_test", Name = "Телеграф", Map = map.Id,
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 9999 } },
            Defeat = new List<ConditionDef>(),
            Units =
            {
                new ScenarioUnitDef { Unit = "orc_brute", Faction = "enemy", C = new HexCoord(5, 2), Facing = HexDirection.W, Tag = "boss1" },
                new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(4, 2), Facing = HexDirection.E },
            },
            Telegraphs = { new TelegraphRuleDef { Skill = "strike", Caster = "boss1", MinLead = 2, Text = "Удар!" } },
        };
        db.Add(sc);
        Simulation Make()
        {
            var s = ScenarioSystem.Create(db, sc, 4);
            s.Ai.Enabled = false;
            foreach (var x in s.Units) s.AutoAttacks.DisabledUnits.Add(x.Id); // only the cast of the test hits
            return s;
        }
        var sim = Make();
        var orc = sim.Units.First(x => x.Tag == "boss1");
        var knight = sim.Units.First(x => x.Def.Id == "knight");
        Assert.Null(sim.Enqueue(orc, Command.Skill("strike", null, knight.Id)));
        sim.Step();
        var cast = orc.Casting!;
        Assert.NotNull(cast);
        Assert.NotNull(cast.Rule);
        Assert.True(cast.Telegraphed);
        Assert.Equal(sim.TicksFor(2), cast.ReleaseTick - cast.StartTick); // the warning lasts at least 2 s
        Assert.Contains(sim.Skills.ActiveCasts, c => c == cast);
        Assert.NotEmpty(cast.TelegraphCells);

        for (int i = 0; i < sim.TicksFor(0.5); i++) sim.Step();
        double hp = knight.Hp;
        sim.Skills.Interrupt(orc, InterruptCause.Control, "оглушение");
        Assert.True(cast.Interrupted);
        Assert.DoesNotContain(sim.Skills.ActiveCasts, c => c == cast); // the warning is gone with the cast
        bool Struck(Simulation s, Unit by) => s.Log.Entries.Any(e => e.SourceUnit == by.Id && e.Type is SimEventType.CastReleased or SimEventType.Hit or SimEventType.Miss or SimEventType.Damage);
        for (int i = 0; i < sim.TicksFor(3); i++) sim.Step();
        Assert.Equal(hp, knight.Hp, 6);
        Assert.False(Struck(sim, orc)); // no release, no roll, no damage: nothing without the combat engine

        // Not interrupted: the hit comes from the skill at the end of the warning.
        var sim2 = Make();
        var orc2 = sim2.Units.First(x => x.Tag == "boss1");
        var knight2 = sim2.Units.First(x => x.Def.Id == "knight");
        Assert.Null(sim2.Enqueue(orc2, Command.Skill("strike", null, knight2.Id)));
        sim2.Step();
        var cast2 = orc2.Casting!;
        for (int i = 0; i < sim2.TicksFor(1.5); i++) sim2.Step();
        Assert.False(Struck(sim2, orc2));                              // not yet: the warning lasts 2 s, not the skill's own 0.35 s
        for (int i = 0; i < sim2.TicksFor(1.5); i++) sim2.Step();
        Assert.True(cast2.Released);
        Assert.True(Struck(sim2, orc2));                               // the skill rolls and hits (or misses) as always
    }
}
