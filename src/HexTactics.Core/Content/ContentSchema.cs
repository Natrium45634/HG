using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>
/// JSON Schema (draft 2020-12) of every content file, generated from the definitions themselves
/// (docs/DECISIONS_5.md §84): types, strict enumerations, ranges, nullable fields, references (as «x-ref»).
/// Unknown fields are allowed — they are kept as they are (ContentPart.Extra) — so a file of a newer version passes.
/// The schemas in docs/schema are checked against the code by a test.
/// </summary>
public static class ContentSchema
{
    public const string Draft = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>Every schema with its file name: «units.schema.json», «scenario.schema.json», «rules.schema.json».</summary>
    public static IEnumerable<(string FileName, JsonObject Schema)> All()
    {
        yield return ("rules.schema.json", Wrapper("rules", "rules", typeof(RulesDef), single: true, "Правила боя (rules.json)"));
        foreach (var (cat, type, file) in ContentDatabase.Layout)
        {
            if (ContentDatabase.IsFolderCategory(cat))
            {
                var kind = ContentDatabase.SingleKind(cat);
                yield return (kind + ".schema.json", Wrapper(kind, kind, type, single: true, $"{ContentValidator.CatName(cat)}: один объект в файле папки {file}/"));
            }
            else
            {
                var kind = ContentIO.KindOf(cat);
                yield return (kind + ".schema.json", Wrapper(kind, "items", type, single: false, $"{ContentValidator.CatName(cat)} ({file})"));
            }
        }
    }

    /// <summary>The schema of a file: { formatVersion, kind, items: [ … ] } or { formatVersion, kind, map: { … } }.</summary>
    public static JsonObject Wrapper(string kind, string key, Type type, bool single, string title)
    {
        var defs = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var item = Ref(type, defs);
        var root = new JsonObject
        {
            ["$schema"] = Draft,
            ["$id"] = $"hextactics/{kind}.schema.json",
            ["title"] = title,
            ["type"] = "object",
            ["required"] = new JsonArray("formatVersion", key),
            ["properties"] = new JsonObject
            {
                ["formatVersion"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = ContentJson.CurrentFormatVersion, ["description"] = "Версия формата; старые версии мигрируются при загрузке" },
                ["kind"] = new JsonObject { ["const"] = kind },
                [key] = single ? item : new JsonObject { ["type"] = "array", ["items"] = item },
            },
        };
        var d = new JsonObject();
        foreach (var (name, schema) in defs) d[name] = schema;
        root["$defs"] = d;
        return root;
    }

    private static JsonNode Ref(Type type, SortedDictionary<string, JsonObject> defs)
    {
        string name = type.Name;
        if (!defs.ContainsKey(name))
        {
            var obj = new JsonObject();
            defs[name] = obj; // placeholder first: recursive types (conditions) refer to themselves
            FillObject(obj, type, defs);
        }
        return new JsonObject { ["$ref"] = "#/$defs/" + name };
    }

    private static readonly NullabilityInfoContext Nullability = new();

    private static void FillObject(JsonObject obj, Type type, SortedDictionary<string, JsonObject> defs)
    {
        var ed = type.GetCustomAttribute<EdAttribute>();
        if (ed != null) obj["description"] = ed.Label;
        obj["type"] = "object";
        var props = new JsonObject();
        var required = new JsonArray();
        var info = ContentJson.Options.GetTypeInfo(type);
        foreach (var p in info.Properties.Where(p => !p.IsExtensionData).OrderBy(p => p.Order).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var pi = p.AttributeProvider as PropertyInfo;
            bool nullable = pi != null && !p.PropertyType.IsValueType && Nullability.Create(pi).WriteState == NullabilityState.Nullable;
            var schema = TypeSchema(p.PropertyType, defs, pi, nullable);
            if (pi?.GetCustomAttribute<EdAttribute>() is { } a)
                schema["description"] = string.IsNullOrEmpty(a.Tip) ? a.Label : $"{a.Label} — {a.Tip}";
            props[p.Name] = schema;
            if (p.Name == "id" && typeof(Def).IsAssignableFrom(type)) required.Add("id");
        }
        obj["properties"] = props;
        if (required.Count > 0) obj["required"] = required;
        // Fields of newer versions are kept, not rejected (docs/DECISIONS_5.md §84).
        obj["additionalProperties"] = true;
    }

    private static JsonObject TypeSchema(Type t, SortedDictionary<string, JsonObject> defs, PropertyInfo? pi, bool nullable)
    {
        var under = Nullable.GetUnderlyingType(t);
        if (under != null)
        {
            var inner = TypeSchema(under, defs, pi, false);
            return new JsonObject { ["anyOf"] = new JsonArray(inner, new JsonObject { ["type"] = "null" }) };
        }
        JsonObject s;
        if (t == typeof(string))
        {
            s = new JsonObject { ["type"] = "string" };
            if (pi?.GetCustomAttribute<EdRefAttribute>() is { } r) s["x-ref"] = r.Category.ToString();
            if (pi?.GetCustomAttribute<EdColorAttribute>() != null) s["pattern"] = "^(#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?)?$";
        }
        else if (t == typeof(bool)) s = new JsonObject { ["type"] = "boolean" };
        else if (t == typeof(int) || t == typeof(long) || t == typeof(uint) || t == typeof(ulong) || t == typeof(short)) s = new JsonObject { ["type"] = "integer" };
        else if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) s = new JsonObject { ["type"] = "number" };
        else if (t.IsEnum)
        {
            s = t.GetCustomAttribute<FlagsAttribute>() != null
                ? new JsonObject { ["type"] = "string", ["description"] = "Флаги через запятую: " + string.Join(", ", Enum.GetNames(t)) }
                : new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()) };
        }
        else if (t == typeof(HexCoord)) s = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["minItems"] = 2, ["maxItems"] = 2, ["description"] = "[q, r]" };
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = t.GetGenericArguments();
            s = new JsonObject { ["type"] = "object", ["additionalProperties"] = TypeSchema(args[1], defs, null, false) };
            if (pi?.GetCustomAttribute<EdKeyRefAttribute>() is { } kr) s["x-key-ref"] = kr.Category.ToString();
        }
        else if (t.IsArray || (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t) && t.GetGenericArguments().Length == 1))
        {
            var el = t.IsArray ? t.GetElementType()! : t.GetGenericArguments()[0];
            var items = TypeSchema(el, defs, null, false);
            if (el == typeof(string) && pi?.GetCustomAttribute<EdRefAttribute>() is { } r) items["x-ref"] = r.Category.ToString();
            s = new JsonObject { ["type"] = "array", ["items"] = items };
        }
        else if (t == typeof(JsonElement) || t == typeof(object)) s = new JsonObject();
        else if (t.IsClass) s = (JsonObject)Ref(t, defs);
        else s = new JsonObject();

        if (pi?.GetCustomAttribute<EdRangeAttribute>() is { } range && s["type"]?.GetValue<string>() is "number" or "integer")
        {
            s["minimum"] = range.Min;
            s["maximum"] = range.Max;
        }
        if (nullable)
        {
            if (s.ContainsKey("$ref")) return new JsonObject { ["anyOf"] = new JsonArray(s, new JsonObject { ["type"] = "null" }) };
            if (s["type"] is JsonValue tv) s["type"] = new JsonArray(tv.GetValue<string>(), "null");
        }
        return s;
    }

    /// <summary>The schema as text (stable order, two-space indent).</summary>
    public static string ToText(JsonObject schema) =>
        schema.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        }).Replace("\r\n", "\n") + "\n";
}
