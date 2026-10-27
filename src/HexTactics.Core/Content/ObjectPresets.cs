using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>
/// Interactive objects in the editor (ТЗ ч. 6, 6.4; docs/DECISIONS_6.md §100): save an object as a preset, put a preset on a
/// map, and «тест на пустой карте» — the object alone on a small field with a fighter of the player next to it.
/// </summary>
public static class ObjectPresets
{
    /// <summary>The preset of an object: a copy without its place and ID.</summary>
    public static ObjectPresetDef From(ScenarioObjectDef o, string id)
    {
        var copy = ContentJson.Clone(o);
        copy.Id = "";
        copy.Anchor = HexCoord.Zero;
        copy.Marker = "";
        return new ObjectPresetDef { Id = id, Name = o.DisplayName, Object = copy };
    }

    /// <summary>A copy of the preset's object at a hex of the scenario, with an ID free in it.</summary>
    public static ScenarioObjectDef Place(ScenarioDef sc, ObjectPresetDef p, HexCoord at)
    {
        var o = ContentJson.Clone(p.Object);
        string baseId = string.Join("_", (p.Id.StartsWith("obj_") ? p.Id[4..] : p.Id).Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
        if (baseId == "") baseId = "object";
        int n = 1;
        string id = baseId;
        while (sc.Objects.Any(x => x.Id == id)) id = $"{baseId}_{++n}";
        o.Id = id;
        o.Anchor = at;
        if (o.Name == "") o.Name = p.DisplayName;
        sc.Objects.Add(o);
        return o;
    }

    /// <summary>
    /// «Тест на пустой карте»: a copy of the content with a field 9×6, the object at its middle (and the receiver of its
    /// signal, if the scenario has it), a knight of the player next to it and, for a turret of the player, a goblin to shoot.
    /// </summary>
    public static (ContentDatabase Db, ScenarioDef Scenario) Sandbox(ContentDatabase db, ScenarioObjectDef o, ScenarioDef? from = null)
    {
        var test = db.Clone();
        var map = MapDef.CreateRhombus("object_sandbox", 9, 6, "grass");
        map.Name = "Пустая карта (тест объекта)";
        test.Table(DefCategory.Map).Replace(map);
        var at = new HexCoord(4, 2);
        var obj = ContentJson.Clone(o);
        obj.Anchor = at;
        if (obj.Id == "") obj.Id = "object";
        var sc = new ScenarioDef
        {
            Id = "object_sandbox", Name = $"Тест объекта «{o.DisplayName}»", Map = map.Id, Seed = 1,
            Description = "Пустая карта: объект и боец рядом. Контент проекта не меняется.",
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 3600 } },
            Defeat = new List<ConditionDef>(),
            Objects = { obj },
        };
        if (o.Interaction is { Effect: InteractionEffect.Signal, Target.Length: > 0 } it && from?.Object(it.Target) is { } target)
        {
            var t = ContentJson.Clone(target);
            t.Anchor = at + new HexCoord(2, 0);
            sc.Objects.Add(t);
        }
        var fighter = db.Unit("knight") ?? db.All<UnitDef>().FirstOrDefault(u => u.Faction == "player");
        if (fighter != null)
        {
            var spot = at + new HexCoord(-1, 0);
            if (obj.Cells().Contains(spot)) spot = at + new HexCoord(-2, 0);
            sc.Units.Add(new ScenarioUnitDef { Unit = fighter.Id, Faction = "player", C = spot, Facing = HexDirection.E });
        }
        if (o.Turret != null && test.RelationOf(o.Faction, "enemy") == Relation.Enemy && db.Unit("goblin") is { } gob)
            sc.Units.Add(new ScenarioUnitDef { Unit = gob.Id, Faction = "enemy", C = at + new HexCoord(3, 0), Facing = HexDirection.W });
        test.Table(DefCategory.Scenario).Replace(sc);
        return (test, sc);
    }
}
