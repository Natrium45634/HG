using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace HexTactics.Core.Content;

/// <summary>
/// Step-by-step upgrade of content files: version N → N+1 until the current version.
/// Files without "formatVersion" are treated as version 0 (pre-release prototype format).
/// Files from a newer version are rejected without modification.
/// </summary>
public static class ContentMigrator
{
    private delegate void Step(JsonObject root, string kind, List<string> notes);

    private static readonly Dictionary<int, Step> Steps = new()
    {
        [0] = V0ToV1,
        [1] = V1ToV2,
        [2] = V2ToV3,
        [3] = V3ToV4,
        [4] = V4ToV5,
    };

    public static JsonObject? Migrate(JsonNode node, string expectedKind, out List<string> notes, out string? error)
    {
        notes = new List<string>();
        error = null;

        JsonObject root;
        if (node is JsonArray arr)
        {
            // v0 category files could be a bare array of items.
            root = new JsonObject { ["items"] = arr.DeepClone() };
        }
        else if (node is JsonObject obj)
        {
            root = (JsonObject)obj.DeepClone();
        }
        else
        {
            error = "Корень файла должен быть объектом";
            return null;
        }

        int version = 0;
        if (root["formatVersion"] is JsonValue v && v.TryGetValue<int>(out var ver)) version = ver;

        if (version > ContentJson.CurrentFormatVersion)
        {
            error = $"Файл создан более новой версией формата ({version}), текущая — {ContentJson.CurrentFormatVersion}. Файл не изменён.";
            return null;
        }

        while (version < ContentJson.CurrentFormatVersion)
        {
            if (!Steps.TryGetValue(version, out var step))
            {
                error = $"Нет миграции с версии {version}";
                return null;
            }
            try
            {
                if (expectedKind == "def") StepSingle(step, root, notes);
                else step(root, expectedKind, notes);
            }
            catch (Exception e)
            {
                error = $"Ошибка миграции {version}→{version + 1}: {e.Message}";
                return null;
            }
            version++;
            root["formatVersion"] = version;
            notes.Add($"Файл обновлён до версии формата {version}");
        }

        if (root["kind"] == null) root["kind"] = expectedKind;
        return root;
    }

    /// <summary>
    /// A single exported object ("kind": "def", "category", "item") migrates like the file of its
    /// category: the item is wrapped the way that file stores it, migrated, and unwrapped.
    /// </summary>
    private static void StepSingle(Step step, JsonObject root, List<string> notes)
    {
        if (root["item"] is not JsonObject item || (string?)root["category"] is not { } catName
            || !Enum.TryParse<DefCategory>(catName, out var cat) || cat == DefCategory.MoveCategory)
        {
            step(root, "def", notes);
            return;
        }
        string kind = ContentIO.KindOf(cat);
        root.Remove("item");
        var wrapper = new JsonObject();
        bool folder = ContentDatabase.IsFolderCategory(cat);
        if (folder) wrapper[kind] = item;
        else wrapper["items"] = new JsonArray(item);
        step(wrapper, kind, notes);
        var migrated = folder ? wrapper[kind] : (wrapper["items"] as JsonArray)?.FirstOrDefault();
        root["item"] = (migrated ?? item).DeepClone();
    }

    /// <summary>
    /// v0 (prototype): units had top-level "hp", "speed", "ap"; map cells had "q", "r",
    /// "terrain", "height"; maps/scenarios were stored without a wrapper object.
    /// </summary>
    private static void V0ToV1(JsonObject root, string kind, List<string> notes)
    {
        if ((kind == "map" || kind == "scenario") && root[kind] == null && root["id"] != null)
        {
            var inner = new JsonObject();
            foreach (var kv in root.ToArray())
            {
                root.Remove(kv.Key);
                if (kv.Key != "formatVersion" && kv.Key != "kind") inner[kv.Key] = kv.Value;
            }
            root[kind] = inner;
            notes.Add($"v0: {kind} перенесён в обёртку");
        }

        if (kind == "units" && root["items"] is JsonArray units)
        {
            foreach (var u in units)
            {
                if (u is not JsonObject uo) continue;
                var stats = uo["stats"] as JsonObject ?? new JsonObject();
                MoveField(uo, "hp", stats, Stats.HpMax, notes);
                MoveField(uo, "speed", stats, Stats.MoveSpeed, notes);
                MoveField(uo, "ap", stats, Stats.ApMax, notes);
                if (stats.Count > 0) uo["stats"] = stats;
            }
        }

        if (kind == "map" && root["map"] is JsonObject map && map["cells"] is JsonArray cells)
        {
            int converted = 0;
            foreach (var c in cells)
            {
                if (c is not JsonObject co) continue;
                if (co["q"] != null && co["r"] != null && co["c"] == null)
                {
                    co["c"] = new JsonArray(co["q"]!.GetValue<int>(), co["r"]!.GetValue<int>());
                    co.Remove("q");
                    co.Remove("r");
                    converted++;
                }
                Rename(co, "terrain", "t");
                Rename(co, "height", "h");
            }
            if (converted > 0) notes.Add($"v0: {converted} клеток переведены в формат c:[q,r]");
        }
    }

