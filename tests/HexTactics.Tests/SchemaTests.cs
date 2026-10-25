using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// JSON Schema of the content files (docs/DECISIONS_5.md §84): generated from the definitions, kept in docs/schema,
/// and every shipped file conforms to it. UPDATE_SCHEMAS=1 rewrites the files after a change of the definitions.
/// </summary>
public class SchemaTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HexTactics.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("корень репозитория не найден");
    }

    [Fact]
    public void Schemas_InDocs_MatchTheDefinitions()
    {
        string dir = Path.Combine(RepoRoot(), "docs", "schema");
        bool update = Environment.GetEnvironmentVariable("UPDATE_SCHEMAS") == "1";
        var names = new List<string>();
        foreach (var (name, schema) in ContentSchema.All())
        {
            names.Add(name);
            var text = ContentSchema.ToText(schema);
            var path = Path.Combine(dir, name);
            if (update)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, text);
                continue;
            }
            Assert.True(File.Exists(path), $"нет {path}: запустите тесты с UPDATE_SCHEMAS=1");
            Assert.True(File.ReadAllText(path).Replace("\r\n", "\n") == text, $"{name} устарела: запустите тесты с UPDATE_SCHEMAS=1");
        }
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("units.schema.json", names);
        Assert.Contains("scenario.schema.json", names);
        Assert.Contains("rules.schema.json", names);
    }

    [Fact]
    public void Schemas_AreStrict_EnumsTypesRangesAndReferences()
    {
        var units = ContentSchema.All().First(s => s.FileName == "units.schema.json").Schema;
        var defs = units["$defs"]!.AsObject();
        var unit = defs["UnitDef"]!["properties"]!.AsObject();
        Assert.Equal("string", unit["id"]!["type"]!.GetValue<string>());
        Assert.Contains("id", defs["UnitDef"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("Skill", unit["basicAttack"]!["x-ref"]!.GetValue<string>());
        var rank = unit["rank"]!;
        Assert.Contains("Boss", rank["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        // Ranges of the editor become minimum/maximum; optional objects allow null.
        var passives = ContentSchema.All().First(s => s.FileName == "passives.schema.json").Schema["$defs"]!.AsObject();
        var modifier = passives["ModifierDef"]!["properties"]!.AsObject();
        Assert.NotNull(modifier["perEach"]!["anyOf"]);
        Assert.Contains(defs.Select(d => d.Value!["properties"]!.AsObject()).SelectMany(p => p), p => p.Value!["minimum"] != null);
    }

    [Fact]
    public void ShippedFiles_ConformToTheirSchemas()
    {
        var schemas = ContentSchema.All().ToDictionary(s => s.FileName, s => s.Schema);
        var root = TestUtil.ContentDir;
        int files = 0;
        foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            string kind = node["kind"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path);
            if (!schemas.TryGetValue(kind + ".schema.json", out var schema)) continue;
            var errors = new List<string>();
            Validate(node, schema, schema, "$", errors);
            Assert.True(errors.Count == 0, $"{Path.GetRelativePath(root, path)}: {string.Join("; ", errors.Take(5))}");
            files++;
        }
        Assert.True(files >= 30, "проверено файлов: " + files);
    }

    [Fact]
    public void Validator_RejectsWrongTypesAndEnums()
    {
        var schema = ContentSchema.All().First(s => s.FileName == "stats.schema.json").Schema;
        var bad = JsonNode.Parse("""{"formatVersion":5,"kind":"stats","items":[{"id":"x","format":"Nope","min":"zero"}]}""")!;
        var errors = new List<string>();
        Validate(bad, schema, schema, "$", errors);
        Assert.Contains(errors, e => e.Contains("format"));
        Assert.Contains(errors, e => e.Contains("min"));
        var good = JsonNode.Parse("""{"formatVersion":5,"kind":"stats","items":[{"id":"x","futureField":1}]}""")!;
        errors.Clear();
        Validate(good, schema, schema, "$", errors);
        Assert.Empty(errors);
    }

    private static double Num(JsonValue v) => double.Parse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A small validator of the subset the generator uses.</summary>
    private static void Validate(JsonNode? node, JsonNode schema, JsonObject root, string path, List<string> errors)
    {
        var s = schema.AsObject();
        if (s["$ref"] is JsonValue r)
        {
            var name = r.GetValue<string>()["#/$defs/".Length..];
            Validate(node, root["$defs"]![name]!, root, path, errors);
            return;
        }
        if (s["anyOf"] is JsonArray any)
        {
            foreach (var option in any)
            {
                var e = new List<string>();
                Validate(node, option!, root, path, e);
                if (e.Count == 0) return;
            }
            errors.Add($"{path}: не подходит ни один вариант");
            return;
        }
        if (s["const"] is JsonValue c && node?.ToJsonString() != c.ToJsonString()) errors.Add($"{path}: ожидалось {c}");
        if (s["type"] is JsonNode t)
        {
            var types = t is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : new List<string> { t.GetValue<string>() };
            string actual = node switch
            {
                null => "null",
                JsonObject => "object",
                JsonArray => "array",
                JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
                JsonValue v when v.TryGetValue<string>(out _) => "string",
                JsonValue v when v.TryGetValue<long>(out _) => "integer",
                _ => "number",
            };
            bool ok = types.Contains(actual) || (actual == "integer" && types.Contains("number"));
            if (actual == "number" && types.Contains("integer") && node is JsonValue nv && Num(nv) is var dv && dv == Math.Floor(dv)) ok = true;
            if (!ok)
            {
                errors.Add($"{path}: тип {actual}, ожидался {string.Join("|", types)}");
                return;
            }
        }
        if (s["enum"] is JsonArray en && node is JsonValue ev && !en.Any(x => x!.ToJsonString() == ev.ToJsonString())) errors.Add($"{path}: «{ev}» не из перечисления");
        if (s["pattern"] is JsonValue pat && node is JsonValue sv && sv.TryGetValue<string>(out var str) && !Regex.IsMatch(str, pat.GetValue<string>())) errors.Add($"{path}: «{str}» не по шаблону");
        if (node is JsonValue num && num.GetValueKind() == System.Text.Json.JsonValueKind.Number && Num(num) is var d)
        {
            if (s["minimum"] is JsonValue mn && d < Num(mn) - 1e-9) errors.Add($"{path}: {d} < {mn}");
            if (s["maximum"] is JsonValue mx && d > Num(mx) + 1e-9) errors.Add($"{path}: {d} > {mx}");
        }
        if (node is JsonObject o)
        {
            if (s["required"] is JsonArray req)
                foreach (var k in req.Select(x => x!.GetValue<string>()))
                    if (!o.ContainsKey(k)) errors.Add($"{path}: нет «{k}»");
            var props = s["properties"]?.AsObject();
            foreach (var (k, v) in o)
            {
                if (props != null && props.FirstOrDefault(p => string.Equals(p.Key, k, StringComparison.OrdinalIgnoreCase)) is { Value: { } ps }) Validate(v, ps, root, path + "." + k, errors);
                else if (s["additionalProperties"] is JsonObject ap) Validate(v, ap, root, path + "." + k, errors);
            }
        }
        if (node is JsonArray arr)
        {
            if (s["minItems"] is JsonValue mi && arr.Count < Num(mi)) errors.Add($"{path}: мало элементов");
            if (s["maxItems"] is JsonValue ma && arr.Count > Num(ma)) errors.Add($"{path}: много элементов");
            if (s["items"] is JsonNode its)
                for (int i = 0; i < arr.Count; i++)
                    Validate(arr[i], its, root, $"{path}[{i}]", errors);
        }
    }
}
