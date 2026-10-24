using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Administrative mode: all content editors on a draft copy. Publishing writes the user content
/// pack (with a backup of the previous version); tests run on the draft and never write anything.
/// The draft is autosaved separately every minute.
/// </summary>
public partial class EditorScreen : Control
{
    private static string _lastSection = "maps";
    private static (DefCategory, string)? _lastDef;

    private EditorContext _ctx = null!;
    private VBoxContainer _sectionList = null!;
    private Control _content = null!;
    private Label _state = null!;
    private Label _issues = null!;
    private Button _undo = null!;

    /// <summary>The editor's context (UI tests).</summary>
    public EditorContext Context => _ctx;
    private Button _redo = null!;
    private readonly Dictionary<string, Button> _sectionButtons = new();
    private Control? _current;
    private string _currentKey = "";
    private float _autosaveTimer = 60;

    private static readonly (string Key, string Title, DefCategory? Cat)[] Sections =
    {
        ("maps", "Карты", DefCategory.Map),
        ("templates", "Шаблоны карт", DefCategory.MapTemplate),
        ("units", "Юниты", DefCategory.Unit),
        ("enemies", "Враги", DefCategory.Unit),
        ("skills", "Навыки", DefCategory.Skill),
        ("terrains", "Типы местности", DefCategory.Terrain),
        ("layers", "Слои существ", DefCategory.Layer),
        ("cell_effects", "Эффекты клеток", DefCategory.CellEffect),
        ("statuses", "Состояния", DefCategory.Status),
        ("synergies", "Синергии", DefCategory.Synergy),
        ("passives", "Пассивные умения", DefCategory.Passive),
        ("talents", "Таланты", DefCategory.Passive),
        ("origins", "Категории существ", DefCategory.Origin),
        ("resources", "Ресурсы", DefCategory.Resource),
        ("stats", "Характеристики", DefCategory.Stat),
        ("elements", "Элементы", DefCategory.Element),
        ("damage", "Типы урона", DefCategory.DamageType),
        ("factions", "Фракции", DefCategory.Faction),
        ("ai", "Профили ИИ", DefCategory.AiProfile),
        ("formations", "Формации", DefCategory.Formation),
        ("scenarios", "Сценарии", DefCategory.Scenario),
        ("td", "TD-сцена", DefCategory.Scenario),
        ("group_presets", "Пресеты групп", DefCategory.GroupPreset),
        ("assets", "Ресурсы", DefCategory.Asset),
        ("vfx", "Визуальные эффекты", DefCategory.Vfx),
        ("requirements", "Требования к ресурсам", null),
        ("rules", "Правила боя", null),
        ("scaling_profiles", "Профили масштабирования", DefCategory.ScalingProfile),
        ("presets", "Пресеты правил", DefCategory.RulesPreset),
        ("catalog", "Каталог операций", null),
        ("check", "Проверка", null),
        ("test", "Тестирование", null),
    };

