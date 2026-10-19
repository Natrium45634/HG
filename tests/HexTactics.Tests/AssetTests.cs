using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Resources (TZ §3.5, §16 п. 20–21, docs/DECISIONS_2.md §34): checked import of models, textures and
/// sounds, missing and damaged files, the base geometry as a replaceable representation.
/// </summary>
public class AssetTests
{
    /// <summary>A minimal valid GLB: one triangle and named animation clips (no external references).</summary>
    public static byte[] Glb(params string[] clips) => GlbWith(clips, null);

    public static byte[] GlbWith(string[] clips, string? bufferUri, int version = 2)
    {
        var bin = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0 };
        string anims = clips.Length == 0 ? "" : ",\"animations\":[" + string.Join(",", clips.Select(c => $"{{\"name\":\"{c}\",\"channels\":[],\"samplers\":[]}}")) + "]";
        string buffer = bufferUri == null ? $"{{\"byteLength\":{bin.Length}}}" : $"{{\"byteLength\":{bin.Length},\"uri\":\"{bufferUri}\"}}";
        string json = "{\"asset\":{\"version\":\"" + version + ".0\"},\"buffers\":[" + buffer + "],\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}]," +
                      "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}]," +
                      "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],\"nodes\":[{\"mesh\":0}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0" + anims + "}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int total = 12 + 8 + jsonBytes.Length + jsonPad + 8 + bin.Length;
        w.Write(Encoding.ASCII.GetBytes("glTF"));
        w.Write((uint)version);
        w.Write((uint)total);
        w.Write((uint)(jsonBytes.Length + jsonPad));
        w.Write(Encoding.ASCII.GetBytes("JSON"));
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
        w.Write((uint)bin.Length);
        w.Write(Encoding.ASCII.GetBytes("BIN\0"));
        w.Write(bin);
        return ms.ToArray();
    }

    public static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 };
    public static readonly byte[] Wav = Encoding.ASCII.GetBytes("RIFF\x24\0\0\0WAVEfmt ").Concat(new byte[20]).ToArray();

    // ------------------------------------------------------------------ checks of files

    [Fact]
    public void Validator_AcceptsKnownFormats_BySignature()
    {
        var glb = AssetValidator.Check(Glb("Idle", "Walk", "Attack"), "drone.glb");
        Assert.True(glb.Ok, glb.Error);
        Assert.Equal(AssetKind.Model, glb.Kind);
        Assert.Equal(new[] { "Idle", "Walk", "Attack" }, glb.Clips);
        Assert.True(AssetValidator.Check(Png, "icon.png").Ok);
        Assert.True(AssetValidator.Check(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1 }, "tex.jpg").Ok);
        Assert.True(AssetValidator.Check(Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 "), "tex.webp").Ok);
        Assert.True(AssetValidator.Check(Encoding.ASCII.GetBytes("OggS\0\x02"), "zap.ogg").Ok);
        Assert.True(AssetValidator.Check(Wav, "zap.wav").Ok);
        var gltf = AssetValidator.Check(Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"data:application/octet-stream;base64,AAAA\",\"byteLength\":3}]}"), "m.gltf");
        Assert.True(gltf.Ok, gltf.Error);
    }

    [Theory]
    [InlineData("evil.gd", "исполняемый код")]
    [InlineData("scene.tscn", "исполняемый код")]
    [InlineData("tool.exe", "исполняемый код")]
    [InlineData("lib.dll", "исполняемый код")]
    [InlineData("pic.bmp", "не поддерживается")]
    public void Validator_RejectsScriptsScenesAndUnknownFormats(string name, string reason)
    {
        var r = AssetValidator.Check(new byte[] { 1, 2, 3 }, name);
        Assert.False(r.Ok);
        Assert.Contains(reason, r.Error);
    }

    [Fact]
    public void Validator_DetectsDamagedAndUnsafeFiles()
    {
        // Wrong signature for the extension (a JPEG named .png, garbage named .glb).
        Assert.Contains("PNG", AssetValidator.Check(new byte[] { 0xFF, 0xD8, 0xFF, 0 }, "x.png").Error);
        Assert.Contains("GLB", AssetValidator.Check(Encoding.ASCII.GetBytes("not a model at all"), "x.glb").Error);
        // Truncated GLB: the header length no longer matches.
        var glb = Glb("idle");
        Assert.Contains("длина", AssetValidator.Check(glb.Take(glb.Length - 4).ToArray(), "x.glb").Error);
        // glTF 1.0 is not supported.
        Assert.Contains("Версия", AssetValidator.Check(GlbWith(new[] { "idle" }, null, version: 1), "x.glb").Error);
        // External references outside the package or to the network.
        Assert.Contains("Недопустимая ссылка", AssetValidator.Check(GlbWith(Array.Empty<string>(), "../../secret.bin"), "x.glb").Error);
        Assert.Contains("Недопустимая ссылка", AssetValidator.Check(GlbWith(Array.Empty<string>(), "C:/Users/a/x.bin"), "x.glb").Error);
        Assert.Contains("Недопустимая ссылка", AssetValidator.Check(GlbWith(Array.Empty<string>(), "http://example.com/x.bin"), "x.glb").Error);
        Assert.Contains("не читается", AssetValidator.Check(Encoding.UTF8.GetBytes("{ broken"), "x.gltf").Error);
        Assert.Contains("пуст", AssetValidator.Check(Array.Empty<byte>(), "x.png").Error);
        Assert.Contains("отсутствует", AssetValidator.Check(null, "x.png").Error);
    }

    [Fact]
    public void Import_DescribesTheFile_AndNeverOverwritesAnotherFile()
    {
        var db = ScenarioSetup.Db();
        var bytes = Glb("Idle", "Run", "Attack_01", "Hit", "Die", "Spawn");
        var def = AssetValidator.Import(db, "Scout Drone.glb", bytes, _ => null, out var error);
        Assert.Null(error);
        Assert.NotNull(def);
        Assert.Equal(AssetKind.Model, def!.Kind);
        Assert.Equal("assets/models/scout_drone.glb", def.Path);
        Assert.Equal(AssetValidator.Sha256(bytes), def.Sha256);
        Assert.Equal(6, def.Clips.Count);
        var map = def.Animations.ToDictionary(a => a.Role, a => a.Clip);
        Assert.Equal("Idle", map[AnimRole.Idle]);
        Assert.Equal("Run", map[AnimRole.Move]);
        Assert.Equal("Attack_01", map[AnimRole.Attack]);
        Assert.Equal("Die", map[AnimRole.Death]);
        Assert.False(map.ContainsKey(AnimRole.Block)); // no clip → procedural

        // A different file with the same name already in the library keeps its place.
        var other = AssetValidator.Import(db, "Scout Drone.glb", Glb("Idle"), p => p == "assets/models/scout_drone.glb" ? bytes : null, out _);
        Assert.StartsWith("assets/models/", other!.Path);
        Assert.NotEqual("assets/models/scout_drone.glb", other.Path);
        // A texture imported as an icon.
        Assert.Equal(AssetKind.Icon, AssetValidator.Import(db, "shield.png", Png, _ => null, out _, AssetKind.Icon)!.Kind);
        // Rejected files produce no resource.
        Assert.Null(AssetValidator.Import(db, "hack.gd", Encoding.UTF8.GetBytes("extends Node"), _ => null, out var why));
        Assert.Contains("исполняемый код", why);
    }

    // ------------------------------------------------------------------ TZ §16 п. 21: missing and damaged resources

    [Fact]
    public void MissingAndDamagedFiles_AreReported_NotFatal()
    {
        var db = ScenarioSetup.Db();
        var glb = Glb("idle");
        var files = new Dictionary<string, byte[]> { ["assets/models/ok.glb"] = glb, ["assets/models/bad.glb"] = glb.Take(40).ToArray() };
        db.Add(new AssetDef { Id = "ok", Kind = AssetKind.Model, Path = "assets/models/ok.glb", Sha256 = AssetValidator.Sha256(glb) });
        db.Add(new AssetDef { Id = "gone", Kind = AssetKind.Model, Path = "assets/models/gone.glb" });
        db.Add(new AssetDef { Id = "bad", Kind = AssetKind.Model, Path = "assets/models/bad.glb", Sha256 = AssetValidator.Sha256(glb) });
        db.Add(new AssetDef { Id = "swapped", Kind = AssetKind.Sound, Path = "assets/models/ok.glb" });
        var issues = AssetValidator.CheckFiles(db, p => files.GetValueOrDefault(p));
        Assert.DoesNotContain(issues, i => i.Source == "Asset:ok");
        Assert.Contains(issues, i => i.Source == "Asset:gone" && i.Message.Contains("отсутствует") && i.Message.Contains("базовое представление"));
        Assert.Contains(issues, i => i.Source == "Asset:bad" && i.Message.Contains("повреждён"));
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity)); // the game keeps running

        // The content check: a model slot pointing to a sound, an unsafe path, a mapping to a missing clip.
        db.Unit("soldier")!.Visual.Model = "swapped";
        db.Add(new AssetDef { Id = "evil", Kind = AssetKind.Texture, Path = "../../etc/passwd.png" });
        var model = db.Get<AssetDef>("ok")!;
        model.Clips.Add("idle");
        model.Animations.Add(new AnimationMapDef { Role = AnimRole.Attack, Clip = "attack" });
        var v = ContentValidator.Validate(db);
        Assert.Contains(v, i => i.Source == "Unit:soldier" && i.Severity == IssueSeverity.Error && i.Message.Contains("нужен Model"));
        Assert.Contains(v, i => i.Source == "Asset:evil" && i.Severity == IssueSeverity.Error && i.Message.Contains("Недопустимый путь"));
        Assert.Contains(v, i => i.Source == "Asset:ok" && i.Severity == IssueSeverity.Warning && i.Message.Contains("процедурно"));
    }

    [Fact]
    public void Packages_CarryResources_AndRejectBadOnes()
    {
        var db = ScenarioSetup.Db();
        var glb = Glb("idle", "death");
        db.Add(new AssetDef { Id = "drone", Kind = AssetKind.Model, Path = "assets/models/drone.glb", Sha256 = AssetValidator.Sha256(glb) });
        db.Unit("soldier")!.Visual.Model = "drone";
        var files = new Dictionary<string, byte[]> { ["assets/models/drone.glb"] = glb };
        var bytes = ContentPackage.Export(db, new Def[] { db.Unit("soldier")! }, new PackageExportOptions { ReadAsset = p => files.GetValueOrDefault(p) });

        // Into a library that already has a different «drone.glb»: ours gets its own path.
        var target = ScenarioSetup.Db();
        target.Table(DefCategory.Unit).Remove("soldier");
        var pkg = ContentPackage.Read(bytes);
        Assert.True(pkg.Ok, string.Join("\n", pkg.Issues));
        var report = ContentPackage.Apply(target, ContentPackage.Plan(target, pkg), existingAsset: p => p == "assets/models/drone.glb" ? Png : null);
        var imported = target.Get<AssetDef>("drone")!;
        Assert.NotEqual("assets/models/drone.glb", imported.Path);
        Assert.Equal(glb, report.AssetFiles[imported.Path]);

        // A package whose «model» is not a model is rejected as a whole (checksums are fine, the content is not).
        var evil = ScenarioSetup.Db();
        evil.Add(new AssetDef { Id = "drone", Kind = AssetKind.Model, Path = "assets/models/drone.glb" });
        var evilBytes = ContentPackage.Export(evil, new Def[] { evil.Get<AssetDef>("drone")! },
            new PackageExportOptions { ReadAsset = _ => Encoding.UTF8.GetBytes("extends Node\nfunc _ready(): OS.execute(\"rm\")") });
        var rejected = ContentPackage.Read(evilBytes);
        Assert.False(rejected.Ok);
        Assert.Contains(rejected.Issues, i => i.Message.StartsWith("Ресурс отклонён"));
    }

    // ------------------------------------------------------------------ TZ §16 п. 20: the base geometry is a replaceable representation

    private static List<string> Battle(bool withVisuals)
    {
        var db = ScenarioSetup.Db();
        if (withVisuals)
        {
            db.Add(new AssetDef { Id = "drone", Kind = AssetKind.Model, Path = "assets/models/drone.glb", Animations = { new AnimationMapDef { Role = AnimRole.Attack, Clip = "attack" } } });
            db.Add(new AssetDef { Id = "zap", Kind = AssetKind.Sound, Path = "assets/sounds/zap.wav" });
            db.Add(new VfxDef { Id = "spark", Name = "Искры", Sound = "zap" });
            foreach (var u in db.All<UnitDef>())
            {
                u.Visual.Model = "drone";
                u.Visual.SpawnVfx = "spark";
                u.Visual.HitSound = "zap";
            }
            db.Skill("hit")!.ImpactVfx = "spark";
            db.Rules.Visuals.Hit = "spark";
        }
        var sc = ScenarioSetup.Scenario();
        sc.Units.Add(new ScenarioUnitDef { Unit = "dummy", C = new HexCoord(2, 0), Facing = HexDirection.W });
        var sim = ScenarioSetup.Sim(db, sc);
        sim.Options.PlayerFactions.Clear();
        var hero = sim.Units.First(u => u.Tag == "hero");
        var foe = sim.Units.First(u => u.Def.Id == "dummy");
        sim.Enqueue(hero, Command.Attack(foe.Id));
        for (int i = 0; i < 200; i++) sim.Step();
        return sim.Log.Entries.Select(e => $"{e.Tick}:{e.Type}:{e.Text}").Concat(sim.Units.Select(u => $"{u.Id}:{u.Hp:0.###}:{u.Pos}")).ToList();
    }

    [Fact]
    public void Models_And_Effects_DoNotChangeTheBattle()
    {
        // Same seed, same commands: with models, sounds and effects or with the base figures — the same battle.
        Assert.Equal(Battle(false), Battle(true));
    }

    [Fact]
    public void AnimationRoles_FollowWhatTheUnitDoes()
    {
        var db = ScenarioSetup.Db();
        db.Add(new StatusDef { Id = "stun", Name = "Оглушён", Duration = 1, Levels = { new StatusLevelDef { Threshold = 1, Flags = new ControlFlagsDef { BlocksActions = true } } } });
        db.Add(new SkillDef { Id = "nova", Name = "Нова", Kind = SkillKind.Spell, Targeting = TargetingMode.Self, ApCost = 1, PrepTime = 1, Stages = { new SkillStageDef() } });
        db.Unit("soldier")!.Skills.Add("nova");
        var sc = ScenarioSetup.Scenario();
        sc.Units.Add(new ScenarioUnitDef { Unit = "dummy", C = new HexCoord(3, 0), Facing = HexDirection.W });
        var sim = ScenarioSetup.Sim(db, sc);
        var hero = sim.Units.First(u => u.Tag == "hero");
        var foe = sim.Units.First(u => u.Def.Id == "dummy");
        Assert.Equal(AnimRole.Spawn, VisualState.RoleOf(sim, hero));
        for (int i = 0; i < sim.TicksFor(VisualState.SpawnSeconds); i++) sim.Step();
        Assert.Equal(AnimRole.Idle, VisualState.RoleOf(sim, hero));
        sim.Enqueue(hero, Command.Move(new HexCoord(1, 0)));
        sim.Step();
        sim.Step();
        Assert.Equal(AnimRole.Move, VisualState.RoleOf(sim, hero));
        for (int i = 0; i < 60 && hero.StepTo.HasValue; i++) sim.Step();
        sim.Enqueue(hero, Command.Skill("nova", null));
        for (int i = 0; i < 10 && hero.Casting == null; i++) sim.Step();
        Assert.Equal(AnimRole.Skill, VisualState.RoleOf(sim, hero));
        for (int i = 0; i < 60 && hero.Casting != null; i++) sim.Step();
        sim.Enqueue(hero, Command.Attack(foe.Id));
        for (int i = 0; i < 40 && hero.Casting == null; i++) sim.Step();
        Assert.Equal(AnimRole.Attack, VisualState.RoleOf(sim, hero));
        sim.Combat.DealDamage(hero, foe, 5, "physical", null, "тест", null, DamageSource.Other);
        Assert.Equal(AnimRole.Hit, VisualState.RoleOf(sim, foe));
        sim.Statuses.Apply(foe, db.Status("stun")!, 1, 0, hero.Id, force: true);
        Assert.Equal(AnimRole.Control, VisualState.RoleOf(sim, foe));
        foe.Hp = 0;
        sim.Combat.Kill(foe, hero, "тест");
        Assert.Equal(AnimRole.Death, VisualState.RoleOf(sim, foe));
        Assert.True(VisualState.Loops(AnimRole.Idle) && !VisualState.Loops(AnimRole.Death));
    }
}
