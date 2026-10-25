using System;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage B — the map editor (docs/DECISIONS_5.md §85): T01 and the operations of the new tools.</summary>
public class Part5MapTests
{
    private static (ContentDatabase Db, MapDef Map) Standard()
    {
        var db = TestUtil.Shipped();
        var t = MapTemplates.CreateBuiltIn(MapTemplates.StandardId, db)!;
        var map = MapTemplates.CreateMap(t, "p5_map", db, "Карта T01");
        db.Add(map);
        return (db, map);
    }

    [Fact]
    public void T01_NewMap10x8_BrushStrokeIsOneUndo_Redo_SaveAndOpenWithoutLoss()
    {
        var (db, map) = Standard();
        Assert.Equal(80, map.Cells.Count);
        var history = new EditHistory(db);
        // One stroke of the brush over ten cells.
        history.Touch(map);
        var stroke = map.Cells.Take(10).Select(c => c.C).ToList();
        foreach (var c in map.Cells.Where(c => stroke.Contains(c.C))) c.T = "lava";
        history.Commit("кисть");
        Assert.Equal(10, map.Cells.Count(c => c.T == "lava"));
        // One undo — the whole stroke; redo — all of it again.
        history.Undo();
        var undone = db.Get<MapDef>("p5_map")!;
        Assert.Equal(0, undone.Cells.Count(c => c.T == "lava"));
        history.Redo();
        var redone = db.Get<MapDef>("p5_map")!;
        Assert.Equal(10, redone.Cells.Count(c => c.T == "lava"));
        // Saved and opened again: nothing lost.
        var dir = Path.Combine(Path.GetTempPath(), "hex_t01_" + Guid.NewGuid().ToString("N"));
        try
        {
            ContentIO.Save(db, new FileSystemStorage(dir));
            var back = ContentIO.Load(new FileSystemStorage(dir)).Db.Get<MapDef>("p5_map")!;
            Assert.Equal(ContentJson.Serialize(redone), ContentJson.Serialize(back));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Rect_TakesTheCellsBetweenTwoCornersOfTheProjection()
    {
        var (_, map) = Standard();
        var a = HexLayout.FromOffset(1, 1);
        var b = HexLayout.FromOffset(4, 3);
        var rect = MapOps.Rect(map, a, b);
        Assert.Contains(a, rect);
        Assert.Contains(b, rect);
        Assert.InRange(rect.Count, 9, 15);
        var (ax, ay) = HexLayout.ToPlane(a);
        var (bx, by) = HexLayout.ToPlane(b);
        Assert.All(rect, c =>
        {
            var (x, y) = HexLayout.ToPlane(c);
            Assert.InRange(x, Math.Min(ax, bx) - 1e-6, Math.Max(ax, bx) + 1e-6);
            Assert.InRange(y, Math.Min(ay, by) - 1e-6, Math.Max(ay, by) + 1e-6);
        });
        // The corners of the whole field (the last row is odd, shifted right): every cell.
        Assert.Equal(map.Cells.Count, MapOps.Rect(map, HexLayout.FromOffset(0, 0), HexLayout.FromOffset(9, 7)).Count);
    }

    [Fact]
    public void Copy_RotateBy60_Mirror_Paste_AndStampAFromTemplate()
    {
        var (db, map) = Standard();
        var pick = new[] { HexLayout.FromOffset(2, 2), HexLayout.FromOffset(3, 2), HexLayout.FromOffset(2, 3) };
        var terrains = new[] { "lava", "wall", "shallow_water" };
        for (int i = 0; i < 3; i++) map.Cells.First(c => c.C == pick[i]).T = terrains[i];
        map.Cells.First(c => c.C == pick[0]).H = 2;
        var clip = MapOps.Copy(map, pick, "проба");
        Assert.Equal(3, clip.Count);
        Assert.Contains(clip.Cells, c => c.C == HexCoord.Zero);
        // Six turns of 60° and two mirrors give the same piece back.
        var same = MapOps.Transform(MapOps.Transform(clip, 6, false), 0, false);
        Assert.Equal(clip.Cells.Select(c => (c.C, c.T)).OrderBy(x => x.C.Q).ThenBy(x => x.C.R), same.Cells.Select(c => (c.C, c.T)).OrderBy(x => x.C.Q).ThenBy(x => x.C.R));
        var twice = MapOps.Transform(MapOps.Transform(clip, 0, true), 0, true);
        Assert.Equal(clip.Cells.Select(c => (c.C, c.T)).OrderBy(x => x.C.Q).ThenBy(x => x.C.R), twice.Cells.Select(c => (c.C, c.T)).OrderBy(x => x.C.Q).ThenBy(x => x.C.R));
        // A turn keeps the cells and their shape (distances between them).
        var turned = MapOps.Transform(clip, 1, false);
        Assert.Equal(terrains.OrderBy(x => x), turned.Cells.Select(c => c.T).OrderBy(x => x));
        int Dist(MapClip k, string t1, string t2) => HexCoord.Distance(k.Cells.First(c => c.T == t1).C, k.Cells.First(c => c.T == t2).C);
        Assert.Equal(Dist(clip, "lava", "wall"), Dist(turned, "lava", "wall"));
        Assert.Equal(2, turned.Cells.First(c => c.T == "lava").H);
        // Pasted elsewhere: replaces cells of the map; outside the map — skipped or added.
        var at = HexLayout.FromOffset(6, 5);
        var (changed, skipped) = MapOps.Paste(map, turned, at, addCells: false);
        Assert.Equal(3, changed.Count + skipped);
        Assert.All(changed, c => Assert.Contains(map.Cells, x => x.C == c && terrains.Contains(x.T)));
        var edge = HexLayout.FromOffset(9, 7);
        var (c2, s2) = MapOps.Paste(map, clip, edge, addCells: false);
        Assert.True(s2 > 0);
        int before = map.Cells.Count;
        var (c3, _) = MapOps.Paste(map, clip, edge, addCells: true);
        Assert.Equal(before + s2, map.Cells.Count);
        // A template is a stamp.
        var stamp = MapOps.FromTemplate(MapTemplates.CreateBuiltIn("small_arena", db)!);
        Assert.True(stamp.Count > 10);
        Assert.Contains(stamp.Cells, c => c.C == HexCoord.Zero);
    }

    [Fact]
    public void Mixed_ShowsTheDifferingProperties_Erase_ClearsOverrides()
    {
        var (db, map) = Standard();
        var a = map.Cells[0];
        var b = map.Cells[1];
        a.T = "lava";
        b.T = "wall";
        a.H = b.H = 1;
        a.Cover = 0.5;
        var mixed = MapOps.Mixed(new[] { a, b }, db);
        Assert.True(mixed.ContainsKey(nameof(MapCellDef.T)));
        Assert.Contains("Лава", mixed[nameof(MapCellDef.T)]);
        Assert.Contains("Стена", mixed[nameof(MapCellDef.T)]);
        Assert.True(mixed.ContainsKey(nameof(MapCellDef.Cover)));
        Assert.False(mixed.ContainsKey(nameof(MapCellDef.H)));
        Assert.False(mixed.ContainsKey(nameof(MapCellDef.C)));
        Assert.Empty(MapOps.Mixed(new[] { a }, db));
        a.Effects = new() { "fire" };
        a.Tags = new() { "x" };
        Assert.True(MapOps.Erase(a));
        Assert.Null(a.Cover);
        Assert.Null(a.Effects);
        Assert.Null(a.Tags);
        Assert.Equal("lava", a.T);
        Assert.False(MapOps.Erase(a));
    }

    [Fact]
    public void Resize_ShowsWhatIsLost_BeforeTheChange()
    {
        var (db, map) = Standard();
        var far = HexLayout.FromOffset(9, 3);
        map.Markers.Add(new MapMarkerDef { Id = "far", Type = MarkerType.Objective, C = far, Label = "Дальняя цель" });
        var sc = new ScenarioDef { Id = "p5_resize", Name = "Сцена", Map = map.Id };
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, HexLayout.FromOffset(8, 4)));
        db.Add(sc);
        var preview = MapOps.ResizePreview(db, map, 8, 8);
        Assert.Equal(16, preview.Removed.Count);
        Assert.Empty(preview.Added);
        Assert.Contains(preview.Losses, l => l.Contains("Дальняя цель"));
        Assert.Contains(preview.Losses, l => l.Contains("сценарий «Сцена»") && l.Contains("объект"));
        Assert.Equal(80, map.Cells.Count); // only a preview
        var done = MapOps.Resize(db, map, 12, 6, "floor");
        Assert.Equal(12 * 6, map.Cells.Count);
        Assert.Equal(20, done.Removed.Count);   // rows 6 and 7
        Assert.Equal(12, done.Added.Count);     // columns 10 and 11 of rows 0–5
        Assert.All(done.Added, c => Assert.Equal("floor", map.Cells.First(x => x.C == c).T));
    }
}
