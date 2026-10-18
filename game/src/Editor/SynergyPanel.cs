using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Synergy editor (ТЗ 16.4): visual table "effect A + effect/terrain B → result". Rows are cell
/// effects (A), columns are cell effects and terrain tags (B). A click on a cell opens the synergy
/// or creates a new one for that pair. The list editor is available as a second tab.
/// </summary>
public partial class SynergyPanel : VBoxContainer
{
    private readonly EditorContext _ctx;
    private GridContainer _grid = null!;
    private VBoxContainer _detail = null!;
    private SynergyDef? _current;

    public SynergyPanel(EditorContext ctx)
    {
        _ctx = ctx;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        var tableTab = Ui.HBox(10);
        tableTab.Name = "Таблица";
        var scroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        var tableBox = Ui.VBox(6);
        tableBox.AddChild(Ui.Label("Строки — эффект A, столбцы — эффект или тег местности B. Нажмите на ячейку, чтобы открыть или создать синергию.", 13, Ui.TextDim, wrap: true).MinSize(500));
        _grid = new GridContainer();
        _grid.AddThemeConstantOverride("h_separation", 3);
        _grid.AddThemeConstantOverride("v_separation", 3);
        tableBox.AddChild(_grid);
        scroll.AddChild(tableBox);
        tableTab.AddChild(scroll);
        var detailScroll = new ScrollContainer { CustomMinimumSize = new Vector2(460, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _detail = Ui.VBox(6).Expand();
        detailScroll.AddChild(_detail);
        tableTab.AddChild(detailScroll);
        tabs.AddChild(tableTab);
        var list = new DefListPanel(_ctx, DefCategory.Synergy, "Синергии (список)") { Name = "Список" };
        tabs.AddChild(list);
        tabs.TabChanged += t =>
        {
            if (t == 0) BuildTable();
        };
        AddChild(tabs);
        BuildTable();
        ShowDetail(null);
    }

    private void BuildTable()
    {
        Ui.ClearChildren(_grid);
        var effects = _ctx.Db.All<CellEffectDef>().ToList();
        var terrainTags = _ctx.Db.All<TerrainDef>().SelectMany(t => t.Tags).Distinct().OrderBy(t => t).ToList();
        var columns = effects.Select(e => new SynergyOperandDef { Kind = SynergyOperandKind.CellEffect, Value = e.Id })
            .Concat(terrainTags.Select(t => new SynergyOperandDef { Kind = SynergyOperandKind.TerrainTag, Value = t })).ToList();
        _grid.Columns = columns.Count + 1;
        _grid.AddChild(Ui.Label("A \\ B", 12, Ui.TextDim));
        foreach (var c in columns)
        {
            var l = Ui.Label(c.Kind == SynergyOperandKind.CellEffect ? _ctx.Db.CellEffect(c.Value)?.DisplayName ?? c.Value : "мест.#" + c.Value, 11,
                c.Kind == SynergyOperandKind.CellEffect ? Ui.Parse(_ctx.Db.CellEffect(c.Value)?.Color, Ui.Text) : new Color(0.7f, 0.8f, 0.6f));
            l.CustomMinimumSize = new Vector2(74, 0);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _grid.AddChild(l);
        }
        var syns = _ctx.Db.All<SynergyDef>().ToList();
        foreach (var a in effects)
        {
            _grid.AddChild(Ui.Label(a.DisplayName, 12, Ui.Parse(a.Color, Ui.Text)).MinSize(110));
            foreach (var b in columns)
            {
                var found = syns.Where(s => Matches(s.A, a) && OperandEquals(s.B, b) || (b.Kind == SynergyOperandKind.CellEffect && Matches(s.B, a) && s.A.Kind == SynergyOperandKind.CellEffect && s.A.Value == b.Value)).ToList();
                var btn = new Button
                {
                    Text = found.Count == 0 ? "·" : string.Join("\n", found.Select(f => f.DisplayName)),
                    CustomMinimumSize = new Vector2(74, 34),
                    FocusMode = FocusModeEnum.None,
                    TooltipText = found.Count == 0 ? $"Создать: {a.DisplayName} + {b}" : string.Join("\n", found.Select(Describe)),
                };
                btn.AddThemeFontSizeOverride("font_size", 10);
                if (found.Count > 0) btn.AddThemeStyleboxOverride("normal", Ui.Box(new Color(0.32f, 0.27f, 0.15f), Ui.Accent, 4, 1, 3));
                var aa = a;
                var bb = b;
                btn.Pressed += () =>
                {
                    if (found.Count > 0) ShowDetail(found[0]);
                    else CreateFor(aa, bb);
                };
                _grid.AddChild(btn);
            }
        }
    }

    private static bool Matches(SynergyOperandDef op, CellEffectDef e) =>
        op.Kind == SynergyOperandKind.CellEffect ? op.Value == e.Id : op.Kind == SynergyOperandKind.CellEffectTag && e.Tags.Contains(op.Value);

    private static bool OperandEquals(SynergyOperandDef x, SynergyOperandDef y) => x.Kind == y.Kind && x.Value == y.Value;

    private string Describe(SynergyDef s)
    {
        var parts = new List<string>();
        if (s.RemoveA) parts.Add("A исчезает");
        if (s.RemoveB && s.B.Kind != SynergyOperandKind.TerrainTag) parts.Add("B исчезает");
        if (s.AddEffects.Count > 0) parts.Add("+ " + string.Join(", ", s.AddEffects));
        if (!string.IsNullOrEmpty(s.ConvertTerrain)) parts.Add("местность → " + s.ConvertTerrain);
        if (s.UnitEffects.Count > 0) parts.Add($"юниты в радиусе {s.Radius}: " + string.Join(", ", s.UnitEffects.Select(e => e.Describe())));
        if (s.RadiusCellEffects.Count > 0) parts.Add($"клетки в радиусе {s.Radius}: " + string.Join(", ", s.RadiusCellEffects));
        return $"{s.DisplayName}: {s.A} + {s.B} → {string.Join("; ", parts)} (кулдаун {s.CooldownPerCell:0.#} с)";
    }

    private void CreateFor(CellEffectDef a, SynergyOperandDef b)
    {
        var s = new SynergyDef
        {
            Id = ContentIO.UniqueId(_ctx.Db, DefCategory.Synergy, $"{a.Id}_{b.Value}"),
            Name = $"{a.DisplayName} + {(b.Kind == SynergyOperandKind.CellEffect ? _ctx.Db.CellEffect(b.Value)?.DisplayName : b.Value)}",
            A = new SynergyOperandDef { Kind = SynergyOperandKind.CellEffect, Value = a.Id },
            B = new SynergyOperandDef { Kind = b.Kind, Value = b.Value },
            RemoveB = b.Kind != SynergyOperandKind.TerrainTag,
        };
        _ctx.Db.Add(s);
        _ctx.MarkDirty();
        BuildTable();
        ShowDetail(s);
    }

    private void ShowDetail(SynergyDef? s)
    {
        _current = s;
        Ui.ClearChildren(_detail);
        if (s == null)
        {
            _detail.AddChild(Ui.Label("Выберите ячейку таблицы.", 14, Ui.TextDim));
            return;
        }
        _detail.AddChild(Ui.Label(Describe(s), 13, Ui.Accent, wrap: true));
        foreach (var i in _ctx.IssuesOf(s)) _detail.AddChild(Ui.Label("⚠ " + i.Message, 13, i.Severity == IssueSeverity.Error ? Ui.Bad : Ui.Warn, wrap: true));
        var grid = new PropertyGrid { LabelWidth = 170 };
        grid.SetTarget(s, _ctx.Db);
        grid.Changed += () => _ctx.MarkDirty();
        _detail.AddChild(grid);
        _detail.AddChild(Ui.Button("Удалить синергию", () => Dialogs.Confirm(this, "Удаление", $"Удалить синергию «{s.DisplayName}»?", () =>
        {
            _ctx.Db.Table(DefCategory.Synergy).Remove(s.Id);
            _ctx.MarkDirty();
            BuildTable();
            ShowDetail(null);
        }, "Удалить")));
        _detail.AddChild(Ui.Button("Обновить таблицу", BuildTable));
    }
}
