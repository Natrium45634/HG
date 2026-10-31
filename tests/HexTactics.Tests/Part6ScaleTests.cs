using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.10 — scales: sanity, morale (ТЗ ч. 6, 18; T33–T40; docs/DECISIONS_6.md §104).</summary>
public class Part6ScaleTests
{
    private static ScaleProfileDef Morale(ContentDatabase db) => db.Get<ScaleProfileDef>("morale")!;

    /// <summary>A field: two knights of the player with a scale, a goblin at a distance; the AI and auto-attacks off.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene(string profile = "morale", System.Action<ScaleProfileDef>? edit = null, bool goblin = true)
    {
        var db = TestUtil.Shipped();
        var p = ContentJson.Clone(db.Get<ScaleProfileDef>(profile)!);
        p.Recovery = 0;
        edit?.Invoke(p);
        db.Table(DefCategory.Scale).Replace(p);
        db.Unit("knight")!.Scale = new ScaleUseDef { Profile = profile };
        var map = TestUtil.Field(14, 6);
        map.Id = "scale_field";
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "scale_test", Name = "Шкала", Map = map.Id,
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 9999 } },
            Defeat = new List<ConditionDef>(),
            Units =
            {
                new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(2, 2), Facing = HexDirection.E, Tag = "a" },
                new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(3, 2), Facing = HexDirection.E, Tag = "b" },
            },
        };
        if (goblin) sc.Units.Add(new ScenarioUnitDef { Unit = "goblin", Faction = "enemy", C = new HexCoord(8, 2), Facing = HexDirection.W, Tag = "g" });
        db.Add(sc);
        return (db, sc);
    }

    private static Simulation Start(ContentDatabase db, ScenarioDef sc, ulong seed = 4)
    {
        var sim = ScenarioSystem.Create(db, sc, seed);
        sim.Ai.Enabled = false;
        foreach (var u in sim.Units) sim.AutoAttacks.DisabledUnits.Add(u.Id);
        sim.Step();
        return sim;
    }

    [Fact]
    public void T33_ByDefault_MinusTwoHundred_Zero_PlusTwoHundred()
    {
        var db = TestUtil.Shipped();
        var p = Morale(db);
        Assert.Equal((-200.0, 200.0, 0.0), (p.Min, p.Max, p.Start));
        Assert.Empty(ScaleProfiles.Check(db, p));
        Assert.Equal(-200, ScaleSystem.Shift(p, 0, -1000));
        Assert.Equal(200, ScaleSystem.Shift(p, 0, 1000));
        Assert.Equal(0, ScaleSystem.Shift(p, -50, 50));
        var (sdb, sc) = Scene();
        var sim = Start(sdb, sc);
        var a = sim.Units.First(u => u.Tag == "a");
        Assert.Equal(0, sim.Scales.Of(a)!.Value);
        Assert.Null(sim.Scales.Of(sim.Units.First(u => u.Tag == "g"))); // no scale — no calculation
    }

    [Fact]
    public void T34_AnotherRange_MinusFiveHundredToEightHundred_ClampsOnItsBounds()
    {
        var db = TestUtil.Shipped();
        var p = ContentJson.Clone(Morale(db));
        p.Min = -500;
        p.Max = 800;
        Assert.Empty(ScaleProfiles.Check(db, p).Where(x => x.Error));
        Assert.Equal(-500, ScaleSystem.Shift(p, 0, -10000));
        Assert.Equal(800, ScaleSystem.Shift(p, 0, 10000));
        Assert.Equal(-300, ScaleSystem.Shift(p, 0, -300)); // beyond the default −200, within its own
        p.Min = 900;
        Assert.Contains(ScaleProfiles.Check(db, p), x => x.Error && x.Message.Contains("меньше максимума"));
    }

    [Fact]
    public void T35_OverflowAndSoftCap_CrossTheBaseBounds_NeverTheHardLimits()
    {
        var db = TestUtil.Shipped();
        var over = ContentJson.Clone(Morale(db));
        over.Mode = ScaleLimitMode.Overflow;
        over.HardMin = -300;
        over.HardMax = 300;
        Assert.Equal(240, ScaleSystem.Shift(over, 190, 50));
        Assert.Equal(300, ScaleSystem.Shift(over, 0, 1e9));
        Assert.Equal(-300, ScaleSystem.Shift(over, 0, -1e9));

        var soft = ContentJson.Clone(db.Get<ScaleProfileDef>("madness")!);
        Assert.Equal(ScaleLimitMode.SoftCap, soft.Mode);
        double v = ScaleSystem.Shift(soft, 190, 50);
        Assert.InRange(v, 200.0001, 239.999); // past the soft bound, but slower
        double far = ScaleSystem.Shift(soft, 0, 1e9);
        Assert.InRange(far, 200.0001, 300);
        Assert.Equal(-300, ScaleSystem.Shift(soft, 0, -1e12) < -300 ? -301 : System.Math.Max(-300, ScaleSystem.Shift(soft, 0, -1e12)));
        // No NaN or infinity ever.
        Assert.Equal(10, ScaleSystem.Shift(soft, 10, double.NaN));
        Assert.Equal(10, ScaleSystem.Shift(soft, 10, double.PositiveInfinity));
        soft.HardMin = -100;
        Assert.Contains(ScaleProfiles.Check(db, soft), x => x.Error && x.Message.StartsWith("мягкие границы"));
    }

    [Fact]
    public void T36_ALowerScale_DrivesTheCurvesOfAccuracyAndPower_SwitchedOff_TheyGo()
    {
        var (db, sc) = Scene(goblin: false);
        var sim = Start(db, sc);
        var a = sim.Units.First(u => u.Tag == "a");
        double acc0 = a.Stat(Stats.Accuracy), pow0 = a.Stat(Stats.Power);
        sim.Scales.Change(a, -40, "тест", null); // above every band: only the curves act
        sim.Step();
        Assert.Equal(acc0 - 0.03, a.Stat(Stats.Accuracy), 6); // (−200; −0.15)…(0; 0) at −40
        Assert.True(a.Stat(Stats.Power) < pow0);
        Assert.Contains(a.InstanceMods, m => m.Source == ScaleSystem.ModSource + "ДУХ");

        var p = db.Get<ScaleProfileDef>("morale")!;
        foreach (var c in p.Curves) c.Enabled = false;
        sim.Step();
        Assert.Equal(acc0, a.Stat(Stats.Accuracy), 6);
        Assert.DoesNotContain(a.InstanceMods, m => m.Source == ScaleSystem.ModSource + "ДУХ");
        Assert.Equal(pow0, a.Stat(Stats.Power), 6);
    }

    [Fact]
    public void T37_ALowScale_IsABuffInOnePreset_AndADebuffInAnother()
    {
        var (db1, sc1) = Scene("morale", goblin: false);
        var s1 = Start(db1, sc1);
        var a1 = s1.Units.First(u => u.Tag == "a");
        double p1 = a1.Stat(Stats.Power);
        s1.Scales.Change(a1, -180, "тест", null);
        s1.Step();
        Assert.True(a1.Stat(Stats.Power) < p1); // morale: low — weaker

        var (db2, sc2) = Scene("madness", goblin: false);
        var s2 = Start(db2, sc2);
        var a2 = s2.Units.First(u => u.Tag == "a");
        double p2 = a2.Stat(Stats.Power);
        s2.Scales.Change(a2, -180, "тест", null);
        s2.Step();
        Assert.True(a2.Stat(Stats.Power) > p2 * 1.2); // madness: low — frenzy, stronger
        Assert.NotNull(a2.FindStatus("frenzy"));
    }

    [Fact]
    public void T38_Panic_OnItsCondition_NotRestartedEveryTick_EndsAndTheQueueGoesOn()
    {
        var (db, sc) = Scene(edit: p =>
        {
            var b = p.Bands.First(x => x.Status == "panic");
            b.Chance = 1;
            b.Duration = 3;
            b.Recheck = 0;
        });
        var sim = Start(db, sc);
        var a = sim.Units.First(u => u.Tag == "a");
        Assert.Null(sim.Enqueue(a, Command.Move(new HexCoord(2, 4))));
        Assert.Null(sim.Enqueue(a, Command.Turn(HexDirection.W)));
        var ids = a.Queue.Items.Select(c => c.Id).ToList();
        sim.Scales.Change(a, -150, "ужас", null);
        sim.Step();
        sim.Step();
        Assert.NotNull(a.FindStatus("panic"));
        Assert.True(a.Flags.Panic);
        Assert.True(sim.Commands.IsHeld(a));
        Assert.Equal(ids, sim.Commands.HeldOf(a).Select(c => c.Id));
        // A new order during the panic waits with the others.
        Assert.Null(sim.Enqueue(a, Command.Turn(HexDirection.E)));
        Assert.Equal(3, sim.Commands.HeldOf(a).Count);
        var waiting = sim.Commands.HeldOf(a).Select(c => c.Id).ToList();
        int Applied() => sim.Log.Entries.Count(e => e.Type == SimEventType.ScaleState && e.TargetUnit == a.Id && e.DamageType == "panic" && e.Text.Contains("от ДУХ"));
        for (int i = 0; i < sim.TicksFor(10) && sim.Commands.IsHeld(a); i++) sim.Step();
        // The panic is over: the flight cancelled, the orders set aside go on in their order.
        Assert.Null(a.FindStatus("panic"));
        Assert.False(sim.Commands.IsHeld(a));
        Assert.Equal(waiting, a.Queue.Items.Select(c => c.Id));
        for (int i = 0; i < sim.TicksFor(10); i++) sim.Step();
        Assert.Equal(1, Applied()); // still below the threshold, not restarted: only after leaving the band
        // Out of the band (beyond the hysteresis) and back: a new panic.
        sim.Scales.Change(a, 100, "ободрение", null);
        sim.Step();
        sim.Scales.Change(a, -100, "новый ужас", null);
        for (int i = 0; i < sim.TicksFor(10); i++) sim.Step();
        Assert.Equal(2, Applied());
    }

    [Fact]
    public void T39_TheSameAt1xAnd4x_AndAfterALoad()
    {
        var (db, sc) = Scene(edit: p => p.Recovery = 2);
        sc.Triggers.Add(new TriggerDef { Name = "ужас", Event = ScenarioEventType.Timer, Interval = 1, Once = true, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.ChangeScale, Amount = -130, Target = ActionTarget.UnitTag, UnitTag = "a" } } });
        var (snap, sim) = BattleSnapshot.Begin(db, sc, 9, autoPlayer: true);
        var rec = BattleReplay.Record(snap, sim);
        var (_, fast) = BattleSnapshot.Begin(db, sc, 9, autoPlayer: true);
        // 1×: tick by tick; 4×: the driver at its fastest speed over the same game time.
        for (int i = 0; i < sim.TicksFor(12); i++) sim.Step();
        var driver = new SimDriver(fast);
        driver.SetSpeedIndex(driver.Speeds.Count - 1);
        for (int guard = 0; fast.Tick < sim.Tick && guard < 100000; guard++)
        {
            driver.Paused = false; // an autopause stops the driver; the test goes on
            driver.Advance(System.Math.Min(fast.Dt * 0.5, (sim.Tick - fast.Tick) * fast.Dt / driver.Speed));
        }
        Assert.Equal(sim.Tick, fast.Tick);
        var a = sim.Units.First(u => u.Tag == "a");
        Assert.True(sim.Scales.Of(a)!.Value < -100, "шкала не изменилась — тест ничего не проверяет");
        Assert.Equal(sim.Scales.Of(a)!.Value, fast.Scales.Of(fast.Units.First(u => u.Tag == "a"))!.Value, 9);
        Assert.Equal(sim.Digest().Hash, fast.Digest().Hash);

        // Saved in the middle (bands, cooldowns and the RNG included), loaded, and on the same.
        var save = BattleReplay.FromJson(rec.SaveAt(snap, sim).ToJson())!;
        var res = save.Resume();
        Assert.True(res.Matches, res.Text);
        var loaded = res.Sim!;
        for (int i = 0; i < sim.TicksFor(10); i++)
        {
            sim.Step();
            loaded.Step();
        }
        Assert.Equal(sim.Digest().Hash, loaded.Digest().Hash);
        Assert.Equal(sim.Scales.Digest(), loaded.Scales.Digest());
    }

    [Fact]
    public void T40_TheDeathOfAnAlly_ChangesTheScaleExactlyOnce()
    {
        var (db, sc) = Scene(goblin: true);
        var sim = Start(db, sc);
        var a = sim.Units.First(u => u.Tag == "a");
        var b = sim.Units.First(u => u.Tag == "b");
        double before = sim.Scales.Of(a)!.Value;
        sim.Combat.Kill(b, null, "тест");
        sim.Combat.Kill(b, null, "тест ещё раз"); // reported twice — still one death
        for (int i = 0; i < 10; i++) sim.Step();
        Assert.Equal(before - 25, sim.Scales.Of(a)!.Value, 9);
        Assert.Single(sim.Log.Entries, e => e.Type == SimEventType.ScaleChanged && e.TargetUnit == a.Id && e.Text.Contains("гибель союзника"));
    }

    [Fact]
    public void Immunity_Script_SkillOperation_MissionModifier_Condition_AndTheForecast()
    {
        var (db, sc) = Scene(goblin: false);
        db.Unit("knight")!.Scale = new ScaleUseDef { Profile = "morale", Mode = ScaleUseMode.Immune };
        var sim = Start(db, sc);
        var a = sim.Units.First(u => u.Tag == "a");
        sim.Scales.Change(a, -100, "тест", null);
        Assert.Equal(0, sim.Scales.Of(a)!.Value); // shown, never changed

        var (db2, sc2) = Scene(goblin: false);
        sc2.Triggers.Add(new TriggerDef { Name = "ужас", Event = ScenarioEventType.Timer, Interval = 1, Once = true, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.ChangeScale, Amount = -40, Target = ActionTarget.UnitTag, UnitTag = "a" } } });
        var s2 = Start(db2, sc2);
        var a2 = s2.Units.First(u => u.Tag == "a");
        while (s2.Scenario.BattleTime < 1.5) s2.Step();
        Assert.Equal(-40, s2.Scales.Of(a2)!.Value, 9);
        var cond = new UnitConditionDef { Type = UnitConditionType.Scale, Subject = ConditionSubject.Self, Compare = CompareMode.AtMost, Value = -30 };
        Assert.True(s2.Conditions.Evaluate(cond, new ConditionContext(a2, null, a2.Pos, null)));
        s2.Combat.ApplyUnitEffect(new SkillEffectDef { Type = EffectType.ChangeScale, Amount = 15 }, a2, a2, a2.Pos, 1, "операция");
        Assert.Equal(-25, s2.Scales.Of(a2)!.Value, 9);

        // The forecast of the editor and the note of the estimate.
        var f = ScaleProfiles.Forecast(db.Get<ScaleProfileDef>("morale")!, -150);
        Assert.Contains(f.Bands, x => x.StartsWith("паника"));
        Assert.Contains(f.Mods, m => m.Stat == "accuracy" && m.Add < 0);
        db.Unit("knight")!.Scale = new ScaleUseDef { Profile = "morale" };
        Assert.Contains(PowerModel.Evaluate(db, db.Unit("knight")!).Notes, n => n.StartsWith("шкала «Боевой дух»: ожидаемо 0"));
        var calm = ContentJson.Clone(db.Get<ScaleProfileDef>("morale")!);
        calm.Rules.Clear();
        db.Table(DefCategory.Scale).Replace(calm);
        Assert.Contains(PowerModel.Evaluate(db, db.Unit("knight")!).Notes, n => n.Contains("недостижим"));
    }
}
