using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>
/// The resource summary of the debug view (TZ §19, docs/DECISIONS_3.md §47): missing and damaged files, references to
/// absent resources, sizes, the heaviest models, resources nobody uses and the warnings of the limits.
/// </summary>
public static class AssetReport
{
    public sealed record Row(AssetDef Asset, string Problem, string Details);

    public sealed class Report
    {
        public List<Row> Rows { get; } = new();
        public List<string> Missing { get; } = new();
        public List<string> Broken { get; } = new();
        public List<string> MissingRefs { get; } = new();
        public List<string> Unused { get; } = new();
        public List<string> Performance { get; } = new();
        public long TotalBytes { get; set; }

        public List<string> Lines()
        {
            var l = new List<string>
            {
                $"Ресурсов: {Rows.Count}, общий объём файлов {AssetAnalysis.Mb(TotalBytes)}",
                Missing.Count == 0 ? "Отсутствующих файлов нет" : $"Отсутствуют файлы ({Missing.Count}): {string.Join(", ", Missing)}",
                Broken.Count == 0 ? "Повреждённых файлов нет" : $"Ошибки файлов ({Broken.Count}): {string.Join("; ", Broken)}",
            };
            if (MissingRefs.Count > 0) l.Add($"Ссылки на несуществующие ресурсы ({MissingRefs.Count}): {string.Join("; ", MissingRefs.Take(12))}{(MissingRefs.Count > 12 ? " …" : "")}");
            if (Unused.Count > 0) l.Add($"Не используются ({Unused.Count}): {string.Join(", ", Unused.Take(12))}{(Unused.Count > 12 ? " …" : "")}");
            l.AddRange(Performance.Select(p => "⚠ " + p));
            return l;
        }
    }

    public static Report Build(ContentDatabase db, Func<string, byte[]?> read)
    {
        var r = new Report();
        var lim = db.Rules.Assets;
        foreach (var a in db.All<AssetDef>().OrderBy(a => a.Kind).ThenBy(a => a.Id))
        {
            string problem = "";
            if (a.HasFile)
            {
                var bytes = read(a.Path);
                problem = AssetValidator.FileProblem(a, bytes) ?? "";
                if (bytes == null) r.Missing.Add($"{a.DisplayName} ({a.Path})");
                else if (problem != "") r.Broken.Add($"{a.DisplayName}: {problem}");
                r.TotalBytes += bytes?.LongLength ?? a.Size;
            }
            string details = a.Kind switch
            {
                AssetKind.Model => $"треугольников {a.Triangles:N0}, костей {a.Bones}, анимаций {a.Clips.Count}",
                AssetKind.Texture or AssetKind.Sprite or AssetKind.Icon => a.Width > 0 ? $"{a.Width}×{a.Height}{(a.HasAlpha ? ", альфа" : "")}" : "",
                AssetKind.Sound => a.Duration > 0 ? $"{a.Duration:0.##} с" : "",
                _ => "",
            };
            r.Rows.Add(new Row(a, problem, details));
            if (a.Kind == AssetKind.Model && a.Triangles > lim.WarnTriangles)
                r.Performance.Add($"Модель «{a.DisplayName}»: {a.Triangles:N0} треугольников — больше {lim.WarnTriangles:N0}; задайте упрощённую модель");
            if (a.Kind == AssetKind.Model && a.Size > lim.WarnModelMb * 1048576)
                r.Performance.Add($"Модель «{a.DisplayName}»: {AssetAnalysis.Mb(a.Size)} — долгая загрузка");
            if (a.Kind is AssetKind.Texture or AssetKind.Sprite or AssetKind.Icon && a.Width > 0)
            {
                var t = lim.For(a.Purpose);
                if (Math.Max(a.Width, a.Height) > t.RecommendedMax * 2)
                    r.Performance.Add($"Текстура «{a.DisplayName}» {a.Width}×{a.Height} — чрезмерно для «{AssetRequirements.PurposeName(a.Purpose)}»");
            }
            if (ContentDependencies.UsedBy(db, a).Count == 0) r.Unused.Add(a.DisplayName);
        }
        if (r.TotalBytes > lim.WarnTotalMb * 1048576)
            r.Performance.Add($"Общий объём ресурсов {AssetAnalysis.Mb(r.TotalBytes)} больше {lim.WarnTotalMb:0} МБ");
        foreach (var d in db.AllDefs())
            foreach (var m in ContentDependencies.Missing(db, d).Where(m => m.Category is DefCategory.Asset or DefCategory.Vfx))
                r.MissingRefs.Add($"{ContentValidator.CatName(d.Category)} «{d.DisplayName}» → {m.Reference}");
        return r;
    }
}
