using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Library dialogs (docs/DECISIONS_2.md §31): publication with the usages of changed objects
/// (keep old instances / update selected / update all), dependencies, version history, pack state.
/// </summary>
public static class LibraryDialogs
{
    private static ScrollContainer Scroll(Control content, float w, float h)
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(w, h), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(content.Expand());
        return scroll;
    }

    // ------------------------------------------------------------------ publication

    /// <summary>One usage of a changed object with the admin's choice (update to the new version or keep the old instance).</summary>
    private sealed class UsageChoice
    {
        public required LibraryChange Change { get; init; }
        public required ReferenceInfo Ref { get; init; }
        public CheckBox Box { get; set; } = null!;
    }

    /// <summary>Publication of the draft. onPublished receives the result.</summary>
    public static void Publish(Node parent, EditorContext ctx, Action<LibraryPublishResult, int> onPublished)
    {
        var cm = Main.Instance.Content;
        ctx.Validate();
        var changes = ContentLibrary.Changes(ctx.Db, cm.Published);
        var created = ctx.Db.AllDefs().Where(d => !cm.Published.Exists(d.Category, d.Id)).ToList();
        var removed = cm.Published.AllDefs().Where(d => !ctx.Db.Exists(d.Category, d.Id)).ToList();

        var v = Ui.VBox(6);
        v.AddChild(Ui.Label($"Пакет: {cm.UserRoot}", 13, Ui.TextDim, wrap: true));
        v.AddChild(Ui.Label("Запись транзакционная: журнал → резервная копия → атомарная запись файлов → манифест. При сбое пакет откатывается.", 13, Ui.TextDim, wrap: true));
        if (ctx.Errors > 0)
            v.AddChild(Ui.Label($"⚠ В контенте {ctx.Errors} ошибок: объекты сохранятся, но навыки с ошибками останутся отключёнными, а проблемные сценарии могут не запуститься.", 13, Ui.Bad, wrap: true));
        v.AddChild(Ui.Label($"Изменено: {changes.Count} · создано: {created.Count} · удалено: {removed.Count}", 15, Ui.Text));

        var choices = new List<UsageChoice>();
        if (changes.Count > 0)
        {
            var bulk = Ui.HBox(6);
            bulk.AddChild(Ui.Button("Обновить все", () => choices.ForEach(c => c.Box.ButtonPressed = true), "Все использования перейдут на новые версии"));
            bulk.AddChild(Ui.Button("Сохранить старые экземпляры", () => choices.ForEach(c => c.Box.ButtonPressed = false),
                "Использования останутся на прежних версиях (ссылка закрепляется: id@N)"));
            v.AddChild(bulk);
            v.AddChild(Ui.Label("Отметьте использования, которые нужно обновить; без отметки остаётся старый экземпляр.", 12, Ui.TextDim, wrap: true));
        }
        foreach (var ch in changes)
        {
            v.AddChild(Ui.Sep());
            v.AddChild(Ui.Label($"{ContentValidator.CatName(ch.Category)} «{ch.Name}» [{ch.Id}]  v{ch.OldVersion} → v{ch.NewVersion}", 15, Ui.Accent, wrap: true));
            var prev = cm.Published.Table(ch.Category).Get(ch.Id);
            var diff = ContentDiff.Compare(prev, ctx.Db.Table(ch.Category).Get(ch.Id));
            if (diff.Count > 0)
                v.AddChild(Ui.Label(string.Join("\n", diff.Take(6).Select(d => "   " + d.Describe())) + (diff.Count > 6 ? $"\n   … и ещё {diff.Count - 6}" : ""), 12, Ui.TextDim, wrap: true));
            if (ch.Direct.Count == 0)
            {
                v.AddChild(Ui.Label("   не используется другими объектами", 12, Ui.TextDim));
                continue;
            }
            foreach (var r in ch.Direct)
            {
                var c = new UsageChoice { Change = ch, Ref = r };
                c.Box = new CheckBox { Text = $"{r.OwnerLabel}  ({r.Path})", ButtonPressed = true, TooltipText = "Отмечено — обновить до новой версии; снято — оставить старый экземпляр" };
                choices.Add(c);
                v.AddChild(c.Box);
            }
            if (ch.Indirect.Count > 0)
                v.AddChild(Ui.Label("   косвенно: " + string.Join(", ", ch.Indirect.Take(8).Select(d => $"{ContentValidator.CatName(d.Category)} «{d.DisplayName}»")) +
                                    (ch.Indirect.Count > 8 ? $" … +{ch.Indirect.Count - 8}" : ""), 12, Ui.TextDim, wrap: true));
        }
        if (created.Count > 0)
            v.AddChild(Ui.Label("Новые: " + string.Join(", ", created.Take(12).Select(d => d.DisplayName)) + (created.Count > 12 ? " …" : ""), 12, Ui.TextDim, wrap: true));
        if (removed.Count > 0)
            v.AddChild(Ui.Label("Удалённые: " + string.Join(", ", removed.Take(12).Select(d => d.DisplayName)) + (removed.Count > 12 ? " …" : ""), 12, Ui.Warn, wrap: true));

        Dialogs.Custom(parent, "Опубликовать изменения", Scroll(v, 680, Math.Min(560, 160 + changes.Count * 70 + choices.Count * 28)), "Опубликовать", () =>
        {
            // «Сохранить старые экземпляры» for the unchecked usages: references are pinned to the previous version.
            var keepOld = choices.Where(c => !c.Box.ButtonPressed).GroupBy(c => c.Change).ToList();
            // After this dialog has closed (follow-up dialogs need the exclusive slot).
            Callable.From(() =>
            {
                int pinned = 0;
                foreach (var group in keepOld) pinned += ContentLibrary.PinUsages(ctx.Db, group.Key, group.Select(c => c.Ref));
                try
                {
                    var result = cm.PublishDraft();
                    ctx.ClearDirty();
                    ctx.Validate();
                    onPublished(result, pinned);
                }
                catch (Exception e)
                {
                    Dialogs.Info(parent, "Ошибка сохранения", e.Message + "\n\nПакет возвращён к состоянию до публикации; черновик не потерян.");
                }
            }).CallDeferred();
            return true;
        }, new Vector2I(720, 0));
    }

    public static string DescribeResult(LibraryPublishResult r, int pinned)
    {
        if (r.Written.Count == 0) return "Изменений нет.";
        var lines = new List<string> { $"Записано файлов: {r.Written.Count}" };
        foreach (var ch in r.Changes) lines.Add($"• {ContentValidator.CatName(ch.Category)} «{ch.Name}»: v{ch.OldVersion} → v{ch.NewVersion}");
        if (pinned > 0) lines.Add($"Сохранено старых экземпляров (закреплённых ссылок): {pinned}");
        if (r.BackupDir.Length > 0) lines.Add("Резервная копия: " + r.BackupDir);
        return string.Join("\n", lines);
    }

    // ------------------------------------------------------------------ dependencies

    public static void Dependencies(Node parent, EditorContext ctx, Def d)
    {
        var v = Ui.VBox(4);
        v.AddChild(Ui.Label("Использует", 16, Ui.Accent));
        var uses = ContentDependencies.Uses(d);
        if (uses.Count == 0) v.AddChild(Ui.Label("   ничего", 13, Ui.TextDim));
        foreach (var g in uses.GroupBy(u => (u.Category, u.Reference)))
        {
            var target = ctx.Db.Resolve(g.Key.Category, g.Key.Reference);
            string pin = ContentRef.Parse(g.Key.Reference).Version is { } pv ? $" · v{pv} (старый экземпляр)" : "";
            string label = target == null ? $"✗ {ContentValidator.CatName(g.Key.Category)} '{g.Key.Reference}' — не найден"
                : $"{ContentValidator.CatName(g.Key.Category)} «{target.DisplayName}»{pin}";
            v.AddChild(Row(label, g.Count() > 1 ? $"{g.Count()} мест" : g.First().Path, target == null ? null : () => ctx.Navigate(g.Key.Category, ContentRef.IdOf(g.Key.Reference)), target == null));
        }
        var closure = ContentDependencies.Closure(ctx.Db, new[] { d });
        if (closure.Count > uses.Select(u => u.Reference).Distinct().Count() + 1)
            v.AddChild(Ui.Label($"   всего с косвенными зависимостями: {closure.Count - 1}", 12, Ui.TextDim));
        v.AddChild(Ui.Sep());
        v.AddChild(Ui.Label("Используется в", 16, Ui.Accent));
        var refs = ctx.Db.FindReferences(d.Category, d.Id).Where(r => r.Owner != d).ToList();
        if (refs.Count == 0) v.AddChild(Ui.Label("   нигде", 13, Ui.TextDim));
        foreach (var r in refs.Take(60))
        {
            var owner = r.Owner;
            string pin = ContentRef.Parse(r.Id).Version is { } pv ? $" · закреплено на v{pv}" : "";
            v.AddChild(Row(r.OwnerLabel + pin, r.Path, owner == null ? null : () => ctx.Navigate(owner.Category, owner.Id), false));
        }
        Dialogs.Custom(parent, $"Связи: {d.DisplayName} [{d.Id}]", Scroll(v, 620, 420), "Закрыть", () => true, new Vector2I(660, 0));
    }

    private static Control Row(string text, string hint, Action? go, bool bad)
    {
        var h = Ui.HBox(6);
        h.AddChild(Ui.Label(text, 13, bad ? Ui.Bad : Ui.Text, wrap: true).Expand());
        h.AddChild(Ui.Label(hint, 11, Ui.TextDim).MinSize(150));
        if (go != null)
        {
            var b = Ui.Button("→", null, "Открыть");
            b.Pressed += () =>
            {
                go();
                // Close the dialog that holds this row.
                for (Node? n = b; n != null; n = n.GetParent())
                    if (n is Window w)
                    {
                        w.QueueFree();
                        break;
                    }
            };
            h.AddChild(b);
        }
        return h;
    }

    // ------------------------------------------------------------------ version history

    public static void Versions(Node parent, EditorContext ctx, Def d, Action<Def> onRestored)
    {
        var cm = Main.Instance.Content;
        var v = Ui.VBox(6);
        v.AddChild(Ui.Label($"Текущая версия: v{d.Version}" + (cm.Published.Exists(d.Category, d.Id) ? "" : " (ещё не опубликован)"), 15, Ui.Text));
        var versions = cm.UsingUserContent ? cm.Library.Versions(d.Category, d.Id) : new List<int>();
        if (versions.Count == 0)
            v.AddChild(Ui.Label("История пуста: предыдущие версии появляются после публикации изменений объекта.", 13, Ui.TextDim, wrap: true));
        var detail = Ui.Label("", 12, Ui.TextDim, wrap: true);
        foreach (var ver in versions.OrderByDescending(x => x))
        {
            int n = ver;
            var h = Ui.HBox(6);
            h.AddChild(Ui.Label($"v{n}", 14, Ui.Accent).MinSize(50));
            h.AddChild(Ui.Button("Различия", () =>
            {
                var old = cm.Library.LoadVersion(d.Category, d.Id, n);
                var diff = ContentDiff.Compare(old, d);
                detail.Text = old == null ? "Файл версии не читается." : diff.Count == 0 ? $"v{n} совпадает с текущей." :
                    $"v{n} → текущая ({diff.Count}):\n" + string.Join("\n", diff.Take(40).Select(x => x.Describe())) + (diff.Count > 40 ? "\n…" : "");
            }, "Что изменилось с этой версии"));
            h.AddChild(Ui.Button("Вернуть в черновик", () =>
            {
                var old = cm.Library.LoadVersion(d.Category, d.Id, n);
                if (old == null) return;
                // The content of the old version replaces the draft object; the version number keeps growing on publication.
                old.Version = d.Version;
                ctx.Db.Table(d.Category).Replace(old);
                ctx.MarkDirty();
                onRestored(old);
                detail.Text = $"Содержимое v{n} перенесено в черновик. Опубликуйте, чтобы оно стало версией v{d.Version + 1}.";
            }, "Содержимое этой версии заменит объект в черновике"));
            v.AddChild(h);
        }
        v.AddChild(detail);
        Dialogs.Custom(parent, $"История версий: {d.DisplayName} [{d.Id}]", Scroll(v, 600, 380), "Закрыть", () => true, new Vector2I(640, 0));
    }

    // ------------------------------------------------------------------ pack state (integrity, backups)

    public static void PackState(Node parent, Action onRestored)
    {
        var cm = Main.Instance.Content;
        var v = Ui.VBox(6);
        v.AddChild(Ui.Label("Источник: " + cm.SourceDescription, 13, Ui.TextDim, wrap: true));
        if (!cm.UsingUserContent)
        {
            v.AddChild(Ui.Label("Используется встроенный пакет (только чтение). Пользовательский пакет с историей версий, журналом и резервными копиями создаётся при первой публикации.", 13, Ui.TextDim, wrap: true));
        }
        else
        {
            var lib = cm.Library;
            var report = lib.CheckIntegrity();
            v.AddChild(Ui.Label("Целостность: " + report.Describe(), 15, report.HasManifest && !report.Ok ? Ui.Bad : Ui.Good, wrap: true));
            foreach (var f in report.Damaged.Take(12)) v.AddChild(Ui.Label("   ✗ повреждён: " + f, 12, Ui.Bad));
            foreach (var f in report.Missing.Take(12)) v.AddChild(Ui.Label("   ✗ отсутствует: " + f, 12, Ui.Bad));
            if (!report.Ok)
                v.AddChild(Ui.Button("Восстановить из резервной копии", () =>
                {
                    var restored = cm.RestoreDamaged();
                    onRestored();
                    Dialogs.Info(parent, "Восстановление", $"Восстановлено файлов: {restored.Count}. " + (cm.Integrity?.Describe() ?? ""));
                }));
            var backups = lib.Backups();
            v.AddChild(Ui.Label($"Резервных копий (по одной на публикацию): {backups.Count}" + (backups.Count > 0 ? $", последняя {backups[0]}" : ""), 13, Ui.Text, wrap: true));
            int history = 0;
            var root = Path.Combine(cm.UserRoot, ContentLibrary.HistoryDir);
            if (Directory.Exists(root)) history = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length;
            v.AddChild(Ui.Label($"Сохранённых прежних версий объектов: {history}", 13, Ui.Text));
            v.AddChild(Ui.Label("Незавершённая публикация (сбой) обнаруживается при запуске по журналу и откатывается автоматически.", 12, Ui.TextDim, wrap: true));
        }
        v.AddChild(Ui.Button("Открыть папку пакета", () => cm.OpenUserFolder()));
        Dialogs.Custom(parent, "Библиотека контента", Scroll(v, 560, 300), "Закрыть", () => true, new Vector2I(600, 0));
    }
}
