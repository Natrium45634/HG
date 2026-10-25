using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>One catalogue of the project: how many objects, what changed in the draft, how many problems.</summary>
public sealed record CategoryOverview(DefCategory Category, string Name, int Total, int New, int Changed, int Incomplete, int Errors, int Warnings);

/// <summary>
/// «Обзор проекта» (ТЗ ч. 5, 25.1; docs/DECISIONS_5.md §84): the catalogues of the content with the state of the draft,
/// the broken references and the fields of other versions — each with the object to go to.
/// </summary>
public sealed class ProjectOverview
{
    public List<CategoryOverview> Categories { get; } = new();
    /// <summary>References to objects that do not exist (or pinned versions that are gone).</summary>
    public List<ContentIssue> BrokenReferences { get; } = new();
    /// <summary>Objects that keep fields of another version.</summary>
    public List<ContentIssue> OtherVersionFields { get; } = new();
    public int Objects => Categories.Sum(c => c.Total);
    public int New => Categories.Sum(c => c.New);
    public int Changed => Categories.Sum(c => c.Changed);
    public int Errors { get; private set; }
    public int Warnings { get; private set; }

    public static ProjectOverview Build(ContentDatabase db, ContentDatabase? published, IReadOnlyList<ContentIssue> issues)
    {
        var o = new ProjectOverview();
        var bySource = issues.GroupBy(i => i.Source).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (cat, _, _) in ContentDatabase.Layout)
        {
            var defs = db.Table(cat).Items.ToList();
            int n = 0, ch = 0, inc = 0, err = 0, warn = 0;
            foreach (var d in defs)
            {
                switch (ContentSearch.StateOf(d, published))
                {
                    case SearchState.New: n++; break;
                    case SearchState.Changed: ch++; break;
                    case SearchState.Incomplete: inc++; break;
                }
                if (bySource.TryGetValue($"{d.Category}:{d.Id}", out var list))
                {
                    err += list.Count(i => i.Severity == IssueSeverity.Error);
                    warn += list.Count(i => i.Severity == IssueSeverity.Warning);
                }
            }
            o.Categories.Add(new CategoryOverview(cat, ContentValidator.CatName(cat), defs.Count, n, ch, inc, err, warn));
        }
        o.BrokenReferences.AddRange(issues.Where(i => i.Severity == IssueSeverity.Error &&
                                                      (i.Message.StartsWith("Ссылка на несуществующий", StringComparison.Ordinal) || i.Message.StartsWith("Закреплённая версия", StringComparison.Ordinal))));
        o.OtherVersionFields.AddRange(issues.Where(i => i.Message.StartsWith("Поля другой версии", StringComparison.Ordinal)));
        o.Errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        o.Warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        return o;
    }

    /// <summary>The object an issue points to («Unit:wolf» → (Unit, wolf)); null for the rules.</summary>
    public static (DefCategory Category, string Id)? Target(ContentIssue i)
    {
        int colon = i.Source.IndexOf(':');
        if (colon < 0 || !Enum.TryParse<DefCategory>(i.Source[..colon], out var cat)) return null;
        return (cat, i.Source[(colon + 1)..]);
    }
}
