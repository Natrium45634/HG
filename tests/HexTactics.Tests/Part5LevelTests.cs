using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage E — the modes of the constructor (ТЗ 21, T17–T19; docs/DECISIONS_5.md §88).</summary>
public class Part5LevelTests
{
    [Fact]
    public void T17_Manual_MoreHealthRaisesOrKeepsTheLevel_AndNothingElseChanges()
    {
        var db = TestUtil.Shipped();
        var knight = db.Unit("knight")!;
        var before = PowerModel.Evaluate(db, knight);
        var stats = knight.Stats.ToDictionary(k => k.Key, k => k.Value);
        knight.Stats[Stats.HpMax] = knight.Stats.GetValueOrDefault(Stats.HpMax, db.StatDefault(Stats.HpMax)) + 60;
        var after = PowerModel.Evaluate(db, knight);
        Assert.True(after.Level >= before.Level);
        Assert.All(stats.Where(k => k.Key != Stats.HpMax), k => Assert.Equal(k.Value, knight.Stats[k.Key]));
        // The level is only computed: no build record, nothing «подогнано».
        Assert.Null(knight.Build);
        Assert.Null(LevelFit.BuildNote(db, knight, after.Level));
    }

    [Fact]
    public void T18_ScaleStatsToLevel30_OnlyHealthAttackArmor_NoNewSkills_DiffAndReach()
    {
        var db = TestUtil.Shipped();
        var knight = db.Unit("knight")!;
        var profile = new ScalingProfileDef
        {
            Id = "hp_atk_armor", Name = "Только здоровье, атака, броня",
            Params =
            {
                new ScaleParamDef { Stat = Stats.HpMax, Weight = 1, MaxChange = 0 },
                new ScaleParamDef { Stat = Stats.BaseDamage, Weight = 1, MaxChange = 0 },
                new ScaleParamDef { Stat = Stats.Armor, Weight = 0.5, MaxChange = 0 },
            },
        };
        db.Add(profile);
        var fit = LevelFit.Fit(db, knight, 30, profile, FitVariant.Stats);
        Assert.Equal(knight.Skills, fit.Unit.Skills);
        Assert.Equal(knight.BasicAttack, fit.Unit.BasicAttack);
        Assert.Equal(knight.Passives, fit.Unit.Passives);
        var keys = fit.Changes.Where(c => c.Key != "role").Select(c => c.Key).ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.Contains(k, new[] { "stat:" + Stats.HpMax, "stat:" + Stats.BaseDamage, "stat:" + Stats.Armor }));
        Assert.All(fit.Changes.Where(c => c.Key != "role"), c => Assert.True(c.To > c.From, $"{c.Label} должно вырасти"));
        // Reached — or said why not; never silently.
        Assert.True(fit.Reached || fit.Message.Contains("недостижим"), fit.Message);
        if (fit.Reached) Assert.InRange(fit.LevelAfter, 29.5, 30.5);
        // The build is recorded on the copy; the source is not changed.
        Assert.Null(knight.Build);
        Assert.Equal(BuildMode.ScaleStats, fit.Unit.Build!.Mode);
        Assert.Equal(30, fit.Unit.Build.TargetLevel);
        Assert.Equal("hp_atk_armor", fit.Unit.Build.Profile);
    }

    private static ScalingProfileDef Pool(ContentDatabase db)
    {
        db.Add(new PassiveDef { Id = "far_future", Name = "Далёкое будущее", StatMods = { new StatModDef { Stat = Stats.Armor, Add = 50 } } });
        var p = new ScalingProfileDef
        {
            Id = "knight_pool", Name = "Пул рыцаря",
            Upgrades =
            {
                new UpgradeOptionDef { Kind = UpgradeKind.Stat, Stat = Stats.HpMax, Step = 20, Weight = 2, MaxPicks = 10 },
                new UpgradeOptionDef { Kind = UpgradeKind.Stat, Stat = Stats.BaseDamage, Step = 2, Weight = 1, MaxPicks = 10 },
                new UpgradeOptionDef { Kind = UpgradeKind.Skill, Skill = "no_such_skill", Weight = 100 },                 // missing — never
                new UpgradeOptionDef { Kind = UpgradeKind.Passive, Passive = "far_future", Weight = 100, MinLevel = 50 },  // above the target — never
                new UpgradeOptionDef { Kind = UpgradeKind.Stat, Stat = Stats.Armor, Step = 1, Weight = 0 },               // weight 0 — never
            },
        };
        db.Add(p);
        return p;
    }

    [Fact]
    public void T19_AutoBuildBudget_SameTemplateSeedAndProfile_SameBuild_UnavailableNeverChosen()
    {
        var db = TestUtil.Shipped();
        var knight = db.Unit("knight")!;
        var profile = Pool(db);
        double target = System.Math.Round(PowerModel.Evaluate(db, knight).Level) + 3;
        var a = LevelFit.AutoBuild(db, knight, target, profile, 42);
        var b = LevelFit.AutoBuild(db, knight, target, profile, 42);
        Assert.Equal(ContentJson.Serialize(a.Unit), ContentJson.Serialize(b.Unit));
        Assert.True(a.Reached, a.Message);
        Assert.DoesNotContain("no_such_skill", a.Unit.Skills);
        Assert.DoesNotContain("far_future", a.Unit.Passives);
        Assert.Equal(knight.Stats.GetValueOrDefault(Stats.Armor, db.StatDefault(Stats.Armor)), a.Unit.Stats.GetValueOrDefault(Stats.Armor, db.StatDefault(Stats.Armor)));
        // Only the pool: the skills are the same.
        Assert.Equal(knight.Skills, a.Unit.Skills);
        // The record: mode, seed, target and computed, the profile and its version.
        var rec = a.Unit.Build!;
        Assert.Equal(BuildMode.AutoBuild, rec.Mode);
        Assert.Equal(42UL, rec.Seed);
        Assert.Equal(target, rec.TargetLevel);
        Assert.Equal("knight_pool", rec.Profile);
        Assert.Equal(profile.Version, rec.ProfileVersion);
        Assert.InRange(rec.ComputedLevel, target - 0.5, target + 1.5);
        Assert.NotEmpty(rec.Choices);
        // Another seed may choose otherwise, but is reproducible as well.
        var c = LevelFit.AutoBuild(db, knight, target, profile, 7);
        Assert.Equal(ContentJson.Serialize(c.Unit), ContentJson.Serialize(LevelFit.AutoBuild(db, knight, target, profile, 7).Unit));
        // In the variants of the dialog when the profile has a pool.
        Assert.Contains(LevelFit.Variants(db, knight, target, profile, null, 42), v => v.Mode == BuildMode.AutoBuild && v.Seed == 42);
        // The note: a newer profile, a level away from the target — shown, not hidden.
        var built = a.Unit;
        profile.Version++;
        built.Stats[Stats.HpMax] = 1;
        var note = LevelFit.BuildNote(db, built, PowerModel.Evaluate(db, built).Level)!;
        Assert.Contains("профиль изменён", note);
        Assert.Contains("не совпадает с целевым", note);
    }

    [Fact]
    public void AutoBuild_AnEmptyPoolSaysSo()
    {
        var db = TestUtil.Shipped();
        var p = new ScalingProfileDef { Id = "tiny", Name = "Маленький пул", Upgrades = { new UpgradeOptionDef { Stat = Stats.HpMax, Step = 1, MaxPicks = 2 } } };
        db.Add(p);
        var r = LevelFit.AutoBuild(db, db.Unit("knight")!, 40, p, 1);
        Assert.False(r.Reached);
        Assert.Contains("пул исчерпан", r.Message);
        Assert.Equal(2, r.Changes.Single(c => c.Key == "stat:" + Stats.HpMax).To - r.Changes.Single(c => c.Key == "stat:" + Stats.HpMax).From, 6);
    }
}
