using System;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>Rules editor (ТЗ 16.5): every rule parameter and formula, with safe formula validation.</summary>
public partial class RulesPanel : ScrollContainer
{
    private readonly EditorContext _ctx;

    public RulesPanel(EditorContext ctx)
    {
        _ctx = ctx;
        HorizontalScrollMode = ScrollMode.Disabled;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        var v = Ui.VBox(8).Expand();
        v.AddChild(Ui.Label("Правила боя", 18, Ui.Accent));
        v.AddChild(Ui.Label("Все параметры по умолчанию описаны в docs/DECISIONS.md. Формулы вводятся из белого списка переменных и функций — " +
                            "произвольный код не выполняется. Пустая формула — встроенный расчёт.", 13, Ui.TextDim, wrap: true));
        var grid = new PropertyGrid { LabelWidth = 300 };
        grid.SetTarget(_ctx.Db.Rules, _ctx.Db);
        grid.Changed += () => _ctx.MarkDirty();
        v.AddChild(grid);
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_right", 14);
        pad.AddChild(v);
        AddChild(pad.Expand());
        _grid = grid;
        _ctx.ContentReplaced += OnContentReplaced;
    }

    private PropertyGrid? _grid;

    private void OnContentReplaced() => _grid?.SetTarget(_ctx.Db.Rules, _ctx.Db);

    public override void _ExitTree() => _ctx.ContentReplaced -= OnContentReplaced;
}

/// <summary>Full validation report with navigation to the offending definition.</summary>
public partial class ValidationPanel : VBoxContainer
{
    private readonly EditorContext _ctx;
    private ItemList _list = null!;
    private Label _summary = null!;
    private OptionButton _filter = null!;

    public ValidationPanel(EditorContext ctx)
    {
        _ctx = ctx;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        var head = Ui.HBox(8);
        head.AddChild(Ui.Label("Проверка контента", 18, Ui.Accent));
        _filter = new OptionButton();
        _filter.AddItem("Ошибки и предупреждения");
        _filter.AddItem("Только ошибки");
        _filter.AddItem("Всё (включая инфо)");
        _filter.ItemSelected += _ => Refresh();
        head.AddChild(_filter);
        head.AddChild(Ui.Button("Проверить сейчас", () => _ctx.Validate()));
        AddChild(head);
        _summary = Ui.Label("", 14, Ui.Text);
        AddChild(_summary);
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        _list.ItemActivated += i => Go((int)i);
        AddChild(_list);
        AddChild(Ui.Label("Двойной клик — перейти к объекту. Проверяются типы, диапазоны, уникальность ID, ссылки, формулы, карты (достижимость, запертые области) и сценарии.", 12, Ui.TextDim, wrap: true));
        _ctx.Validated += Refresh;
        Refresh();
    }

    public override void _ExitTree() => _ctx.Validated -= Refresh;

    private System.Collections.Generic.List<ContentIssue> _shown = new();

    private void Refresh()
    {
        if (!IsInstanceValid(_list)) return;
        _list.Clear();
        _shown = _ctx.Issues.Where(i => _filter.Selected switch
        {
            1 => i.Severity == IssueSeverity.Error,
            2 => true,
            _ => i.Severity != IssueSeverity.Info,
        }).OrderByDescending(i => i.Severity).ToList();
        foreach (var i in _shown)
        {
            int idx = _list.AddItem($"{(i.Severity == IssueSeverity.Error ? "✗" : i.Severity == IssueSeverity.Warning ? "⚠" : "i")}  {i.Source}  {i.Path}  —  {i.Message}");
            _list.SetItemCustomFgColor(idx, i.Severity == IssueSeverity.Error ? Ui.Bad : i.Severity == IssueSeverity.Warning ? Ui.Warn : Ui.TextDim);
        }
        _summary.Text = $"Ошибок: {_ctx.Errors} · предупреждений: {_ctx.Warnings}" + (_ctx.Db.DisabledSkills.Count > 0 ? $" · отключённых навыков: {_ctx.Db.DisabledSkills.Count}" : "");
    }

