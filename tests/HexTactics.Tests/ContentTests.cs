using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Expr;
using HexTactics.Core.Hex;
using Xunit;

namespace HexTactics.Tests;

public class ContentTests
{
    [Fact]
    public void ShippedContent_LoadsWithoutErrors()
    {
        var res = ContentIO.Load(new FileSystemStorage(TestUtil.ContentDir, readOnly: true));
        var errors = res.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.True(res.Db.Table(DefCategory.Unit).Count >= 10);
        Assert.True(res.Db.Table(DefCategory.Skill).Count >= 20);
        Assert.True(res.Db.Table(DefCategory.Map).Count >= 3);
        Assert.Empty(res.Db.DisabledSkills);
        // Required terrain kinds exist.
        foreach (var t in new[] { "grass", "pit", "highland", "lowland", "shallow_water", "toxic_swamp", "wall", "rock", "crates", "altar" })
            Assert.NotNull(res.Db.Terrain(t));
    }

    [Fact]
    public void SaveLoad_RoundTrip_IsLossless()
    {
        var db = TestUtil.Shipped();
        var mem = new MemoryStorage();
        ContentIO.Save(db, mem);
        var back = ContentIO.Load(mem);
        Assert.False(back.HasErrors, string.Join("\n", back.Issues));
        var mem2 = new MemoryStorage();
        ContentIO.Save(back.Db, mem2);
        Assert.Equal(mem.Files.Keys.OrderBy(k => k), mem2.Files.Keys.OrderBy(k => k));
        foreach (var k in mem.Files.Keys) Assert.Equal(mem.Files[k], mem2.Files[k]);
    }

    [Fact]
    public void BrokenData_IsReported_NotSilentlyFixed()
    {
        var mem = new MemoryStorage();
        ContentIO.Save(TestUtil.Minimal(), mem);
        // Broken JSON in one file, missing reference and duplicate id in another, out-of-range value.
        mem.Files["passives.json"] = "{ this is not json";
        var units = mem.Files["units.json"];
        units = units.Replace("\"basicAttack\": \"hit\"", "\"basicAttack\": \"no_such_skill\"");
        mem.Files["units.json"] = units;
        var terrains = mem.Files["terrains.json"].Replace("\"moveCost\": 3", "\"moveCost\": -5");
        mem.Files["terrains.json"] = terrains;
        var dup = mem.Files["factions.json"].Replace("\"id\": \"neutral\"", "\"id\": \"enemy\"");
        mem.Files["factions.json"] = dup;
        var res = ContentIO.Load(mem);
        Assert.True(res.HasErrors);
        Assert.Contains(res.Issues, i => i.Source == "passives.json" && i.Message.Contains("JSON"));
        Assert.Contains(res.Issues, i => i.Message.Contains("no_such_skill"));
        Assert.Contains(res.Issues, i => i.Message.Contains("вне диапазона"));
        Assert.Contains(res.Issues, i => i.Message.Contains("Повторяющийся ID"));
        // The rest of the content is still loaded.
        Assert.NotNull(res.Db.Terrain("grass"));
    }

