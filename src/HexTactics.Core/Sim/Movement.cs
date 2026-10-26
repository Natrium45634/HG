using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum StepCheck
{
    Ok,
    Missing,
    Impassable,
    Blocked,
    Barrier,
    TooHigh,
    TooLow,
    Occupied,
    /// <summary>The cell is a pit that affects the mover (only forced movement may enter it).</summary>
    Pit,
    /// <summary>Beyond the leash of a called fighter (part 6, 3.10).</summary>
    Leash,
}

/// <summary>Layer helpers (docs/DECISIONS_2.md §22).</summary>
public static class LayerResolver
{
    private static readonly Dictionary<string, LayerDef> Fallbacks = new(StringComparer.Ordinal);

    public static string LayerIdOf(ContentDatabase db, UnitDef def) =>
        !string.IsNullOrEmpty(def.Layer) ? def.Layer : string.IsNullOrEmpty(db.Rules.Layers.Default) ? Layers.Ground : db.Rules.Layers.Default;

    /// <summary>Layer definition (an implicit default one when the content has none, e.g. old packs).</summary>
    public static LayerDef Get(ContentDatabase db, string id)
    {
        var d = db.Layer(id);
        if (d != null) return d;
        lock (Fallbacks)
        {
            if (!Fallbacks.TryGetValue(id, out d))
            {
                d = new LayerDef { Id = id, Name = id };
                Fallbacks[id] = d;
            }
            return d;
        }
    }

    public static bool Collides(LayerDef a, string b, ContentDatabase db)
    {
        if (a.Id == b) return true;
        if (a.CollidesWith.Contains(b)) return true;
        var bd = db.Layer(b);
        return bd != null && bd.CollidesWith.Contains(a.Id);
    }

    public static bool IsMelee(SkillDef s) => s.Kind == SkillKind.Attack && !s.Ranged;

    /// <summary>Layers a skill can hit: its own list or the rule default by skill kind.</summary>
    public static IReadOnlyList<string> TargetLayers(RulesDef rules, SkillDef s)
    {
        if (s.TargetLayers.Count > 0) return s.TargetLayers;
        if (IsMelee(s)) return rules.Layers.MeleeTargets;
        if (s.Kind == SkillKind.Attack) return rules.Layers.RangedTargets;
        return rules.Layers.SpellTargets;
    }

    public static bool CanTarget(RulesDef rules, SkillDef s, Unit target) => target.Layer.Length == 0 || Contains(TargetLayers(rules, s), target.Layer);

    public static bool CanCastFrom(SkillDef s, Unit caster) => s.CasterLayers.Count == 0 || s.CasterLayers.Contains(caster.Layer);

    public static IReadOnlyList<string> EffectLayers(RulesDef rules, CellEffectDef e) =>
        e.AffectsLayers.Count > 0 ? e.AffectsLayers : rules.Layers.CellEffectLayers;

    public static bool EffectAffects(RulesDef rules, CellEffectDef e, Unit u) => Contains(EffectLayers(rules, e), u.Layer);

    public static bool EffectAffectsLayer(RulesDef rules, CellEffectDef e, string layer) => Contains(EffectLayers(rules, e), layer);

    private static bool Contains(IReadOnlyList<string> list, string v)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == v)
                return true;
        return false;
    }

    /// <summary>Does this pit affect a creature (layer, immunity, rescue statuses)?</summary>
    public static bool PitAffects(TerrainDef t, string layer, LayerDef layerDef, UnitDef? def, Unit? unit)
    {
        if (t.Kind != TerrainKind.Pit) return false;
        if (layerDef.ImmuneToPits) return false;
        var pit = t.Pit;
        if (pit.AffectedLayers.Count > 0 ? !pit.AffectedLayers.Contains(layer) : layer != Layers.Ground) return false;
        if (def != null)
            foreach (var im in pit.Immune)
            {
                if (im == def.CreatureCategory || im == def.Id) return false;
                if (im.StartsWith("tag:", StringComparison.Ordinal) && def.Tags.Contains(im.Substring(4))) return false;
            }
        if (unit != null)
        {
            foreach (var s in pit.RescueStatuses)
                if (unit.FindStatus(s) != null)
                    return false;
        }
        return true;
    }
}

