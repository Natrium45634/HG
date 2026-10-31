using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum EffectOrigin
{
    Terrain,
    MapStart,
    Skill,
    Synergy,
    Spread,
    Scenario,
    Corpse,
    /// <summary>Created by element damage (an element's cell effects or an interaction).</summary>
    Element,
}

/// <summary>Runtime instance of a cell effect (fire, poison...).</summary>
public sealed class CellEffectInstance
{
    public CellEffectDef Def { get; init; } = null!;
    public int Level { get; set; } = 1;
    /// <summary>Remaining ticks; -1 = permanent.</summary>
    public long RemainingTicks { get; set; } = -1;
    public long TotalTicks { get; set; } = -1;
    public long TickTimer { get; set; }
    public long SpreadTimer { get; set; }
    public int Generation { get; set; }
    public int SourceUnit { get; set; } = -1;
    /// <summary>The faction of the creator when the field was made (part 6, §105: relations «при создании», diagnostics).</summary>
    public string SourceFaction { get; set; } = "";
    public bool Intrinsic { get; set; }
    public long Serial { get; init; }
    /// <summary>Where the instance came from (map/terrain vs action) — docs/DECISIONS_2.md §30.</summary>
    public EffectOrigin Origin { get; set; } = EffectOrigin.Skill;
    public long AdjacentTimer { get; set; }

    public double Fraction => TotalTicks <= 0 ? 1.0 : Math.Clamp((double)RemainingTicks / TotalTicks, 0, 1);
}

/// <summary>Runtime cell: terrain type + height + per-cell overrides + current effects + occupancy.</summary>
public sealed class Cell
{
    internal Cell(HexCoord coord, int index, TerrainDef terrain, int height)
    {
        Coord = coord;
        Index = index;
        Terrain = terrain;
        Height = height;
    }

    public HexCoord Coord { get; }
    public int Index { get; }
    public TerrainDef Terrain { get; internal set; }
    public int Height { get; internal set; }

    public double? MoveCostOverride { get; internal set; }
    public bool Blocked { get; internal set; }
    public bool? BlocksLoSOverride { get; internal set; }
    public double? CoverOverride { get; internal set; }
    public bool? AllowPlacementOverride { get; internal set; }
    public int ObstacleHp { get; internal set; }
    public List<string>? AllowedEffects { get; internal set; }
    public List<string> Tags { get; } = new();
    public string Name { get; internal set; } = "";
    public string Description { get; internal set; } = "";

    public List<CellEffectInstance> Effects { get; } = new();
    public List<Unit> Occupants { get; } = new();
    /// <summary>Units currently stepping into this cell (several when the cell may be shared).</summary>
    public List<Unit> Reservations { get; } = new();
    /// <summary>A group order reserved this hex as the end point of this unit (docs/DECISIONS_3.md §44); -1 — free.</summary>
    public int DestinationOf { get; set; } = -1;
    public Unit? ReservedBy => Reservations.Count > 0 ? Reservations[0] : null;

    /// <summary>Neighbor cells by direction index (null when missing).</summary>
    public Cell?[] Neighbors { get; } = new Cell?[6];

    public bool TerrainBlocksLoS => BlocksLoSOverride ?? Terrain.BlocksLoS;

    public bool EffectBlocksLoS
    {
        get
        {
            foreach (var e in Effects)
                if (e.Def.BlocksLoS)
                    return true;
            return false;
        }
    }

    public bool EffectBlocksMovement
    {
        get
        {
            foreach (var e in Effects)
                if (e.Def.BlocksMovement)
                    return true;
            return false;
        }
    }

    public double Cover => CoverOverride ?? Terrain.Cover;

    public bool AllowsPlacement => !Blocked && (AllowPlacementOverride ?? Terrain.AllowsPlacement);

    /// <summary>Top of the obstacle in height levels (for line of sight).</summary>
    public double ObstacleTop => Height + (TerrainBlocksLoS ? Math.Max(Terrain.ObstacleHeight, 0.0) : 0.0);

    public bool IsHazard
    {
        get
        {
            if (Terrain.Hazard) return true;
            foreach (var e in Effects)
                if (e.Def.Hazard)
                    return true;
            return false;
        }
    }

    public double EffectMoveCost
    {
        get
        {
            double s = 0;
            foreach (var e in Effects) s += e.Def.MoveCostAdd;
            return s;
        }
    }

    public bool HasTag(string tag) => Tags.Contains(tag) || Terrain.Tags.Contains(tag);

    public bool HasEffect(string id)
    {
        foreach (var e in Effects)
            if (e.Def.Id == id)
                return true;
        return false;
    }

    public CellEffectInstance? FindEffect(string id)
    {
        foreach (var e in Effects)
            if (e.Def.Id == id)
                return e;
        return null;
    }

    public Unit? Occupant => Occupants.Count > 0 ? Occupants[0] : null;

    public bool IsOccupiedOrReserved(Unit? except)
    {
        foreach (var u in Occupants)
            if (u != except && u.Alive)
                return true;
        foreach (var u in Reservations)
            if (u != except && u.Alive)
                return true;
        return false;
    }

