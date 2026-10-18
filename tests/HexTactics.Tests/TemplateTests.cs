using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Geometry;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

public class TemplateTests
{
    [Fact]
    public void StandardTemplate_Is10x8_AndFirstInList()
    {
        var db = TestUtil.Shipped();
        var ordered = MapTemplates.Ordered(db);
        Assert.Equal(MapTemplates.StandardId, ordered[0].Id);
        var t = ordered[0];
        Assert.Equal((10, 8), t.Size());
        Assert.Equal(80, t.Cells.Count);
        // 10 columns in every row, 8 rows.
        var rows = t.Cells.GroupBy(c => c.C.R).ToList();
        Assert.Equal(8, rows.Count);
        Assert.All(rows, r => Assert.Equal(10, r.Count()));

        var map = MapTemplates.CreateMap(t, "m1", db);
        Assert.Equal(80, map.Cells.Count);
        Assert.Equal(t.Id, map.TemplateId);
        var sim = new Simulation(db, map, new SimOptions { FogOfWar = false });
        Assert.Equal(80, sim.Map.Count);
        Assert.Empty(sim.BuildIssues);
    }

    [Fact]
    public void BuiltInTemplates_AllShipped_AndRestorable()
    {
        var db = TestUtil.Shipped();
        foreach (var id in MapTemplates.BuiltInIds)
        {
            var shipped = db.Get<MapTemplateDef>(id);
            Assert.NotNull(shipped);
            Assert.True(shipped!.BuiltIn);
            var restored = MapTemplates.CreateBuiltIn(id, db)!;
            Assert.Equal(shipped.Cells.Count, restored.Cells.Count);
            Assert.Equal(shipped.Markers.Count, restored.Markers.Count);
        }
        Assert.Empty(db.Get<MapTemplateDef>("empty")!.Cells);
        Assert.NotEmpty(db.Get<MapTemplateDef>("defense_12x8")!.Routes);
    }

    [Fact]
    public void SaveMapAsTemplate_RespectsSelectedParts()
    {
        var db = TestUtil.Shipped();
        var map = MapTemplates.CreateMap(db.Get<MapTemplateDef>("defense_12x8")!, "def", db);
        map.Cells[5].H = 2;
        var parts = TemplatePartsDef.ShapeOnly();
        parts.Obstacles = true;
        var t = MapTemplates.FromMap(map, "my_tpl", parts, db, "Мой шаблон");

        Assert.Equal(map.Cells.Count, t.Cells.Count);
        Assert.Empty(t.Markers);
        Assert.Empty(t.Routes);
        Assert.All(t.Cells, c => Assert.Equal(0, c.H));
        // Obstacles kept, everything else became floor.
        Assert.Contains(t.Cells, c => c.T == "wall");
        Assert.All(t.Cells.Where(c => c.T != "wall"), c => Assert.Equal(MapTemplates.DefaultFloor, c.T));
        Assert.False(t.BuiltIn);
        Assert.Equal(MapTemplates.UserGroup, t.Group);
    }

    [Fact]
    public void UserTemplate_SaveLoad_AndImport()
    {
        var db = TestUtil.Shipped();
        var map = MapTemplates.Generate("odd", MapShape.Hexagon, 2, 0, "floor");
        map.Cells.RemoveAll(c => c.C == new HexCoord(1, 0)); // irregular shape
        map.Markers.Add(new MapMarkerDef { Id = "p", Type = MarkerType.PlayerStart, C = HexCoord.Zero });
        var t = MapTemplates.FromMap(map, "odd_tpl", new TemplatePartsDef(), db, "Неровный");
        Assert.True(db.Add(t));

        // Save → load through the regular content pipeline.
        var storage = new MemoryStorage();
        ContentIO.Save(db, storage);
        Assert.NotNull(storage.ReadText("map_templates/odd_tpl.json"));
        var loaded = ContentIO.Load(storage);
        Assert.DoesNotContain(loaded.Issues, i => i.Severity == IssueSeverity.Error);
        var lt = loaded.Db.Get<MapTemplateDef>("odd_tpl")!;
        Assert.Equal(18, lt.Cells.Count);
        Assert.Single(lt.Markers);

        // Export as a single object and import into another library.
        var json = ContentIO.ExportDef(lt);
        var issues = new List<ContentIssue>();
        var imported = ContentIO.ImportDef(json, issues) as MapTemplateDef;
        Assert.NotNull(imported);
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(18, imported!.Cells.Count);
    }

