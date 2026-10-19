using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Export of packages with dependencies and the import of TZ §14.2 (docs/DECISIONS_2.md §32):
/// check → compatibility → dependencies → preview → ID conflicts → resolution → import into the
/// draft → validation → report. A single object file (.json) goes through the same steps.
/// </summary>
public static class PackageDialogs
{
    private static ScrollContainer Scroll(Control content, float w, float h)
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(w, h), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(content.Expand());
        return scroll;
    }

    private static void Later(Action a) => Callable.From(a).CallDeferred();

    // ------------------------------------------------------------------ export

    /// <summary>Exports the roots with all their dependencies into a .hexpack chosen by the admin.</summary>
    public static void Export(Node parent, EditorContext ctx, List<Def> roots)
    {
        if (roots.Count == 0) return;
        var objects = ContentPackage.Collect(ctx.Db, roots);
        var missing = objects.SelectMany(d => ContentDependencies.Missing(ctx.Db, d).Select(m => (d, m))).ToList();
        var v = Ui.VBox(6);
        v.AddChild(Ui.Label($"Объектов: {roots.Count}, вместе с зависимостями: {objects.Count}", 15, Ui.Text));
        foreach (var g in objects.GroupBy(o => o.Category).OrderBy(g => g.Key))
            v.AddChild(Ui.Label($"   {ContentValidator.CatName(g.Key)}: {string.Join(", ", g.Take(10).Select(d => d.DisplayName))}{(g.Count() > 10 ? $" … (+{g.Count() - 10})" : "")}", 12, Ui.TextDim, wrap: true));
        if (missing.Count > 0)
            v.AddChild(Ui.Label($"⚠ Не найдено зависимостей: {missing.Count} — пакет будет отмечен как неполный и импортируется только черновиком:\n" +
                                string.Join("\n", missing.Take(6).Select(x => $"   {x.d.DisplayName} → {ContentValidator.CatName(x.m.Category)} '{x.m.Reference}'")), 12, Ui.Warn, wrap: true));
        var name = new LineEdit { Text = roots.Count == 1 ? roots[0].DisplayName : $"Пакет ({roots.Count} объектов)", PlaceholderText = "Название пакета" };
        var author = new LineEdit { PlaceholderText = "Автор (необязательно)" };
        var desc = new LineEdit { PlaceholderText = "Описание (необязательно)" };
        v.AddChild(Ui.HBox(6, Ui.Label("Название", 13, Ui.TextDim).MinSize(90), name.Expand()));
        v.AddChild(Ui.HBox(6, Ui.Label("Автор", 13, Ui.TextDim).MinSize(90), author.Expand()));
        v.AddChild(Ui.HBox(6, Ui.Label("Описание", 13, Ui.TextDim).MinSize(90), desc.Expand()));
        v.AddChild(Ui.Label("В пакете: manifest.json (версии формата и объектов, зависимости, SHA-256), objects/…, assets/… — только относительные пути.", 12, Ui.TextDim, wrap: true));
        Dialogs.Custom(parent, "Экспорт пакета", Scroll(v, 620, 320), "Сохранить пакет…", () =>
        {
            var opts = new PackageExportOptions { Name = name.Text.Trim(), Author = author.Text.Trim(), Description = desc.Text.Trim(), ReadAsset = Render.Assets.Read };
            Later(() => Dialogs.SaveFile(parent, "Экспорт пакета", ContentIO.SafeFileName(roots[0].Id) + ContentPackage.Extension, path =>
            {
                try
                {
                    if (!path.EndsWith(ContentPackage.Extension, StringComparison.OrdinalIgnoreCase)) path += ContentPackage.Extension;
                    var bytes = ContentPackage.Export(ctx.Db, roots, opts, out var manifest);
                    File.WriteAllBytes(path, bytes);
                    Dialogs.Info(parent, "Экспорт пакета", $"Пакет «{manifest.Name}» сохранён: объектов {manifest.Objects.Count}" +
                                                             (manifest.Incomplete ? " (неполный)" : "") + $"\n{path}");
                }
                catch (Exception e)
                {
                    Dialogs.Info(parent, "Ошибка экспорта", e.Message);
                }
            }, Dialogs.PackageFilter));
            return true;
        }, new Vector2I(660, 0));
    }

    // ------------------------------------------------------------------ import

    /// <summary>Chooses a .hexpack or .json file and runs the import steps on the draft.</summary>
    public static void Import(Node parent, EditorContext ctx, Action<ImportReport> onImported)
    {
        Dialogs.OpenFile(parent, "Импорт пакета или объекта", path =>
        {
            PackageContents pkg;
            try
            {
                pkg = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? ContentPackage.ReadSingle(File.ReadAllText(path))
                    : ContentPackage.Read(File.ReadAllBytes(path));
            }
            catch (Exception e)
            {
                Dialogs.Info(parent, "Ошибка импорта", e.Message);
                return;
            }
            Later(() => Preview(parent, ctx, pkg, Path.GetFileName(path), onImported));
        }, Dialogs.PackageFilter + ", *.json", Dialogs.PackageFilter, Dialogs.JsonFilter);
    }

    private sealed class Row
    {
        public required ImportItem Item { get; init; }
        public OptionButton Choice { get; set; } = null!;
    }

    /// <summary>Steps 1–6 shown to the admin; OK runs steps 7–9.</summary>
    public static void Preview(Node parent, EditorContext ctx, PackageContents pkg, string fileName, Action<ImportReport> onImported)
    {
        // 1–2. Format, integrity, compatibility.
        if (!pkg.Ok)
        {
            Dialogs.Info(parent, "Импорт отклонён", $"{fileName}: пакет не прошёл проверку, ничего не изменено.\n\n" +
                                                    string.Join("\n", pkg.Issues.Where(i => i.Severity == IssueSeverity.Error).Take(12).Select(i => "✗ " + i.Message)));
            return;
        }
        // 3–5. Dependencies, preview, conflicts.
        var plan = ContentPackage.Plan(ctx.Db, pkg);
        var m = pkg.Manifest;
        var v = Ui.VBox(5);
        v.AddChild(Ui.Label(pkg.IsSingleObject ? $"Объект из файла {fileName}" : $"Пакет «{m.Name}»" + (m.Author.Length > 0 ? $" · {m.Author}" : "") + (m.Created.Length > 0 ? $" · {m.Created}" : ""), 16, Ui.Accent, wrap: true));
        if (m.Description.Length > 0) v.AddChild(Ui.Label(m.Description, 13, Ui.TextDim, wrap: true));
        v.AddChild(Ui.Label((pkg.IsSingleObject ? "✓ формат проверен" : "✓ формат и целостность проверены (SHA-256 каждого файла и пакета)") +
                            (pkg.MigratedFrom is { } from ? $" · формат v{from} будет обновлён до v{ContentJson.CurrentFormatVersion}" : $" · формат v{m.FormatVersion}"), 12, Ui.Good, wrap: true));
        foreach (var w in pkg.Issues.Where(i => i.Severity == IssueSeverity.Warning).Take(5)) v.AddChild(Ui.Label("⚠ " + w.Message + " — " + w.Path, 12, Ui.Warn, wrap: true));

        var news = plan.Items.Where(i => i.Status == ImportStatus.New).ToList();
        var same = plan.Items.Where(i => i.Status == ImportStatus.Identical).ToList();
        var conflicts = plan.Conflicts.ToList();
        v.AddChild(Ui.Label($"Новых: {news.Count} · уже есть (совпадают): {same.Count} · конфликтов ID: {conflicts.Count}" +
                            (pkg.PinnedObjects.Count > 0 ? $" · старых экземпляров: {pkg.PinnedObjects.Count}" : "") +
                            (pkg.Assets.Count > 0 ? $" · ресурсов: {pkg.Assets.Count}" : ""), 14, Ui.Text, wrap: true));
        if (plan.FromLibrary.Count > 0)
            v.AddChild(Ui.Label($"Зависимостей из вашей библиотеки: {plan.FromLibrary.Select(x => x.Dependency.Reference).Distinct().Count()}", 12, Ui.TextDim));

        CheckBox? draftBox = null;
        if (plan.NeedsIncomplete)
        {
            v.AddChild(Ui.Sep());
            v.AddChild(Ui.Label("Отсутствуют зависимости (нет ни в пакете, ни в библиотеке):", 14, Ui.Bad));
            foreach (var (owner, dep) in plan.Missing.Take(12))
                v.AddChild(Ui.Label($"   ✗ {ContentValidator.CatName(owner.Category)} «{owner.DisplayName}» → {ContentValidator.CatName(dep.Category)} '{dep.Reference}'", 12, Ui.Bad, wrap: true));
            if (plan.Missing.Count == 0 && m.Incomplete) v.AddChild(Ui.Label("   пакет отмечен автором как неполный", 12, Ui.Bad));
            draftBox = new CheckBox { Text = "Импортировать как черновик: объекты без зависимостей получат отметку «неполный» и не будут использоваться в бою" };
            v.AddChild(draftBox);
        }

        if (news.Count > 0)
        {
            v.AddChild(Ui.Sep());
            v.AddChild(Ui.Label("Новые объекты", 14, Ui.Text));
            foreach (var i in news.Take(40))
                v.AddChild(Ui.Label($"   ＋ {ContentValidator.CatName(i.Def.Category)} «{i.Def.DisplayName}» [{i.Def.Id}] v{i.Def.Version}" +
                                    (i.MissingDependencies.Count > 0 ? " — не хватает зависимостей" : ""), 12, i.MissingDependencies.Count > 0 ? Ui.Warn : Ui.TextDim));
            if (news.Count > 40) v.AddChild(Ui.Label($"   … и ещё {news.Count - 40}", 12, Ui.TextDim));
        }
        var rows = new List<Row>();
        if (conflicts.Count > 0)
        {
            v.AddChild(Ui.Sep());
            v.AddChild(Ui.Label("Конфликты ID: в библиотеке есть другой объект с тем же ID", 14, Ui.Warn, wrap: true));
            var bulk = Ui.HBox(6);
            bulk.AddChild(Ui.Label("Все:", 12, Ui.TextDim));
            bulk.AddChild(Ui.Button("пропустить", () => rows.ForEach(r => r.Choice.Select(0))));
            bulk.AddChild(Ui.Button("копии с новым ID", () => rows.ForEach(r => r.Choice.Select(1))));
            bulk.AddChild(Ui.Button("заменить", () => rows.ForEach(r => r.Choice.Select(2)), "Замена — только после подтверждения"));
            v.AddChild(bulk);
            foreach (var c in conflicts)
            {
                var row = new Row { Item = c };
                var h = Ui.HBox(6);
                h.AddChild(Ui.Label($"{ContentValidator.CatName(c.Def.Category)} «{c.Def.DisplayName}» [{c.Def.Id}]  v{c.Existing!.Version} в библиотеке / v{c.Def.Version} в пакете", 12, Ui.Text, wrap: true).Expand());
                row.Choice = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(170, 0) };
                row.Choice.AddItem("Пропустить");
                row.Choice.AddItem("Копия с новым ID");
                row.Choice.AddItem("Заменить");
                row.Choice.Selected = 0;
                h.AddChild(row.Choice);
                var diffLabel = Ui.Label("", 11, Ui.TextDim, wrap: true);
                diffLabel.Visible = false;
                diffLabel.Text = $"Различия ({c.Diff.Count}), библиотека → пакет:\n" + string.Join("\n", c.Diff.Take(30).Select(d => "   " + d.Describe())) + (c.Diff.Count > 30 ? "\n   …" : "");
                h.AddChild(Ui.Button("Различия", () => diffLabel.Visible = !diffLabel.Visible, "Показать разницу полей между версиями"));
                v.AddChild(h);
                v.AddChild(diffLabel);
                rows.Add(row);
            }
        }
        if (same.Count > 0)
            v.AddChild(Ui.Label("Совпадают с библиотекой (пропускаются): " + string.Join(", ", same.Take(14).Select(i => i.Def.DisplayName)) + (same.Count > 14 ? " …" : ""), 12, Ui.TextDim, wrap: true));

        ConfirmationDialog? dialog = null;
        dialog = Dialogs.Custom(parent, "Импорт: предпросмотр", Scroll(v, 760, Math.Min(560, 200 + conflicts.Count * 40 + Math.Min(40, news.Count) * 18)), "Импортировать", () =>
        {
            if (draftBox != null && !draftBox.ButtonPressed)
            {
                // The preview stays open: the hint is its child window.
                Dialogs.Info(dialog!, "Импорт", "Не хватает зависимостей: импорт возможен только как черновик — поставьте отметку «Импортировать как черновик».");
                return false;
            }
            foreach (var r in rows) r.Item.Resolution = (ConflictResolution)r.Choice.Selected;
            var replaced = rows.Where(r => r.Item.Resolution == ConflictResolution.Replace).ToList();
            void Run() => Later(() => Apply(parent, ctx, plan, draftBox?.ButtonPressed == true, onImported));
            if (replaced.Count == 0) Run();
            else
                Later(() => Dialogs.Confirm(parent, "Замена объектов",
                    $"Заменить {replaced.Count} объект(ов) библиотеки содержимым пакета?\n" + string.Join("\n", replaced.Take(12).Select(r => $" • {r.Item.Def.DisplayName} [{r.Item.Def.Id}]")) +
                    "\n\nЗамена попадёт в черновик; при публикации версия объекта увеличится, прежняя сохранится в истории.", Run, "Заменить"));
            return true;
        }, new Vector2I(800, 0));
    }

    /// <summary>Steps 7–9: import into the draft, validation of the imported objects, report.</summary>
    private static void Apply(Node parent, EditorContext ctx, ImportPlan plan, bool asDraft, Action<ImportReport> onImported)
    {
        ImportReport report;
        try
        {
            report = ContentPackage.Apply(ctx.Db, plan, asDraft, Render.Assets.Read);
            // Resource files (already checked) go into the user pack; another file is never overwritten.
            foreach (var (rel, bytes) in report.AssetFiles)
            {
                var full = Path.Combine(App.Main.Instance.Content.UserRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                if (!File.Exists(full)) File.WriteAllBytes(full, bytes);
            }
        }
        catch (Exception e)
        {
            Dialogs.Info(parent, "Импорт не выполнен", e.Message + "\nЧерновик не изменён.");
            return;
        }
        ctx.MarkDirty();
        ctx.Replace(ctx.Db);
        onImported(report);
        Dialogs.Info(parent, "Импорт: отчёт", report.Describe() + "\n\nИмпортировано в черновик — нажмите «Сохранить», чтобы опубликовать.");
    }
}