    /// <summary>Alive units in the cell in the given layer.</summary>
    public IEnumerable<Unit> UnitsInLayer(string layer)
    {
        foreach (var u in Occupants)
            if (u.Alive && u.Layer == layer)
                yield return u;
    }

    public string DisplayName => string.IsNullOrEmpty(Name) ? Terrain.DisplayName : Name;

    public override string ToString() => $"{Coord} {Terrain.Id} h{Height}";
}

/// <summary>Runtime map built from a <see cref="MapDef"/>. Missing cells are never passable.</summary>
public sealed class BattleMap
{
    private readonly Dictionary<HexCoord, Cell> _byCoord = new();
    private readonly List<Cell> _cells = new();

    public IReadOnlyList<Cell> Cells => _cells;
    public int Count => _cells.Count;
    public MapDef Def { get; private set; } = new();
    public double Darkness { get; set; }

    /// <summary>Incremented on every change that may affect pathing or line of sight.</summary>
    public int Version { get; private set; }

    /// <summary>Incremented on visual-only changes too (effects, terrain) — the renderer watches it.</summary>
    public int VisualVersion { get; private set; }

    /// <summary>Incremented when terrain type or height of a cell changes (terrain mesh rebuild).</summary>
    public int TerrainVersion { get; private set; }

    public int MinX { get; private set; }
    public int MaxX { get; private set; }

    public void Touch(bool affectsPathing = true)
    {
        if (affectsPathing) Version++;
        VisualVersion++;
    }

    public Cell? Get(HexCoord c) => _byCoord.TryGetValue(c, out var cell) ? cell : null;

    public bool Contains(HexCoord c) => _byCoord.ContainsKey(c);

    /// <summary>Lowest possible entry cost of any cell (admissible A* heuristic factor).</summary>
    public double MinStepCost { get; private set; } = 1.0;

    public static BattleMap Build(MapDef def, ContentDatabase db, List<ContentIssue>? issues = null)
    {
        var map = new BattleMap { Def = def, Darkness = def.Rules.Darkness };
        var fallback = db.All<TerrainDef>().FirstOrDefault() ?? new TerrainDef { Id = "ground", Name = "Земля" };
        foreach (var cd in def.Cells)
        {
            if (map._byCoord.ContainsKey(cd.C)) continue;
            var terrain = db.Terrain(cd.T);
            if (terrain == null)
            {
                issues?.Add(new ContentIssue(IssueSeverity.Error, "Map:" + def.Id, cd.C.ToString(), $"Неизвестная местность '{cd.T}' — использована '{fallback.Id}'"));
                terrain = fallback;
            }
            var cell = new Cell(cd.C, map._cells.Count, terrain, cd.H)
            {
                MoveCostOverride = cd.MoveCost,
                Blocked = cd.Blocked ?? false,
                BlocksLoSOverride = cd.BlocksLoS,
                CoverOverride = cd.Cover,
                AllowPlacementOverride = cd.AllowPlacement,
                ObstacleHp = cd.ObstacleHp ?? terrain.DestructibleHp,
                AllowedEffects = cd.AllowedEffects == null ? null : new List<string>(cd.AllowedEffects),
                Name = cd.Name ?? "",
                Description = cd.Description ?? "",
            };
            if (cd.Tags != null) cell.Tags.AddRange(cd.Tags);
            map._byCoord[cd.C] = cell;
            map._cells.Add(cell);
        }
        foreach (var c in map._cells)
            for (int i = 0; i < 6; i++)
                c.Neighbors[i] = map.Get(c.Coord.Neighbor((HexDirection)i));
        map.RecomputeMinCost(db);
        return map;
    }

    public void RecomputeMinCost(ContentDatabase db)
    {
        double min = double.MaxValue;
        foreach (var t in db.All<TerrainDef>())
        {
            min = Math.Min(min, t.MoveCost);
            foreach (var v in t.MoveCostByCategory.Values) min = Math.Min(min, v);
        }
        foreach (var c in _cells)
            if (c.MoveCostOverride is double o)
                min = Math.Min(min, o);
        foreach (var e in db.All<CellEffectDef>())
            if (e.MoveCostAdd < 0)
                min += e.MoveCostAdd;
        MinStepCost = Math.Max(0.05, min == double.MaxValue ? 1.0 : min);
    }

    public void SetTerrain(Cell cell, TerrainDef terrain)
    {
        cell.Terrain = terrain;
        cell.ObstacleHp = terrain.DestructibleHp;
        TerrainVersion++;
        Touch();
    }

    public void SetHeight(Cell cell, int h)
    {
        cell.Height = h;
        TerrainVersion++;
        Touch();
    }

    public IEnumerable<Cell> InRadius(HexCoord center, int radius)
    {
        foreach (var c in HexCoord.Spiral(center, radius))
        {
            var cell = Get(c);
            if (cell != null) yield return cell;
        }
    }
}
