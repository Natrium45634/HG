using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Content library (docs/DECISIONS_2.md §31): versions, history, pins, transactional saving.</summary>
public class LibraryTests
{
    private static ContentDatabase Db()
    {
        var db = ScenarioSetup.Db();
        db.Add(ScenarioSetup.Scenario());
        return db;
    }

    private static ContentDatabase Reload(IContentStorage storage)
    {
        var res = ContentIO.Load(storage);
        Assert.False(res.HasErrors, string.Join("\n", res.Issues.Where(i => i.Severity == IssueSeverity.Error)));
        new ContentLibrary(storage).LoadPins(res.Db);
        ContentValidator.Validate(res.Db);
        return res.Db;
    }

    /// <summary>Pack files without the library's own folders (backups, history) and the journal.</summary>
    private static Dictionary<string, string> PackFiles(MemoryStorage mem) => mem.Files
        .Where(kv => !kv.Key.StartsWith(ContentLibrary.BackupDir + "/", StringComparison.Ordinal) && kv.Key != ContentLibrary.JournalFile)
        .ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void Publish_IncreasesVersion_AndKeepsHistory()
    {
        var mem = new MemoryStorage();
        var lib = new ContentLibrary(mem);
        var published = Db();
        lib.Publish(published.Clone(), null);
        Assert.True(mem.Files.ContainsKey(ContentLibrary.ManifestFile));
        Assert.True(lib.CheckIntegrity().Ok);

        var draft = Reload(mem);
        draft.Skill("hit")!.Stages[0].Effects[0].Amount = 25;
        draft.Skill("hit")!.Name = "Сильный удар";
        var result = lib.Publish(draft, published);
        var change = Assert.Single(result.Changes);
        Assert.Equal((DefCategory.Skill, "hit", 1, 2), (change.Category, change.Id, change.OldVersion, change.NewVersion));
        // Direct users (units with the skill) and indirect ones (the scenario placing such a unit).
        Assert.Contains(change.Direct, r => r.Owner?.Id == "soldier");
        Assert.Contains(change.Indirect, d => d.Id == "test");

        var after = Reload(mem);
        Assert.Equal(2, after.Skill("hit")!.Version);
        Assert.Equal(new List<int> { 1 }, lib.Versions(DefCategory.Skill, "hit"));
        var v1 = Assert.IsType<SkillDef>(lib.LoadVersion(DefCategory.Skill, "hit", 1));
        Assert.Equal(10, v1.Stages[0].Effects[0].Amount);
        Assert.Equal("Удар", v1.Name);
        // Unchanged definitions keep their version.
        Assert.Equal(1, after.Unit("soldier")!.Version);
        Assert.True(lib.CheckIntegrity().Ok);
    }

    [Fact]
    public void KeepOldInstances_PinsUsages_ToThePreviousVersion()
    {
        var mem = new MemoryStorage();
        var lib = new ContentLibrary(mem);
        var published = Db();
        published.Add(new UnitDef
        {
            Id = "veteran", Name = "Ветеран", Faction = "player", BasicAttack = "hit",
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2, [Stats.DetectionRange] = 8 },
        });
        lib.Publish(published.Clone(), null);

        var draft = Reload(mem);
        draft.Skill("hit")!.Stages[0].Effects[0].Amount = 40;
        var change = Assert.Single(LibraryChangeOf(draft, published));
        // Keep the old instance for the veteran only; the soldier follows the new version.
        int pinned = ContentLibrary.PinUsages(draft, change, change.Direct.Where(r => r.Owner?.Id == "veteran"));
        Assert.Equal(1, pinned);
        Assert.Equal("hit@1", draft.Unit("veteran")!.BasicAttack);
        Assert.Equal("hit", draft.Unit("soldier")!.BasicAttack);
        lib.Publish(draft, published);

