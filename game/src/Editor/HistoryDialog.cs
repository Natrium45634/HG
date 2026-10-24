using System;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Журнал…» (docs/DECISIONS_4.md §69): the operations of this session (newest first) with «Вернуть к этой точке»,
/// the log of earlier sessions from the file, the restore points with «Восстановить» and «Создать точку».
/// </summary>
public static class HistoryDialog
{
    public const string Title = "Журнал изменений";

    public static void Show(Node parent, EditorContext ctx, Action rebuild)
    {
        ctx.Commit();
        var v = Ui.VBox(4);
        v.AddChild(Ui.Label("Операции этого сеанса (новые сверху). «Вернуть к этой точке» отменяет всё, что сделано после; отменённое можно повторить (↷).", 13, Ui.TextDim, wrap: true).MinSize(700));
        var entries = ctx.History.Log.AsEnumerable().Reverse().ToList();
        if (entries.Count == 0) v.AddChild(Ui.Label("Изменений пока нет.", 13, Ui.TextDim));
        ConfirmationDialog? dlg = null;
        foreach (var e in entries.Take(60))
        {
            var row = Ui.HBox(6);
            row.Name = "LogEntry_" + e.Number;
            var l = Ui.Label($"#{e.Number} {e.Time:HH:mm:ss} {e.Describe()}" + (e.Undone ? " (отменено)" : ""), 13, e.Undone ? Ui.TextDim : Ui.Text, wrap: true).Expand();
            row.AddChild(l);
            if (!e.Undone && ctx.History.UndoStack.Contains(e) && ctx.History.UndoStack[^1] != e)
            {
                var ee = e;
                var back = Ui.Button("Вернуть к этой точке", () =>
                {
                    int n = ctx.History.UndoTo(ee);
                    ctx.Validate();
                    ctx.MarkDirtyAfterHistory();
                    dlg?.Hide();
                    dlg?.QueueFree();
                    rebuild();
                    Dialogs.Info(parent, Title, $"Отменено операций: {n}. Их можно повторить кнопкой ↷.");
                }, "Отменить все операции после этой");
                back.Name = "LogBack_" + e.Number;
                row.AddChild(back);
            }
            v.AddChild(row);
        }

        v.AddChild(Ui.Sep());
        v.AddChild(Ui.Label("Точки восстановления (создаются перед массовыми изменениями):", 14, Ui.Accent));
        var points = RestorePoints.List();
        if (points.Count == 0) v.AddChild(Ui.Label("Точек нет.", 13, Ui.TextDim));
        foreach (var p in points.Take(RestorePoints.Keep))
        {
            var pp = p;
            var row = Ui.HBox(6);
            row.AddChild(Ui.Label($"{p.Time:yyyy-MM-dd HH:mm:ss} — {p.Label}", 13).Expand());
            var restore = Ui.Button("Восстановить", () =>
            {
                Dialogs.Confirm(parent, "Точка восстановления", $"Вернуть черновик к точке «{pp.Label}»? Текущее состояние сохранится точкой восстановления, возврат можно отменить (Ctrl+Z).", () =>
                {
                    RestorePoints.Create(ctx, "перед возвратом к «" + pp.Label + "»");
                    var err = RestorePoints.Restore(ctx, pp);
                    rebuild();
                    if (err != null) Dialogs.Info(parent, Title, "Не удалось: " + err);
                }, "Восстановить");
                dlg?.Hide();
                dlg?.QueueFree();
            });
            restore.Name = "RestorePoint_" + System.IO.Path.GetFileName(p.Dir);
            row.AddChild(restore);
            v.AddChild(row);
        }
        var create = Ui.Button("Создать точку восстановления", () =>
        {
            var p = RestorePoints.Create(ctx, "вручную");
            dlg?.Hide();
            dlg?.QueueFree();
            Dialogs.Info(parent, Title, p != null ? $"Точка создана: {p.Dir}" : "Не удалось создать точку.");
        });
        create.Name = "RestorePointCreate";
        v.AddChild(create);

        v.AddChild(Ui.Sep());
        v.AddChild(Ui.Label("Прошлые сеансы (файл журнала):", 14, Ui.Accent));
        foreach (var line in EditLog.Recent(30)) v.AddChild(Ui.Label(line, 12, Ui.TextDim, wrap: true));

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(760, 520), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(v.Expand());
        dlg = Dialogs.Custom(parent, Title, scroll, "Закрыть", () => true, new Vector2I(800, 620));
    }
}
