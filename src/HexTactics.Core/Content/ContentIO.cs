using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexTactics.Core.Content;

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record ContentIssue(IssueSeverity Severity, string Source, string Path, string Message)
{
    public override string ToString()
    {
        string sev = Severity switch { IssueSeverity.Error => "ОШИБКА", IssueSeverity.Warning => "Предупреждение", _ => "Инфо" };
        return string.IsNullOrEmpty(Path) ? $"[{sev}] {Source}: {Message}" : $"[{sev}] {Source} → {Path}: {Message}";
    }
}

public sealed class ContentLoadResult
{
    public ContentDatabase Db { get; init; } = new();
    public List<ContentIssue> Issues { get; } = new();
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>
/// Loads and saves content packs (folder of JSON files). Every file is versioned and goes
/// through <see cref="ContentMigrator"/> before deserialization. Items are deserialized one
/// by one so a single broken item does not discard the whole file.
/// </summary>
public static class ContentIO
{
    public const string RulesFile = "rules.json";

    public static string KindOf(DefCategory c) => c switch
    {
        DefCategory.Map => "map",
        DefCategory.Scenario => "scenario",
        _ => ContentDatabase.FileOf(c).Replace(".json", ""),
    };

    public static ContentLoadResult Load(IContentStorage storage, bool validate = true)
    {
        var result = new ContentLoadResult();
        var db = result.Db;

        var rulesText = storage.ReadText(RulesFile);
        if (rulesText == null)
        {
            result.Issues.Add(new ContentIssue(IssueSeverity.Warning, RulesFile, "", "Файл не найден — используются правила по умолчанию"));
        }
        else
        {
            var root = ParseAndMigrate(rulesText, RulesFile, "rules", result.Issues);
            if (root != null)
            {
                try
                {
                    var rn = root["rules"];
                    if (rn != null) db.Rules = ContentJson.Deserialize<RulesDef>(rn) ?? new RulesDef();
                    else result.Issues.Add(new ContentIssue(IssueSeverity.Error, RulesFile, "rules", "Нет объекта rules"));
                }
                catch (Exception e)
                {
                    result.Issues.Add(new ContentIssue(IssueSeverity.Error, RulesFile, "rules", "Ошибка чтения правил: " + Short(e)));
                }
            }
        }

        foreach (var (cat, type, file) in ContentDatabase.Layout)
        {
            if (cat == DefCategory.Map || cat == DefCategory.Scenario)
            {
                string single = cat == DefCategory.Map ? "map" : "scenario";
                foreach (var name in storage.ListFiles(file, ".json"))
                {
                    var rel = file + "/" + name;
                    var text = storage.ReadText(rel);
                    if (text == null) continue;
                    var root = ParseAndMigrate(text, rel, single, result.Issues);
                    if (root == null) continue;
                    var node = root[single];
                    if (node == null)
                    {
                        result.Issues.Add(new ContentIssue(IssueSeverity.Error, rel, single, "Нет объекта " + single));
                        continue;
                    }
                    AddItem(db, cat, type, node, rel, single, result.Issues);
                }
                continue;
            }

            var catText = storage.ReadText(file);
            if (catText == null)
            {
                result.Issues.Add(new ContentIssue(IssueSeverity.Warning, file, "", "Файл не найден — категория пуста"));
                continue;
            }
            var croot = ParseAndMigrate(catText, file, KindOf(cat), result.Issues);
            if (croot == null) continue;
            if (croot["items"] is not JsonArray items)
            {
                result.Issues.Add(new ContentIssue(IssueSeverity.Error, file, "items", "Нет массива items"));
                continue;
            }
            for (int i = 0; i < items.Count; i++)
            {
                var n = items[i];
                if (n == null) continue;
                AddItem(db, cat, type, n, file, $"items[{i}]", result.Issues);
            }
        }

        if (validate) result.Issues.AddRange(ContentValidator.Validate(db));
        return result;
    }

