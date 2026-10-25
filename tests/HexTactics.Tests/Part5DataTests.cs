using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 5, stage A (docs/DECISIONS_5.md §84): data and checks — fields of other versions, the summary of dependents
/// before a deletion (T03), renaming (T04).
/// </summary>
public class Part5DataTests
{
    private static string TempPack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hex_p5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(TestUtil.ContentDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(TestUtil.ContentDir, f);
            var to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        return dir;
    }

    [Fact]
    public void UnknownFields_OnEveryLevel_AreKept_Reported_AndWrittenBack()
    {
        var dir = TempPack();
        try
        {
            // A file of a «newer version»: a new field of a passive, of its modifier, and of the rules.
            var path = Path.Combine(dir, "passives.json");
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var item = root["items"]!.AsArray().First(i => i!["modifiers"]!.AsArray().Count > 0)!.AsObject();
            string id = item["id"]!.GetValue<string>();
            item["futureFlag"] = true;
            item["modifiers"]![0]!["futureCurve"] = new JsonObject { ["kind"] = "smooth", ["k"] = 2 };
            File.WriteAllText(path, root.ToJsonString());
            var rpath = Path.Combine(dir, "rules.json");
            var rroot = JsonNode.Parse(File.ReadAllText(rpath))!.AsObject();
            rroot["rules"]!["time"]!["futureTiming"] = 7;
            File.WriteAllText(rpath, rroot.ToJsonString());

            var res = ContentIO.Load(new FileSystemStorage(dir));
            Assert.False(res.HasErrors);
            var p = res.Db.Get<PassiveDef>(id)!;
            Assert.True(p.Extra!.ContainsKey("futureFlag"));
            Assert.True(p.Modifiers[0].Extra!.ContainsKey("futureCurve"));
            // The check names each field with its place.
            var warn = res.Issues.Single(i => i.Source == $"Passive:{id}" && i.Message.Contains("другой версии"));
            Assert.Contains($"{id}.futureFlag", warn.Message);
            Assert.Contains($"{id}.Modifiers[0].futureCurve", warn.Message);
            Assert.Contains(res.Issues, i => i.Source == "Правила" && i.Message.Contains("Time.futureTiming"));

            // Saved and loaded again: the fields are still there, unchanged.
            ContentIO.Save(res.Db, new FileSystemStorage(dir));
            var again = JsonNode.Parse(File.ReadAllText(path))!["items"]!.AsArray().First(i => i!["id"]!.GetValue<string>() == id)!;
            Assert.True(again["futureFlag"]!.GetValue<bool>());
            Assert.Equal("smooth", again["modifiers"]![0]!["futureCurve"]!["kind"]!.GetValue<string>());
            Assert.Equal(7, JsonNode.Parse(File.ReadAllText(rpath))!["rules"]!["time"]!["futureTiming"]!.GetValue<int>());
            // Drafts (clones) and the history keep them too.
            var clone = ContentJson.Clone(p);
            Assert.True(clone.Modifiers[0].Extra!.ContainsKey("futureCurve"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ShippedContent_HasNoUnknownFields()
    {
        var db = TestUtil.Shipped();
        Assert.Empty(ContentCompat.Check(db));
    }

    [Fact]
    public void RandomStreams_Version2_AiAndSpawnDoNotShiftTheBattleRolls_Version1_IsOneStream()
    {
        var db = TestUtil.Shipped();
        Assert.Equal(2, db.Rules.Time.SimVersion);
        var sc = db.All<ScenarioDef>().First();
        var a = ScenarioSystem.Create(db, sc, 42);
        var b = ScenarioSystem.Create(db, sc, 42);
        Assert.NotSame(a.Rng, a.AiRng);
        Assert.NotSame(a.Rng, a.SpawnRng);
        // Extra draws of the AI and the spawn (a changed profile, another group) leave the battle's rolls as they were.
        for (int i = 0; i < 100; i++)
        {
            b.AiRng.NextULong();
            b.SpawnRng.NextULong();
        }
        for (int i = 0; i < 50; i++) Assert.Equal(a.Rng.NextULong(), b.Rng.NextULong());
        var c = ScenarioSystem.Create(db, sc, 42);
        Assert.NotEqual(c.Rng.NextULong(), c.AiRng.NextULong());
        // The same seed still gives the same battle.
        var r1 = AutoBattle.Run(db, sc, 7, new AutoBattleOptions { MaxSeconds = 60 });
        var r2 = AutoBattle.Run(db, sc, 7, new AutoBattleOptions { MaxSeconds = 60 });
        Assert.Equal(ContentJson.Serialize(r1), ContentJson.Serialize(r2));
        // Rules version 1 (packs of 1.0): one stream, as before.
        db.Rules.Time.SimVersion = 1;
        var old = ScenarioSystem.Create(db, sc, 42);
        Assert.Same(old.Rng, old.AiRng);
        Assert.Same(old.Rng, old.SpawnRng);
        Assert.Equal(1, new RulesDef().Time.SimVersion);
    }

    [Fact]
    public void T03_DeletingAUsedTemplate_SaysWhereByKind()
    {
        var db = TestUtil.Shipped();
        // An enemy used in waves of several scenarios.
        var unit = db.All<UnitDef>().OrderByDescending(u => db.FindReferences(DefCategory.Unit, u.Id).Count(r => r.Path.Contains(".Waves["))).First();
        var refs = db.FindReferences(DefCategory.Unit, unit.Id);
        var text = ContentDependencies.Summary(refs);
        Assert.StartsWith("используется в ", text);
        Assert.Matches(@"\d+ (волне|волнах) \(\d+ сценари", text);
        Assert.Equal("нигде не используется", ContentDependencies.Summary(Array.Empty<ReferenceInfo>()));
        // Units in three waves of two scenarios and one passive.
        var fake = new[]
        {
            new ReferenceInfo(db.All<ScenarioDef>().First(), "a.Waves[0].Groups[0].Unit", DefCategory.Unit, unit.Id),
            new ReferenceInfo(db.All<ScenarioDef>().First(), "a.Waves[0].Groups[1].Unit", DefCategory.Unit, unit.Id),
            new ReferenceInfo(db.All<ScenarioDef>().First(), "a.Waves[1].Groups[0].Unit", DefCategory.Unit, unit.Id),
            new ReferenceInfo(db.All<ScenarioDef>().Skip(1).First(), "b.Waves[0].Groups[0].Unit", DefCategory.Unit, unit.Id),
            new ReferenceInfo(db.All<PassiveDef>().First(), "p.AuraTypes[0]", DefCategory.Unit, unit.Id),
        };
        Assert.Equal("используется в 3 волнах (2 сценария) и 1 пассиве", ContentDependencies.Summary(fake));
    }

    [Fact]
    public void T04_RenamingEveryTemplate_KeepsTheScenarioWorking()
    {
        var db = TestUtil.Shipped();
        var sc = db.All<ScenarioDef>().First(s => s.Waves.Count > 0);
        var before = AutoBattle.Run(db, sc, 11, new AutoBattleOptions { MaxSeconds = 90 });
        foreach (var d in db.AllDefs()) d.Name = "Переименовано: " + d.Name;
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error);
        var after = AutoBattle.Run(db, db.Get<ScenarioDef>(sc.Id)!, 11, new AutoBattleOptions { MaxSeconds = 90 });
        Assert.Equal(before.Outcome, after.Outcome);
        Assert.Equal(before.Ticks, after.Ticks);
        Assert.Equal(before.EnemiesKilled, after.EnemiesKilled);
    }

    [Fact]
    public void T25_CyclicPassives_StopWithTheChainNamed()
    {
        var db = TestUtil.Minimal();
        db.Add(new PassiveDef { Id = "thorns", Name = "Шипы", Trigger = PassiveTrigger.OnDamaged, Action = PassiveAction.EffectsOnSource, Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 1, Filter = TargetFilterDef.Everyone() } } });
        db.Add(new PassiveDef { Id = "spite", Name = "Злоба", Trigger = PassiveTrigger.OnDamaged, Action = PassiveAction.EffectsOnSource, Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "fire", Amount = 1, Filter = TargetFilterDef.Everyone() } } });
        db.Unit("soldier")!.Passives.Add("thorns");
        db.Unit("dummy")!.Passives.Add("spite");
        var sim = TestUtil.Sim(db, TestUtil.Field());
        var a = sim.Spawn(db.Unit("soldier")!, null, new HexCoord(2, 2), HexDirection.E);
        var b = sim.Spawn(db.Unit("dummy")!, null, new HexCoord(3, 2), HexDirection.W);
        sim.Combat.DealDamage(a, b, 1, "fire", null, "тест");
        sim.Reactions.Process();
        sim.Step();
        Assert.True(sim.Reactions.DroppedByRecursion > 0);
        var why = sim.Log.Entries.Concat(sim.DrainEvents()).Select(e => e.Text).FirstOrDefault(t => t.Contains("рекурсия"));
        Assert.NotNull(why);
        Assert.Contains("цепочка:", why);
        Assert.Contains("Злоба", why);
        Assert.Contains("Шипы", why);
        Assert.Contains("цепная реакция выключена", why);
    }
}