    private void Go(int i)
    {
        if (i < 0 || i >= _shown.Count) return;
        var src = _shown[i].Source;
        int colon = src.IndexOf(':');
        if (colon < 0) return;
        if (Enum.TryParse<DefCategory>(src[..colon], out var cat)) _ctx.Navigate(cat, src[(colon + 1)..]);
    }
}

/// <summary>Test launcher: runs a battle on the draft content in admin/diagnostic mode.</summary>
public partial class TestPanel : VBoxContainer
{
    private readonly EditorContext _ctx;
    private readonly Action<ScenarioDef, ulong> _run;
    private OptionButton _scenario = null!;
    private SpinBox _seed = null!;

    public TestPanel(EditorContext ctx, Action<ScenarioDef, ulong> run)
    {
        _ctx = ctx;
        _run = run;
    }

    public override void _Ready()
    {
        AddChild(Ui.Label("Тестовый бой", 18, Ui.Accent));
        AddChild(Ui.Label("Бой запускается на черновике в административном режиме: видно всех юнитов, объяснения ИИ, диагностический журнал и профилировщик (F3), " +
                          "можно управлять любыми юнитами, ставить паузу, менять скорость и шагать по тикам (клавиша «.»). " +
                          "Тест ничего не записывает в опубликованный контент.", 13, Ui.TextDim, wrap: true).MinSize(600));
        var h = Ui.HBox(8);
        h.AddChild(Ui.Label("Сценарий:", 14, Ui.TextDim));
        _scenario = new OptionButton { CustomMinimumSize = new Vector2(280, 0) };
        foreach (var s in _ctx.Db.All<ScenarioDef>()) _scenario.AddItem($"{s.DisplayName} [{s.Id}]");
        h.AddChild(_scenario);
        h.AddChild(Ui.Label("Seed:", 14, Ui.TextDim));
        _seed = new SpinBox { MinValue = 0, MaxValue = 1e9, Value = 12345, CustomMinimumSize = new Vector2(120, 0), TooltipText = "Фиксированный seed для воспроизводимости" };
        h.AddChild(_seed);
        h.AddChild(Ui.Button("▶ Запустить тест", () =>
        {
            var list = _ctx.Db.All<ScenarioDef>().ToList();
            if (list.Count == 0) return;
            _ctx.Validate();
            _run(list[Math.Max(0, _scenario.Selected)], (ulong)_seed.Value);
        }).MinSize(180, 40));
        AddChild(h);
    }
}

/// <summary>Unit/enemy editor: form + rotating 3D preview of the piece.</summary>
public partial class UnitEditorPanel : DefListPanel
{
    private SubViewport? _vp;
    private Node3D? _pivot;
    private Simulation? _sim;

    public UnitEditorPanel(EditorContext ctx, string title, Func<Def, bool> filter, string defaultFaction)
        : base(ctx, DefCategory.Unit, title, filter, () => new UnitDef
        {
            Name = defaultFaction == "enemy" ? "Новый враг" : "Новый юнит",
            Faction = defaultFaction,
            BasicAttack = ctx.Db.Ids(DefCategory.Skill).FirstOrDefault() ?? "",
            AiProfile = ctx.Db.Ids(DefCategory.AiProfile).FirstOrDefault() ?? "",
        })
    {
    }