    private static void AddItem(ContentDatabase db, DefCategory cat, Type type, JsonNode node, string file, string path, List<ContentIssue> issues)
    {
        Def? def;
        try
        {
            def = (Def?)node.Deserialize(type, ContentJson.Options);
        }
        catch (Exception e)
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, file, path, "Не удалось прочитать: " + Short(e)));
            return;
        }
        if (def == null) return;
        if (string.IsNullOrWhiteSpace(def.Id))
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, file, path, "Пустой ID — элемент пропущен"));
            return;
        }
        if (!db.Add(def))
            issues.Add(new ContentIssue(IssueSeverity.Error, file, path, $"Повторяющийся ID '{def.Id}' — второй элемент пропущен"));
    }

    private static string Short(Exception e)
    {
        var msg = e.Message;
        if (e is JsonException je && je.Path != null) msg += $" (путь {je.Path})";
        return msg.Length > 300 ? msg.Substring(0, 300) + "…" : msg;
    }

    public static JsonObject? ParseAndMigrate(string text, string source, string expectedKind, List<ContentIssue> issues)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception e)
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, source, "", "Некорректный JSON: " + Short(e)));
            return null;
        }
        if (node == null)
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, source, "", "Пустой файл"));
            return null;
        }
        var migrated = ContentMigrator.Migrate(node, expectedKind, out var notes, out var error);
        foreach (var n in notes) issues.Add(new ContentIssue(IssueSeverity.Info, source, "", n));
        if (error != null)
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, source, "", error));
            return null;
        }
        var kind = migrated!["kind"]?.GetValue<string>();
        if (kind != null && kind != expectedKind)
            issues.Add(new ContentIssue(IssueSeverity.Warning, source, "kind", $"Ожидался вид '{expectedKind}', в файле '{kind}'"));
        return migrated;
    }

    // ---------- Saving ----------

    public static string SerializeRules(RulesDef rules)
    {
        var root = new JsonObject
        {
            ["formatVersion"] = ContentJson.CurrentFormatVersion,
            ["kind"] = "rules",
            ["rules"] = ContentJson.ToNode(rules),
        };
        return CompactJson.Write(root);
    }

    public static string SerializeCategory(ContentDatabase db, DefCategory c)
    {
        var arr = new JsonArray();
        var type = ContentDatabase.TypeOf(c);
        foreach (var d in db.Table(c).Items) arr.Add(JsonSerializer.SerializeToNode(d, type, ContentJson.Options));
        var root = new JsonObject
        {
            ["formatVersion"] = ContentJson.CurrentFormatVersion,
            ["kind"] = KindOf(c),
            ["items"] = arr,
        };
        return CompactJson.Write(root);
    }

    public static string SerializeSingle(Def d)
    {
        string kind = d.Category == DefCategory.Map ? "map" : "scenario";
        var root = new JsonObject
        {
            ["formatVersion"] = ContentJson.CurrentFormatVersion,
            ["kind"] = kind,
            [kind] = JsonSerializer.SerializeToNode(d, d.GetType(), ContentJson.Options),
        };
        return CompactJson.Write(root);
    }

    /// <summary>
    /// Saves the whole pack. Existing files that will change are first copied to
    /// backups/&lt;stamp&gt;/ (when backupStamp is given). Maps/scenarios no longer present are deleted
    /// (after backup). Returns the list of written files.
    /// </summary>
    public static List<string> Save(ContentDatabase db, IContentStorage storage, string? backupStamp = null)
    {
        var plan = new Dictionary<string, string>(StringComparer.Ordinal) { [RulesFile] = SerializeRules(db.Rules) };
        foreach (var (cat, _, file) in ContentDatabase.Layout)
        {
            if (cat == DefCategory.Map || cat == DefCategory.Scenario)
            {
                foreach (var d in db.Table(cat).Items) plan[file + "/" + SafeFileName(d.Id) + ".json"] = SerializeSingle(d);
                continue;
            }
            plan[file] = SerializeCategory(db, cat);
        }

        var toDelete = new List<string>();
        foreach (var dir in new[] { "maps", "scenarios" })
        foreach (var name in storage.ListFiles(dir, ".json"))
        {
            var rel = dir + "/" + name;
            if (!plan.ContainsKey(rel)) toDelete.Add(rel);
        }

        if (backupStamp != null)
        {
            foreach (var rel in plan.Keys.Concat(toDelete))
            {
                var old = storage.ReadText(rel);
                if (old != null && (!plan.TryGetValue(rel, out var neu) || neu != old))
                    storage.WriteText($"backups/{backupStamp}/{rel}", old);
            }
        }

        var written = new List<string>();
        foreach (var kv in plan)
        {
            if (storage.ReadText(kv.Key) == kv.Value) continue;
            storage.WriteText(kv.Key, kv.Value);
            written.Add(kv.Key);
        }
        foreach (var rel in toDelete)
        {
            storage.Delete(rel);
            written.Add(rel + " (удалён)");
        }
        return written;
    }

    public static string SafeFileName(string id)
    {
        var chars = id.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray();
        return new string(chars);
    }

    // ---------- Import / export of single definitions ----------

    public static string ExportDef(Def d)
    {
        var root = new JsonObject
        {
            ["formatVersion"] = ContentJson.CurrentFormatVersion,
            ["kind"] = "def",
            ["category"] = d.Category.ToString(),
            ["item"] = JsonSerializer.SerializeToNode(d, d.GetType(), ContentJson.Options),
        };
        return CompactJson.Write(root);
    }

    public static Def? ImportDef(string text, List<ContentIssue> issues)
    {
        var root = ParseAndMigrate(text, "импорт", "def", issues);
        if (root == null) return null;
        var catName = root["category"]?.GetValue<string>();
        if (catName == null || !Enum.TryParse<DefCategory>(catName, out var cat) || cat == DefCategory.MoveCategory)
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, "импорт", "category", "Неизвестная категория"));
            return null;
        }
        var item = root["item"];
        if (item == null)
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, "импорт", "item", "Нет элемента"));
            return null;
        }
        try
        {
            return (Def?)item.Deserialize(ContentDatabase.TypeOf(cat), ContentJson.Options);
        }
        catch (Exception e)
        {
            issues.Add(new ContentIssue(IssueSeverity.Error, "импорт", "item", Short(e)));
            return null;
        }
    }

    /// <summary>Generates an unused id based on the given one: id_copy, id_copy2…</summary>
    public static string UniqueId(ContentDatabase db, DefCategory c, string baseId)
    {
        if (!db.Exists(c, baseId)) return baseId;
        string b = baseId + "_copy";
        if (!db.Exists(c, b)) return b;
        for (int i = 2; ; i++)
            if (!db.Exists(c, b + i))
                return b + i;
    }
}