    /// <summary>
    /// v1 → v2 (part 2 of the spec): terrain types get a logical kind derived from their visual
    /// style; computed fields written by older builds ("displayName", "any") are dropped.
    /// </summary>
    private static void V1ToV2(JsonObject root, string kind, List<string> notes)
    {
        int dropped = DropComputed(root);
        if (dropped > 0) notes.Add($"v1→v2: удалено служебных полей: {dropped}");
        if (kind != "terrains" || root["items"] is not JsonArray items) return;
        int classified = 0;
        foreach (var n in items)
        {
            if (n is not JsonObject t || t["kind"] != null) continue;
            string visual = t["visual"]?.GetValue<string>() ?? "Ground";
            bool walkable = t["passableFor"] is not JsonArray pf || pf.Any(x => x?.GetValue<string>() == "walk");
            int defaultHeight = t["defaultHeight"] is JsonValue dh && dh.TryGetValue<int>(out var h) ? h : 0;
            string k = visual switch
            {
                "Water" => "Liquid",
                "Pit" => "Pit",
                "Hazard" => "Hazard",
                "Special" => "Special",
                _ when !walkable => "Obstacle",
                _ when visual is "Wall" or "Rock" or "Crate" => "Obstacle",
                _ when defaultHeight > 0 => "Elevation",
                _ => "Floor",
            };
            t["kind"] = k;
            classified++;
        }
        if (classified > 0) notes.Add($"v1→v2: определён вид местности для {classified} типов");
    }

    /// <summary>v2 → v3: scenario flag "allowStacking" becomes the stacking mode (true → AllowAll).</summary>
    private static void V2ToV3(JsonObject root, string kind, List<string> notes)
    {
        if (kind != "scenario" || root["scenario"]?["limits"] is not JsonObject limits) return;
        var old = limits["allowStacking"];
        if (old == null) return;
        bool allow = old is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        limits.Remove("allowStacking");
        if (limits["stacking"] == null) limits["stacking"] = allow ? "AllowAll" : "ByUnitType";
        notes.Add($"v2→v3: «несколько юнитов на клетке» = {allow} → режим {(allow ? "AllowAll" : "ByUnitType")}");
    }

    /// <summary>
    /// v3 → v4 (unified damage, docs/DECISIONS_2.md §29): unit resistances as fractions
    /// ({"fire": 0.5}) become defense modifiers in percent ({"match": "fire", "percent": -50});
    /// 1 and more — immunity. Computed fields written by older builds are dropped.
    /// </summary>
    private static void V3ToV4(JsonObject root, string kind, List<string> notes)
    {
        int dropped = DropComputed(root);
        if (dropped > 0) notes.Add($"v3→v4: удалено служебных полей: {dropped}");
        if (kind != "units" || root["items"] is not JsonArray items) return;
        int converted = 0;
        foreach (var n in items)
        {
            if (n is not JsonObject u || u["resistances"] is not JsonObject res) continue;
            u.Remove("resistances");
            if (res.Count == 0) continue;
            var defense = u["defense"] as JsonObject ?? new JsonObject();
            var mods = defense["modifiers"] as JsonArray ?? new JsonArray();
            var immune = defense["immune"] as JsonArray ?? new JsonArray();
            foreach (var kv in res)
            {
                double f = kv.Value is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
                if (f >= 1) immune.Add(kv.Key);
                else if (Math.Abs(f) > 1e-9) mods.Add(new JsonObject { ["match"] = kv.Key, ["percent"] = Math.Round(-f * 100, 2) });
                converted++;
            }
            defense["modifiers"] = mods;
            defense["immune"] = immune;
            u["defense"] = defense;
        }
        if (converted > 0) notes.Add($"v3→v4: сопротивления ({converted}) переведены в проценты: 0.5 → −50 %, 1 → иммунитет");
    }

