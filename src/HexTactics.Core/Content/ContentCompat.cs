using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>
/// Compatibility of files of other versions (docs/DECISIONS_5.md §84): fields this version does not know are kept in
/// <see cref="ContentPart.Extra"/> on every level and written back on save; the check names each of them so nothing
/// disappears silently.
/// </summary>
public static class ContentCompat
{
    /// <summary>Every unknown field kept in the object and in its nested parts: (path of the part, field name).</summary>
    public static IEnumerable<(string Path, string Field)> UnknownFields(object obj, string path) => Walk(obj, path, 0);

    private static IEnumerable<(string, string)> Walk(object obj, string path, int depth)
    {
        if (depth > 24) yield break;
        if (obj is ContentPart part && part.Extra is { Count: > 0 } extra)
            foreach (var k in extra.Keys.OrderBy(k => k, StringComparer.Ordinal))
                yield return (path, k);
        foreach (var prop in ReferenceWalker.PropsOf(obj.GetType()))
        {
            var t = prop.PropertyType;
            if (t == typeof(string) || t.IsValueType) continue;
            object? v;
            try
            {
                v = prop.GetValue(obj);
            }
            catch
            {
                continue;
            }
            string p = path + "." + prop.Name;
            switch (v)
            {
                case ContentPart cp:
                    foreach (var x in Walk(cp, p, depth + 1)) yield return x;
                    break;
                case IDictionary dict:
                    foreach (DictionaryEntry e in dict)
                        if (e.Value is ContentPart dv)
                            foreach (var x in Walk(dv, $"{p}[{e.Key}]", depth + 1)) yield return x;
                    break;
                case IList list:
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] is ContentPart lv)
                            foreach (var x in Walk(lv, $"{p}[{i}]", depth + 1)) yield return x;
                    break;
            }
        }
    }

    /// <summary>The issues of the content: one warning per object that keeps unknown fields.</summary>
    public static IEnumerable<ContentIssue> Check(ContentDatabase db)
    {
        foreach (var d in db.AllDefs())
        {
            var found = UnknownFields(d, d.Id).ToList();
            if (found.Count > 0) yield return Issue($"{d.Category}:{d.Id}", d.Id, found);
        }
        var rules = UnknownFields(db.Rules, "rules").ToList();
        if (rules.Count > 0) yield return Issue("Правила", "rules", rules);
    }

    private static ContentIssue Issue(string source, string path, List<(string Path, string Field)> found) =>
        new(IssueSeverity.Warning, source, path,
            "Поля другой версии программы сохраняются без изменений, но этой версией не используются: " +
            string.Join(", ", found.Take(8).Select(f => f.Path + "." + f.Field)) + (found.Count > 8 ? $" и ещё {found.Count - 8}" : ""));
}