        var db = Reload(mem);
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error);
        Assert.Equal(40, db.Get<SkillDef>("hit")!.Stages[0].Effects[0].Amount);
        Assert.Equal(10, db.Get<SkillDef>("hit@1")!.Stages[0].Effects[0].Amount);
        Assert.Equal(40, db.Get<SkillDef>("hit@2")!.Stages[0].Effects[0].Amount);
        // The pinned reference is a usage of the skill too, but an update does not touch it.
        Assert.Contains(db.FindReferences(DefCategory.Skill, "hit"), r => r.Owner?.Id == "veteran");
        db.Skill("hit")!.Stages[0].Effects[0].Amount = 50;
        var next = Assert.Single(LibraryChangeOf(db, Reload(mem)));
        Assert.DoesNotContain(next.Direct, r => r.Owner?.Id == "veteran");

        // In battle the veteran hits with the old version, the soldier with the new one.
        var sc = db.Get<ScenarioDef>("test")!;
        sc.Units.Clear();
        sc.Units.Add(new ScenarioUnitDef { Unit = "veteran", C = new HexCoord(2, 2), Facing = HexDirection.E });
        sc.Units.Add(new ScenarioUnitDef { Unit = "dummy", C = new HexCoord(3, 2), Facing = HexDirection.W });
        var sim = ScenarioSystem.Create(db, sc);
        var veteran = sim.Units.First(u => u.Def.Id == "veteran");
        var dummy = sim.Units.First(u => u.Def.Id == "dummy");
        Assert.Equal(10, sim.Content.Get<SkillDef>(veteran.Def.BasicAttack)!.Stages[0].Effects[0].Amount);
        double before = dummy.Hp;
        Assert.Null(sim.Enqueue(veteran, Command.Attack(dummy.Id)));
        for (int i = 0; i < 60 && dummy.Hp == before; i++) sim.Step();
        Assert.InRange(before - dummy.Hp, 5, 15);
    }

    private static List<LibraryChange> LibraryChangeOf(ContentDatabase draft, ContentDatabase previous) => ContentLibrary.Changes(draft, previous);

    [Fact]
    public void MissingPinnedVersion_IsReported()
    {
        var db = Db();
        db.Unit("soldier")!.BasicAttack = "hit@7";
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("v7") && i.Message.Contains("истории"));
        db.Unit("soldier")!.BasicAttack = "hit@1"; // the current version is a valid pin
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Path.Contains("BasicAttack"));
    }

    [Theory]
    [InlineData("hit", "hit", null)]
    [InlineData("hit@3", "hit", 3)]
    [InlineData("a.b@12", "a.b", 12)]
    [InlineData("@3", "@3", null)]
    [InlineData("x@", "x@", null)]
    [InlineData("x@y", "x@y", null)]
    public void ContentRef_ParsesPins(string text, string id, int? version)
    {
        Assert.Equal((id, version), ContentRef.Parse(text));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(999)] // the manifest write (after every content file)
    public void CrashDuringPublication_IsRolledBack(int failAt)
    {
        var mem = new MemoryStorage();
        var published = Db();
        new ContentLibrary(mem).Publish(published.Clone(), null);
        var before = PackFiles(mem);

        var draft = Reload(mem);
        draft.Skill("hit")!.Stages[0].Effects[0].Amount = 33;
        draft.Terrain("grass")!.MoveCost = 1.5;
        draft.Get<MapDef>("corridor")!.Name = "Коридор 2";
        draft.Table(DefCategory.Scenario).Remove("test");
        int writes = 0;
        var lib = new ContentLibrary(mem)
        {
            BeforeWrite = rel =>
            {
                if (++writes == failAt || (failAt == 999 && rel == ContentLibrary.ManifestFile)) throw new IOException("сбой питания (имитация)");
            },
        };
        Assert.Throws<IOException>(() => lib.Publish(draft, published));
        Assert.True(mem.Files.ContainsKey(ContentLibrary.JournalFile));
        if (failAt > 1) Assert.NotEqual(before, PackFiles(mem)); // a half-written pack

        // Next start: the journal is found, the pack is rolled back.
        var report = new ContentLibrary(mem).RecoverIfNeeded();
        Assert.True(report.Recovered);
        Assert.Contains("восстановлен", report.Message);
        Assert.False(mem.Files.ContainsKey(ContentLibrary.JournalFile));
        Assert.Equal(before, PackFiles(mem));
        Assert.True(new ContentLibrary(mem).CheckIntegrity().Ok);
        var db = Reload(mem);
        Assert.Equal(10, db.Skill("hit")!.Stages[0].Effects[0].Amount);
        Assert.NotNull(db.Get<ScenarioDef>("test"));
        // Nothing to recover any more.
        Assert.False(new ContentLibrary(mem).RecoverIfNeeded().Recovered);
    }

    [Fact]
    public void CrashDuringFirstPublication_RemovesCreatedFiles()
    {
        var mem = new MemoryStorage();
        int writes = 0;
        var lib = new ContentLibrary(mem) { BeforeWrite = _ => { if (++writes == 4) throw new IOException("сбой"); } };
        Assert.Throws<IOException>(() => lib.Publish(Db(), null));
        Assert.True(new ContentLibrary(mem).RecoverIfNeeded().Recovered);
        Assert.Empty(PackFiles(mem));
    }

    [Fact]
    public void CrashRecovery_OnRealFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hextactics_lib_" + Guid.NewGuid().ToString("N"));
        try
        {
            var fs = new FileSystemStorage(dir);
            var published = Db();
            new ContentLibrary(fs).Publish(published.Clone(), null);
            string skills = File.ReadAllText(Path.Combine(dir, "skills.json"));

            var draft = ContentIO.Load(fs).Db;
            draft.Skill("hit")!.Stages[0].Effects[0].Amount = 77;
            draft.Terrain("grass")!.MoveCost = 2.5;
            var lib = new ContentLibrary(fs) { BeforeWrite = rel => { if (rel == "terrains.json") throw new IOException("сбой"); } };
            Assert.Throws<IOException>(() => lib.Publish(draft, published));
            Assert.True(File.Exists(Path.Combine(dir, ContentLibrary.JournalFile)));

            Assert.True(new ContentLibrary(fs).RecoverIfNeeded().Recovered);
            Assert.Equal(skills, File.ReadAllText(Path.Combine(dir, "skills.json")));
            Assert.True(new ContentLibrary(fs).CheckIntegrity().Ok);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Integrity_DetectsDamage_AndRestoresFromCopy()
    {
        var mem = new MemoryStorage();
        var lib = new ContentLibrary(mem);
        var published = Db();
        lib.Publish(published.Clone(), null);
        var draft = Reload(mem);
        draft.Skill("hit")!.Description = "v2";
        lib.Publish(draft, published);
        string goodSkills = mem.Files["skills.json"];
        string goodMap = mem.Files["maps/corridor.json"];

        mem.Files["skills.json"] = goodSkills.Replace("v2", "испорчено");
        mem.Files.Remove("maps/corridor.json");
        var report = lib.CheckIntegrity();
        Assert.False(report.Ok);
        Assert.Contains("skills.json", report.Damaged);
        Assert.Contains("maps/corridor.json", report.Missing);

        var restored = lib.RestoreFromBackup(report.Damaged.Concat(report.Missing));
        Assert.Equal(2, restored.Count);
        Assert.Equal(goodSkills, mem.Files["skills.json"]);
        Assert.Equal(goodMap, mem.Files["maps/corridor.json"]);
        Assert.True(lib.CheckIntegrity().Ok);
    }

    [Fact]
    public void DamagedJournal_IsRemoved_WithMessage()
    {
        var mem = new MemoryStorage();
        new ContentLibrary(mem).Publish(Db(), null);
        mem.Files[ContentLibrary.JournalFile] = "{ это не json";
        var r = new ContentLibrary(mem).RecoverIfNeeded();
        Assert.False(r.Recovered);
        Assert.Contains("повреждён", r.Message);
        Assert.False(mem.Files.ContainsKey(ContentLibrary.JournalFile));
    }

    [Fact]
    public void OldBackups_ArePruned()
    {
        var mem = new MemoryStorage();
        var t = new DateTime(2026, 1, 1);
        var lib = new ContentLibrary(mem, () => t) { KeepBackups = 2 };
        var prev = Db();
        lib.Publish(prev.Clone(), null);
        for (int i = 0; i < 4; i++)
        {
            t = t.AddMinutes(1);
            var draft = Reload(mem);
            draft.Skill("hit")!.Description = "версия " + i;
            lib.Publish(draft, prev);
            prev = Reload(mem);
        }
        Assert.Equal(2, lib.Backups().Count);
        Assert.Equal(new List<int> { 1, 2, 3, 4 }, lib.Versions(DefCategory.Skill, "hit"));
        Assert.Equal(5, prev.Skill("hit")!.Version);
    }

    [Fact]
    public void RulesPreset_ReplacesGlobalRules_ForTheScenario()
    {
        var db = Db();
        var preset = new RulesPresetDef { Id = "hard", Name = "Хардкор", Rules = ContentJson.Clone(db.Rules) };
        preset.Rules.Ap.DefendCost = 3.5;
        db.Add(preset);
        var sc = db.Get<ScenarioDef>("test")!;
        sc.RulesPreset = "hard";
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error);
        var sim = ScenarioSystem.Create(db, sc);
        Assert.Same(preset.Rules, sim.Rules);
        sc.RulesPreset = "";
        Assert.Same(db.Rules, ScenarioSystem.Create(db, sc).Rules);

        // Round trip through the pack.
        var mem = new MemoryStorage();
        new ContentLibrary(mem).Publish(db, null);
        Assert.Equal(3.5, Reload(mem).Get<RulesPresetDef>("hard")!.Rules.Ap.DefendCost);
    }

    [Fact]
    public void IncompleteDraft_IsKept_ButBlocksScenario()
    {
        var db = Db();
        db.Unit("soldier")!.Incomplete = true;
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Source == "Unit:soldier" && i.Path == "Incomplete");
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Source == "Scenario:test" && i.Message.Contains("неполные"));
        var mem = new MemoryStorage();
        new ContentLibrary(mem).Publish(db, null);
        Assert.True(ContentIO.Load(mem).Db.Unit("soldier")!.Incomplete);
        Assert.DoesNotContain("incomplete", mem.Files["skills.json"]);
    }

    [Fact]
    public void Dependencies_UsesAndUsedBy()
    {
        var db = Db();
        var scenario = db.Get<ScenarioDef>("test")!;
        var uses = ContentDependencies.UsedDefs(db, scenario);
        Assert.Contains(uses, d => d.Id == "corridor");
        Assert.Contains(uses, d => d.Id == "soldier");
        var closure = ContentDependencies.Closure(db, new Def[] { scenario });
        Assert.Contains(closure, d => d.Category == DefCategory.Skill && d.Id == "hit");
        Assert.Contains(closure, d => d.Category == DefCategory.Terrain && d.Id == "grass");
        Assert.Contains(ContentDependencies.UsedBy(db, db.Unit("soldier")!), d => d.Id == "test");
        db.Unit("soldier")!.BasicAttack = "nope";
        Assert.Contains(ContentDependencies.Missing(db, db.Unit("soldier")!), m => m.Reference == "nope");
    }
}