    /// <summary>
    /// v4 → v5 (docs/DECISIONS_4.md §73): an old wave — spawn markers of the map and a list «unit × count» — becomes
    /// spawn bases linked to the markers (one per marker, shared by the waves) and one group per line of the list.
    /// The schedule stays the same: the i-th enemy of the wave appears at «interval × i» from the i-th marker in turn.
    /// </summary>
    private static void V4ToV5(JsonObject root, string kind, List<string> notes)
    {
        if (kind != "scenario" || root["scenario"] is not JsonObject sc || sc["waves"] is not JsonArray waves) return;
        var objects = sc["objects"] as JsonArray ?? new JsonArray();
        sc["objects"] = objects;
        int groups = 0, bases = 0;
        for (int wi = 0; wi < waves.Count; wi++)
        {
            if (waves[wi] is not JsonObject w || w["units"] is not JsonArray units || units.Count == 0) continue;
            var markers = new List<string>();
            if ((string?)w["spawnMarker"] is { Length: > 0 } m0) markers.Add(m0);
            if (w["spawnMarkers"] is JsonArray extra)
                foreach (var m in extra)
                    if ((string?)m is { Length: > 0 } ms && !markers.Contains(ms))
                        markers.Add(ms);
            if (markers.Count == 0) continue;
            var baseIds = new List<string>();
            foreach (var m in markers)
            {
                string id = "spawn_" + m;
                baseIds.Add(id);
                if (objects.OfType<JsonObject>().Any(o => (string?)o["id"] == id)) continue;
                objects.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = $"Появление «{m}»", ["type"] = "SpawnBase", ["faction"] = (string?)w["faction"] ?? "enemy", ["marker"] = m,
                    ["spawn"] = new JsonObject { ["blocked"] = "NearestFree", ["searchRadius"] = 4 },
                });
                bases++;
            }
            double interval = w["interval"] is JsonValue iv && iv.TryGetValue<double>(out var ivd) ? ivd : 0;
            var list = w["groups"] as JsonArray ?? new JsonArray();
            int start = 0;
            for (int r = 0; r < units.Count; r++)
            {
                if (units[r] is not JsonObject u) continue;
                int count = u["count"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : 1;
                var rotation = new JsonArray();
                for (int k = 1; k < baseIds.Count; k++) rotation.Add(baseIds[(start + k) % baseIds.Count]);
                list.Add(new JsonObject
                {
                    ["id"] = $"w{wi + 1}_g{r + 1}", ["unit"] = (string?)u["unit"] ?? "", ["count"] = count,
                    ["delay"] = Math.Round(interval * start, 6), ["interval"] = interval,
                    ["spawnBase"] = baseIds[start % baseIds.Count], ["extraBases"] = rotation,
                    ["route"] = (string?)w["route"] ?? "", ["tag"] = (string?)u["tag"] ?? "", ["aiProfile"] = (string?)u["aiProfile"] ?? "",
                });
                start += count;
                groups++;
            }
            w["groups"] = list;
            w["units"] = new JsonArray();
            w["spawnMarker"] = "";
            w["spawnMarkers"] = new JsonArray();
        }
        if (groups > 0) notes.Add($"v4→v5: состав волн переведён в группы ({groups}), точки появления — в базы спавна ({bases})");
    }

    private static int DropComputed(JsonNode? node)
    {
        int n = 0;
        switch (node)
        {
            case JsonObject o:
                if (o.Remove("displayName")) n++;
                if (o.Remove("usesChosenDirection")) n++;
                if (o.Remove("targetsUnits")) n++;
                if (o.Remove("hasFacing")) n++;
                if (o.Remove("isEmpty")) n++;
                if (o["blocksMovement"] != null && o["interruptsCast"] != null && o.Remove("any")) n++;
                foreach (var kv in o.ToArray()) n += DropComputed(kv.Value);
                break;
            case JsonArray a:
                foreach (var x in a) n += DropComputed(x);
                break;
        }
        return n;
    }

    private static void MoveField(JsonObject from, string field, JsonObject to, string newName, List<string> notes)
    {
        var val = from[field];
        if (val == null) return;
        from.Remove(field);
        to[newName] = val;
        notes.Add($"v0: поле '{field}' перенесено в stats.{newName}");
    }

    private static void Rename(JsonObject o, string oldName, string newName)
    {
        var val = o[oldName];
        if (val == null || o[newName] != null) return;
        o.Remove(oldName);
        o[newName] = val;
    }
}
