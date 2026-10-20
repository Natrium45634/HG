using System;
using System.IO;
using System.Linq;
using System.Text;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 3, stage 7: what a resource file contains, the limits, the requirements page (docs/DECISIONS_3.md §47).</summary>
public class AssetImportTests
{
    private static readonly AssetLimitsDef Limits = new();

    public static byte[] Png(int w, int h, int colorType = 6)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Be(int v) => ms.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        Be(13);
        ms.Write(Encoding.ASCII.GetBytes("IHDR"));
        Be(w);
        Be(h);
        ms.Write(new byte[] { 8, (byte)colorType, 0, 0, 0 });
        Be(0); // CRC (not checked by the analyzer)
        Be(0);
        ms.Write(Encoding.ASCII.GetBytes("IEND"));
        Be(0);
        return ms.ToArray();
    }

    private static byte[] Jpeg(int w, int h) => new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0, 17, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9,
    };

    private static byte[] Tga(int w, int h, int bpp)
    {
        var b = new byte[18 + w * h * (bpp / 8)];
        b[2] = 2;
        b[12] = (byte)w;
        b[13] = (byte)(w >> 8);
        b[14] = (byte)h;
        b[15] = (byte)(h >> 8);
        b[16] = (byte)bpp;
        b[17] = (byte)(bpp == 32 ? 8 : 0);
        return b;
    }

    private static byte[] Exr(int w, int h)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x76, 0x2F, 0x31, 0x01, 2, 0, 0, 0 });
        void Attr(string name, string type, byte[] value)
        {
            ms.Write(Encoding.ASCII.GetBytes(name + "\0" + type + "\0"));
            ms.Write(BitConverter.GetBytes(value.Length));
            ms.Write(value);
        }
        var ch = new MemoryStream();
        foreach (var c in new[] { "A", "B", "G", "R" })
        {
            ch.Write(Encoding.ASCII.GetBytes(c + "\0"));
            ch.Write(new byte[16]);
        }
        ch.WriteByte(0);
        Attr("channels", "chlist", ch.ToArray());
        Attr("dataWindow", "box2i", BitConverter.GetBytes(0).Concat(BitConverter.GetBytes(0)).Concat(BitConverter.GetBytes(w - 1)).Concat(BitConverter.GetBytes(h - 1)).ToArray());
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static byte[] Wav(double seconds, int rate = 22050)
    {
        int data = (int)(seconds * rate * 2);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(data);
        w.Write(new byte[data]);
        return ms.ToArray();
    }

    private static string Gltf(bool lying = false, double minY = 0, int tris = 4, bool externalImage = false) =>
        "{\"asset\":{\"version\":\"2.0\"}," +
        "\"accessors\":[{\"count\":" + tris * 3 + ",\"type\":\"SCALAR\"},{\"count\":8,\"type\":\"VEC3\",\"min\":[-0.4," + minY.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ",-0.4],\"max\":[0.4," + (lying ? 0.2 + minY : 1.8 + minY).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",0.4]},{\"count\":10,\"type\":\"SCALAR\",\"max\":[1.25]},{\"count\":10,\"type\":\"SCALAR\",\"max\":[0.8]}]," +
        "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":1},\"indices\":0,\"material\":0}]}]," +
        "\"materials\":[{\"name\":\"body\"},{\"name\":\"spare\"}]," +
        "\"skins\":[{\"joints\":[1,2,3]},{\"joints\":[4]}]," +
        "\"nodes\":[{\"mesh\":0,\"skin\":0},{},{},{},{}]," +
        "\"animations\":[{\"name\":\"idle\",\"channels\":[],\"samplers\":[{\"input\":2,\"output\":2}]},{\"name\":\"attack\",\"channels\":[],\"samplers\":[{\"input\":3,\"output\":3}]}]" +
        (externalImage ? ",\"images\":[{\"uri\":\"textures/skin.png\"}]" : "") + "}";

    [Fact]
    public void Images_DimensionsAndAlpha_ByFormat()
    {
        var png = AssetAnalyzer.Analyze(Png(512, 256), "grass.png", Limits, TexturePurpose.HexSurface);
        Assert.True(png.Ok, string.Join("; ", png.Errors));
        Assert.Equal((512, 256), (png.Width, png.Height));
        Assert.True(png.HasAlpha);
        var rgb = AssetAnalyzer.Analyze(Png(64, 64, colorType: 2), "x.png", Limits);
        Assert.False(rgb.HasAlpha);
        var jpg = AssetAnalyzer.Analyze(Jpeg(1024, 768), "wall.jpg", Limits, TexturePurpose.Environment);
        Assert.Equal((1024, 768), (jpg.Width, jpg.Height));
        Assert.False(jpg.HasAlpha);
        var tga = AssetAnalyzer.Analyze(Tga(8, 4, 32), "mask.tga", Limits, TexturePurpose.Mask);
        Assert.True(tga.Ok, string.Join("; ", tga.Errors));
        Assert.Equal((8, 4, true), (tga.Width, tga.Height, tga.HasAlpha));
        var hdr = AssetAnalyzer.Analyze(Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 256 +X 512\n" + new string('\0', 64)), "sky.hdr", Limits);
        Assert.Equal((512, 256), (hdr.Width, hdr.Height));
        var exr = AssetAnalyzer.Analyze(Exr(300, 200), "light.exr", Limits);
        Assert.True(exr.Ok, string.Join("; ", exr.Errors));
        Assert.Equal((300, 200, true), (exr.Width, exr.Height, exr.HasAlpha));
        Assert.Contains(exr.Notes, n => n.Contains("HDR/EXR"));
    }

    [Fact]
    public void TextureLimits_ByPurpose_FromTheRules()
    {
        Assert.Contains(AssetAnalyzer.Analyze(Png(4096, 4096), "icon.png", Limits, TexturePurpose.SkillIcon).Errors, e => e.Contains("больше максимума"));
        Assert.Contains(AssetAnalyzer.Analyze(Png(1024, 1024), "icon.png", Limits, TexturePurpose.SkillIcon).Warnings, w => w.Contains("чрезмерно"));
        Assert.True(AssetAnalyzer.Analyze(Png(4096, 4096), "atlas.png", Limits, TexturePurpose.Atlas).Ok);
        Assert.Contains(AssetAnalyzer.Analyze(Jpeg(128, 128), "icon.jpg", Limits, TexturePurpose.SkillIcon).Warnings, w => w.Contains("JPEG не хранит прозрачность"));
        Assert.Contains(AssetAnalyzer.Analyze(Png(128, 128, 2), "icon.png", Limits, TexturePurpose.StatusIcon).Warnings, w => w.Contains("альфа-канала"));
        // The limits are data: a project may raise them without code.
        var raised = new AssetLimitsDef();
        raised.For(TexturePurpose.SkillIcon).Max = 8192;
        raised.For(TexturePurpose.SkillIcon).RecommendedMax = 4096;
        Assert.True(AssetAnalyzer.Analyze(Png(4096, 4096), "icon.png", raised, TexturePurpose.SkillIcon).Ok);
    }

    [Fact]
    public void Gltf_Geometry_Materials_Skeleton_Animations_Bounds()
    {
        var a = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(Gltf()), "knight.gltf", Limits, exists: _ => true);
        Assert.True(a.Ok, string.Join("; ", a.Errors));
        Assert.Equal(4, a.Triangles);
        Assert.Equal(2, a.Materials);
        Assert.Equal(new[] { "spare" }, a.UnusedMaterials);
        Assert.Equal(4, a.Bones);
        Assert.Equal(1, a.UnusedSkins);
        Assert.Equal(new[] { ("idle", 1.25), ("attack", 0.8) }, a.Animations.ToArray());
        Assert.Contains(a.Warnings, w => w.Contains("Неиспользуемые материалы"));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("лежит") || w.Contains("опоры"));
        Assert.Contains(a.Lines(), l => l.Contains("треугольников 4"));

        var lying = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(Gltf(lying: true)), "x.gltf", Limits);
        Assert.Contains(lying.Warnings, w => w.Contains("лежит"));
        var floating = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(Gltf(minY: 0.9)), "x.gltf", Limits);
        Assert.Contains(floating.Warnings, w => w.Contains("опоры"));
        var heavy = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(Gltf(tris: 150_000)), "x.gltf", Limits);
        Assert.Contains(heavy.Warnings, w => w.Contains("Треугольников"));
        var missing = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(Gltf(externalImage: true)), "x.gltf", Limits, exists: _ => false);
        Assert.Contains(missing.Errors, e => e.Contains("textures/skin.png"));
        // GLB — the same JSON inside the binary container.
        var glb = AssetAnalyzer.Analyze(AssetTests.Glb("idle"), "crate.glb", Limits);
        Assert.True(glb.Ok, string.Join("; ", glb.Errors));
        Assert.Equal(1, glb.Triangles);
    }

    [Fact]
    public void Obj_Fbx_Sounds()
    {
        string obj = "mtllib crate.mtl\no box\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nusemtl wood\nf 1 2 3 4\n";
        var o = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(obj), "crate.obj", Limits, exists: f => f == "crate.mtl");
        Assert.True(o.Ok, string.Join("; ", o.Errors));
        Assert.Equal((2, 1), (o.Triangles, o.Materials));
        Assert.Contains(o.Notes, n => n.Contains("статичная"));
        Assert.False(AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(obj), "crate.obj", Limits, exists: _ => false).Ok);
        var fbx = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0").Concat(BitConverter.GetBytes(7400)).Concat(new byte[64]).ToArray();
        var f = AssetAnalyzer.Analyze(fbx, "hero.fbx", Limits);
        Assert.True(f.Ok && f.GeometryByEngine, string.Join("; ", f.Errors));
        var w = AssetAnalyzer.Analyze(Wav(1.5), "hit.wav", Limits);
        Assert.Equal(1.5, w.Duration, 2);
        Assert.Contains(AssetAnalyzer.Analyze(Wav(20, 8000), "music.wav", Limits).Warnings, x => x.Contains("длится"));
        var mp3 = new byte[] { 0xFF, 0xFB, 0x90, 0x64 }.Concat(new byte[16000 - 4]).ToArray(); // 128 kbit/s
        var m = AssetAnalyzer.Analyze(mp3, "step.mp3", Limits);
        Assert.True(m.Ok, string.Join("; ", m.Errors));
        Assert.Equal(1.0, m.Duration, 1);
    }

    [Fact]
    public void DamagedFiles_GiveErrors_NeverExceptions()
    {
        foreach (var name in new[] { "a.png", "a.jpg", "a.tga", "a.hdr", "a.exr", "a.gltf", "a.glb", "a.obj", "a.fbx", "a.wav", "a.mp3", "a.webp" })
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var r = AssetAnalyzer.Analyze(bytes, name, Limits);
            Assert.False(r.Ok);
        }
        var truncated = Png(64, 64).Take(20).ToArray();
        Assert.False(AssetAnalyzer.Analyze(truncated, "cut.png", Limits).Ok);
        Assert.False(AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"meshes\":[{"), "bad.gltf", Limits).Ok);
    }

    [Fact]
    public void RequirementsPage_FromTheLimits_TextAndHtml()
    {
        var lim = new AssetLimitsDef();
        var text = AssetRequirements.ToText(lim);
        Assert.Contains("Иконка навыка | 128–256 | 1024×1024", text);
        Assert.Contains("glTF 2.0", text);
        Assert.Contains("Маркеры", text, StringComparison.OrdinalIgnoreCase);
        lim.WarnTriangles = 50_000;
        Assert.Contains("50", AssetRequirements.ToText(lim));
        var html = AssetRequirements.ToHtml(lim);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<table>", html);
        foreach (var title in new[] { "Поддерживаемые форматы", "Размеры текстур", "Ограничения по объёму", "Требования к моделям", "Требования к текстурам", "Требования к материалам",
                     "Требования к скелетам", "Маркеры анимации", "Точки привязки", "производительности", "Именование", "Примеры корректного импорта", "Распространённые ошибки" })
            Assert.Contains(title, html);
    }

    [Fact]
    public void Import_RecordsTheAnalysis_AndValidatorChecksLimitsAndEffects()
    {
        var db = TestUtil.Minimal();
        var a = AssetValidator.Import(db, "Hero Knight.gltf", Encoding.UTF8.GetBytes(Gltf()), _ => null, out var err)!;
        Assert.Null(err);
        Assert.Equal("Hero Knight.gltf", a.SourceFile);
        Assert.False(string.IsNullOrEmpty(a.ImportedAt));
        Assert.Equal(4, a.Triangles);
        Assert.Equal(new[] { 1.25, 0.8 }, a.ClipLengths);
        Assert.Contains("треугольников", a.Analysis);
        db.Add(a);
        a.Animations.First(m => m.Role == AnimRole.Attack).Markers.Add(new AnimMarkerDef { Kind = AnimMarker.HitCheck, Time = 2 });
        var icon = AssetValidator.Import(db, "big_icon.png", Png(2048, 2048), _ => null, out _)!;
        icon.Kind = AssetKind.Icon;
        icon.Purpose = TexturePurpose.SkillIcon;
        db.Add(icon);
        db.Add(new VfxDef { Id = "loop_a", Name = "A", Type = VfxType.Composite, Layers = { "loop_b" } });
        db.Add(new VfxDef { Id = "loop_b", Name = "B", Type = VfxType.Composite, Layers = { "loop_a" } });
        var issues = ContentValidator.Validate(db);
        Assert.Contains(issues, i => i.Source.EndsWith(":" + a.Id) && i.Message.Contains("позже конца клипа"));
        Assert.Contains(issues, i => i.Source.EndsWith(":" + icon.Id) && i.Severity == IssueSeverity.Error && i.Message.Contains("больше максимума"));
        Assert.Contains(issues, i => i.Source.EndsWith(":loop_a") && i.Message.Contains("цикл"));
    }

    [Fact]
    public void TooLargeImage_OffersReduction_WithRussianPurposeNames()
    {
        var an = AssetAnalyzer.Analyze(Png(8192, 4096), "huge.png", Limits, TexturePurpose.HexSurface);
        Assert.Single(an.Errors);
        Assert.Equal(4096, an.ResizeTo);
        Assert.Contains("«Поверхность гекса»", an.Errors[0]);
        var ok = AssetAnalyzer.Analyze(Png(1024, 1024), "hex.png", Limits, TexturePurpose.HexSurface);
        Assert.True(ok.Ok);
        Assert.Equal(0, ok.ResizeTo);
        // The same file for a different purpose: an icon of 1024 is at its maximum, a 2048 one is too large.
        Assert.Equal(1024, AssetAnalyzer.Analyze(Png(2048, 2048), "i.png", Limits, TexturePurpose.SkillIcon).ResizeTo);
    }

    [Fact]
    public void Binding_AssignsTheResource_ToTheChosenObject()
    {
        var db = TestUtil.Minimal();
        var terrain = db.All<TerrainDef>().First();
        var skill = db.All<SkillDef>().First();
        var tex = AssetValidator.Import(db, "hex_grass_1024.png", Png(1024, 1024), _ => null, out _, purpose: TexturePurpose.HexSurface)!;
        Assert.Equal(TexturePurpose.HexSurface, tex.Purpose);
        db.Add(tex);
        var targets = AssetBinding.Targets(db, AssetKind.Texture);
        var top = targets.First(t => t.Owner == terrain && t.Label.Contains("текстура верха"));
        Assert.Equal(TexturePurpose.HexSurface, top.Purpose);
        top.Apply(db, tex);
        Assert.Equal(tex.Id, terrain.Look.TopTexture);
        // The side needs a material: one is created around the texture.
        targets.First(t => t.Owner == terrain && t.Label.Contains("боковых")).Apply(db, tex);
        var mat = db.Get<AssetDef>(terrain.Look.SideMaterial)!;
        Assert.Equal(AssetKind.Material, mat.Kind);
        Assert.Equal(tex.Id, mat.Texture);

        var icon = new AssetDef { Id = "ic", Name = "ic", Kind = AssetKind.Icon };
        db.Add(icon);
        AssetBinding.Targets(db, AssetKind.Icon).First(t => t.Owner == skill).Apply(db, icon);
        Assert.Equal("ic", skill.IconAsset);
        var model = new AssetDef { Id = "arrow_m", Name = "arrow", Kind = AssetKind.Model };
        db.Add(model);
        skill.Stages.Add(new SkillStageDef());
        AssetBinding.Targets(db, AssetKind.Model).First(t => t.Owner == skill && t.Label.Contains("снаряд")).Apply(db, model);
        Assert.True(skill.Stages[0].Projectile.Enabled);
        Assert.Equal("arrow_m", skill.Stages[0].Projectile.Model);
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Message.Contains("arrow_m"));
    }

    [Fact]
    public void AnimationTiming_MarkersBecomeStageDelays()
    {
        var s = new SkillDef { Id = "combo", Name = "Комбо" };
        for (int i = 0; i < 4; i++)
            s.Stages.Add(new SkillStageDef { Delay = i * 0.5, Effects = { new SkillEffectDef { Type = EffectType.Damage, Amount = 5 } } });
        var model = new AssetDef { Id = "m", Name = "Рыцарь", Kind = AssetKind.Model };
        var map = new AnimationMapDef { Role = AnimRole.Attack, Clip = "attack" };
        model.Animations.Add(map);
        Assert.Equal("", AnimationTiming.Apply(s, model)); // no markers — nothing taken
        map.Markers.Add(new AnimMarkerDef { Kind = AnimMarker.HitCheck, Time = 0.9 });
        map.Markers.Add(new AnimMarkerDef { Kind = AnimMarker.HitCheck, Time = 0.3 });
        map.Markers.Add(new AnimMarkerDef { Kind = AnimMarker.HitCheck, Time = 0.55 });
        string report = AnimationTiming.Apply(s, model);
        Assert.Contains("маркеров: 3", report);
        Assert.Equal(new[] { 0.0, 0.25, 0.6, 1.1 }, s.Stages.Select(x => x.Delay).ToArray());
        // The first marker is the release: the stage moments are relative to it.
        var db = TestUtil.Minimal();
        db.Add(model);
        Assert.Contains(model, AnimationTiming.Candidates(db, s));
    }

    [Fact]
    public void ExternalFiles_AreListed_AndMissingOnesNamed()
    {
        var an = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(Gltf(externalImage: true)), "m.gltf", Limits, exists: rel => false);
        Assert.NotEmpty(an.ExternalFiles);
        Assert.Equal(an.ExternalFiles, an.MissingFiles);
        var ok = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes(Gltf(externalImage: true)), "m.gltf", Limits, exists: rel => true);
        Assert.Empty(ok.MissingFiles);
        var obj = AssetAnalyzer.Analyze(Encoding.UTF8.GetBytes("mtllib box.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"), "box.obj", Limits, exists: _ => false);
        Assert.Contains("box.mtl", obj.ExternalFiles);
        Assert.Contains("box.mtl", obj.MissingFiles);
    }

    [Fact]
    public void AssetReport_MissingBrokenUnusedAndHeavy()
    {
        var db = TestUtil.Minimal();
        var ok = AssetValidator.Import(db, "a.png", Png(256, 256), _ => null, out _)!;
        db.Add(ok);
        db.Add(new AssetDef { Id = "gone", Name = "Пропавшая", Kind = AssetKind.Texture, Path = "assets/textures/gone.png", Sha256 = "00", Size = 10 });
        db.Add(new AssetDef { Id = "heavy", Name = "Тяжёлая", Kind = AssetKind.Model, Triangles = 250_000 });
        db.All<SkillDef>().First().IconAsset = "nobody_here";
        var files = new System.Collections.Generic.Dictionary<string, byte[]> { [ok.Path] = Png(256, 256) };
        var r = AssetReport.Build(db, p => files.TryGetValue(p, out var b) ? b : null);
        Assert.Contains(r.Missing, m => m.Contains("Пропавшая"));
        Assert.Contains(r.Performance, p => p.Contains("Тяжёлая") && p.Contains("250"));
        Assert.Contains(r.MissingRefs, m => m.Contains("nobody_here"));
        Assert.Contains(r.Unused, u => u == ok.DisplayName);
        Assert.Contains(r.Lines(), l => l.StartsWith("Ресурсов: 3"));
    }
}