/// <summary>Who is moving: movement category, layer, stacking rules, faction (spawn checks have no unit yet).</summary>
public readonly struct MoveContext
{
    public MoveContext(RulesDef rules, ContentDatabase db, string moveCategory, LayerDef layer, Unit? self, UnitDef? def, string faction, StackingMode stacking)
    {
        Rules = rules;
        Db = db;
        MoveCategory = moveCategory;
        Layer = layer;
        Self = self;
        Def = def ?? self?.Def;
        Faction = faction;
        Stacking = stacking;
    }

    public RulesDef Rules { get; }
    public ContentDatabase Db { get; }
    public string MoveCategory { get; }
    public LayerDef Layer { get; }
    public Unit? Self { get; }
    public UnitDef? Def { get; }
    public string Faction { get; }
    public StackingMode Stacking { get; }

    public static MoveContext For(Simulation sim, Unit u) =>
        new(sim.Rules, sim.Content, u.Def.MoveCategory, u.LayerDef, u, u.Def, u.FactionId, sim.Stacking)
        {
            Sim = sim, Extra = MovementRules.ExtraMoves(sim, u), Size = u.Stat(Stats.Size),
        };

    /// <summary>The simulation (passives of the mover); null for map analysis and tools.</summary>
    public Simulation? Sim { get; init; }

    /// <summary>Extra move categories acting now (docs/DECISIONS_4.md §60): passable by any of them.</summary>
    public IReadOnlyList<string> Extra { get; init; } = Array.Empty<string>();

    /// <summary>The «размер» of the mover (narrow passages).</summary>
    public double Size { get; init; } = 1;

    /// <summary>The terrain lets the mover through by its main or any acting extra move category.</summary>
    public bool Passable(TerrainDef t)
    {
        if (t.IsPassableFor(MoveCategory)) return true;
        foreach (var x in Extra)
            if (t.IsPassableFor(x))
                return true;
        return false;
    }

    public static MoveContext For(Simulation sim, UnitDef def, string? faction)
    {
        var layer = LayerResolver.Get(sim.Content, LayerResolver.LayerIdOf(sim.Content, def));
        var extra = sim.Categories.Of(def).MoveCategories.Concat(def.MoveTypes.Where(m => m.Condition == null).Select(m => m.Category)).Distinct().ToList();
        double size = def.Stats.TryGetValue(Stats.Size, out var sz) ? sz : sim.Content.Stat(Stats.Size)?.Default ?? 1;
        return new(sim.Rules, sim.Content, def.MoveCategory, layer, null, def, string.IsNullOrEmpty(faction) ? def.Faction : faction!, sim.Stacking) { Sim = sim, Extra = extra, Size = size };
    }

    /// <summary>Generic ground walker (map analysis, tools).</summary>
    public static MoveContext Walker(RulesDef rules, ContentDatabase db, string moveCategory = "walk") =>
        new(rules, db, moveCategory, LayerResolver.Get(db, rules.Layers.Default), null, null, "", StackingMode.ByUnitType);
}

/// <summary>Who may share a cell (docs/DECISIONS_2.md §22, §26). Collisions are counted per layer.</summary>
public static class Occupancy
{
    /// <summary>The mover and the other unit do not collide (different layers — a flyer over a walker).</summary>
    public static bool CanShare(in MoveContext c, Unit o) => !LayerResolver.Collides(c.Layer, o.Layer, c.Db);

    /// <summary>Other alive units in the cell (occupants and units stepping in) that collide with the mover's layer.</summary>
    public static int Colliding(in MoveContext c, Cell cell, List<Unit>? into = null)
    {
        int n = 0;
        void Visit(Unit o, ref int count, in MoveContext ctx, List<Unit>? sink)
        {
            if (!o.Alive || o == ctx.Self) return;
            if (!LayerResolver.Collides(ctx.Layer, o.Layer, ctx.Db)) return;
            if (sink != null)
            {
                if (sink.Contains(o)) return;
                sink.Add(o);
            }
            count++;
        }
        var list = into ?? new List<Unit>(4);
        foreach (var o in cell.Occupants) Visit(o, ref n, c, list);
        foreach (var o in cell.Reservations) Visit(o, ref n, c, list);
        return n;
    }