    protected override Control BuildDetail(Def d)
    {
        var u = (UnitDef)d;
        var root = Ui.HBox(10).Expand();
        var grid = new PropertyGrid();
        grid.SetTarget(u, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(u);
            RebuildPreview(u);
        };
        root.AddChild(grid.Expand());
        var side = Ui.VBox(6);
        var container = new SubViewportContainer { Stretch = true, CustomMinimumSize = new Vector2(260, 260) };
        _vp = new SubViewport { OwnWorld3D = true, TransparentBg = false, Msaa3D = Viewport.Msaa.Msaa4X };
        container.AddChild(_vp);
        side.AddChild(container);
        side.AddChild(Ui.Label("Предпросмотр фишки (поворачивается)", 12, Ui.TextDim));
        side.AddChild(Ui.Label("Тестовый просмотр на карте — в разделе «Тест» или кнопкой ниже.", 12, Ui.TextDim, wrap: true).MinSize(260));
        var catScroll = new ScrollContainer { CustomMinimumSize = new Vector2(300, 260), SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _cats = Ui.VBox(3).Expand();
        _cats.Name = "UnitCategories";
        catScroll.AddChild(_cats);
        side.AddChild(catScroll);
        root.AddChild(side);
        grid.Changed += () => Categories(u);
        RebuildPreview(u);
        Categories(u);
        return root;
    }

    private VBoxContainer? _cats;

    /// <summary>
    /// The categories of the template (docs/DECISIONS_4.md §61): the chain of each one, every rule with its source and
    /// whether it acts (overridden, excluded, conditional, yields to…), the conflicts.
    /// </summary>
    private void Categories(UnitDef u)
    {
        if (_cats == null || !IsInstanceValid(_cats)) return;
        Ui.ClearChildren(_cats);
        var res = CategoryTree.Resolve(Ctx.Db, u);
        _cats.AddChild(Ui.Label("Категории и правила", 15, Ui.Accent));
        if (res.Assigned.Count == 0)
        {
            _cats.AddChild(Ui.Label("Нет категорий: задайте «Основная категория» или «Дополнительные категории».", 12, Ui.TextDim, wrap: true));
            return;
        }
        foreach (var a in res.Assigned)
        {
            var chain = CategoryTree.Chain(Ctx.Db, a);
            var b = Ui.Button(string.Join(" → ", chain.Select(c => c.DisplayName)) + (a.Id == u.MainCategory ? " (основная)" : ""), () => Ctx.Navigate(DefCategory.Origin, a.Id));
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            _cats.AddChild(b);
        }
        int n = 0;
        foreach (var r in res.Rules.Where(r => r.Note != "исключает правило родителя"))
        {
            var l = Ui.Label($"{(r.Active ? "✓" : "✗")} {r.Rule.Describe(Ctx.Db)} — «{r.From.DisplayName}»" + (r.Note != "" ? $" ({r.Note})" : ""), 12, r.Active ? Ui.Text : Ui.TextDim, wrap: true);
            l.Name = "CatRule_" + n++;
            _cats.AddChild(l);
        }
        if (res.Tags.Any()) _cats.AddChild(Ui.Label("Теги от категорий: " + string.Join(", ", res.Tags), 12, Ui.TextDim, wrap: true));
        foreach (var p in res.Problems.Distinct()) _cats.AddChild(Ui.Label("⚠ " + p, 12, Ui.Warn, wrap: true));
    }

    private void RebuildPreview(UnitDef u)
    {
        if (_vp == null) return;
        foreach (var c in _vp.GetChildren()) c.QueueFree();
        var env = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.12f, 0.13f, 0.15f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.7f, 0.72f, 0.78f),
                AmbientLightEnergy = 0.6f,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        };
        _vp.AddChild(env);
        _vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, -35, 0), LightEnergy = 1.2f, ShadowEnabled = true });
        var camPos = new Vector3(0, 1.9f, 3.0f);
        var cam = new Camera3D { Fov = 40, Transform = new Transform3D(Basis.LookingAt(new Vector3(0, 0.6f, 0) - camPos, Vector3.Up), camPos) };
        _vp.AddChild(cam);
        var tile = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.95f, BottomRadius = 0.95f, Height = 0.2f, RadialSegments = 6 }, MaterialOverride = Mats.Solid(new Color(0.53f, 0.64f, 0.36f)), Position = new Vector3(0, -0.1f, 0) };
        tile.RotationDegrees = new Vector3(0, 30, 0);
        _vp.AddChild(tile);
        _pivot = new Node3D();
        _vp.AddChild(_pivot);
        try
        {
            _sim = new Simulation(Ctx.Db, MapDef.CreateHexagon("preview", 1, Ctx.Db.Ids(DefCategory.Terrain).FirstOrDefault() ?? "grass"), new SimOptions { FogOfWar = false });
            var unit = _sim.Spawn(u, null, HexCoord.Zero, HexDirection.SE);
            var view = new UnitView(unit, Ctx.Db);
            _pivot.AddChild(view);
        }
        catch (Exception e)
        {
            GD.PushWarning("Предпросмотр юнита: " + e.Message);
        }
    }

    public override void _Process(double delta)
    {
        if (_pivot != null && IsInstanceValid(_pivot)) _pivot.RotateY((float)delta * 0.6f);
    }
}
