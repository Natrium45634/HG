using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage E — the cache of the estimate (T24), solo and pack (T20), the calibration (ТЗ 24); docs/DECISIONS_5.md §88.</summary>
public class Part5RatingTests
{
    [Fact]
    public void T24_ChangeAPassive_TheCacheIsInvalidated_AndRecomputed()
    {
        var db = TestUtil.Shipped();
        var leader = db.Unit("wolf_leader")!;
        var first = PowerCache.Evaluate(db, leader);
        var (h0, m0) = PowerCache.Stats(db);
        var again = PowerCache.Evaluate(db, leader);
        var (h1, m1) = PowerCache.Stats(db);
        Assert.Same(first, again);
        Assert.Equal(h0 + 1, h1);
        Assert.Equal(m0, m1);
        // An unrelated object changes: still the cached result.
        db.Unit("knight")!.Stats[Stats.HpMax] = 999;
        Assert.Same(first, PowerCache.Evaluate(db, leader));
        // A passive of its own changes: a new key, the estimate is computed again and differs.
        var strength = db.Get<PassiveDef>("pack_strength")!;
        strength.Modifiers[0].Value = 9;
        var changed = PowerCache.Evaluate(db, leader, new PowerContext { Kind = PowerContextKind.TypicalGroup, Allies = 3, AllyDefs = Enumerable.Repeat(db.Unit("wolf")!, 3).ToList() });
        var before = PowerModel.Evaluate(db, leader, new PowerContext { Kind = PowerContextKind.TypicalGroup, Allies = 3, AllyDefs = Enumerable.Repeat(db.Unit("wolf")!, 3).ToList() });
        Assert.Equal(before.Power, changed.Power, 9);
        var (_, m2) = PowerCache.Stats(db);
        Assert.True(m2 > m1);
        strength.Modifiers[0].Value = 5;
        var back = PowerCache.Evaluate(db, leader);
        Assert.Equal(first.Power, back.Power, 9);
        // The rules of the estimate and its category's rules are in the key as well.
        db.Rules.Power.TypicalAllies = 7;
        Assert.NotSame(first, PowerCache.Evaluate(db, leader));
    }

    [Fact]
    public void T20_SoloAndPack_DifferForAReason_TheSynergyIsBoundedPastTheCap()
    {
        var db = TestUtil.Shipped();
        var leader = db.Unit("wolf_leader")!;
        var wolf = db.Unit("wolf")!;
        PowerReport With(int n) => PowerModel.Evaluate(db, leader, new PowerContext { Kind = PowerContextKind.TypicalGroup, Allies = n, AllyDefs = Enumerable.Repeat(wolf, n).ToList() });
        var solo = PowerModel.Evaluate(db, leader);
        var pack = With(3);
        Assert.True(pack.Power > solo.Power, "в стае вожак сильнее");
        Assert.True(pack.S > 0 && solo.S == 0, "разница — синергия: аура и «за каждого»");
        // Past the caps («Сила стаи» — 10 волков, «Вой вожака» — гексы в радиусе 3) nothing more is added.
        var p40 = With(40).Power;
        var p100 = With(100).Power;
        Assert.Equal(p40, p100, 9);
        Assert.Contains(With(100).Notes, n => n.Contains("получателей не больше"));
        Assert.Equal(36, PassiveDef.AuraCapacity(db.Get<PassiveDef>("leader_howl")!));
    }

    [Fact]
    public void Calibration_DuelsOfTheReferences_ForecastAgainstTheWins_Deterministic()
    {
        var db = TestUtil.Shipped();
        var all = Calibration.DefaultSet(db);
        Assert.Equal(new[] { "knight", "sentinel", "wolf_scout", "archer", "priestess", "shaman", "skeleton", "wolf_leader", "wolf_leader" }, all.Select(r => r.Unit));
        var refs = all.Where(r => r.Unit is "knight" or "skeleton" || r.Pack > 0).ToList();
        var maps = db.All<MapDef>().Count();
        var a = Calibration.Run(db, refs, 4, 100, maxSeconds: 60);
        Assert.Equal(maps, db.All<MapDef>().Count()); // the author's project is not touched
        Assert.Equal(3, a.Rows.Count);
        Assert.All(a.Rows, r =>
        {
            Assert.Equal(4, r.Runs);
            Assert.InRange(r.Actual, 0, 1);
            Assert.InRange(r.Predicted, 0.0001, 0.9999);
        });
        // The pack beats a lone knight more often than not, and the forecast agrees.
        var packRow = a.Rows.Single(r => r.A == "базовый воин" && r.B == "волк-вожак со стаей");
        Assert.True(packRow.Predicted < 0.5);
        var b = Calibration.Run(db, refs, 4, 100, maxSeconds: 60);
        Assert.Equal(a.Rows.Select(r => r.Actual), b.Rows.Select(r => r.Actual));
        Assert.Contains("Калибровка оценки силы", a.ToText());
        Assert.Equal(4, ReportCsv.Parse(a.ToCsv()).Count);
    }
}
