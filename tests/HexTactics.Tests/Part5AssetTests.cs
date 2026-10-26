using System.Linq;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage F — the license of a resource and the replacement of its file with its dependents (ТЗ 7.5; docs/DECISIONS_5.md §89).</summary>
public class Part5AssetTests
{
    [Fact]
    public void ReplaceFile_ListsTheDependents_KeepsTheIdAndTheAuthorsSettings()
    {
        var db = TestUtil.Minimal();
        var terrain = db.All<TerrainDef>().First();
        var first = AssetImportTests.Png(256, 256);
        var tex = AssetValidator.Import(db, "hex_top.png", first, _ => null, out _, purpose: TexturePurpose.HexSurface)!;
        tex.License = "CC0, собственная работа";
        tex.UvScale = 2;
        db.Add(tex);
        AssetBinding.Targets(db, AssetKind.Texture).First(t => t.Owner == terrain && t.Label.Contains("текстура верха")).Apply(db, tex);
        string oldPath = tex.Path, oldSha = tex.Sha256;

        // A different file under the same name: a new path in the pack (the old file stays for older versions).
        var second = AssetImportTests.Png(512, 512);
        var r = AssetValidator.PrepareReplace(db, tex, "hex_top.png", second, p => p == oldPath ? first : null, out var error)!;
        Assert.Null(error);
        Assert.Contains(terrain, r.Dependents);
        Assert.Contains(r.Notes, n => n.Contains("256×256 → 512×512"));
        Assert.Equal(oldPath, tex.Path); // nothing changed before Apply
        r.Apply();
        Assert.NotEqual(oldPath, tex.Path);
        Assert.NotEqual(oldSha, tex.Sha256);
        Assert.Equal(512, tex.Width);
        Assert.Equal(tex.Id, terrain.Look.TopTexture);
        Assert.Equal("CC0, собственная работа", tex.License);
        Assert.Equal(2, tex.UvScale);
        Assert.Null(AssetValidator.FileProblem(tex, second));

        // The same file again: said so; another kind of file: refused, nothing changes.
        Assert.Contains(AssetValidator.PrepareReplace(db, tex, "hex_top.png", second, _ => second, out _)!.Notes, n => n.Contains("Файл тот же"));
        Assert.Null(AssetValidator.PrepareReplace(db, tex, "sound.wav", System.Text.Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEfmt "), _ => null, out var kindError));
        Assert.NotNull(kindError);
    }

    [Fact]
    public void License_IsOptional_SavedWhenSet_AbsentWhenEmpty()
    {
        var a = new AssetDef { Id = "m", Name = "m", Kind = AssetKind.Material };
        Assert.DoesNotContain("license", ContentJson.Serialize(a));
        a.License = "CC-BY 4.0 — автор Иванов";
        var json = ContentJson.Serialize(a);
        Assert.Contains("CC-BY 4.0", json);
        Assert.Equal(a.License, ContentJson.Deserialize<AssetDef>(json)!.License);
        // Never required by the check.
        var db = TestUtil.Minimal();
        db.Add(new AssetDef { Id = "m2", Name = "m2", Kind = AssetKind.Material });
        Assert.DoesNotContain(ContentValidator.Validate(db), i => i.Message.Contains("ицензи"));
    }

    [Fact]
    public void PreviewPatch_OneHex_Neighbours_Steps_OnACopy()
    {
        var single = MaterialPreview.Map("floor", "tile", PreviewShape.Single);
        Assert.Equal(2, single.Cells.Count(c => c.T == "tile"));
        var group = MaterialPreview.Map("floor", "tile", PreviewShape.Group);
        Assert.Equal(7, group.Cells.Count(c => c.T == "tile"));
        Assert.All(group.Cells.Where(c => c.T == "tile"), c => Assert.Equal(0, c.H));
        var steps = MaterialPreview.Map("floor", "tile", PreviewShape.Steps);
        // A ring of steps: every height 0…6 once — every drop of a level is seen from the sides.
        Assert.Equal(Enumerable.Range(0, 7), steps.Cells.Where(c => c.T == "tile").Select(c => c.H).OrderBy(h => h));
        Assert.Equal(19, steps.Cells.Count);

        var db = TestUtil.Minimal();
        var tex = new AssetDef { Id = "stone_tex", Name = "Камень", Kind = AssetKind.Texture, Path = "assets/textures/stone.png" };
        var mat = new AssetDef { Id = "stone_mat", Name = "Камень", Kind = AssetKind.Material, Texture = "stone_tex" };
        db.Add(tex);
        db.Add(mat);
        int terrains = db.All<TerrainDef>().Count();
        var p = MaterialPreview.ForAsset(db, mat)!;
        var tile = p.Terrain(MaterialPreview.TileId)!;
        Assert.Equal("stone_mat", tile.Look.TopMaterial);
        Assert.Equal("stone_mat", tile.Look.SideMaterial);
        Assert.NotNull(p.Get<AssetDef>("stone_tex")); // the texture the material reads
        Assert.NotNull(p.Terrain(MaterialPreview.FloorId));
        Assert.Equal("stone_tex", MaterialPreview.ForAsset(db, tex)!.Terrain(MaterialPreview.TileId)!.Look.TopTexture);
        Assert.Null(MaterialPreview.ForAsset(db, new AssetDef { Kind = AssetKind.Sound }));
        // The author's project: no new terrain.
        Assert.Equal(terrains, db.All<TerrainDef>().Count());
        var map = HexTactics.Core.Sim.BattleMap.Build(MaterialPreview.Map(MaterialPreview.FloorId, MaterialPreview.TileId, PreviewShape.Steps), p);
        Assert.Equal(7, map.Cells.Count(c => c.Terrain.Id == MaterialPreview.TileId));
    }
}