    [Fact]
    public void InvalidSkill_IsDisabled_WithDiagnostics()
    {
        var db = TestUtil.Minimal();
        db.Add(new SkillDef { Id = "bad", Name = "Плохой", Stages = new() { new SkillStageDef { Area = new AreaDef { Shape = AreaShape.Mask } } } });
        var issues = ContentValidator.Validate(db);
        Assert.True(db.DisabledSkills.ContainsKey("bad"));
        Assert.Contains(issues, i => i.Source == "Skill:bad" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Migration_FromV0_UpgradesFields()
    {
        var mem = new MemoryStorage();
        ContentIO.Save(TestUtil.Minimal(), mem);
        mem.Files["units.json"] = "[ { \"id\": \"old\", \"name\": \"Старый\", \"faction\": \"player\", \"hp\": 77, \"speed\": 3 } ]";
        mem.Files["maps/old_map.json"] = "{ \"id\": \"old_map\", \"name\": \"Старая\", \"cells\": [ { \"q\": 0, \"r\": 0, \"terrain\": \"grass\", \"height\": 1 }, { \"q\": 1, \"r\": 0, \"terrain\": \"grass\" } ] }";
        var res = ContentIO.Load(mem);
        var u = res.Db.Unit("old")!;
        Assert.Equal(77, u.Stats[Stats.HpMax]);
        Assert.Equal(3, u.Stats[Stats.MoveSpeed]);
        var m = res.Db.Get<MapDef>("old_map")!;
        Assert.Equal(2, m.Cells.Count);
        Assert.Equal(new HexCoord(0, 0), m.Cells[0].C);
        Assert.Equal(1, m.Cells[0].H);
        Assert.Contains(res.Issues, i => i.Severity == IssueSeverity.Info && i.Message.Contains("версии формата"));
    }

    [Fact]
    public void FutureVersion_IsRejected()
    {
        var mem = new MemoryStorage();
        ContentIO.Save(TestUtil.Minimal(), mem);
        mem.Files["terrains.json"] = "{ \"formatVersion\": 999, \"kind\": \"terrains\", \"items\": [] }";
        var res = ContentIO.Load(mem);
        Assert.Contains(res.Issues, i => i.Source == "terrains.json" && i.Message.Contains("более новой версией"));
    }

    [Fact]
    public void Rename_UpdatesReferences_AndDependenciesAreFound()
    {
        var db = TestUtil.Shipped();
        var refs = db.FindReferences(DefCategory.Skill, "fireball");
        Assert.Contains(refs, r => r.Owner?.Id == "mage");
        Assert.True(db.Rename(DefCategory.Skill, "fireball", "fire_orb"));
        Assert.Contains("fire_orb", db.Unit("mage")!.Skills);
        Assert.DoesNotContain(db.FindReferences(DefCategory.Skill, "fireball"), r => r.Id == "fireball");
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error);
        // Terrain rename updates map cells.
        Assert.True(db.Rename(DefCategory.Terrain, "grass", "meadow"));
        Assert.Contains(db.Get<MapDef>("valley")!.Cells, c => c.T == "meadow");
    }

    [Fact]
    public void Backup_IsWritten_BeforeOverwrite()
    {
        var mem = new MemoryStorage();
        var db = TestUtil.Minimal();
        ContentIO.Save(db, mem);
        db.Terrain("grass")!.MoveCost = 1.25;
        var written = ContentIO.Save(db, mem, "20260101_000000");
        Assert.Contains("terrains.json", written);
        Assert.True(mem.Files.ContainsKey("backups/20260101_000000/terrains.json"));
        Assert.DoesNotContain("1.25", mem.Files["backups/20260101_000000/terrains.json"]);
    }

    [Fact]
    public void ImportExport_SingleDefinition()
    {
        var db = TestUtil.Shipped();
        var json = ContentIO.ExportDef(db.Skill("meteor")!);
        var issues = new System.Collections.Generic.List<ContentIssue>();
        var d = ContentIO.ImportDef(json, issues) as SkillDef;
        Assert.NotNull(d);
        Assert.Equal(2, d!.Stages.Count);
        Assert.Equal("meteor_copy", ContentIO.UniqueId(db, DefCategory.Skill, "meteor"));
    }

    [Fact]
    public void MapAnalysis_FindsLockedAreas_AndUnreachableObjectives()
    {
        var db = TestUtil.Minimal();
        var map = MapDef.CreateRhombus("m", 9, 5, "grass");
        foreach (var c in map.Cells.Where(c => c.C.Q == 6)) c.T = "wall";
        map.Markers.Add(new MapMarkerDef { Id = "s", Type = MarkerType.PlayerStart, C = new HexCoord(1, 2) });
        map.Markers.Add(new MapMarkerDef { Id = "goal", Type = MarkerType.Objective, C = new HexCoord(8, 2), Label = "Цель" });
        var issues = MapAnalysis.Analyze(db, map);
        Assert.Contains(issues, i => i.Message.Contains("недостижима"));
        Assert.Contains(issues, i => i.Message.Contains("Запертая область"));
    }
}

public class ExprTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("2 ^ 3 ^ 2", 512)]
    [InlineData("-2 ^ 2", -4)]
    [InlineData("min(3, 1, 2) + max(1, 5)", 6)]
    [InlineData("clamp(5, 0, 1)", 1)]
    [InlineData("if(2 > 1, 10, 20)", 10)]
    [InlineData("10 / 0", 0)]
    [InlineData("round(2.5) + floor(1.9) + ceil(1.1)", 6)]
    public void Evaluates(string text, double expected)
    {
        Assert.Equal(expected, SafeExpression.Parse(text).Evaluate(new System.Collections.Generic.Dictionary<string, double>()), 9);
    }

    [Fact]
    public void Variables_Whitelist()
    {
        var e = SafeExpression.Parse("base + accuracy * 2", new[] { "base", "accuracy" });
        Assert.Equal(1.1, e.Evaluate(new System.Collections.Generic.Dictionary<string, double> { ["base"] = 0.9, ["accuracy"] = 0.1 }), 9);
        Assert.False(SafeExpression.TryParse("base + secret", new[] { "base" }, out _, out var err));
        Assert.Contains("secret", err);
    }

    [Theory]
    [InlineData("System.IO.File.Delete(1)")]
    [InlineData("exec(1)")]
    [InlineData("1 +")]
    [InlineData("((((((((((((((((((((((((((((((((((1))))))))))))))))))))))))))))))))))")]
    [InlineData("")]
    public void Rejects_UnsafeOrInvalid(string text)
    {
        Assert.False(SafeExpression.TryParse(text, null, out _, out _));
    }
}
