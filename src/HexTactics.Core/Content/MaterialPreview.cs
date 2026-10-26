using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

public enum PreviewShape
{
    [Ed("Один гекс", "Плитка в центре, вокруг — пол")] Single,
    [Ed("Группа соседей", "Плитка и шесть её соседей: стыки и повторение текстуры")] Group,
    [Ed("Ступени", "Плитки по кругу на высотах 0…6: боковые грани и фаски на разных перепадах")] Steps,
}

/// <summary>
/// The preview patch of a tile (ТЗ ч. 5, 7.4; docs/DECISIONS_5.md §89): a hexagon of floor with the tile alone, with its six
/// neighbours, or as a ring of steps rising by one level each — the same renderer as the battle draws it. A resource is shown
/// through a tile of its own in a small separate database, so the author's terrains are never touched.
/// </summary>
public static class MaterialPreview
{
    public const string TileId = "__preview_tile";
    public const string FloorId = "__preview_floor";

    /// <summary>A hexagon of radius 2 of <paramref name="floor"/> with <paramref name="tile"/> placed by the shape.</summary>
    public static MapDef Map(string floor, string tile, PreviewShape shape, int baseHeight = 0)
    {
        var map = MapTemplates.Generate("material_preview", MapShape.Hexagon, 2, 0, floor);
        var ring = HexCoord.Zero.Neighbors().ToList();
        foreach (var c in map.Cells)
        {
            if (c.C == HexCoord.Zero || shape == PreviewShape.Single && c.C == new HexCoord(1, 0))
            {
                c.T = tile;
                c.H = baseHeight;
                continue;
            }
            int k = ring.IndexOf(c.C);
            if (k < 0 || shape == PreviewShape.Single) continue;
            c.T = tile;
            c.H = baseHeight + (shape == PreviewShape.Steps ? k + 1 : 0);
        }
        return map;
    }

    /// <summary>
    /// A database for the preview of a texture or a material: the floor of the project, a tile that wears the resource
    /// (a texture on top; a material on top and on the sides) and the resources it reads. Null — not a texture or a material.
    /// </summary>
    public static ContentDatabase? ForAsset(ContentDatabase db, AssetDef a)
    {
        if (a.Kind is not (AssetKind.Texture or AssetKind.Material)) return null;
        var p = new ContentDatabase { Rules = db.Rules };
        var floor = db.Terrain(MapTemplates.DefaultFloor) ?? db.All<TerrainDef>().FirstOrDefault();
        var f = floor != null ? ContentJson.Clone(floor) : new TerrainDef { Name = "Пол", Color = "#3a3f47" };
        f.Id = FloorId;
        p.Add(f);
        var tile = new TerrainDef { Id = TileId, Name = a.DisplayName, Color = "#8d939c", Look = new TerrainLookDef { Material = TerrainMaterial.Matte, OutlineColor = "#8d9aa8" } };
        if (a.Kind == AssetKind.Texture) tile.Look.TopTexture = a.Id;
        else
        {
            tile.Look.TopMaterial = a.Id;
            tile.Look.SideMaterial = a.Id;
        }
        p.Add(tile);
        // The resource and what it refers to (a material's textures and maps; the floor's own resources).
        var seen = new HashSet<string>();
        void AddAsset(string id)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id) || db.Get<AssetDef>(id) is not { } x) return;
            p.Add(x);
            foreach (var r in ContentDependencies.Uses(x).Where(u => u.Category == DefCategory.Asset)) AddAsset(r.Id);
        }
        AddAsset(a.Id);
        foreach (var r in ContentDependencies.Uses(f).Where(u => u.Category == DefCategory.Asset)) AddAsset(r.Id);
        return p;
    }
}
