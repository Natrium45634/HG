using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Packages .hexpack (docs/DECISIONS_2.md §32, TZ §14): export with dependencies, the nine import steps.</summary>
public class PackageTests
{
    private static PackageExportOptions Opts(string name = "Тест") => new() { Name = name, Author = "admin", Created = new DateTime(2026, 5, 1, 12, 0, 0) };

    /// <summary>A library without the scenario's content: only the basics every pack has.</summary>
    private static ContentDatabase EmptyLibrary()
    {
        var db = TestUtil.Minimal();
        foreach (var id in new[] { "soldier", "dummy" }) db.Table(DefCategory.Unit).Remove(id);
        db.Table(DefCategory.Skill).Remove("hit");
        return db;
    }

    [Fact]
    public void Export_CollectsTransitiveDependencies_WithManifest()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario();
        db.Add(sc);
        var bytes = ContentPackage.Export(db, new Def[] { sc }, Opts(), out var manifest);
        Assert.Equal(PackageManifest.CurrentPackageFormat, manifest.PackageFormat);
        Assert.Equal(ContentJson.CurrentFormatVersion, manifest.FormatVersion);
        Assert.Contains(manifest.Objects, o => o.Category == "Scenario" && o.Id == "test");
        Assert.Contains(manifest.Objects, o => o.Category == "Map" && o.Id == "corridor");
        Assert.Contains(manifest.Objects, o => o.Category == "Unit" && o.Id == "soldier");
        Assert.Contains(manifest.Objects, o => o.Category == "Skill" && o.Id == "hit");          // via the unit
        Assert.Contains(manifest.Objects, o => o.Category == "DamageType" && o.Id == "physical"); // via the skill
        Assert.Contains(manifest.Objects, o => o.Category == "Terrain" && o.Id == "grass");       // via the map
        var unit = manifest.Objects.First(o => o.Id == "soldier");
        Assert.Contains("Skill:hit", unit.Dependencies);
        Assert.All(manifest.Objects, o => Assert.True(ContentPackage.IsSafePath(o.Path) && o.Path.StartsWith("objects/", StringComparison.Ordinal)));
        Assert.False(manifest.Incomplete);
        Assert.Equal(64, manifest.Hash.Length);
        // Deterministic: same content → same bytes.
        Assert.Equal(bytes, ContentPackage.Export(db, new Def[] { sc }, Opts()));
        // No absolute paths of the author's computer anywhere.
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.All(zip.Entries, e => Assert.True(ContentPackage.IsSafePath(e.FullName)));
    }

    [Fact]
    public void RoundTrip_IntoAnotherLibrary_ImportsEverything()
    {
        var db = ScenarioSetup.Db();
        var sc = ScenarioSetup.Scenario();
        db.Add(sc);
        var bytes = ContentPackage.Export(db, new Def[] { sc }, Opts());

        var target = EmptyLibrary();
        var pkg = ContentPackage.Read(bytes);
        Assert.True(pkg.Ok, string.Join("\n", pkg.Issues));
        var plan = ContentPackage.Plan(target, pkg);
        Assert.Empty(plan.Missing);
        Assert.Contains(plan.Items, i => i.Def.Id == "test" && i.Status == ImportStatus.New);
        Assert.Contains(plan.Items, i => i.Def.Id == "grass" && i.Status == ImportStatus.Identical);
        var report = ContentPackage.Apply(target, plan);
        Assert.Contains(report.Added, d => d.Id == "soldier");
        Assert.True(report.Identical > 0);
        Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.DoesNotContain(ContentValidator.Validate(target), i => i.Severity == IssueSeverity.Error);
        // The imported scenario runs.
        var sim = HexTactics.Core.Sim.ScenarioSystem.Create(target, target.Get<ScenarioDef>("test")!);
        Assert.Contains(sim.Units, u => u.Def.Id == "soldier");
    }

    [Fact]
    public void Conflicts_Skip_Copy_Replace()
    {
        var db = ScenarioSetup.Db();
        var bytes = ContentPackage.Export(db, new Def[] { db.Unit("soldier")! }, Opts());

        // The target library has its own "soldier" and "hit".
        var target = ScenarioSetup.Db();
        target.Unit("soldier")!.Name = "Местный солдат";
        target.Skill("hit")!.Stages[0].Effects[0].Amount = 99;
        target.Skill("hit")!.Version = 3;
        var pkg = ContentPackage.Read(bytes);
        var plan = ContentPackage.Plan(target, pkg);
        var soldier = plan.Items.First(i => i.Def.Id == "soldier");
        var hit = plan.Items.First(i => i.Def.Id == "hit");
        Assert.Equal(ImportStatus.Conflict, soldier.Status);
        Assert.Contains(soldier.Diff, d => d.Path == "name");
        Assert.Equal(ImportStatus.Conflict, hit.Status);
        // Default: skip — nothing is overwritten silently.
        Assert.All(plan.Conflicts, c => Assert.Equal(ConflictResolution.Skip, c.Resolution));

        soldier.Resolution = ConflictResolution.Copy;
        hit.Resolution = ConflictResolution.Copy;
        var copyTarget = ScenarioSetupCopy(target);
        var report = ContentPackage.Apply(copyTarget, ContentPackage.Plan(copyTarget, pkg).Also(p =>
        {
            foreach (var c in p.Conflicts) c.Resolution = ConflictResolution.Copy;
        }));
        var newSoldier = copyTarget.Unit("soldier_copy")!;
        Assert.Equal("Солдат", newSoldier.Name);
        Assert.Equal("hit_copy", newSoldier.BasicAttack); // references inside the package follow the copy
        Assert.Equal(10, copyTarget.Skill("hit_copy")!.Stages[0].Effects[0].Amount);
        Assert.Equal("Местный солдат", copyTarget.Unit("soldier")!.Name);
        Assert.Equal(99, copyTarget.Skill("hit")!.Stages[0].Effects[0].Amount);
        Assert.Equal(2, report.Copied.Count);

        // Replace keeps the library version (the publication raises it and keeps the old one in history).
        var replaceTarget = ScenarioSetupCopy(target);
        var plan2 = ContentPackage.Plan(replaceTarget, pkg);
        plan2.Conflicts.First(c => c.Def.Id == "hit").Resolution = ConflictResolution.Replace;
        var r2 = ContentPackage.Apply(replaceTarget, plan2);
        Assert.Single(r2.Replaced);
        Assert.Equal(10, replaceTarget.Skill("hit")!.Stages[0].Effects[0].Amount);
        Assert.Equal(3, replaceTarget.Skill("hit")!.Version);
        Assert.Equal("Местный солдат", replaceTarget.Unit("soldier")!.Name); // skipped

        var mem = new MemoryStorage();
        var lib = new ContentLibrary(mem);
        lib.Publish(target.Clone(), null);
        var pub = lib.Publish(replaceTarget, target);
        Assert.Contains(pub.Changes, c => c.Id == "hit" && c.OldVersion == 3 && c.NewVersion == 4);
        Assert.Equal(99, Assert.IsType<SkillDef>(lib.LoadVersion(DefCategory.Skill, "hit", 3)).Stages[0].Effects[0].Amount);
    }

    private static ContentDatabase ScenarioSetupCopy(ContentDatabase db)
    {
        var c = db.Clone();
        ContentValidator.Validate(c);
        return c;
    }

    [Fact]
    public void MissingDependencies_ImportOnlyAsIncompleteDraft()
    {
        var db = ScenarioSetup.Db();
        var unit = db.Unit("soldier")!;
        // A single exported object without its skill.
        var single = ContentPackage.ReadSingle(ContentIO.ExportDef(unit));
        var target = EmptyLibrary();
        var plan = ContentPackage.Plan(target, single);
        Assert.Contains(plan.Missing, m => m.Dependency.Category == DefCategory.Skill && m.Dependency.Reference == "hit");
        Assert.True(plan.NeedsIncomplete);
        Assert.Throws<InvalidOperationException>(() => ContentPackage.Apply(target, plan));
        Assert.Null(target.Unit("soldier")); // nothing imported

        var report = ContentPackage.Apply(target, plan, allowIncomplete: true);
        var imported = target.Unit("soldier")!;
        Assert.True(imported.Incomplete);
        Assert.Contains(report.MarkedIncomplete, d => d.Id == "soldier");
        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Error && i.Path.Contains("BasicAttack"));
    }

    [Fact]
    public void DamagedOrForeignPackages_AreRejected()
    {
        var db = ScenarioSetup.Db();
        var bytes = ContentPackage.Export(db, new Def[] { db.Unit("soldier")! }, Opts());

        // A changed object file (checksum mismatch).
        var tampered = Rewrite(bytes, (name, text) => name == "objects/Unit/soldier.json" ? text.Replace("Солдат", "Взломщик") : text);
        var r1 = ContentPackage.Read(tampered);
        Assert.False(r1.Ok);
        Assert.Contains(r1.Issues, i => i.Message.Contains("повреждён"));
        Assert.Throws<InvalidOperationException>(() => ContentPackage.Apply(db, ContentPackage.Plan(db, r1)));

        // A future package format.
        var future = Rewrite(bytes, (name, text) => name == ContentPackage.ManifestEntry ? text.Replace("\"packageFormat\": 1", "\"packageFormat\": 9") : text);
        Assert.Contains(ContentPackage.Read(future).Issues, i => i.Message.Contains("более новой"));

        // An unsafe path.
        var evil = Rewrite(bytes, null, ("../../evil.txt", "x"));
        Assert.Contains(ContentPackage.Read(evil).Issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("Недопустимый путь"));

        // Not a ZIP at all.
        Assert.False(ContentPackage.Read(Encoding.UTF8.GetBytes("{}")).Ok);

        // An unlisted extra file is only a warning.
        var extra = Rewrite(bytes, null, ("notes.txt", "hello"));
        var r5 = ContentPackage.Read(extra);
        Assert.True(r5.Ok);
        Assert.Contains(r5.Issues, i => i.Severity == IssueSeverity.Warning && i.Path == "notes.txt");
    }

    [Theory]
    [InlineData("objects/Unit/a.json", true)]
    [InlineData("assets/models/m.glb", true)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Users/admin/x.json", false)]
    [InlineData("assets\\x.png", false)]
    [InlineData("assets/../x", false)]
    [InlineData("", false)]
    public void SafePaths(string path, bool ok) => Assert.Equal(ok, ContentPackage.IsSafePath(path));

    [Fact]
    public void OldFormatObject_IsMigrated_OnImport()
    {
        // A part-1 single object (format v1: flat resistances on the unit) imports through the migrations.
        var json = """
        { "formatVersion": 1, "kind": "def", "category": "Unit",
          "item": { "id": "old_guard", "name": "Старый страж", "faction": "player", "basicAttack": "hit",
                    "stats": { "hp_max": 80 }, "resistances": { "fire": 0.5 } } }
        """;
        var pkg = ContentPackage.ReadSingle(json);
        Assert.True(pkg.Ok, string.Join("\n", pkg.Issues));
        Assert.Equal(1, pkg.MigratedFrom);
        var target = ScenarioSetup.Db();
        ContentPackage.Apply(target, ContentPackage.Plan(target, pkg));
        var u = target.Unit("old_guard")!;
        Assert.Contains(u.Defense.Modifiers, m => m.Match == "fire" && Math.Abs(m.Percent + 50) < 1e-9);
    }

    [Fact]
    public void PinnedVersions_TravelWithThePackage()
    {
        var mem = new MemoryStorage();
        var lib = new ContentLibrary(mem);
        var published = ScenarioSetup.Db();
        lib.Publish(published.Clone(), null);
        var draft = ContentIO.Load(mem).Db;
        draft.Skill("hit")!.Stages[0].Effects[0].Amount = 40;
        var change = ContentLibrary.Changes(draft, published).Single();
        ContentLibrary.PinUsages(draft, change, change.Direct.Where(r => r.Owner?.Id == "dummy"));
        lib.Publish(draft, published);
        var db = ContentIO.Load(mem).Db;
        Assert.Equal("hit@1", db.Unit("dummy")!.BasicAttack);

        var bytes = ContentPackage.Export(db, new Def[] { db.Unit("dummy")!, db.Unit("soldier")! }, Opts(), out var manifest);
        Assert.Contains(manifest.Objects, o => o.Id == "hit" && o.Pinned && o.Version == 1 && o.Path.EndsWith("hit@1.json"));
        Assert.Contains(manifest.Objects, o => o.Id == "hit" && !o.Pinned && o.Version == 2);

        var target = EmptyLibrary();
        var report = ContentPackage.Apply(target, ContentPackage.Plan(target, ContentPackage.Read(bytes)));
        Assert.Equal(1, report.PinnedAdded);
        Assert.Equal(10, target.Get<SkillDef>("hit@1")!.Stages[0].Effects[0].Amount);
        Assert.Equal(40, target.Get<SkillDef>("hit")!.Stages[0].Effects[0].Amount);
        Assert.DoesNotContain(ContentValidator.Validate(target), i => i.Severity == IssueSeverity.Error);

        // Publishing the importing library stores the pinned version in its history.
        var mem2 = new MemoryStorage();
        new ContentLibrary(mem2).Publish(target, null);
        Assert.True(mem2.Files.ContainsKey("history/Skill/hit/v1.json"));
        Assert.Equal(10, ContentIO.Load(mem2).Db.Get<SkillDef>("hit@1")!.Stages[0].Effects[0].Amount);
    }

    [Fact]
    public void Assets_AreIncluded_WithIntegrity()
    {
        var db = ScenarioSetup.Db();
        var files = new Dictionary<string, byte[]> { ["models/guard.glb"] = new byte[] { 1, 2, 3, 4 } };
        var o = Opts();
        o.AssetPaths.Add("models/guard.glb");
        o.ReadAsset = p => files.GetValueOrDefault(p);
        var bytes = ContentPackage.Export(db, new Def[] { db.Unit("soldier")! }, o, out var manifest);
        Assert.Single(manifest.Assets);
        var pkg = ContentPackage.Read(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, pkg.Assets["models/guard.glb"]);
        o.AssetPaths.Add("../outside.png");
        Assert.Throws<ArgumentException>(() => ContentPackage.Export(db, new Def[] { db.Unit("soldier")! }, o));
    }

    /// <summary>Rewrites entries of a package (without fixing the hashes) and optionally adds one.</summary>
    private static byte[] Rewrite(byte[] package, Func<string, string, string>? change, (string Name, string Text)? add = null)
    {
        using var src = new ZipArchive(new MemoryStream(package));
        using var ms = new MemoryStream();
        using (var dst = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var e in src.Entries)
            {
                using var r = new StreamReader(e.Open());
                var text = r.ReadToEnd();
                var ne = dst.CreateEntry(e.FullName);
                using var w = new StreamWriter(ne.Open());
                w.Write(change == null ? text : change(e.FullName, text));
            }
            if (add is { } a)
            {
                var ne = dst.CreateEntry(a.Name);
                using var w = new StreamWriter(ne.Open());
                w.Write(a.Text);
            }
        }
        return ms.ToArray();
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