    public override void _Ready()
    {
        var app = Main.Instance;
        Theme = app.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore }.Also(r => r.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect)));

        var draft = app.Content.BeginEdit();
        _ctx = new EditorContext(draft);
        PropertyGrid.IdLocked = d => app.Content.Published.Exists(d.Category, d.Id) && app.Content.Published.Table(d.Category).Get(d.Id)?.GetType() == d.GetType();
        if (app.Content.DraftDirty) _ctx.MarkDirty();
        _ctx.DirtyChanged += UpdateState;
        _ctx.Validated += UpdateState;
        _ctx.NavigateRequested += Navigate;
        _ctx.HistoryChanged += UpdateHistoryButtons;

        var root = Ui.VBox(6);
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.OffsetLeft = 8;
        root.OffsetTop = 8;
        root.OffsetRight = -8;
        root.OffsetBottom = -8;
        AddChild(root);

        // Top bar: one line whatever the state says — the state label takes the free width and is clipped (the full
        // text is its tooltip), so a long message never moves the forms below.
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 8);
        top.AddChild(Ui.Label("Административный режим", 18, Ui.Accent));
        _state = Ui.Label("", 14, Ui.TextDim);
        _state.ClipText = true;
        _state.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _state.CustomMinimumSize = new Vector2(60, 0);
        _state.MouseFilter = MouseFilterEnum.Pass;
        top.AddChild(_state);
        _issues = Ui.Label("", 14, Ui.TextDim);
        top.AddChild(_issues);
        _undo = Ui.Button("↶", () => UndoRedo(false), "Отменить действие (Ctrl+Z)");
        _undo.Name = "EdUndo";
        top.AddChild(_undo);
        _redo = Ui.Button("↷", () => UndoRedo(true), "Повторить действие (Ctrl+Y, Ctrl+Shift+Z)");
        _redo.Name = "EdRedo";
        top.AddChild(_redo);
        var log = Ui.Button("Журнал…", () => HistoryDialog.Show(this, _ctx, RebuildAfterHistory), "Журнал последних изменений, возврат к точке, точки восстановления");
        log.Name = "EdLog";
        top.AddChild(log);
        top.AddChild(Ui.Button("Сохранить", Publish, "Опубликовать: записать черновик в пользовательский пакет контента (версии, история, резервная копия)"));
        top.AddChild(Ui.Button("Библиотека…", () => LibraryDialogs.PackState(this, ReloadAfterRestore), "Целостность пакета, резервные копии, история версий"));
        top.AddChild(Ui.Button("Импорт…", () => PackageDialogs.Import(this, _ctx, _ => ShowSection(_currentKey)), "Импорт пакета .hexpack или объекта .json в черновик"));
        top.AddChild(Ui.Button("Отменить", Revert, "Отменить неопубликованные изменения и вернуть опубликованную версию"));
        top.AddChild(Ui.Button("Автосохр.", RestoreAutosave, "Восстановить автосохранённый черновик (сохраняется раз в минуту)"));
        top.AddChild(Ui.Button("Встроенный", ResetBuiltIn, "Удалить пользовательский пакет (с копией) и вернуться к встроенному контенту"));
        top.AddChild(Ui.Button("В меню", Exit));
        root.AddChild(top);

        var body = Ui.HBox(8).Expand(true, true);
        var sideScroll = new ScrollContainer { CustomMinimumSize = new Vector2(210, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _sectionList = Ui.VBox(3);
        foreach (var (key, title, _) in Sections)
        {
            var k = key;
            var b = Ui.Button(title, () => ShowSection(k)).MinSize(200, 34);
            b.Alignment = HorizontalAlignment.Left;
            b.ToggleMode = true;
            _sectionButtons[key] = b;
            _sectionList.AddChild(b);
        }
        sideScroll.AddChild(_sectionList);
        body.AddChild(Ui.PanelBox(sideScroll, null, 6));
        _content = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        ((PanelContainer)_content).AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, Ui.Border, 8, 1, 10));
        body.AddChild(_content);
        root.AddChild(body);

        string section = app.Args.GetValueOrDefault("tab", _lastSection);
        ShowSection(section);
        if (_lastDef is { } ld && app.Args.Count == 0) Navigate(ld.Item1, ld.Item2);
        if (app.Args.TryGetValue("select", out var sel) && _current is DefListPanel dl) dl.Select(sel);
        UpdateState();
        UpdateHistoryButtons();
        // Screenshots: --dialog=history|wavereport|compare|mass.
        if (app.Args.TryGetValue("dialog", out var dialog)) Callable.From(() => OpenDialogForShot(dialog)).CallDeferred();

        if (app.Content.HasAutosave && !app.Content.DraftDirty && !app.Args.ContainsKey("auto") && !app.Args.ContainsKey("uitest"))
            Dialogs.Confirm(this, "Автосохранение", "Найден автоматически сохранённый черновик с несохранёнными изменениями. Восстановить его?", RestoreAutosave, "Восстановить", "Нет");
    }

    public override void _Process(double delta)
    {
        _ctx.Tick();
        _autosaveTimer -= (float)delta;
        if (_autosaveTimer <= 0)
        {
            _autosaveTimer = 60;
            if (_ctx.Dirty)
            {
                Main.Instance.Content.Autosave();
                SetState("● изменения не опубликованы (автосохранено " + DateTime.Now.ToString("HH:mm") + ")");
            }
        }
        if (_current is DefListPanel p && p.Current != null) _lastDef = (p.Current.Category, p.Current.Id);
    }

    private void OpenDialogForShot(string which)
    {
        switch (which)
        {
            case "history":
                HistoryDialog.Show(this, _ctx, RebuildAfterHistory);
                break;
            case "wavereport" when _current is TdScenePanel td && td.Scene != null:
                td.ShowWaveReport();
                break;
            case "compare" when _current is UnitEditorPanel up && up.Current is UnitDef u:
                var other = ContentJson.Clone(u);
                other.Stats[Stats.HpMax] = Math.Round(u.Stats.GetValueOrDefault(Stats.HpMax, 100) / 2);
                VersionCompareDialog.Show(this, _ctx, u, other, "условная v0 (половина здоровья)");
                break;
        }
    }

    private void UpdateHistoryButtons()
    {
        if (!IsInstanceValid(_undo)) return;
        _undo.Disabled = !_ctx.History.CanUndo && !_ctx.History.Pending;
        _redo.Disabled = !_ctx.History.CanRedo;
        _undo.TooltipText = "Отменить (Ctrl+Z)" + (_ctx.History.UndoStack.Count > 0 ? ": " + _ctx.History.UndoStack[^1].Describe() : "");
    }

    /// <summary>Ctrl+Z / Ctrl+Y in the editor only (in a battle Z is the speed). A focused text field keeps its own undo.</summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, CtrlPressed: true } k) return;
        if (k.Keycode == Key.Z && !k.ShiftPressed) UndoRedo(false);
        else if (k.Keycode == Key.Y || k.Keycode == Key.Z && k.ShiftPressed) UndoRedo(true);
        else return;
        GetViewport().SetInputAsHandled();
    }

    public void UndoRedo(bool redo)
    {
        var e = redo ? _ctx.Redo() : _ctx.Undo();
        if (e == null)
        {
            SetState(redo ? "Нечего повторять" : "Нечего отменять");
            return;
        }
        RebuildAfterHistory();
        SetState((redo ? "↷ повторено: " : "↶ отменено: ") + e.Describe());
    }

    /// <summary>The objects are new instances after an undo: the open section is rebuilt on the same object and mode.</summary>
    private void RebuildAfterHistory()
    {
        var sel = (_current as DefListPanel)?.Current;
        var mode = (_current as TdScenePanel)?.Mode;
        ShowSection(_currentKey);
        if (_current is DefListPanel p && sel != null && _ctx.Db.Exists(sel.Category, sel.Id))
        {
            p.Select(sel.Id);
            if (mode != null && _current is TdScenePanel td) Callable.From(() => td.SetMode(mode.Value)).CallDeferred();
        }
        UpdateHistoryButtons();
        UpdateState();
    }

    private void SetState(string text) => _state.Text = _state.TooltipText = text;

    private void UpdateState()
    {
        if (_state == null) return;
        SetState(_ctx.Dirty ? "● изменения не опубликованы" : "✓ всё опубликовано");
        _state.AddThemeColorOverride("font_color", _ctx.Dirty ? Ui.Warn : Ui.Good);
        _issues.Text = _issues.TooltipText = $"ошибок {_ctx.Errors} · предупреждений {_ctx.Warnings}";
        _issues.AddThemeColorOverride("font_color", _ctx.Errors > 0 ? Ui.Bad : Ui.TextDim);
    }

    private void ShowSection(string key)
    {
        // The edits of the section left behind are their own operation of the history.
        _ctx.Commit();
        if (!Sections.Any(s => s.Key == key)) key = "maps";
        foreach (var kv in _sectionButtons) kv.Value.ButtonPressed = kv.Key == key;
        if (_current != null)
        {
            _content.RemoveChild(_current);
            _current.QueueFree();
        }
        _currentKey = key;
        _lastSection = key;
        _current = key switch
        {
            "maps" => new MapEditorPanel(_ctx, PreviewMap),
            "templates" => new MapTemplatePanel(_ctx, id => Navigate(DefCategory.Map, id)),
            "units" => new UnitEditorPanel(_ctx, "Юниты (союзники и нейтралы)", d => ((UnitDef)d).Faction != "enemy", "player"),
            "enemies" => new UnitEditorPanel(_ctx, "Враги", d => ((UnitDef)d).Faction == "enemy", "enemy"),
            "skills" => new SkillEditorPanel(_ctx, TestSkill),
            "terrains" => new TerrainPanel(_ctx),
            "layers" => new DefListPanel(_ctx, DefCategory.Layer, "Слои существ"),
            "cell_effects" => new DefListPanel(_ctx, DefCategory.CellEffect, "Эффекты клеток"),
            "statuses" => new DefListPanel(_ctx, DefCategory.Status, "Состояния", null, () => new StatusDef
            {
                Name = "Новое состояние",
                Levels = new() { new StatusLevelDef { Threshold = 1 } },
            }),
            "synergies" => new SynergyPanel(_ctx),
            "passives" => new PassivePanel(_ctx, PassiveKind.Passive),
            "talents" => new PassivePanel(_ctx, PassiveKind.Talent),
            "origins" => new OriginPanel(_ctx),
            "resources" => new DefListPanel(_ctx, DefCategory.Resource, "Ресурсы"),
            "stats" => new DefListPanel(_ctx, DefCategory.Stat, "Характеристики"),
            "elements" => new DefListPanel(_ctx, DefCategory.Element, "Элементы (стихии с наследованием)"),
            "damage" => new DefListPanel(_ctx, DefCategory.DamageType, "Типы урона"),
            "factions" => new DefListPanel(_ctx, DefCategory.Faction, "Фракции"),
            "ai" => new DefListPanel(_ctx, DefCategory.AiProfile, "Профили ИИ"),
            "formations" => new FormationPanel(_ctx),
            "scenarios" => new ScenarioEditorPanel(_ctx, TestScenario),
            "td" => new TdScenePanel(_ctx, TestScenario),
            "group_presets" => new DefListPanel(_ctx, DefCategory.GroupPreset, "Пресеты групп волн (применяются в TD-сцене)", null, () => new GroupPresetDef { Name = "Новый пресет" }),
            "rules" => new RulesPanel(_ctx),
            "scaling_profiles" => new DefListPanel(_ctx, DefCategory.ScalingProfile, "Профили масштабирования («Подогнать под уровень», уровень экземпляра)", null, () => new ScalingProfileDef { Name = "Новый профиль" }),
            "assets" => new AssetPanel(_ctx),
            "vfx" => new VfxPanel(_ctx),
            "requirements" => new RequirementsPanel(_ctx),
            "presets" => new DefListPanel(_ctx, DefCategory.RulesPreset, "Пресеты правил (выбираются в сценарии)", null, () => new RulesPresetDef
            {
                Name = "Новый пресет",
                Rules = ContentJson.Clone(_ctx.Db.Rules),
            }),
            "check" => new ValidationPanel(_ctx),
            "catalog" => new CatalogPanel(),
            _ => new TestPanel(_ctx, (sc, seed) => RunTest(sc, seed)),
        };
        _current.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _current.SizeFlagsVertical = SizeFlags.ExpandFill;
        _content.AddChild(_current);
    }

    private void Navigate(DefCategory c, string id)
    {
        string key = c switch
        {
            DefCategory.Map => "maps",
            DefCategory.MapTemplate => "templates",
            DefCategory.Unit => _ctx.Db.Unit(id)?.Faction == "enemy" ? "enemies" : "units",
            DefCategory.Skill => "skills",
            DefCategory.Terrain => "terrains",
            DefCategory.Element => "elements",
            DefCategory.Layer => "layers",
            DefCategory.CellEffect => "cell_effects",
            DefCategory.Status => "statuses",
            DefCategory.Synergy => "synergies",
            DefCategory.Passive => _ctx.Db.Get<PassiveDef>(id)?.Kind == PassiveKind.Talent ? "talents" : "passives",
            DefCategory.Origin => "origins",
            DefCategory.Resource => "resources",
            DefCategory.Stat => "stats",
            DefCategory.DamageType => "damage",
            DefCategory.Faction => "factions",
            DefCategory.AiProfile => "ai",
            DefCategory.Formation => "formations",
            DefCategory.Scenario => "scenarios",
            DefCategory.GroupPreset => "group_presets",
            DefCategory.RulesPreset => "presets",
            DefCategory.ScalingProfile => "scaling_profiles",
            DefCategory.Asset => "assets",
            DefCategory.Vfx => "vfx",
            _ => "rules",
        };
        ShowSection(key);
        if (_current is DefListPanel p) Callable.From(() => p.Select(id)).CallDeferred();
    }

    // ------------------------------------------------------------------ publish / revert

    /// <summary>Publication through the library: usages of changed objects, versions, transactional write (docs/DECISIONS_2.md §31).</summary>
    private void Publish()
    {
        LibraryDialogs.Publish(this, _ctx, (result, pinned) =>
        {
            // Versions changed in the draft: refresh the open form.
            ShowSection(_currentKey);
            if (_lastDef is { } ld) Navigate(ld.Item1, ld.Item2);
            Dialogs.Info(this, "Сохранено", LibraryDialogs.DescribeResult(result, pinned));
        });
    }

    private void ReloadAfterRestore()
    {
        var cm = Main.Instance.Content;
        if (cm.DraftDirty) return;
        var draft = cm.BeginEdit();
        _ctx.Replace(draft);
        _ctx.ClearDirty();
        ShowSection(_currentKey);
    }

    private void Revert()
    {
        Dialogs.Confirm(this, "Отменить изменения", "Отбросить все неопубликованные изменения черновика?", () =>
        {
            var cm = Main.Instance.Content;
            cm.DiscardDraft();
            _ctx.Replace(cm.Draft!);
            _ctx.ClearDirty();
            ShowSection(_currentKey);
        }, "Отбросить");
    }

    private void RestoreAutosave()
    {
        var cm = Main.Instance.Content;
        var res = cm.RestoreAutosave();
        if (res == null)
        {
            Dialogs.Info(this, "Автосохранение", "Автосохранённого черновика нет.");
            return;
        }
        _ctx.Replace(cm.Draft!);
        _ctx.MarkDirty();
        ShowSection(_currentKey);
    }

    private void ResetBuiltIn()
    {
        var cm = Main.Instance.Content;
        if (!cm.UsingUserContent)
        {
            Dialogs.Info(this, "Сброс", "Уже используется встроенный пакет контента.");
            return;
        }
        Dialogs.Confirm(this, "Сброс к встроенному контенту",
            "Пользовательский пакет будет перемещён в резервную папку, а игра вернётся к встроенному контенту. Продолжить?", () =>
            {
                var backup = cm.ResetToBuiltIn();
                var draft = cm.BeginEdit();
                _ctx.Replace(draft);
                _ctx.ClearDirty();
                ShowSection(_currentKey);
                Dialogs.Info(this, "Готово", "Копия пользовательского пакета: " + backup);
            }, "Сбросить");
    }

    private void Exit()
    {
        if (!_ctx.Dirty)
        {
            Main.Instance.ShowMainMenu();
            return;
        }
        Main.Instance.Content.Autosave();
        Dialogs.Confirm(this, "Неопубликованные изменения", "Черновик сохранён в автосохранение, но не опубликован — игра продолжит использовать опубликованный контент. Выйти?",
            () => Main.Instance.ShowMainMenu(), "Выйти");
    }

    // ------------------------------------------------------------------ tests (draft only)

    private void RunTest(ScenarioDef sc, ulong seed)
    {
        var db = _ctx.Db.Clone();
        ContentValidator.Validate(db);
        var copy = db.Get<ScenarioDef>(sc.Id) ?? sc;
        if (db.Get<MapDef>(copy.Map) == null)
        {
            Dialogs.Info(this, "Тест невозможен", $"Сценарий «{copy.DisplayName}» ссылается на отсутствующую карту «{copy.Map}».");
            return;
        }
        copy.Seed = seed;
        Main.Instance.Content.Autosave();
        Main.Instance.ShowBattle(copy, db, admin: true, onExit: () => Main.Instance.ShowEditor());
    }

    private void TestScenario(ScenarioDef sc) => RunTest(sc, sc.Seed == 0 ? 12345 : sc.Seed);

    private void TestSkill(SkillDef skill)
    {
        var db = _ctx.Db.Clone();
        var baseUnit = db.All<UnitDef>().FirstOrDefault(u => u.Faction == "player" && u.Visual.Shape == UnitShape.Mage) ?? db.All<UnitDef>().FirstOrDefault();
        if (baseUnit == null)
        {
            Dialogs.Info(this, "Тест невозможен", "Нет ни одного юнита для теста.");
            return;
        }
        var tester = ContentJson.Clone(baseUnit);
        tester.Id = "editor_tester";
        tester.Name = "Испытатель";
        tester.Faction = "player";
        tester.Skills = new() { skill.Id };
        tester.Stats[Stats.ApMax] = Math.Max(skill.ApCost + 4, tester.Stats.GetValueOrDefault(Stats.ApMax, 10));
        tester.Resources = db.All<ResourceDef>().Select(r => new UnitResourceDef { Resource = r.Id, Max = 999, Start = 999, Regen = 5 }).ToList();
        db.Table(DefCategory.Unit).Replace(tester);
        var map = db.Get<MapDef>("proving_ground") ?? db.All<MapDef>().FirstOrDefault();
        if (map == null)
        {
            Dialogs.Info(this, "Тест невозможен", "Нет ни одной карты.");
            return;
        }
        var cells = map.Cells.Where(c => db.Terrain(c.T)?.IsPassableFor("walk") == true && db.Terrain(c.T)?.AllowsPlacement == true).Select(c => c.C).ToList();
        var start = map.Markers.FirstOrDefault(m => m.Type == MarkerType.PlayerStart)?.C ?? cells.FirstOrDefault();
        var enemyDefs = db.All<UnitDef>().Where(u => u.Faction == "enemy").Take(3).ToList();
        var sc = new ScenarioDef
        {
            Id = "editor_skill_test",
            Name = "Тест навыка: " + skill.DisplayName,
            Map = map.Id,
            Seed = 12345,
            Intro = $"Тест навыка «{skill.DisplayName}» (клавиша 1). Мишени не атакуют. Черновик не сохраняется.",
            Victory = new(),
            Defeat = new(),
        };
        sc.Units.Add(new ScenarioUnitDef { Unit = tester.Id, C = start, Facing = HexDirection.E });
        int k = 0;
        foreach (var off in new[] { new HexCoord(3, 0), new HexCoord(4, -1), new HexCoord(3, 1), new HexCoord(5, 0) })
        {
            var c = start + off;
            if (!cells.Contains(c) || enemyDefs.Count == 0) continue;
            sc.Units.Add(new ScenarioUnitDef { Unit = enemyDefs[k++ % enemyDefs.Count].Id, C = c, Facing = HexDirection.W, AiProfile = db.Get<AiProfileDef>("skittish") != null ? "hold" : "", Name = "Мишень " + k });
        }
        sc.Sides = new() { new ScenarioSideDef { Faction = "player", Controller = SideController.Player } };
        db.Table(DefCategory.Scenario).Replace(sc);
        ContentValidator.Validate(db);
        Main.Instance.Content.Autosave();
        Main.Instance.ShowBattle(sc, db, admin: true, onExit: () => Main.Instance.ShowEditor());
    }

    private void PreviewMap(MapDef map)
    {
        var db = _ctx.Db.Clone();
        var m = db.Get<MapDef>(map.Id)!;
        var passable = m.Cells.Where(c => db.Terrain(c.T)?.IsPassableFor("walk") == true && db.Terrain(c.T)?.AllowsPlacement == true && c.Blocked != true).Select(c => c.C).ToHashSet();
        if (passable.Count == 0)
        {
            Dialogs.Info(this, "Просмотр невозможен", "На карте нет клеток, где можно разместить юнитов.");
            return;
        }
        var sc = new ScenarioDef { Id = "editor_map_preview", Name = "Просмотр карты: " + m.DisplayName, Map = m.Id, Victory = new(), Defeat = new(), Seed = 1 };
        var players = db.All<UnitDef>().Where(u => u.Faction == "player").Take(3).ToList();
        var used = new HashSet<HexCoord>();
        HexCoord Place(HexCoord near)
        {
            foreach (var c in HexCoord.Spiral(near, 6))
                if (passable.Contains(c) && used.Add(c))
                    return c;
            var any = passable.First(c => !used.Contains(c));
            used.Add(any);
            return any;
        }
        var start = m.Markers.FirstOrDefault(x => x.Type == MarkerType.PlayerStart)?.C ?? passable.First();
        foreach (var p in players) sc.Units.Add(new ScenarioUnitDef { Unit = p.Id, C = Place(start), Facing = HexDirection.E });
        var enemy = db.All<UnitDef>().FirstOrDefault(u => u.Faction == "enemy");
        if (enemy != null)
            foreach (var mk in m.Markers.Where(x => x.Type == MarkerType.EnemySpawn))
                sc.Units.Add(new ScenarioUnitDef { Unit = enemy.Id, C = Place(mk.C), Facing = HexDirection.W });
        sc.Intro = "Просмотр карты в игровом режиме (черновик).";
        db.Table(DefCategory.Scenario).Replace(sc);
        ContentValidator.Validate(db);
        Main.Instance.ShowBattle(sc, db, admin: true, onExit: () => Main.Instance.ShowEditor());
    }
}