    [Fact]
    public void ChangingTemplate_DoesNotChangeMaps_UntilExplicitUpdate()
    {
        var db = TestUtil.Shipped();
        var t = MapTemplates.Derive(db.Get<MapTemplateDef>(MapTemplates.StandardId)!, "std_copy");
        db.Add(t);
        var map = MapTemplates.CreateMap(t, "from_tpl", db);
        db.Add(map);
        Assert.Equal(1, map.TemplateVersion);

        // Edit the template (new version): the map is untouched.
        t.Cells[0].H = 3;
        t.Cells.RemoveAt(t.Cells.Count - 1);
        t.Version = 2;
        Assert.Equal(80, map.Cells.Count);
        Assert.Equal(0, map.Cells[0].H);
        var deps = MapTemplates.MapsFrom(db, t);
        Assert.Single(deps);
        Assert.True(deps[0].Outdated);

        // Explicit update.
        MapTemplates.ApplyToMap(t, map, db);
        Assert.Equal(79, map.Cells.Count);
        Assert.Equal(3, map.Cells.First(c => c.C == t.Cells[0].C).H);
        Assert.Equal(2, map.TemplateVersion);
        Assert.False(MapTemplates.MapsFrom(db, t)[0].Outdated);
    }

    [Fact]
    public void Generator_CustomSizes()
    {
        Assert.Equal(7 * 5, MapTemplates.Generate("a", MapShape.Rectangle, 7, 5, "floor").Cells.Count);
        Assert.Equal(6 * 4, MapTemplates.Generate("b", MapShape.Rhombus, 6, 4, "floor").Cells.Count);
        Assert.Equal(19, MapTemplates.Generate("c", MapShape.Hexagon, 2, 0, "floor").Cells.Count);
        Assert.Empty(MapTemplates.Generate("d", MapShape.Rectangle, 0, 0, "floor").Cells);
    }
}

public class CameraFitTests
{
    private const float Tilt = 60f, Fov = 38f;

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1080)]
    [InlineData(1366, 768)]
    public void Standard10x8_FitsWorkingArea_WithReadableHexes(int w, int h)
    {
        var pts = CameraFit.RectanglePoints(10, 8);
        var insets = ScreenInsets.BattleHud(w, h);
        var fit = CameraFit.Fit(pts, w, h, insets, Tilt, 0, Fov);
        Assert.True(fit.Fits);
        foreach (var p in pts)
        {
            Assert.True(CameraFit.Project(p, fit.Pivot, fit.Distance, Tilt, 0, Fov, w, h, out var px));
            Assert.InRange(px.X, insets.Left - 0.5f, w - insets.Right + 0.5f);
            Assert.InRange(px.Y, insets.Top - 0.5f, h - insets.Bottom + 0.5f);
        }
        // Large, comfortable hexes; scales with resolution.
        Assert.InRange(fit.HexPixelWidth, 64f * h / 1080f, 170f * h / 1080f);
    }

    [Fact]
    public void FullHd_HexSize_IsLargeButMapDoesNotDominate()
    {
        var fit = CameraFit.Fit(CameraFit.RectanglePoints(10, 8), 1920, 1080, ScreenInsets.BattleHud(1920, 1080), Tilt, 0, Fov);
        Assert.InRange(fit.HexPixelWidth, 80f, 140f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(180)]
    public void Fit_WorksForAnyYaw_AndBiggerMapsNeedMoreDistance(float yaw)
    {
        var small = CameraFit.Fit(CameraFit.RectanglePoints(10, 8), 1920, 1080, ScreenInsets.BattleHud(1920, 1080), Tilt, yaw, Fov);
        var big = CameraFit.Fit(CameraFit.RectanglePoints(24, 16), 1920, 1080, ScreenInsets.BattleHud(1920, 1080), Tilt, yaw, Fov);
        Assert.True(small.Fits && big.Fits);
        Assert.True(big.Distance > small.Distance * 1.5f);
    }
}