    private static bool AnyColliding(in MoveContext c, Cell cell)
    {
        foreach (var o in cell.Occupants)
            if (o.Alive && o != c.Self && LayerResolver.Collides(c.Layer, o.Layer, c.Db))
                return true;
        foreach (var o in cell.Reservations)
            if (o.Alive && o != c.Self && LayerResolver.Collides(c.Layer, o.Layer, c.Db))
                return true;
        return false;
    }

    /// <summary>Can the mover stand in (or step into) the cell given who is already there?</summary>
    public static bool CanEnter(in MoveContext c, Cell cell)
    {
        if (!AnyColliding(c, cell)) return true;
        var others = new List<Unit>(4);
        if (Colliding(c, cell, others) == 0) return true;
        bool allSameFaction = true;
        foreach (var o in others)
        {
            if (c.Faction.Length > 0 && c.Db.RelationOf(c.Faction, o.FactionId) == Relation.Enemy) return false;
            if (o.FactionId != c.Faction) allSameFaction = false;
        }
        // Part 1 rule: allies do not block paths when the rule is off.
        if (!c.Rules.Move.AlliesBlockPath && allSameFaction && c.Self != null) return true;
        switch (c.Stacking)
        {
            case StackingMode.ForbidAll:
                return false;
            case StackingMode.AllowAll:
                return others.Count + 1 <= Math.Max(1, c.Rules.Limits.MaxPerHex);
        }
        var def = c.Def;
        if (def == null || !def.Stacking.CanShare) return false;
        int limit = def.Stacking.MaxPerHex;
        foreach (var o in others)
        {
            if (!StackingDef.Mutual(def, o.Def)) return false;
            limit = Math.Min(limit, o.Def.Stacking.MaxPerHex);
        }
        return others.Count + 1 <= Math.Max(1, limit);
    }
}

/// <summary>
/// Step rules shared by pathfinding and movement execution (docs/DECISIONS.md §4,
/// docs/DECISIONS_2.md §21–22, §26).
/// </summary>
public static class MovementRules
{
    /// <summary>
    /// Extra move categories of a unit now: those of its categories and the template's types whose condition holds
    /// (docs/DECISIONS_4.md §60).
    /// </summary>
    public static IReadOnlyList<string> ExtraMoves(Simulation sim, Unit u)
    {
        if (u.CategoryMoves.Count == 0 && u.Def.MoveTypes.Count == 0) return Array.Empty<string>();
        var list = new List<string>(u.CategoryMoves);
        foreach (var m in u.Def.MoveTypes)
            if (m.Category != "" && !list.Contains(m.Category) && sim.Conditions.Evaluate(m.Condition, new ConditionContext(u, null, u.Pos, null)))
                list.Add(m.Category);
        return list;
    }

    /// <summary>The unit may stand on the terrain by any of its acting move categories.</summary>
    public static bool CanPassTerrain(Simulation sim, Unit u, TerrainDef t) => MoveContext.For(sim, u).Passable(t);

    /// <summary>
    /// A narrow passage (docs/DECISIONS_4.md §60): two opposite neighbours of the hex are impassable for the mover
    /// (or off the map) — creatures of «размер для узких мест» and more cannot enter it.
    /// </summary>
    public static bool IsNarrow(in MoveContext c, Cell to)
    {
        var map = c.Sim!.Map;
        var mc = c;
        bool Closed(HexCoord h)
        {
            var cell = map.Get(h);
            return cell == null || cell.Blocked || !mc.Passable(cell.Terrain);
        }
        for (int d = 0; d < 3; d++)
        {
            var dir = (HexDirection)d;
            if (Closed(to.Coord.Neighbor(dir)) && Closed(to.Coord.Neighbor(dir.Opposite()))) return true;
        }
        return false;
    }

    public const double MinStepCost = 0.1;

