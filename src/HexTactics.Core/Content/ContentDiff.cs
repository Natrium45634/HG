using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexTactics.Core.Content;

/// <summary>One difference between two versions of a definition (a JSON path and both values).</summary>
public sealed record DiffLine(string Path, string? Old, string? New)
{
    public string Describe() => Old == null ? $"+ {Path} = {New}" : New == null ? $"− {Path} (было {Old})" : $"~ {Path}: {Old} → {New}";
}

/// <summary>
/// Differences between two versions of a definition (docs/DECISIONS_2.md §31–32): shown when a
/// version is restored from the history and when an import replaces an existing object.
/// </summary>
public static class ContentDiff
{
    public static List<DiffLine> Compare(Def? oldDef, Def? newDef, bool ignoreVersion = true)
    {
        var a = Flatten(oldDef);
        var b = Flatten(newDef);
        if (ignoreVersion)
        {
            a.Remove("version");
            b.Remove("version");
        }
        var list = new List<DiffLine>();
        foreach (var key in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            a.TryGetValue(key, out var x);
            b.TryGetValue(key, out var y);
            if (x != y) list.Add(new DiffLine(key, x, y));
        }
        return list;
    }

    private static Dictionary<string, string> Flatten(Def? d)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (d == null) return map;
        var node = JsonSerializer.SerializeToNode(d, d.GetType(), ContentJson.Options);
        Walk(node, "", map);
        return map;
    }

    private static void Walk(JsonNode? node, string path, Dictionary<string, string> map)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o) Walk(kv.Value, path.Length == 0 ? kv.Key : path + "." + kv.Key, map);
                break;
            case JsonArray arr:
                if (arr.Count == 0) map[path] = "[]";
                for (int i = 0; i < arr.Count; i++) Walk(arr[i], $"{path}[{i}]", map);
                break;
            case null:
                break;
            default:
                map[path] = node.ToJsonString();
                break;
        }
    }
}
