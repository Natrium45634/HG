using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Map template editor (docs/DECISIONS_2.md §18): list of templates (standard 10 × 8 first),
/// properties and stored parts, 3D preview, create a map, derive a template, explicitly update
/// maps created from the template, restore the built-in templates.
/// </summary>
public partial class MapTemplatePanel : DefListPanel
{
    private readonly Action<string> _openMap;
    private BoardViewport? _view;

    public MapTemplatePanel(EditorContext ctx, Action<string> openMap)
        : base(ctx, DefCategory.MapTemplate, "Шаблоны карт", null, null)
    {
        _openMap = openMap;
    }

    protected override Def CreateNew()
    {
        var std = Ctx.Db.Get<MapTemplateDef>(MapTemplates.StandardId) ?? MapTemplates.CreateBuiltIn(MapTemplates.StandardId, Ctx.Db)!;
        var t = MapTemplates.Derive(std, "template");
        t.Name = "Новый шаблон";
        return t;
    }

    protected override Control BuildDetail(Def d)
    {
        var t = (MapTemplateDef)d;
        var root = Ui.VBox(8).Expand(true, true);
        var (cols, rows) = t.Size();
        root.AddChild(Ui.Label($"{(t.BuiltIn ? "Встроенный шаблон" : "Пользовательский шаблон")} · версия {t.Version} · {cols} × {rows} · клеток {t.Cells.Count}, меток {t.Markers.Count}, маршрутов {t.Routes.Count}",
            14, Ui.TextDim, wrap: true));

        var actions = new HFlowContainer();
        actions.AddThemeConstantOverride("h_separation", 6);
        actions.AddChild(Ui.Button("Создать карту из шаблона…", () => CreateMap(t), "Новая независимая карта из этого шаблона"));
        actions.AddChild(Ui.Button("Новый шаблон на основе", () => DeriveFrom(t)));
        actions.AddChild(Ui.Button("Обновить карты из шаблона…", () => UpdateMaps(t), "Перенести части шаблона в карты, созданные из него (с подтверждением)"));
        actions.AddChild(Ui.Button("Восстановить встроенные…", RestoreBuiltIn, "Пересоздать исходные встроенные шаблоны"));
        root.AddChild(actions);

        var split = Ui.HBox(10).Expand(true, true);
        var grid = new PropertyGrid { LabelWidth = 190 };
        grid.SetTarget(t, Ctx.Db);
        grid.Changed += () => OnEdited(t);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(420, 0) };
        scroll.AddChild(grid.Expand());
        split.AddChild(scroll.Expand(false, true));

        _view = new BoardViewport { CustomMinimumSize = new Vector2(420, 360), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        split.AddChild(_view);
        root.AddChild(split);
        Callable.From(() => Preview(t)).CallDeferred();
        return root;
    }

    private void Preview(MapTemplateDef t)
    {
        if (_view == null || !IsInstanceValid(_view)) return;
        if (t.Cells.Count == 0) return;
        try
        {
            var map = MapTemplates.CreateMap(t, "preview", Ctx.Db);
            map.Markers = t.Markers.Select(ContentJson.Clone).ToList();
            var sim = new Simulation(Ctx.Db, map, new SimOptions { FogOfWar = false });
            _view.Bind(sim.Map, Ctx.Db, true);
            _view.Labels.Begin();
            foreach (var m in t.Markers) _view.Labels.Add(Render.Board.CellTop(sim.Map.Get(m.C) ?? sim.Map.Cells[0]) + new Vector3(0, 0.4f, 0),
                string.IsNullOrEmpty(m.Label) ? Names.Of(m.Type) : m.Label, Ui.Accent, 34);
            _view.Labels.End();
        }
        catch (Exception e)
        {
            GD.PushWarning("Предпросмотр шаблона: " + e.Message);
        }
    }

    private void CreateMap(MapTemplateDef t)
    {
        Dialogs.Prompt(this, "Карта из шаблона", $"Название новой карты (шаблон «{t.DisplayName}»). Карта — независимая копия: изменения шаблона её не меняют.", t.DisplayName, name =>
        {
            var id = ContentIO.UniqueId(Ctx.Db, DefCategory.Map, "map");
            var map = MapTemplates.CreateMap(t, id, Ctx.Db, name.Length > 0 ? name : t.DisplayName);
            Ctx.Db.Add(map);
            Ctx.MarkDirty();
            _openMap(map.Id);
        });
    }

    private void DeriveFrom(MapTemplateDef t)
    {
        var copy = MapTemplates.Derive(t, ContentIO.UniqueId(Ctx.Db, DefCategory.MapTemplate, t.Id + "_copy"));
        Ctx.Db.Add(copy);
        Ctx.MarkDirty();
        RefreshList();
        Select(copy);
    }

    private void UpdateMaps(MapTemplateDef t)
    {
        var deps = MapTemplates.MapsFrom(Ctx.Db, t);
        if (deps.Count == 0)
        {
            Dialogs.Info(this, "Обновление карт", $"Из шаблона «{t.DisplayName}» не создано ни одной карты.");
            return;
        }
        var box = Ui.VBox(6);
        box.AddChild(Ui.Label("Выберите карты. В них будут заменены части, отмеченные в шаблоне (форма, местность, высоты, препятствия, метки, маршруты, прочее). " +
                              "Остальное в картах сохранится.", 13, Ui.TextDim, wrap: true).MinSize(520));
        var checks = new List<(CheckBox Box, MapDef Map)>();
        foreach (var (m, outdated) in deps)
        {
            var cb = new CheckBox { Text = $"{m.DisplayName} [{m.Id}] — версия шаблона {m.TemplateVersion}{(outdated ? $" (устарела, текущая {t.Version})" : " (актуальна)")}", ButtonPressed = outdated };
            checks.Add((cb, m));
            box.AddChild(cb);
        }
        Dialogs.Custom(this, "Обновить карты из шаблона", box, "Обновить выбранные", () =>
        {
            int n = 0;
            foreach (var (cb, m) in checks)
            {
                if (!cb.ButtonPressed) continue;
                MapTemplates.ApplyToMap(t, m, Ctx.Db);
                n++;
            }
            if (n > 0) Ctx.MarkDirty();
            Dialogs.Info(this, "Обновление карт", n == 0 ? "Ничего не выбрано — карты не изменены." : $"Обновлено карт: {n}. Изменения попадут в контент при сохранении.");
            return true;
        });
    }

    private void RestoreBuiltIn()
    {
        Dialogs.Confirm(this, "Восстановить встроенные шаблоны",
            "Встроенные шаблоны будут пересозданы в исходном виде (ваши изменения в них будут потеряны после сохранения; прежние версии сохранятся в истории). " +
            "Пользовательские шаблоны и карты не изменятся. Продолжить?", () =>
            {
                foreach (var fresh in MapTemplates.CreateAllBuiltIn(Ctx.Db))
                {
                    var old = Ctx.Db.Get<MapTemplateDef>(fresh.Id);
                    if (old != null) fresh.Version = old.Version;
                    Ctx.Db.Table(DefCategory.MapTemplate).Replace(fresh);
                }
                Ctx.MarkDirty();
                RefreshList();
                Select(MapTemplates.StandardId);
            }, "Восстановить");
    }
}