    /// <param name="forced">Push/pull: the mover may be shoved into a pit (and fall).</param>
    public static StepCheck Check(in MoveContext c, Cell? from, Cell? to, bool ignoreUnits, out double cost, bool forced = false)
    {
        cost = 0;
        if (to == null) return StepCheck.Missing;
        if (to.Blocked) return StepCheck.Blocked;
        // The leash of a called fighter (ТЗ ч. 6, 3.10): ordinary movement keeps within it; a push does not.
        if (!forced && c.Self is { LeashRadius: > 0, LeashAnchor: { } anchor } && HexCoord.Distance(anchor, to.Coord) > c.Self.LeashRadius) return StepCheck.Leash;
        var t = to.Terrain;
        var layer = c.Layer;
        if (t.Kind == TerrainKind.Pit)
        {
            if (LayerResolver.PitAffects(t, layer.Id, layer, c.Def, c.Self))
            {
                if (!(forced && t.Pit.Enterable)) return StepCheck.Pit;
            }
        }
        else if (!c.Passable(t))
        {
            bool over = layer.PassesObstaclesUpTo > 0 && Math.Max(0, t.ObstacleHeight) < layer.PassesObstaclesUpTo + 1e-9;
            if (!over) return StepCheck.Impassable;
        }
        if (c.Size >= c.Rules.Move.NarrowSize && c.Sim != null && IsNarrow(c, to)) return StepCheck.Impassable;
        foreach (var e in to.Effects)
            if (e.Def.BlocksMovement && LayerResolver.EffectAffectsLayer(c.Rules, e.Def, layer.Id))
                return StepCheck.Barrier;
        int dh = from == null ? 0 : to.Height - from.Height;
        bool ignoreHeight = layer.IgnoresHeight || c.MoveCategory == "fly";
        if (!ignoreHeight)
        {
            if (dh > c.Rules.Height.MaxClimb) return StepCheck.TooHigh;
            if (!forced && -dh > c.Rules.Height.MaxDrop) return StepCheck.TooLow; // can be pushed down a ledge
        }
        if (!ignoreUnits && !Occupancy.CanEnter(c, to)) return StepCheck.Occupied;
        cost = EntryCost(c, from, to);
        return StepCheck.Ok;
    }

    public static double EntryCost(in MoveContext c, Cell? from, Cell to)
    {
        double cost;
        if (c.Layer.IgnoresTerrainCost)
        {
            cost = 1;
        }
        else
        {
            cost = to.MoveCostOverride ?? to.Terrain.CostFor(c.MoveCategory);
            cost += to.EffectMoveCost;
            if (from != null && c.MoveCategory != "fly" && !c.Layer.IgnoresHeight)
            {
                int dh = to.Height - from.Height;
                if (dh > 0) cost += dh * c.Rules.Height.ClimbCostPerLevel;
                else if (dh < 0) cost += -dh * c.Rules.Height.DescendCostPerLevel;
            }
        }
        // Passives «стоимость движения по гексам» of the mover (docs/DECISIONS_3.md §46).
        if (c.Self is { PassiveInstances.Count: > 0 } self && c.Sim != null) cost *= c.Sim.Passives.TerrainFactor(self, to);
        if (c.Def != null && c.Def.Stacking.CanShare && c.Def.Stacking.CrowdMoveCost > 0 && (to.Occupants.Count > 0 || to.Reservations.Count > 0))
        {
            int crowd = Occupancy.Colliding(c, to);
            if (crowd > 0) cost += crowd * c.Def.Stacking.CrowdMoveCost;
        }
        return Math.Max(MinStepCost, cost);
    }

    public static string Describe(StepCheck s, Cell? to) => s switch
    {
        StepCheck.Ok => "",
        StepCheck.Missing => "клетки нет на карте",
        StepCheck.Impassable => $"местность непроходима ({to?.Terrain.DisplayName})",
        StepCheck.Blocked => "клетка закрыта",
        StepCheck.Barrier => "барьер на клетке",
        StepCheck.TooHigh => "слишком высокий подъём",
        StepCheck.TooLow => "слишком крутой спуск",
        StepCheck.Occupied => $"клетка занята{(to?.Occupant != null ? ": " + to.Occupant.Name : "")}",
        StepCheck.Pit => $"пропасть ({to?.Terrain.DisplayName})",
        StepCheck.Leash => "дальше поводка от места вызова",
        _ => s.ToString(),
    };
}
