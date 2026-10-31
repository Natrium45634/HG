using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// Battle HUD: time controls, selected unit panel (HP, AP with reserve, resources, statuses and
/// accumulation bars), action bar with reasons for unavailable actions, command queue, combat log,
/// cell tooltip, context menus, banners, outcome and help. Panels can be collapsed.
/// </summary>
public partial class BattleHud : Control
{
    private readonly BattleScreen _bs;
    private Simulation Sim => _bs.Sim;

    private readonly List<Control> _panels = new();
    private Label _time = null!;
    private Label _status = null!;
    private Button _pauseBtn = null!;
    private readonly List<Button> _speedBtns = new();

    private PanelContainer _unitPanel = null!;
    private VBoxContainer _unitBox = null!;
    private Label _unitName = null!;
    private Label _unitSub = null!;
    private Bar _hp = null!;
    private Bar _ap = null!;
    private Label _apInfo = null!;
    private VBoxContainer _resources = null!;
    private VBoxContainer _statuses = null!;
    private Label _current = null!;
    private Label _aiInfo = null!;
    private VBoxContainer _statsBox = null!;
    private bool _showStats;
    private Button _statsBtn = null!;
    private HFlowContainer _stackStrip = null!;
    private string _stackKey = "";
    private Label _blockInfo = null!;
    private Label _autoInfo = null!;
    private Button _autoToggle = null!;
    private HFlowContainer _interactRow = null!;
    private string _interactKey = "";
    private VBoxContainer _intel = null!;

    private PanelContainer _actionPanel = null!;
    private HFlowContainer _actions = null!;
    private Label _modeHint = null!;
    private string _actionsKey = "";

    private PanelContainer _queuePanel = null!;
    private VBoxContainer _queueList = null!;
    private Label _queueFooter = null!;
    private string _queueKey = "";
    private CheckButton _appendToggle = null!;
    private OptionButton _routeMode = null!;
    private ScrollContainer _queueScroll = null!;

    private PanelContainer _logPanel = null!;
    private RichTextLabel _log = null!;
    private int _logLines;
    private OptionButton _logFilter = null!;

    private PanelContainer _tooltip = null!;
    private RichTextLabel _tooltipText = null!;
    private Label _banner = null!;
    private float _bannerTime;
    private PanelContainer _outcome = null!;
    private Label _outcomeCampaign = null!;
    private Label _outcomeText = null!;
    private PanelContainer _help = null!;
    private PanelContainer _menu = null!;
    private PanelContainer _diagPanel = null!;
    private Label _diagText = null!;
    private PopupMenu _popup = null!;
    private PopupMenu _skillSub = null!;
    private List<ActionInfo> _popupActions = new();
    private int _popupInsert = -1;
    private int _waypointIndex = -1;
    private float _refreshTimer;
    private string _lastMessage = "";

    public BattleHud(BattleScreen bs)
    {
        _bs = bs;
    }

    public override void _Ready()
    {
        Theme = Main.Instance.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        BuildTopBar();
        BuildUnitPanel();
        BuildActionBar();
        BuildQueuePanel();
        BuildObjectives();
        BuildBossBar();
        BuildLog();
        BuildTooltip();
        BuildBanner();
        BuildOutcome();
        BuildHelp();
        BuildMenu();
        BuildDiag();
        BuildPopup();
        _groupPanel = Track(new GroupPanel(_bs));
        AddChild(_groupPanel);
        // Part 4, stage 5 (docs/DECISIONS_4.md §64): preparation, report, breakpoints.
        Placement = Track(new PlacementPanel(_bs));
        AddChild(Placement);
        // Part 6, stage 6.2 (docs/DECISIONS_6.md §97): the squad and its points — only while the deployment is on.
        Roster = Track(new RosterRibbon(_bs));
        AddChild(Roster);
        Report = Track(new ReportPanel(_bs));
        AddChild(Report);
        Breakpoints = Track(new BreakpointsPanel(_bs));
        AddChild(Breakpoints);
        // Part 6, stage 6.6 (§101): what the graphs of events did, breakpoints on their nodes.
        GraphDebug = Track(new GraphDebugPanel(_bs));
        AddChild(GraphDebug);
        _boxRect = new Panel { MouseFilter = MouseFilterEnum.Ignore, Visible = false, Name = "SelectionBox" };
        _boxRect.AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.5f, 1f, 0.7f, 0.12f), new Color(0.5f, 1f, 0.7f, 0.9f), 2, 2, 0));
        AddChild(_boxRect);
        AddChild(Radial);
        _skillList = new PopupMenu { Name = "SkillList" };
        _skillList.IdPressed += id =>
        {
            int i = (int)id;
            if (i >= 0 && i < _skillListItems.Count) _skillListPick?.Invoke(_skillListItems[i]);
        };
        AddChild(_skillList);
        OnSelectionChanged();
    }

    /// <summary>Screen area covered by the HUD (top bar, action bar, side panels) for camera fitting.</summary>
    public HexTactics.Core.Geometry.ScreenInsets WorkInsets()
    {
        var size = Size;
        if (size.X < 10 || size.Y < 10) return HexTactics.Core.Geometry.ScreenInsets.BattleHud(1920, 1080);
        float top = _topBar != null && _topBar.Visible ? _topBar.GetGlobalRect().End.Y : 0;
        float bottom = _actionPanel != null && _actionPanel.Visible ? size.Y - _actionPanel.GetGlobalRect().Position.Y : 0;
        float left = _unitPanel != null && _unitPanel.Visible ? _unitPanel.GetGlobalRect().End.X : 0;
        float right = _queuePanel != null && _queuePanel.Visible ? size.X - _queuePanel.GetGlobalRect().Position.X : 0;
        // Side panels only cover the upper part of the screen; never let them squeeze the board below half the width.
        if (left + right > size.X * 0.5f) left = right = size.X * 0.25f;
        var formula = HexTactics.Core.Geometry.ScreenInsets.BattleHud(size.X, size.Y);
        if (top <= 0 || top > size.Y * 0.25f) top = formula.Top;
        // The preparation panel lies under the top bar: the board fits below it while it is open.
        if (Placement != null && Placement.Visible) top = Math.Max(top, Math.Min(size.Y * 0.4f, Placement.GetGlobalRect().End.Y + 6));
        if (bottom <= 0 || bottom > size.Y * 0.4f) bottom = formula.Bottom;
        // The ribbon of the squad lies above the bar of actions: the board fits above it (folded — one line).
        if (Roster != null && Roster.Visible) bottom = Math.Max(bottom, Math.Min(size.Y * 0.4f, size.Y - Roster.GetGlobalRect().Position.Y + 6));
        return new HexTactics.Core.Geometry.ScreenInsets(Math.Max(0, left), Math.Max(0, top), Math.Max(0, right), Math.Max(0, bottom));
    }

    private PanelContainer? _topBar;
    /// <summary>The top bar (UI tests: on the screen at any scale).</summary>
    public Control? TopBar => _topBar;

    // ------------------------------------------------------------------ objectives and boss (TZ part 2 §4)

    private PanelContainer _objPanel = null!;
    private VBoxContainer _objList = null!;
    private string _objKey = "";
    private PanelContainer _bossPanel = null!;
    private Label _bossName = null!;
    private Bar _bossHp = null!;

    private void BuildObjectives()
    {
        var box = Ui.VBox(4);
        var head = Ui.HBox(4);
        head.AddChild(Ui.Label("Цели", 16, Ui.Text).Expand());
        _objList = Ui.VBox(3);
        head.AddChild(Ui.Button("–", () => _objList.Visible = !_objList.Visible, "Свернуть"));
        box.AddChild(head);
        box.AddChild(_objList);
        _objPanel = Track(Ui.PanelBox(box, null, 10));
        _objPanel.CustomMinimumSize = new Vector2(320, 0);
        _objPanel.Visible = false;
        AddChild(_objPanel);
    }

    private void BuildBossBar()
    {
        var box = Ui.VBox(2);
        _bossName = Ui.Label("", 15, new Color(1f, 0.6f, 0.5f));
        _bossName.HorizontalAlignment = HorizontalAlignment.Center;
        _bossHp = new Bar { FillColor = new Color(0.85f, 0.25f, 0.25f) }.MinSize(460, 16);
        box.AddChild(_bossName);
        box.AddChild(_bossHp);
        _bossPanel = Track(Ui.PanelBox(box, new Color(0.08f, 0.05f, 0.06f, 0.85f), 6));
        _bossPanel.Visible = false;
        AddChild(_bossPanel);
    }

    private static string ObjIcon(ObjectiveState s) => s switch
    {
        ObjectiveState.Completed => "✓",
        ObjectiveState.Failed => "✗",
        ObjectiveState.Locked => "🔒",
        _ => "•",
    };

    private void RefreshObjectives()
    {
        var obs = Sim.Scenario.Objectives;
        // Waves of the dispatcher (docs/DECISIONS_4.md §58): the current ones with progress and why they wait.
        var waves = Sim.Scenario.Waves.Where(w => w.State is WaveState.Scheduled or WaveState.Active).ToList();
        // Variables of the scenario with a caption (docs/DECISIONS_4.md §62): «Время суток: night».
        var vars = (Sim.Scenario.Def?.Environment ?? new()).Where(v => v.Caption != "").ToList();
        // Part 6 (§99): the variant of the mission and what it changes — one line, the list in the hint.
        var notes = Sim.Scenario.Def?.AppliedNotes ?? new List<string>();
        string variant = Sim.Mission.Variant;
        _objPanel.Visible = obs.Count > 0 || waves.Count > 0 || vars.Count > 0 || notes.Count > 0 || variant != "";
        if (!_objPanel.Visible) return;
        string WaitOf(WaveRuntime w) => w.Planned.FirstOrDefault(p => !p.Done && p.WaitReason != "")?.WaitReason ?? "";
        string key = string.Join("|", obs.Select(o => $"{o.State}:{o.Progress}")) + "#" + string.Join("|", waves.Select(w => $"{w.Index}:{w.Spawned}/{w.Total}:{WaitOf(w)}"))
                     + "#" + string.Join("|", vars.Select(v => Sim.Scenario.Variable(v.Name)));
        if (key == _objKey) return;
        _objKey = key;
        Ui.ClearChildren(_objList);
        if (variant != "" || notes.Count > 0)
        {
            string vname = Sim.Content.Get<DifficultyVariantDef>(variant)?.DisplayName ?? variant;
            var vl = Ui.Label($"⚑ {(vname != "" ? $"Вариант «{vname}»" : "Модификаторы миссии")}" + (notes.Count > 0 ? $": изменений {notes.Count}" : ""), 13, Ui.Warn, wrap: true);
            vl.Name = "MissionVariant";
            vl.MouseFilter = Control.MouseFilterEnum.Stop;
            vl.TooltipText = string.Join("\n", notes.Select(n => "• " + n));
            _objList.AddChild(vl);
        }
        var logic = Sim.Scenario.Def?.ObjectiveLogic ?? ObjectiveLogic.AllRequired;
        if (logic != ObjectiveLogic.AllRequired) _objList.AddChild(Ui.Label(Names.Of(logic), 12, Ui.TextDim));
        foreach (var o in obs)
        {
            if (o.State == ObjectiveState.Locked && o.Def.HiddenUntilActive) continue;
            var col = o.State switch
            {
                ObjectiveState.Completed => Ui.Good,
                ObjectiveState.Failed => Ui.Bad,
                ObjectiveState.Locked => Ui.TextDim,
                _ => Ui.Text,
            };
            string title = $"{ObjIcon(o.State)} {o.Def.Name}" + (o.Def.Required ? "" : " (дополнительная)");
            var l = Ui.Label(title, 14, col, wrap: true);
            l.TooltipText = string.IsNullOrEmpty(o.Def.Description) ? Names.Of(o.Def.Kind) : o.Def.Description;
            _objList.AddChild(l);
            string sub = o.State switch
            {
                ObjectiveState.Locked => "после: " + string.Join(", ", o.Def.DependsOn.Select(d => Sim.Scenario.Objective(d)?.Def.Name ?? d)),
                ObjectiveState.Failed => o.Reason,
                ObjectiveState.Completed => "",
                _ => o.Progress,
            };
            if (sub.Length > 0) _objList.AddChild(Ui.Label("   " + sub, 12, Ui.TextDim, wrap: true));
        }
        foreach (var v in vars)
        {
            var vl = Ui.Label($"◐ {v.Caption}: {Names.EnvValue(Sim.Scenario.Variable(v.Name))}", 13, Ui.Text);
            vl.Name = "EnvVar_" + v.Name;
            _objList.AddChild(vl);
        }
        foreach (var w in waves)
        {
            var wl = Ui.Label($"≋ Волна «{w.Def.Name}»: {w.Spawned}/{w.Total}", 13, Ui.Accent, wrap: true);
            wl.Name = "WaveProgress_" + w.Index;
            _objList.AddChild(wl);
            if (WaitOf(w) is { Length: > 0 } why) _objList.AddChild(Ui.Label("   ждёт: " + why, 12, Ui.Warn, wrap: true));
        }
    }

    private void RefreshBoss()
    {
        var boss = Sim.Units.FirstOrDefault(u => u.Alive && u.Boss.IsBoss && _bs.UnitVisible(u));
        _bossPanel.Visible = boss != null;
        if (boss == null) return;
        string phase = Sim.Scenario.PhaseOf(boss);
        var info = Sim.Knowledge.GetInfo(boss);
        _bossName.Text = (string.IsNullOrEmpty(boss.Boss.Title) ? "" : boss.Boss.Title + " · ") + info.Name + (phase.Length > 0 ? $" — {phase}" : "");
        if (info.HealthField.Known) _bossHp.Set(info.Hp, info.HpMax, $"{info.Hp:0}/{info.HpMax:0}");
        else _bossHp.Set(1, 1, "?");
        _bossPanel.ResetSize();
        _bossPanel.Position = new Vector2((Size.X - _bossPanel.Size.X) / 2, (_topBar?.GetGlobalRect().End.Y ?? 50) + 6);
    }

    private T Track<T>(T c) where T : Control
    {
        _panels.Add(c);
        return c;
    }

    public bool IsMouseOverUi() => IsOverUi(GetViewport().GetMousePosition());

    /// <summary>Is a screen point over a HUD panel or an open menu.</summary>
    public bool IsOverUi(Vector2 m)
    {
        foreach (var p in _panels)
            if (p.Visible && p.IsVisibleInTree() && p.GetGlobalRect().HasPoint(m))
                return true;
        return _popup.Visible || Radial.Visible || _skillList.Visible;
    }

    private GroupPanel _groupPanel = null!;
    private Panel _boxRect = null!;
    public GroupPanel GroupPanel => _groupPanel;
    public PlacementPanel Placement { get; private set; } = null!;
    public RosterRibbon Roster { get; private set; } = null!;
    public ReportPanel Report { get; private set; } = null!;
    public BreakpointsPanel Breakpoints { get; private set; } = null!;
    public GraphDebugPanel GraphDebug { get; private set; } = null!;
    public PopupMenu TestMenu { get; private set; } = null!;

    /// <summary>Items of the «Тест» menu (docs/DECISIONS_4.md §64).</summary>
    public const int TestReport = 1, TestLogicOnly = 2, TestNoEffects = 3, TestBreakpoints = 4, TestGraph = 5;

    private void OnTestMenu(long id)
    {
        switch (id)
        {
            case TestReport:
                Report.Open();
                break;
            case TestLogicOnly:
                _bs.RunLogicOnly();
                break;
            case TestNoEffects:
                _bs.NoEffects = !_bs.NoEffects;
                TestMenu.SetItemChecked(TestMenu.GetItemIndex(TestNoEffects), _bs.NoEffects);
                ShowBanner(_bs.NoEffects ? "Без визуальных эффектов: события только в журнале" : "Визуальные эффекты включены", Ui.Text, 2);
                break;
            case TestBreakpoints:
                Breakpoints.Visible = !Breakpoints.Visible;
                break;
            case TestGraph:
                if (GraphDebug.Visible) GraphDebug.Visible = false;
                else GraphDebug.Open();
                break;
        }
    }

    /// <summary>The radial skill menu (docs/DECISIONS_3.md §42).</summary>
    public RadialMenu Radial { get; } = new() { Name = "RadialMenu" };

    private PopupMenu _skillList = null!;
    private List<RadialItem> _skillListItems = new();
    private Action<RadialItem>? _skillListPick;

    /// <summary>The list form of the skill menu (the radial menu switched off in the settings).</summary>
    public void ShowSkillList(Vector2 at, string title, List<RadialItem> items, Action<RadialItem> onPick)
    {
        _skillList.Clear();
        _skillListItems = items;
        _skillListPick = onPick;
        _skillList.AddSeparator(title);
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            _skillList.AddItem($"{it.Icon} {it.Label}" + (it.Enabled ? (it.Approach ? " (подойдёт)" : "") : " — " + it.Reason), i);
            int idx = _skillList.GetItemIndex(i);
            _skillList.SetItemDisabled(idx, !it.Enabled);
            _skillList.SetItemTooltip(idx, it.Tooltip);
        }
        _skillList.Position = (Vector2I)at;
        _skillList.Popup();
    }

    // ------------------------------------------------------------------ build

    private void BuildTopBar()
    {
        var bar = Ui.HBox(6);
        _pauseBtn = Ui.Button("‖", () => _bs.TogglePause(), "Пауза (Пробел)", 44);
        bar.AddChild(_pauseBtn);
        for (int i = 0; i < _bs.Driver.Speeds.Count; i++)
        {
            int idx = i;
            var b = Ui.Button($"{_bs.Driver.Speeds[i]:0.##}×", () =>
            {
                _bs.Driver.SetSpeedIndex(idx);
                _bs.Resume();
            }, "Скорость (Z/X)", 52);
            b.ToggleMode = true;
            _speedBtns.Add(b);
            bar.AddChild(b);
        }
        _time = Ui.Label("00:00", 18, Ui.Text).MinSize(90);
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        bar.AddChild(_time);
        _status = Ui.Label("", 14, Ui.TextDim).MinSize(160);
        bar.AddChild(_status);
        // Part 6 (ТЗ 20.6): the summoned of the player — a compact counter, no panel per creature.
        SummonCounter = Ui.Label("", 14, Ui.Accent);
        SummonCounter.Name = "SummonCounter";
        SummonCounter.Visible = false;
        bar.AddChild(SummonCounter);
        bar.AddChild(Ui.Button("⤢", () => _bs.FitMap(), "Вписать карту в экран (V)", 38));
        bar.AddChild(Ui.Button("1:1", () => _bs.Board.Rig.ResetStandardZoom(), "Стандартный масштаб (Ctrl+0)", 44));
        bar.AddChild(Ui.Button("Журнал", ToggleLog, "Показать/скрыть журнал (L)"));
        var test = new MenuButton { Text = "Тест ▾", Flat = false, Name = "TestMenu", TooltipText = "Отчёт прогона, только логика, без эффектов, остановка на событии" };
        TestMenu = test.GetPopup();
        TestMenu.AddItem("Отчёт прогона", TestReport);
        TestMenu.AddItem("Только логика: до конца боя", TestLogicOnly);
        TestMenu.AddCheckItem("Без визуальных эффектов", TestNoEffects);
        TestMenu.AddItem("Остановка на событии…", TestBreakpoints);
        TestMenu.AddItem("Граф событий…", TestGraph);
        if (_bs.Sim.Scenario.Def?.Graphs.Count is null or 0)
        {
            TestMenu.SetItemDisabled(TestMenu.GetItemIndex(TestGraph), true);
            TestMenu.SetItemTooltip(TestMenu.GetItemIndex(TestGraph), "В сценарии нет графов событий");
        }
        TestMenu.IdPressed += OnTestMenu;
        bar.AddChild(test);
        bar.AddChild(Ui.Button("?", ToggleHelp, "Справка (F1)", 34));
        bar.AddChild(Ui.Button("Меню", ToggleMenu, "Меню (Esc)"));
        var panel = Track(Ui.PanelBox(bar, null, 6));
        _topBar = panel;
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop, LayoutPresetMode.Minsize);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.OffsetTop = 8;
    }

    private void BuildUnitPanel()
    {
        _unitBox = Ui.VBox(5);
        _unitName = Ui.Label("", 19, Ui.Text);
        _unitSub = Ui.Label("", 13, Ui.TextDim);
        _hp = new Bar { FillColor = Ui.HpColor }.MinSize(300, 18);
        _ap = new Bar { FillColor = Ui.ApColor }.MinSize(300, 18);
        _apInfo = Ui.Label("", 12, Ui.TextDim, wrap: true);
        _resources = Ui.VBox(3);
        _statuses = Ui.VBox(3);
        _current = Ui.Label("", 13, Ui.Accent, wrap: true);
        _aiInfo = Ui.Label("", 12, new Color(1f, 0.75f, 0.85f), wrap: true);
        _statsBox = Ui.VBox(1);
        var head = Ui.HBox(4);
        head.AddChild(_unitName.Expand());
        head.AddChild(Ui.Button("–", () => _unitBox.Visible = !_unitBox.Visible, "Свернуть панель"));
        var outer = Ui.VBox(4);
        _stackStrip = new HFlowContainer { Visible = false };
        _stackStrip.AddThemeConstantOverride("h_separation", 4);
        outer.AddChild(_stackStrip);
        outer.AddChild(head);
        outer.AddChild(_unitBox);
        _unitBox.AddChild(_unitSub);
        _unitBox.AddChild(_hp);
        _unitBox.AddChild(_ap);
        _unitBox.AddChild(_apInfo);
        _unitBox.AddChild(_resources);
        _unitBox.AddChild(_statuses);
        _unitBox.AddChild(_current);
        _blockInfo = Ui.Label("", 13, new Color(1f, 0.7f, 0.4f), wrap: true);
        _unitBox.AddChild(_blockInfo);
        // Part 6 (§100): the objects within reach — a button per interaction (disabled with the reason).
        _interactRow = new HFlowContainer { Name = "InteractRow", Visible = false };
        _interactRow.AddThemeConstantOverride("h_separation", 4);
        _unitBox.AddChild(_interactRow);
        var autoRow = Ui.HBox(6);
        _autoInfo = Ui.Label("", 12, new Color(0.75f, 0.9f, 1f), wrap: true).Expand();
        _autoToggle = Ui.Button("Автоатака: вкл", ToggleAutoAttack, "Включить/выключить автоатаки выбранного юнита");
        autoRow.AddChild(_autoInfo);
        autoRow.AddChild(_autoToggle);
        _unitBox.AddChild(autoRow);
        _unitBox.AddChild(_aiInfo);
        _intel = Ui.VBox(2);
        _unitBox.AddChild(_intel);
        _statsBtn = Ui.Button("Характеристики (I)", ToggleInfo);
        _unitBox.AddChild(_statsBtn);
        _unitBox.AddChild(_statsBox);
        _unitPanel = Track(Ui.PanelBox(outer, null, 10));
        _unitPanel.Position = new Vector2(10, 64);
        _unitPanel.CustomMinimumSize = new Vector2(330, 0);
        AddChild(_unitPanel);
    }

    private void BuildActionBar()
    {
        var v = Ui.VBox(4);
        _modeHint = Ui.Label("", 13, Ui.Accent);
        _modeHint.HorizontalAlignment = HorizontalAlignment.Center;
        _actions = new HFlowContainer();
        _actions.AddThemeConstantOverride("h_separation", 5);
        _actions.AddThemeConstantOverride("v_separation", 4);
        v.AddChild(_modeHint);
        v.AddChild(_actions);
        _actionPanel = Track(Ui.PanelBox(v, null, 8));
        AddChild(_actionPanel);
    }

    private void BuildQueuePanel()
    {
        var v = Ui.VBox(4);
        var head = Ui.HBox(4);
        head.AddChild(Ui.Label("Очередь команд", 16, Ui.Text).Expand());
        _appendToggle = new CheckButton { Text = "добавлять", ButtonPressed = Main.Instance.Settings.AppendByDefault, FocusMode = FocusModeEnum.None, TooltipText = "Вкл — клик добавляет команду в конец очереди, выкл — заменяет очередь. Shift инвертирует." };
        _appendToggle.Toggled += on =>
        {
            Main.Instance.Settings.AppendByDefault = on;
            Main.Instance.Settings.Save();
        };
        head.AddChild(_appendToggle);
        var body = Ui.VBox(3);
        head.AddChild(Ui.Button("–", () => body.Visible = !body.Visible, "Свернуть"));
        v.AddChild(head);
        _queueList = Ui.VBox(3);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(320, 30), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _queueScroll = scroll;
        scroll.AddChild(_queueList.Expand());
        body.AddChild(scroll);
        _queueFooter = Ui.Label("", 12, Ui.TextDim, wrap: true);
        body.AddChild(_queueFooter);
        var btns = Ui.HBox(4);
        btns.AddChild(Ui.Button("↶ Точка", _bs.UndoLastPoint, "Удалить последний невыполняемый элемент маршрута (Backspace)"));
        btns.AddChild(Ui.Button("Очистить", _bs.ClearPendingOrders, "Удалить приказы, которые ещё не выполняются; выполняемый остаётся (Delete)"));
        btns.AddChild(Ui.Button("Отменить всё", _bs.CancelAll, "Отменить и выполняемый приказ (Shift+Delete)"));
        body.AddChild(btns);
        // Routing mode of new routes (docs/DECISIONS_3.md §41).
        var route = Ui.HBox(4);
        route.AddChild(Ui.Label("Маршрут:", 12, Ui.TextDim));
        _routeMode = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Как строится маршрут новых приказов движения" };
        foreach (var m in Enum.GetValues<RouteMode>()) _routeMode.AddItem(Names.Of(m), (int)m);
        _routeMode.Select((int)Main.Instance.Settings.RouteMode);
        _routeMode.ItemSelected += i =>
        {
            Main.Instance.Settings.RouteMode = (RouteMode)_routeMode.GetItemId((int)i);
            Main.Instance.Settings.Save();
        };
        route.AddChild(_routeMode.Expand());
        body.AddChild(route);
        v.AddChild(body);
        _queuePanel = Track(Ui.PanelBox(v, null, 8));
        _queuePanel.CustomMinimumSize = new Vector2(340, 0);
        AddChild(_queuePanel);
    }

    private void BuildLog()
    {
        var v = Ui.VBox(4);
        var head = Ui.HBox(4);
        head.AddChild(Ui.Label("Журнал боя", 15, Ui.Text).Expand());
        _logFilter = new OptionButton { FocusMode = FocusModeEnum.None };
        _logFilter.AddItem("Основной");
        _logFilter.AddItem("Подробный");
        if (_bs.Admin) _logFilter.AddItem("Диагностика");
        _logFilter.Selected = _bs.Admin ? 2 : Main.Instance.Settings.DetailedLog ? 1 : 0;
        _logFilter.ItemSelected += _ => RebuildLog();
        head.AddChild(_logFilter);
        head.AddChild(Ui.Button("–", ToggleLog, "Скрыть (L)"));
        v.AddChild(head);
        _log = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            CustomMinimumSize = new Vector2(470, 190),
            SelectionEnabled = true,
            FitContent = false,
        };
        _log.AddThemeFontSizeOverride("normal_font_size", 13);
        v.AddChild(_log);
        _logPanel = Track(Ui.PanelBox(v, new Color(0.11f, 0.12f, 0.14f, 0.92f), 8));
        AddChild(_logPanel);
    }

    private void BuildTooltip()
    {
        _tooltipText = new RichTextLabel { BbcodeEnabled = true, FitContent = true, CustomMinimumSize = new Vector2(300, 0), ScrollActive = false, MouseFilter = MouseFilterEnum.Ignore };
        _tooltipText.AddThemeFontSizeOverride("normal_font_size", 13);
        _tooltip = Ui.PanelBox(_tooltipText, new Color(0.07f, 0.08f, 0.09f, 0.94f), 8);
        _tooltip.MouseFilter = MouseFilterEnum.Ignore;
        _tooltip.Visible = false;
        AddChild(_tooltip);
    }

    private void BuildBanner()
    {
        _banner = Ui.Label("", 17, Ui.Text, wrap: true);
        _banner.HorizontalAlignment = HorizontalAlignment.Center;
        _banner.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _banner.AddThemeConstantOverride("outline_size", 6);
        _banner.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_banner);
    }

    private void BuildOutcome()
    {
        var v = Ui.VBox(10);
        _outcomeText = Ui.Label("", 30, Ui.Accent);
        _outcomeText.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(_outcomeText);
        _outcomeCampaign = Ui.Label("", 15, Ui.Text, wrap: true);
        _outcomeCampaign.Name = "OutcomeCampaign";
        _outcomeCampaign.CustomMinimumSize = new Vector2(560, 0);
        _outcomeCampaign.HorizontalAlignment = HorizontalAlignment.Center;
        _outcomeCampaign.Visible = false;
        v.AddChild(_outcomeCampaign);
        var h = Ui.HBox(8);
        h.AddChild(Ui.Button("Заново", () => _bs.Restart()).MinSize(140, 40));
        h.AddChild(Ui.Button("Выйти", () => _bs.ExitBattle()).MinSize(140, 40));
        h.AddChild(Ui.Button("Осмотреться", () => _outcome!.Visible = false).MinSize(140, 40));
        var rep = Ui.Button("Отчёт", () =>
        {
            _outcome!.Visible = false;
            Report.Open();
        }).MinSize(140, 40);
        rep.Name = "OutcomeReport";
        h.AddChild(rep);
        v.AddChild(h);
        _outcome = Track(Ui.PanelBox(v, Ui.PanelLight, 24));
        _outcome.Visible = false;
        AddChild(_outcome);
    }

    /// <summary>The help with the keys as they are bound now (part 6, §107).</summary>
    private static string HelpText()
    {
        static string K(string id) => Hotkeys.Label(id) is { Length: > 0 } k ? k : "—";
        return string.Join("\n", new[]
        {
            $"{K("pause")} — пауза/продолжить · {K("slower")} / {K("faster")} — скорость · {K("step")} — один тик (на паузе)",
            $"ЛКМ по своему юниту — выбрать (стрелка взгляда, панель команд) · Shift+ЛКМ, рамка — несколько · {K("cycle")} — следующий",
            "Стрелка взгляда: короткий жест — поворот, длинный — маршрут по гексам; в конце маршрута — направление после движения",
            $"Жест или ЛКМ на враге — меню навыков против него (защитный навык в центре) · {K("skill_menu")} — меню навыков у курсора",
            $"{K("move")} — движение (несколько ЛКМ = маршрут) · {K("turn")} — поворот · {K("attack")} — атака · {K("skill1")}…{K("skill9")} — навыки (автоматический — сразу)",
            "Ctrl+1…9 — сохранить группу · Alt+1…9 — выбрать группу; приказ выделенным — групповой",
            $"{K("defend")} — защитная стойка · {K("wait")} — ожидание 2 с · {K("undo_point")} — убрать последнюю точку · {K("clear_orders")} — ожидающие приказы · Shift+{K("clear_orders")} — всё",
            "ЛКМ по номеру точки маршрута — действие после этой точки (навык, поворот, стойка…)",
            "ПКМ — быстрый приказ; во время жеста — отмена · Esc — шаг назад: жест → режим → выделение → меню",
            "Shift при клике — заменить очередь вместо добавления (или наоборот, см. переключатель)",
            "Камера: WASD/стрелки, средняя кнопка мыши, край экрана · колесо/PgUp/PgDn — зум · Q/E — поворот",
            $"{K("fit")} — вписать карту в экран · {K("reset_zoom")} — стандартный масштаб",
            "ЛКМ по врагу — сведения о нём (только то, что видели или наблюдали)",
            "Несколько существ на гексе (×N): клик — полоска выбора, повторный клик или Tab — следующий, Esc — закрыть",
            "Оранжевая цепь — блок (у выбранного и наведённого; все — настройка или F3): «Блок 2/3» у блокирующего, «ещё N проходят» — переполнено, «заблокирован» / «обходит блок» у врага",
            "Стрелка у основания — взгляд; несколько стрелок — несколько направлений; кольцо — круговой обзор; нет стрелки — без направления",
            $"{K("info")} — характеристики · {K("log")} — журнал · {K("diag")} — диагностика · {K("focus")} — к выбранному юниту · {K("help")} — справка",
            $"Лента отряда (вызов за очки): карточка → гекс зоны → направление (курсор или 1–6) → ЛКМ/Enter; Esc/ПКМ — шаг назад · {K("retreat")} — отозвать",
            "",
            "Обозначения: синяя линия — запланированный маршрут; жёлтая — предпросмотр пути;",
            "серые точки — пройденный путь; заливка цвета навыка — область; штриховка + «!» — угроза врага;",
            "серая штриховка — клетка закрыта препятствием; пунктир «?» — последнее известное место врага.",
            "Основание фишки: круг — отряд, ромб — враги, квадрат — нейтралы. Стрелка — направление взгляда.",
        });
    }

    private void BuildHelp()
    {
        var text = HelpText();
        _helpText = Ui.Label(text, 14, Ui.Text);
        _helpText.Name = "HelpText";
        var v = Ui.VBox(8, Ui.Label("Управление и обозначения", 20, Ui.Accent), _helpText, Ui.Button("Закрыть", ToggleHelp));
        _help = Track(Ui.PanelBox(v, Ui.PanelLight, 18));
        _help.Visible = false;
        AddChild(_help);
    }

    private void BuildMenu()
    {
        var v = Ui.VBox(8, Ui.Label("Меню", 20, Ui.Accent));
        v.AddChild(Ui.Button("Продолжить", ToggleMenu).MinSize(240, 40));
        v.AddChild(Ui.Button("Начать заново", () => _bs.Restart()).MinSize(240, 40));
        var save = Ui.Button("Сохранить реплей", () =>
        {
            try
            {
                ShowBanner("Реплей сохранён: " + _bs.SaveReplay(), Ui.Good, 5f);
            }
            catch (System.Exception e)
            {
                ShowBanner("Реплей не сохранён: " + e.Message, Ui.Bad, 5f);
            }
        }).MinSize(240, 40);
        save.Name = "SaveReplay";
        save.TooltipText = "Seed, контент и действия игрока по тикам — бой воспроизводится в редакторе («Проверить реплей»)";
        v.AddChild(save);
        // Part 6 (T28): the battle saved in the middle — loading plays it back to this tick and goes on paused.
        var saveBattle = Ui.Button("Сохранить бой", () =>
        {
            try
            {
                ShowBanner("Бой сохранён: " + System.IO.Path.GetFileName(_bs.SaveBattle()), Ui.Good, 4f);
            }
            catch (System.Exception e)
            {
                ShowBanner("Бой не сохранён: " + e.Message, Ui.Bad, 5f);
            }
        }, "Очки развёртывания, перерывы, фазы босса и сработавшие триггеры восстановятся при загрузке").MinSize(240, 40);
        saveBattle.Name = "SaveBattle";
        v.AddChild(saveBattle);
        var loadBattle = Ui.Button("Загрузить бой", () => SavedBattlesDialog.Open(this, _bs.Admin, _bs.OnExit), "Список сохранённых боёв").MinSize(240, 40);
        loadBattle.Name = "LoadBattle";
        v.AddChild(loadBattle);
        v.AddChild(Ui.Button("Настройки", () =>
        {
            var dlg = new SettingsDialog();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(560, 620));
            dlg.TreeExited += () => _bs.Board.ApplySettings(Main.Instance.Settings);
        }).MinSize(240, 40));
        v.AddChild(Ui.Button(_bs.Admin ? "Вернуться в редактор" : "Выйти в главное меню", () => _bs.ExitBattle()).MinSize(240, 40));
        _menu = Track(Ui.PanelBox(v, Ui.PanelLight, 18));
        _menu.Visible = false;
        AddChild(_menu);
    }

    private void BuildDiag()
    {
        _diagText = Ui.Label("", 12, new Color(0.8f, 0.9f, 1f));
        _diagText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _diagText.CustomMinimumSize = new Vector2(520, 0);
        _diagPanel = Track(Ui.PanelBox(_diagText, new Color(0.05f, 0.06f, 0.08f, 0.85f), 8));
        _diagPanel.Visible = false;
        AddChild(_diagPanel);
    }

    private void BuildPopup()
    {
        _popup = new PopupMenu();
        _skillSub = new PopupMenu { Name = "skills" };
        _popup.AddChild(_skillSub);
        _popup.IdPressed += id => OnPopup((int)id);
        _skillSub.IdPressed += id => OnPopup((int)id);
        AddChild(_popup);
    }

    // ------------------------------------------------------------------ public API

    /// <summary>Closes an open popup (action/skill menus); true if something was closed (Esc chain).</summary>
    public bool CloseTransient()
    {
        if (_popup.Visible)
        {
            _popup.Hide();
            return true;
        }
        if (Radial.Visible)
        {
            Radial.Close();
            return true;
        }
        if (_skillList.Visible)
        {
            _skillList.Hide();
            return true;
        }
        return false;
    }

    /// <summary>The last banner text (tests and diagnostics).</summary>
    public string LastBanner { get; private set; } = "";

    public void ShowBanner(string text, Color color, float seconds = 4f)
    {
        LastBanner = text;
        _banner.Text = text;
        _banner.AddThemeColorOverride("font_color", color);
        _bannerTime = seconds;
    }

    public void ToggleLog() => _logPanel.Visible = !_logPanel.Visible;
    private Label _helpText = null!;

    public void ToggleHelp()
    {
        _help.Visible = !_help.Visible;
        if (_help.Visible) _helpText.Text = HelpText(); // the keys may have been reassigned since
    }
    public void ToggleMenu() => _menu.Visible = !_menu.Visible;

    public void ToggleInfo()
    {
        _showStats = !_showStats;
        _unitBox.Visible = true;
        RefreshStats();
    }

    public void FocusQueue() => _queuePanel.Visible = true;

    public void OnSelectionChanged()
    {
        _actionsKey = "";
        _queueKey = "";
        RefreshStats();
    }

    public void OnModeChanged() => _actionsKey = "";
    public void OnQueueChanged() => _queueKey = "";

    public void ShowCellInfo(Cell c) => ShowBanner(CellText(c, false).Replace("[b]", "").Replace("[/b]", "").Split('\n')[0], Ui.TextDim, 2.5f);

    public void ShowActionMenu(Vector2 pos, int insertIndex)
    {
        var u = _bs.Selected;
        if (u == null) return;
        _popup.Clear();
        _skillSub.Clear();
        _popupActions = Sim.Planner.Actions(u);
        _popupInsert = insertIndex;
        _waypointIndex = -1;
        int skillN = 0;
        for (int i = 0; i < _popupActions.Count; i++)
        {
            var a = _popupActions[i];
            if (a.Key == "queue" && insertIndex >= 0) continue;
            string hot = a.Key is "move" or "turn" or "attack" or "defend" or "wait" or "info" && Hotkeys.Label(a.Key) is { Length: > 0 } hk ? $" ({hk})" : "";
            if (a.Key.StartsWith("skill:"))
            {
                skillN++;
                string cost = a.Skill != null ? $" — {a.Skill.ApCost:0.#} ОД" : "";
                _skillSub.AddItem($"{skillN}. {a.Label}{cost}{(a.Enabled ? "" : " — " + a.Reason)}", i);
                int idx = _skillSub.ItemCount - 1;
                _skillSub.SetItemDisabled(idx, !a.Enabled);
                _skillSub.SetItemTooltip(idx, a.Enabled ? (a.Skill?.Description ?? "") + (a.Warning != "" ? "\n" + a.Warning : "") : a.Reason);
                continue;
            }
            string text = a.Label + hot + (a.Enabled ? "" : " — " + a.Reason);
            if (a.Key == "attack" && a.Skill != null && a.Enabled) text = $"{a.Label} — {a.Skill.ApCost:0.#} ОД{hot}";
            _popup.AddItem(text, i);
            int pi = _popup.ItemCount - 1;
            _popup.SetItemDisabled(pi, !a.Enabled);
            _popup.SetItemTooltip(pi, a.Enabled ? a.Warning : a.Reason);
            if (a.Key == "attack") _popup.AddSubmenuNodeItem("Навыки", _skillSub, 900);
        }
        if (insertIndex >= 0)
        {
            _popup.AddSeparator();
            _popup.AddItem("Удалить эту команду", 1000);
        }
        _popup.Position = new Vector2I((int)pos.X + 12, (int)pos.Y + 8);
        _popup.ResetSize();
        _popup.Popup();
    }

    public void ShowWaypointMenu(Vector2 pos, int commandIndex)
    {
        ShowActionMenu(pos, commandIndex + 1);
        _waypointIndex = commandIndex;
        _popup.Title = $"После точки {commandIndex + 1}";
    }

    private void OnPopup(int id)
    {
        var u = _bs.Selected;
        if (u == null) return;
        if (id == 1000)
        {
            if (_waypointIndex >= 0 && _waypointIndex < u.Queue.Count) Sim.Input.RemoveCommand(u, u.Queue.Items[_waypointIndex].Id);
            OnQueueChanged();
            return;
        }
        if (id < 0 || id >= _popupActions.Count) return;
        _bs.Activate(_popupActions[id], _popupInsert);
    }

    // ------------------------------------------------------------------ log

    private static string LogColor(SimEvent e) => e.Type switch
    {
        SimEventType.Damage => "#ff8f7a",
        SimEventType.Heal => "#8ee68e",
        SimEventType.Death => "#ff5555",
        SimEventType.Miss => "#a0a4aa",
        SimEventType.Block => "#9cc8ff",
        SimEventType.Crit => "#ffd060",
        SimEventType.Surprise => "#ffa060",
        SimEventType.AttackNoticed or SimEventType.AttackUnnoticed => "#b8c0c8",
        SimEventType.StatusApplied or SimEventType.StatusLevel => "#d8b8ff",
        SimEventType.StatusRemoved or SimEventType.StatusResisted => "#a898c0",
        SimEventType.Synergy => "#ffd27a",
        SimEventType.CellEffectAdded or SimEventType.TerrainChanged or SimEventType.ObstacleDestroyed => "#c8b48a",
        SimEventType.CastInterrupted => "#ffb050",
        SimEventType.CommandFailed or SimEventType.CommandSkipped or SimEventType.QueueCancelled => "#ffb0a0",
        SimEventType.UnitSpotted => "#ffe080",
        SimEventType.WaveStarted or SimEventType.Message => "#ffe9a8",
        SimEventType.Victory => "#90ff90",
        SimEventType.Defeat => "#ff7070",
        SimEventType.ApSpent or SimEventType.ApRefund or SimEventType.ApFull => "#8fb8ff",
        SimEventType.AiDecision or SimEventType.Diagnostic => "#d0a0d0",
        _ => "#d8dadc",
    };

    private int LogMaxLevel => _logFilter?.Selected ?? 0;

    private readonly List<(SimEvent E, bool Visible)> _logBuffer = new();

    public void AppendLog(SimEvent e, bool visible)
    {
        _logBuffer.Add((e, visible));
        if (_logBuffer.Count > 1500) _logBuffer.RemoveRange(0, 300);
        if (e.Type == SimEventType.Message || e.Type == SimEventType.WaveStarted)
        {
            if (visible) ShowBanner(e.Text, Ui.Accent, 6f);
        }
        if (e.Type == SimEventType.CommandFailed && visible) ShowBanner(e.Text, Ui.Bad, 5f);
        if (_log != null && Passes(e, visible)) AddLogLine(e);
    }

    private bool Passes(SimEvent e, bool visible)
    {
        if (!visible) return false;
        int lvl = e.Level switch { LogLevel.Normal => 0, LogLevel.Detail => 1, _ => 2 };
        return lvl <= LogMaxLevel;
    }

    private void AddLogLine(SimEvent e)
    {
        if (_logLines > 400)
        {
            RebuildLog();
            return;
        }
        string esc = e.Text.Replace("[", "[lb]");
        _log.AppendText($"[color=#7d8590]{Ui.FormatTime(e.Time)}[/color] [color={LogColor(e)}]{esc}[/color]\n");
        _logLines++;
    }

    private void RebuildLog()
    {
        _log.Clear();
        _logLines = 0;
        foreach (var (e, v) in _logBuffer.Where(x => Passes(x.E, x.Visible)).TakeLast(250)) AddLogLine(e);
    }

    // ------------------------------------------------------------------ per-frame refresh

    public void Refresh()
    {
        float dt = (float)GetProcessDeltaTime();
        var vp = Size;
        // Layout of floating panels: log bottom-left, actions bottom (to the right of the log), queue right.
        _logPanel.Position = new Vector2(10, vp.Y - _logPanel.Size.Y - 10);
        float left = _logPanel.Visible ? _logPanel.Size.X + 20 : 10;
        float avail = vp.X - left - 10;
        _actions.CustomMinimumSize = new Vector2(Math.Min(avail - 20, 1010), 0);
        _actionPanel.Size = new Vector2(Math.Min(avail, 1030), 0);
        _actionPanel.Position = new Vector2(left + Math.Max(0, (avail - _actionPanel.Size.X) / 2), vp.Y - _actionPanel.Size.Y - 10);
        _queuePanel.Position = new Vector2(vp.X - _queuePanel.Size.X - 10, 64);
        _groupPanel.Visible = _bs.GroupActive;
        if (_groupPanel.Visible)
        {
            _groupPanel.Size = new Vector2(Math.Min(avail, 1030), 0);
            _groupPanel.Position = new Vector2(left + Math.Max(0, (avail - _groupPanel.Size.X) / 2), vp.Y - _groupPanel.Size.Y - 10);
            _groupPanel.Refresh();
        }
        var box = _bs.BoxRect;
        _boxRect.Visible = box.HasValue;
        if (box is { } br)
        {
            _boxRect.Position = br.Position;
            _boxRect.Size = br.Size;
        }
        _objPanel.Position = new Vector2(vp.X - _objPanel.Size.X - 10, _queuePanel.Visible ? _queuePanel.GetRect().End.Y + 8 : 64);
        _banner.Position = new Vector2(vp.X * 0.2f, 64);
        _banner.Size = new Vector2(vp.X * 0.6f, 40);
        _outcome.Position = (vp - _outcome.Size) / 2;
        Placement.Refresh();
        Placement.Position = new Vector2((vp.X - Placement.Size.X) / 2, 112);
        Roster.Refresh();
        if (Roster.Visible)
        {
            // Above the bar of actions (or the group's), centred in the same free width; it folds to one line.
            float above = _groupPanel.Visible ? _groupPanel.Position.Y : _actionPanel.Visible ? _actionPanel.Position.Y : vp.Y - 4;
            Roster.Size = new Vector2(Math.Min(avail, 900), 0);
            Roster.Position = new Vector2(left + Math.Max(0, (avail - Roster.Size.X) / 2), above - Roster.Size.Y - 6);
        }
        Report.Position = (vp - Report.Size) / 2;
        Breakpoints.Position = new Vector2(vp.X - Breakpoints.Size.X - 10, 64);
        if (GraphDebug.Visible) GraphDebug.Fit(vp);
        GraphDebug.Position = new Vector2(Math.Max(0, (vp.X - GraphDebug.Size.X) / 2), 64);
        _help.Position = (vp - _help.Size) / 2;
        _menu.Position = (vp - _menu.Size) / 2;
        _diagPanel.Position = new Vector2(vp.X - _diagPanel.Size.X - 10, vp.Y - _diagPanel.Size.Y - _actionPanel.Size.Y - 24);

        // Top bar.
        var d = _bs.Driver;
        _pauseBtn.Text = d.Paused ? "▶" : "‖";
        _pauseBtn.TooltipText = d.Paused ? "Продолжить (Пробел)" : "Пауза (Пробел)";
        for (int i = 0; i < _speedBtns.Count; i++) _speedBtns[i].ButtonPressed = !d.Paused && i == d.SpeedIndex;
        // A narrow screen or a big scale of the interface (ТЗ ч. 6, 11.7): the speeds shrink to 1× and the current one (Z/X change
        // them), so the pause, the menu and the rest of the bar never leave the screen.
        if (_topBar != null)
        {
            float room = vp.X - 16;
            bool compact = _speedBtns.Any(b => !b.Visible) ? _topBar.Size.X + _speedBtns.Where(b => !b.Visible).Sum(b => b.Size.X + 6) > room : _topBar.Size.X > room;
            int one = _bs.Driver.Speeds.IndexOf(1);
            for (int i = 0; i < _speedBtns.Count; i++) _speedBtns[i].Visible = !compact || i == d.SpeedIndex || i == one;
            if (compact) _topBar.ResetSize();
            _topBar.Position = new Vector2(Math.Max(8, (vp.X - _topBar.Size.X) / 2), _topBar.Position.Y);
        }
        _time.Text = Ui.FormatTime(Sim.Time);
        _status.Text = d.Paused ? "ПАУЗА" : $"{d.Speed:0.##}×";
        _status.AddThemeColorOverride("font_color", d.Paused ? Ui.Warn : Ui.TextDim);
        UpdateSummonCounter();

        if (_bannerTime > 0)
        {
            _bannerTime -= dt;
            _banner.Modulate = new Color(1, 1, 1, Math.Clamp(_bannerTime, 0, 1));
        }
        else
        {
            _banner.Text = "";
        }

        if (Sim.Outcome != BattleOutcome.None && !_outcome.Visible && _lastMessage != Sim.OutcomeText)
        {
            _lastMessage = Sim.OutcomeText;
            _outcomeText.Text = (Sim.Outcome == BattleOutcome.Victory ? "ПОБЕДА" : "ПОРАЖЕНИЕ") + "\n" + Sim.OutcomeText;
            // A mission of a campaign (part 6, §103): the result goes into the progress once.
            _outcomeCampaign.Text = _bs.RecordCampaign();
            _outcomeCampaign.Visible = _outcomeCampaign.Text != "";
            _outcomeText.AddThemeColorOverride("font_color", Sim.Outcome == BattleOutcome.Victory ? Ui.Good : Ui.Bad);
            _outcome.Visible = true;
        }

        _refreshTimer -= dt;
        bool slow = _refreshTimer <= 0;
        if (slow) _refreshTimer = 0.2f;

        RefreshUnit(slow);
        if (slow)
        {
            RefreshObjectives();
            RefreshBoss();
        }
        RefreshActions(slow);
        RefreshQueue();
        RefreshTooltip();
        RefreshDiag();
    }

    private static string ShapeGlyph(FactionShape s) => s switch { FactionShape.Diamond => "◆", FactionShape.Square => "■", FactionShape.Triangle => "▲", _ => "●" };

    /// <summary>Several creatures on the selected hex: a strip of buttons to pick one (Tab cycles while it is open).</summary>
    private void RefreshStackStrip()
    {
        var stack = _bs.StackOf(_bs.Selected);
        bool show = stack.Count > 1;
        string key = show ? string.Join(",", stack.Select(x => x.Id)) + ":" + _bs.Selected?.Id : "";
        if (key == _stackKey) return;
        _stackKey = key;
        Ui.ClearChildren(_stackStrip);
        _stackStrip.Visible = show;
        if (!show) return;
        _stackStrip.AddChild(Ui.Label($"На гексе {stack.Count}:", 12, Ui.TextDim));
        foreach (var x in stack)
        {
            var unit = x;
            var faction = _bs.Db.Faction(x.FactionId);
            string name = Sim.Knowledge.GetInfo(x).Name;
            var b = Ui.Button($"{ShapeGlyph(faction?.Shape ?? FactionShape.Circle)} {name}", () => _bs.SelectKeepStack(unit), "Выбрать (Tab — следующий на этом гексе)");
            b.ToggleMode = true;
            b.ButtonPressed = x == _bs.Selected;
            b.AddThemeColorOverride("font_color", Ui.Parse(faction?.Color, Ui.Text).Lightened(0.25f));
            _stackStrip.AddChild(b);
        }
    }

    private void ToggleAutoAttack()
    {
        var u = _bs.Selected;
        if (u == null) return;
        var off = Sim.AutoAttacks.DisabledUnits;
        if (!off.Remove(u.Id)) off.Add(u.Id);
        ShowBanner(off.Contains(u.Id) ? $"{u.Name}: автоатаки выключены" : $"{u.Name}: автоатаки включены", Ui.TextDim, 1.5f);
    }

    /// <summary>"Автоатака «Выстрел»: атакует Гоблин" / "нет целей в зоне" / "нет свободных ОД".</summary>
    private void RefreshAutoAttack(Unit u)
    {
        bool has = u.Def.AutoAttacks.Count > 0 && u.Alive;
        _autoInfo.GetParent<Control>().Visible = has;
        if (!has) return;
        bool off = Sim.AutoAttacks.DisabledUnits.Contains(u.Id) || !Sim.Rules.AutoAttacks.Enabled;
        var states = Sim.AutoAttacks.StatesOf(u);
        var parts = new List<string>();
        for (int i = 0; i < u.Def.AutoAttacks.Count; i++)
        {
            var name = Sim.AutoAttacks.SkillOf(u, i)?.DisplayName ?? "—";
            var st = i < states.Count ? states[i].Status : "";
            parts.Add($"«{name}»" + (st.Length > 0 ? $": {st}" : ""));
        }
        _autoInfo.Text = "Автоатака " + (off ? "(выкл.) " : "") + string.Join("; ", parts);
        _autoToggle.Text = off ? "Автоатака: выкл" : "Автоатака: вкл";
        _autoToggle.Visible = Sim.CanPlayerCommand(u);
    }

    private string BlockLine(Unit u)
    {
        var bl = Sim.Blocking;
        int limit = bl.Limit(u);
        if (limit > 0)
        {
            var held = bl.BlockedBy(u).Where(_bs.UnitVisible).Select(x => Sim.Knowledge.GetInfo(x).Name).ToList();
            int over = bl.Overflow(u);
            return $"Блок {bl.Load(u)}/{limit}" + (held.Count > 0 ? " — держит: " + string.Join(", ", held) : " — свободен")
                   + (over > 0 ? $"; переполнено: ещё {over} проходят" : "")
                   + (u.Def.Blocking.MaxDuration > 0 ? $"; держит не дольше {u.Def.Blocking.MaxDuration:0.#} с" : "");
        }
        switch (bl.HoldOf(u))
        {
            case MovementHold.Blocked:
                var by = bl.BlockerOf(u);
                return $"Заблокирован: {(by != null ? Sim.Knowledge.GetInfo(by).Name : "?")} — не может продолжить движение"
                       + (u.Def.Blockable.WhenBlocked == BlockedBehavior.BreakThrough ? " (прорывается)" : "");
            case MovementHold.Held:
                return u.Flags.BlocksMovement ? "Удерживается: обездвижен состоянием" : "Удерживается: замедлен";
        }
        return "";
    }

    /// <summary>
    /// The interactions within reach of the selected unit of the player (ТЗ ч. 6, 6.2 «Interaction»): an object whose reach
    /// plus two hexes covers the unit is listed; the button is off with the reason while it cannot be used.
    /// </summary>
    private void RefreshInteractions(Unit u)
    {
        var sc = Sim.Scenario.Def;
        var near = new List<(ScenarioObjectDef O, string? Why)>();
        if (sc != null && u.Alive && Sim.CanPlayerCommand(u))
            foreach (var o in sc.Objects)
                if (o.Interaction is { } it && o.Cells().Min(c => HexCoord.Distance(c, u.Pos)) <= it.Range + 2)
                    near.Add((o, Sim.Interactive.CanInteract(u, o.Id)));
        string key = u.Id + "|" + string.Join(";", near.Select(n => n.O.Id + ":" + n.Why + ":" + Sim.Interactive.StateOf(n.O.Id)));
        if (key == _interactKey) return;
        _interactKey = key;
        Ui.ClearChildren(_interactRow);
        _interactRow.Visible = near.Count > 0;
        foreach (var (o, why) in near)
        {
            var it = o.Interaction!;
            string state = Sim.Interactive.CurrentState(o.Id)?.DisplayName ?? "";
            var b = Ui.Button($"⚙ {it.Prompt} — {o.DisplayName}" + (state != "" ? $" ({state})" : ""), () => _bs.InteractWith(o.Id),
                why ?? $"{(it.ApCost > 0 ? $"{it.ApCost:0.#} ОД, " : "")}{it.Duration:0.#} с" + (it.Cooldown > 0 ? $", перезарядка {it.Cooldown:0.#} с" : ""));
            b.Name = "Interact_" + o.Id;
            b.Disabled = why != null;
            _interactRow.AddChild(b);
        }
    }

    private void RefreshUnit(bool slow)
    {
        var u = _bs.Selected;
        _unitPanel.Visible = u != null;
        if (u == null) return;
        RefreshStackStrip();
        if (slow)
        {
            _blockInfo.Text = u.Alive ? BlockLine(u) : "";
            _blockInfo.Visible = _blockInfo.Text.Length > 0;
            RefreshInteractions(u);
        }
        bool intel = !Sim.Knowledge.IsFullyKnown(u);
        if (intel) _autoInfo.GetParent<Control>().Visible = false;
        _statsBtn.Visible = !intel;
        _intel.Visible = intel;
        if (intel)
        {
            _statsBox.Visible = false;
            RefreshEnemyIntel(u, slow);
            return;
        }
        _hp.Visible = _ap.Visible = _apInfo.Visible = true;
        _resources.Visible = _statuses.Visible = _current.Visible = true;
        // A structure (docs/DECISIONS_4.md §56) has no action points and takes no orders.
        if (u.IsStructure) _ap.Visible = _apInfo.Visible = false;
        if (slow) RefreshAutoAttack(u);
        var faction = _bs.Db.Faction(u.FactionId);
        var fcol = Ui.Parse(faction?.Color, Ui.Text);
        _unitName.Text = $"{ShapeGlyph(faction?.Shape ?? FactionShape.Circle)} {u.Name}" + (u.Alive ? "" : " (погиб)");
        _unitName.AddThemeColorOverride("font_color", fcol.Lightened(0.25f));
        var rel = Sim.Options.PlayerFactions.Select(pf => _bs.Db.RelationOf(pf, u.FactionId)).FirstOrDefault();
        _unitSub.Text = $"{faction?.DisplayName ?? u.FactionId} · {Names.Of(rel)} · {u.Def.CreatureCategory} · {u.LayerDef.DisplayName.ToLowerInvariant()} слой · взгляд: {u.Facing.RuName()}" + (u.AiControlled ? " · ИИ" : "");
        double shield = u.ShieldTotal;
        _hp.Set(u.Hp, u.HpMax, $"HP {u.Hp:0}/{u.HpMax:0}" + (shield > 0.5 ? $"  · щит {shield:0}" : ""));
        _hp.FillColor = u.HpFraction > 0.5 ? Ui.HpColor : u.HpFraction > 0.25 ? Ui.Warn : Ui.Bad;
        bool mine = Sim.CanPlayerCommand(u);
        double reserved = mine ? u.ReservedAp : 0;
        _ap.Set(u.Ap, u.ApMax, $"ОД {u.Ap:0.#}/{u.ApMax:0.#}" + (reserved > 0 ? $"  · резерв {reserved:0.#}" : ""), reserved);
        if (mine)
        {
            double rest = u.Ap - reserved;
            double regen = u.Flags.BlocksApRegen ? 0 : u.Stat(Stats.ApRegen);
            string wait = rest < 0 ? (regen > 0 ? $" — ожидание ≈{-rest / regen:0.#} с" : " — ОД не восстанавливаются!") : "";
            _apInfo.Text = $"Восстановление {regen:0.##}/с · ожидаемый остаток {rest:0.#} ОД{wait}";
            _apInfo.AddThemeColorOverride("font_color", rest < 0 ? Ui.Warn : Ui.TextDim);
        }
        else
        {
            _apInfo.Text = $"Восстановление {u.Stat(Stats.ApRegen):0.##}/с";
        }
        if (!slow) return;

        Ui.ClearChildren(_resources);
        foreach (var r in u.Resources.Values)
        {
            if (!r.Def.ShowBar) continue;
            // A resource in debt is shown with its minus sign (no hidden negative values, docs/DECISIONS_3.md §45).
            bool debt = r.Current < -1e-9;
            var b = new Bar { FillColor = debt ? Ui.Bad : Ui.Parse(r.Def.Color, Ui.ApColor) }.MinSize(300, 14);
            b.Set(debt ? -r.Current : r.Current, debt ? Math.Max(1, -r.Def.MinValue) : r.Max,
                $"{r.Def.DisplayName} {r.Current:0.#}/{r.Max:0}" + (debt ? $" — долг (минимум {r.Def.MinValue:0})" : "") + (r.Regen != 0 ? $" (+{r.Regen:0.#}/с)" : ""));
            _resources.AddChild(b);
        }
        AddScaleRow(u);
        AddSummonRow(u);

        Ui.ClearChildren(_statuses);
        foreach (var s in u.Statuses)
        {
            var col = Ui.Parse(s.Def.Color, Ui.Text);
            // An image icon (resource «иконка состояния») replaces the text sign.
            var image = Render.Assets.Texture(Sim.Content.Get<AssetDef>(s.Def.IconAsset));
            string icon = image != null || string.IsNullOrEmpty(s.Def.Icon) ? "" : $"[{s.Def.Icon}] ";
            void Row(Label l)
            {
                if (image == null)
                {
                    _statuses.AddChild(l);
                    return;
                }
                var tr = new TextureRect { Texture = image, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(18, 18), Name = "StatusIcon_" + s.Def.Id };
                _statuses.AddChild(Ui.HBox(4, tr, l));
            }
            if (s.Def.Kind == StatusKind.Accumulating)
            {
                Row(Ui.Label($"{icon}{s.LevelName}" + (s.LastGain > 0 || s.Points > 0 ? $" — {s.Points:0}/{s.Def.MaxPoints:0}" : ""), 13, col));
                if (s.Def.ShowBar)
                {
                    var b = new Bar { FillColor = col, ShowThresholds = true, Thresholds = s.Def.Levels.Select(l => l.Threshold).ToArray() }.MinSize(300, 10);
                    b.Set(s.Points, s.Def.MaxPoints, "");
                    _statuses.AddChild(b);
                }
            }
            else
            {
                string time = s.RemainingTicks > 0 ? $" — {s.RemainingTicks * Sim.Dt:0.#} с" : "";
                string stacks = s.Stacks > 1 ? $" ×{s.Stacks}" : "";
                Row(Ui.Label($"{icon}{s.LevelName}{stacks}{time}", 13, col));
            }
        }
        var flags = new List<string>();
        if (u.Flags.BlocksActions) flags.Add("оглушён");
        if (u.Flags.BlocksMovement) flags.Add("обездвижен");
        if (u.Flags.BlocksSkills) flags.Add("немота");
        if (u.Flags.Blind) flags.Add("ослеплён");
        if (u.Flags.Invisible) flags.Add("невидим");
        if (u.Flags.Fear) flags.Add("страх");
        if (u.Defending && u.Stance == null) flags.Add("в стойке");
        if (flags.Count > 0) _statuses.AddChild(Ui.Label("Состояние: " + string.Join(", ", flags), 12, Ui.Warn));
        if (u.Stance is { } stance)
        {
            string left = stance.UntilTick == long.MaxValue ? "до другого приказа" : $"ещё {(stance.UntilTick - Sim.Tick) * Sim.Dt:0.#} с";
            string blocks = stance.BlocksLeft > 0 ? $", осталось атак: {stance.BlocksLeft}" : "";
            _statuses.AddChild(Ui.Label($"▣ «{stance.Skill.DisplayName}»: {stance.Def.Describe()} — {left}{blocks}" +
                                        (stance.Avoided + stance.Reduced > 0 ? $" · отражено {stance.Avoided}, ослаблено {stance.Reduced}" : ""), 12, Ui.Accent, wrap: true));
        }
        var passives = u.PassiveInstances.Where(PassiveSystem.ActsOnOwner).Select(pi => string.IsNullOrEmpty(pi.Def.Icon) ? pi.Def.DisplayName : $"{pi.Def.Icon} {pi.Def.DisplayName}").Distinct().ToList();
        if (passives.Count > 0)
        {
            var pl = Ui.Label("Пассивы: " + string.Join(", ", passives), 12, Ui.TextDim, wrap: true);
            pl.MouseFilter = MouseFilterEnum.Stop;
            pl.TooltipText = string.Join("\n", PassiveLines(u, full: true).Select(x => x.Text));
            _statuses.AddChild(pl);
        }
        var series = Sim.Skills.PendingStages.FirstOrDefault(p => p.Cast.Caster == u && !p.Cast.Series.Stopped)?.Cast;
        if (series != null && series.Series.TotalHits > 1)
            _statuses.AddChild(Ui.Label($"⚔ «{series.Skill.DisplayName}»: удар {series.Series.HitsDone} из {series.Series.TotalHits}", 12, Ui.Accent));

        if (u.Casting != null && !u.Casting.Released)
            _current.Text = $"Готовит «{u.Casting.Skill.DisplayName}» — {u.Casting.Progress(Sim.Tick):P0}";
        else if (u.Queue.Current is { } c)
            _current.Text = $"{c.Describe(Sim)} — {Names.Of(c.Status)}{(string.IsNullOrEmpty(c.StatusText) ? "" : ": " + c.StatusText)}";
        else if (u.IsStructure)
            _current.Text = !u.Alive ? "Разрушена"
                : $"Сооружение · гексов {u.Footprint?.Count ?? 1} · прорвалось врагов: {Sim.Structures.Reached.GetValueOrDefault(u.ObjectId)}"
                  + (u.Indestructible ? " · неразрушима" : "");
        else
            _current.Text = !u.Alive ? ""
                : u.StopTick >= 0 && Sim.Tick - u.StopTick < Sim.TicksFor(6) && u.StopReason != "" ? "Остановлен: " + u.StopReason
                : "Ожидает приказов";
        _aiInfo.Visible = u.AiControlled && (_bs.DiagnosticsVisible || _bs.Admin);
        _aiInfo.Text = $"ИИ ({u.AiProfile?.DisplayName}): {u.Ai.Explanation}";
        if (_showStats) RefreshStats();
    }

    private static string Age(InfoField f) => f.Stale ? $" (≈{f.StaleSeconds:0} с назад)" : "";

    /// <summary>The scale of a unit (part 6, §104): the value within its bounds, the marks of its bands, what it is in and why it moved.</summary>
    /// <summary>The summoned of the player's side now (top bar): how many, of what, and the pending ones.</summary>
    public Label SummonCounter { get; private set; } = null!;

    private void UpdateSummonCounter()
    {
        var mine = Sim.Summons.Alive.Where(s => Sim.IsPlayerFaction(s.FactionId)).ToList();
        int pending = Sim.Summons.PendingCount;
        SummonCounter.Visible = mine.Count > 0 || pending > 0;
        if (!SummonCounter.Visible) return;
        SummonCounter.Text = $"Призванные: {mine.Count}" + (pending > 0 ? $" (+{pending} ждут)" : "");
        SummonCounter.TooltipText = string.Join("\n", mine.GroupBy(s => s.Def.DisplayName).Select(g => $"{g.Key} ×{g.Count()}"));
    }

    /// <summary>
    /// The summoned (part 6, §106): for a summoner — a compact counter of its group, the limit and what they do; for a summoned unit —
    /// whose it is, how long it lives and its behaviour.
    /// </summary>
    private void AddSummonRow(Unit u)
    {
        var lines = new List<string>();
        var mine = Sim.Summons.Of(u);
        if (mine.Count > 0)
        {
            var st = mine.Select(s => s.Summon).FirstOrDefault(s => s != null);
            string limit = st is { Def.MaxPerSummoner: > 0 } ? $"/{st.Def.MaxPerSummoner}" : "";
            lines.Add($"Призванные: {mine.Count}{limit} — " + string.Join(", ", mine.GroupBy(s => s.Def.DisplayName).Select(g => $"{g.Key} ×{g.Count()}")) +
                      (st != null ? " · " + EdUtil.EnumLabel(st.Behavior).ToLowerInvariant() : ""));
        }
        var who = u.SummonerId >= 0 ? Sim.GetUnit(u.SummonerId) : null;
        long end = u.Summon?.EndTick ?? u.ExpiresAtTick;
        string left = end > 0 ? $" · ещё {Math.Max(0, (end - Sim.Tick) * Sim.Dt):0} с" : "";
        if (u.Summon is { } own)
            lines.Add((own.Independent ? "Призванный, независимый" : $"Призван: {who?.Name ?? "?"}") + left + " · " + EdUtil.EnumLabel(own.Behavior).ToLowerInvariant());
        else if (who != null) lines.Add($"Призван: {who.Name}{left}");
        if (lines.Count == 0) return;
        var l = Ui.Label(string.Join("\n", lines), 12, Ui.Accent, wrap: true);
        l.Name = "SummonInfo";
        _resources.AddChild(l);
    }

    private void AddScaleRow(Unit u)
    {
        if (Sim.Scales.Of(u) is not { } sv) return;
        var p = sv.Profile;
        double lo = p.Lowest, hi = p.Highest;
        var inBands = p.Bands.Where(b => b.Enabled && sv.Value >= b.From && sv.Value <= b.To).Select(b => b.Name != "" ? b.Name : b.Status).ToList();
        var bar = new Bar
        {
            Name = "ScaleBar", FillColor = Ui.Parse(p.Color, Ui.Accent), ShowThresholds = true,
            Thresholds = p.Bands.Where(b => b.Enabled).SelectMany(b => new[] { b.From - lo, b.To - lo }).Concat(new[] { p.Base - lo }).ToArray(),
            TooltipText = $"{p.DisplayName}: {sv.Value:0.#} из [{lo:0.#}; {hi:0.#}]" + (sv.Mode == ScaleUseMode.Immune ? " — иммунитет" : "") +
                          (sv.Recent.Count > 0 ? "\nПричины: " + string.Join("; ", sv.Recent.AsEnumerable().Reverse().Select(r => $"{r.Delta:+0.#;-0.#} {r.Source}")) : ""),
        }.MinSize(300, 14);
        bar.Set(sv.Value - lo, hi - lo, $"{p.Short} {sv.Value:0}" + (inBands.Count > 0 ? " · " + string.Join(", ", inBands) : "") + (sv.Mode == ScaleUseMode.Immune ? " · иммунитет" : ""));
        _resources.AddChild(bar);
        if (sv.Recent.Count > 0 && Sim.Tick - sv.Recent[^1].Tick < Sim.TicksFor(5))
        {
            var why = Ui.Label($"{sv.Recent[^1].Delta:+0.#;-0.#} — {sv.Recent[^1].Source}", 11, Ui.TextDim);
            why.Name = "ScaleWhy";
            _resources.AddChild(why);
        }
    }

    /// <summary>
    /// Enemy information panel (docs/DECISIONS_2.md §24): only what the player side has seen or observed,
    /// by the disclosure rules; unknown categories show how to learn them.
    /// </summary>
    private void RefreshEnemyIntel(Unit u, bool slow)
    {
        var info = Sim.Knowledge.GetInfo(u);
        var faction = _bs.Db.Faction(u.FactionId);
        var fcol = Ui.Parse(faction?.Color, Ui.Text);
        _unitName.Text = $"{ShapeGlyph(faction?.Shape ?? FactionShape.Circle)} {info.Name}" + (u.Alive ? "" : " (погиб)");
        _unitName.AddThemeColorOverride("font_color", fcol.Lightened(0.25f));
        _aiInfo.Visible = false;
        if (!info.PanelEnabled)
        {
            _unitSub.Text = "Сведения об этом существе недоступны";
            _hp.Visible = _ap.Visible = _apInfo.Visible = false;
            _resources.Visible = _statuses.Visible = _current.Visible = _intel.Visible = false;
            return;
        }
        _resources.Visible = _statuses.Visible = _current.Visible = true;
        var rel = Sim.Options.PlayerFactions.Select(pf => _bs.Db.RelationOf(pf, u.FactionId)).FirstOrDefault();
        string seen = info.VisibleNow ? "на виду" : "не виден";
        _unitSub.Text = $"{faction?.DisplayName ?? u.FactionId} · {Names.Of(rel)}" + (info.Type.Length > 0 ? " · " + info.Type : "") + $" · {seen}" + Age(info.NameField);
        _hp.Visible = true;
        if (info.HealthField.Known)
        {
            _hp.Set(info.Hp, info.HpMax, $"HP {info.Hp:0}/{info.HpMax:0}{Age(info.HealthField)}");
            double f = info.HpMax <= 0 ? 0 : info.Hp / info.HpMax;
            _hp.FillColor = info.HealthField.Stale ? Ui.TextDim : f > 0.5 ? Ui.HpColor : f > 0.25 ? Ui.Warn : Ui.Bad;
        }
        else
        {
            _hp.Set(0, 1, "HP ? — " + info.HealthField.Hint);
        }
        // Action points are a resource of the creature.
        _ap.Visible = info.ResourcesField.Known && info.VisibleNow;
        if (_ap.Visible) _ap.Set(u.Ap, u.ApMax, $"ОД {u.Ap:0.#}/{u.ApMax:0.#}");
        _apInfo.Visible = false;
        _current.Text = info.IntentField.Known ? "Намерение: " + info.Intent : "Намерение: ? — " + info.IntentField.Hint;
        if (!slow) return;

        Ui.ClearChildren(_resources);
        if (info.ResourcesField.Known)
        {
            foreach (var (name, value, max) in info.Resources)
            {
                var b = new Bar { FillColor = info.ResourcesField.Stale ? Ui.TextDim : Ui.ApColor }.MinSize(300, 14);
                b.Set(value, max, $"{name} {value:0}/{max:0}{Age(info.ResourcesField)}");
                _resources.AddChild(b);
            }
        }
        else if (u.Resources.Count > 0)
        {
            _resources.AddChild(Ui.Label("Ресурсы: ? — " + info.ResourcesField.Hint, 12, Ui.TextDim));
        }
        if (info.ResourcesField.Known) AddScaleRow(u);
        if (info.ResourcesField.Known) AddSummonRow(u);

        Ui.ClearChildren(_statuses);
        if (info.StatusesField.Known)
        {
            foreach (var (name, detail) in info.Statuses)
                _statuses.AddChild(Ui.Label(name + (detail.Length > 0 ? " — " + detail : "") + Age(info.StatusesField), 13, Ui.Text));
        }
        else
        {
            _statuses.AddChild(Ui.Label("Состояния: ? — " + info.StatusesField.Hint, 12, Ui.TextDim));
        }

        Ui.ClearChildren(_intel);
        _intel.AddChild(Ui.Label("Взгляд: " + (info.FacingField.Known ? info.Facing + Age(info.FacingField) : "? — " + info.FacingField.Hint), 12,
            info.FacingField.Known ? Ui.Text : Ui.TextDim, wrap: true));
        if (info.ResistancesField.Known)
        {
            _intel.AddChild(Ui.Label("Известные реакции на урон:", 12, Ui.TextDim));
            if (info.Resistances.Count == 0) _intel.AddChild(Ui.Label("  особых сопротивлений не замечено", 12, Ui.Text));
            foreach (var (name, value) in info.Resistances)
                _intel.AddChild(Ui.Label($"  {name}: {value}", 12, value.Contains("уязв") ? Ui.Good : value.Contains("сопрот") || value.Contains("имм") || value.Contains("не проходит") ? Ui.Warn : Ui.Text));
        }
        else
        {
            _intel.AddChild(Ui.Label("Сопротивления: ? — " + info.ResistancesField.Hint, 12, Ui.TextDim, wrap: true));
        }
        if (info.SkillsField.Known)
            _intel.AddChild(Ui.Label($"Навыки ({info.Skills.Count} из {info.TotalSkills}): " + string.Join(", ", info.Skills), 12, Ui.Text, wrap: true));
        else
            _intel.AddChild(Ui.Label("Навыки: ? — " + info.SkillsField.Hint, 12, Ui.TextDim, wrap: true));
    }

    private void RefreshStats()
    {
        if (_statsBox == null) return;
        Ui.ClearChildren(_statsBox);
        var u = _bs.Selected;
        _statsBox.Visible = _showStats && u != null;
        if (!_showStats || u == null) return;
        foreach (var sd in _bs.Db.All<StatDef>())
        {
            double v = u.Stat(sd.Id);
            bool isResist = sd.Id.StartsWith("resist_");
            if (!sd.ShowInPanel && !(isResist && Math.Abs(v) > 1e-9)) continue;
            string val = sd.Format == StatFormat.Percent ? $"{v:P0}" : $"{v:0.##}";
            double b = u.Stats.Base(sd.Id);
            string mod = Math.Abs(v - b) > 1e-6 ? $"  ({(v > b ? "+" : "")}{(sd.Format == StatFormat.Percent ? $"{v - b:P0}" : $"{v - b:0.##}")})" : "";
            var row = Ui.HBox(4, Ui.Label(sd.DisplayName, 12, Ui.TextDim).Expand(), Ui.Label(val + mod, 12, Math.Abs(v - b) > 1e-6 ? (v > b ? Ui.Good : Ui.Bad) : Ui.Text));
            // The share of every source (docs/DECISIONS_3.md §46): hover the row.
            var br = u.Stats.Explain(sd.Id);
            if (br.Items.Count > 0)
            {
                row.MouseFilter = MouseFilterEnum.Stop;
                row.TooltipText = BreakdownText(sd, br);
            }
            _statsBox.AddChild(row);
        }
        foreach (var line in Sim.Damage.DefenseSummary(u)) _statsBox.AddChild(Ui.Label(line, 12, Ui.Text, wrap: true));
        foreach (var (text, color) in PassiveLines(u, full: true)) _statsBox.AddChild(Ui.Label(text, 12, color, wrap: true));
        if (u.Def.Immunities.Count > 0) _statsBox.AddChild(Ui.Label("Иммунитеты: " + string.Join(", ", u.Def.Immunities), 12, Ui.TextDim, wrap: true));
    }

    /// <summary>Base, every contribution (acting or not, with the reason) and the result of a stat.</summary>
    private static string BreakdownText(StatDef sd, StatBreakdown b)
    {
        string F(double v) => sd.Format == StatFormat.Percent ? $"{v:P0}" : $"{v:0.##}";
        var lines = new List<string> { $"{sd.DisplayName}: база {F(b.Base)} · порядок: {Names.Of(b.Order)}" };
        foreach (var (c, applied, note) in b.Items)
        {
            string val = c.Mode switch
            {
                ModifierMode.Multiplier => $"×{c.Value:0.##}",
                ModifierMode.FlatBefore or ModifierMode.FlatAfter => (c.Value >= 0 ? "+" : "") + F(c.Value),
                _ => $"{c.Value:+0.#;-0.#} %",
            };
            lines.Add($"{(applied ? "•" : "✕")} {val} — {c.Source} ({Names.Of(c.Mode)}{(note != "" ? ", " + note : "")})");
        }
        lines.Add($"Итог: {F(b.Final)}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Passives and talents of the unit with their sources; exact values when the passive shows them; the ones
    /// not acting now in grey with the reason.
    /// </summary>
    private IEnumerable<(string Text, Color Color)> PassiveLines(Unit u, bool full)
    {
        foreach (var pi in u.PassiveInstances.Where(x => x.AuraOwner >= 0 || !(x.Def.AuraRadius > 0 && x.Def.AuraPassives.Count == 0 && !x.Def.AuraIncludesSelf) || full))
        {
            var p = pi.Def;
            string icon = string.IsNullOrEmpty(p.Icon) ? (p.Kind == PassiveKind.Talent ? "★" : "◈") : p.Icon;
            string kind = p.Kind == PassiveKind.Talent ? "талант" : "пассив";
            string head = $"{icon} {p.DisplayName}{(pi.Stacks > 1 ? $" ×{pi.Stacks}" : "")} — {kind}, {pi.Source}";
            if (pi.AllSources.Count > 1) head += $" (+ ещё {pi.AllSources.Count - 1})";
            bool acts = PassiveSystem.ActsOnOwner(pi);
            string body = p.ShowExactValues ? string.Join("; ", PassiveText.Lines(p, Sim.Content)) : p.Description;
            if (p.AuraRadius > 0 && pi.AuraOwner < 0) body = $"аура {p.AuraRadius} гекс. на {Names.Of(p.AuraAffects).ToLowerInvariant()}: " + body;
            if (!full) body = "";
            string state = acts ? "" : $" · не действует: {(pi.Inactive != "" ? pi.Inactive : "только для других (аура)")}";
            yield return (head + (body != "" ? "\n   " + body : "") + state, acts ? Ui.Text : Ui.TextDim);
        }
    }

    private void RefreshActions(bool slow)
    {
        var u = _bs.Selected;
        _modeHint.Text = _bs.Mode == InputMode.None ? (u != null && Sim.CanPlayerCommand(u) ? "Выберите действие или ПКМ по клетке/врагу" : "") : _bs.Hint;
        _modeHint.Visible = _modeHint.Text != "";
        _actionPanel.Visible = u != null && Sim.CanPlayerCommand(u) && !_bs.GroupActive;
        if (u == null || !_actionPanel.Visible) return;
        if (!slow && _actionsKey != "") return;
        var actions = Sim.Planner.Actions(u);
        string key = u.Id + ":" + (int)_bs.Mode + ":" + _bs.ModeSkill?.Id + ":" + string.Join("|", actions.Select(a => a.Key + a.Enabled + a.Warning));
        if (key == _actionsKey) return;
        _actionsKey = key;
        Ui.ClearChildren(_actions);
        int skillN = 0;
        foreach (var a in actions)
        {
            if (a.Key == "queue" || a.Key == "info") continue;
            string hot = a.Key is "move" or "turn" or "attack" or "defend" or "wait" ? Hotkeys.Label(a.Key) : "";
            if (a.Key.StartsWith("skill:")) hot = Hotkeys.Label($"skill{++skillN}");
            var image = a.Skill == null ? null : Render.Assets.Texture(Sim.Content.Get<AssetDef>(a.Skill.IconAsset));
            string icon = image != null ? "" : a.Skill != null && !string.IsNullOrEmpty(a.Skill.Icon) ? a.Skill.Icon + " " : a.Key switch { "move" => "➜ ", "turn" => "↻ ", "defend" => "▣ ", "wait" => "‖ ", _ => "" };
            string cost = a.Skill != null ? $"\n{a.Skill.ApCost:0.#} ОД" : a.Key == "defend" ? $"\n{Sim.Rules.Ap.DefendCost:0.#} ОД" : "\n";
            long cd = a.Skill != null ? u.CooldownRemaining(a.Skill.Id, Sim.Tick) : 0;
            if (cd > 0) cost = $"\n◷ {cd * Sim.Dt:0.#} с";
            var b = new Button
            {
                Text = $"{icon}{a.Label}{cost}",
                Disabled = !a.Enabled,
                FocusMode = FocusModeEnum.None,
                CustomMinimumSize = new Vector2(92, 52),
                TooltipText = $"[{hot}] {a.Label}" + (a.Skill != null ? "\n" + SkillTooltip(a.Skill, u) : "") + (a.Enabled ? (a.Warning != "" ? "\n⚠ " + a.Warning : "") : "\nНедоступно: " + a.Reason),
            };
            b.AddThemeFontSizeOverride("font_size", 12);
            if (image != null)
            {
                b.Icon = image;
                b.ExpandIcon = true;
                b.IconAlignment = HorizontalAlignment.Left;
                b.AddThemeConstantOverride("icon_max_width", 28);
            }
            bool active = (_bs.Mode == InputMode.Move && a.Key == "move") || (_bs.Mode == InputMode.Turn && a.Key == "turn") ||
                          (_bs.Mode == InputMode.Attack && a.Key == "attack") || (_bs.Mode == InputMode.Skill && a.Skill == _bs.ModeSkill && a.Key != "attack");
            if (active) b.AddThemeStyleboxOverride("normal", Ui.Box(new Color(0.34f, 0.30f, 0.20f), Ui.Accent, 5, 2, 8));
            else if (a.Enabled && a.Warning != "") b.AddThemeStyleboxOverride("normal", Ui.Box(new Color(0.24f, 0.22f, 0.17f), Ui.Warn, 5, 1, 8));
            var act = a;
            if (a.Skill != null && a.Key != "defend")
            {
                // Skill icon: a click uses it automatically (if the skill allows), a drag onto the map aims it (docs/DECISIONS_3.md §43).
                b.ButtonDown += () => _bs.BeginSkillPress(act);
                b.Pressed += () => _bs.SkillClick(act);
            }
            else
            {
                b.Pressed += () => _bs.Activate(act);
            }
            var wrap = Ui.VBox(1);
            wrap.AddChild(b);
            var hotLabel = Ui.Label(hot, 11, Ui.TextDim);
            hotLabel.HorizontalAlignment = HorizontalAlignment.Center;
            wrap.AddChild(hotLabel);
            if (!a.Enabled)
            {
                var reason = Ui.Label(a.Reason, 10, Ui.Bad);
                reason.HorizontalAlignment = HorizontalAlignment.Center;
                reason.CustomMinimumSize = new Vector2(92, 0);
                reason.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                reason.MaxLinesVisible = 2;
                wrap.AddChild(reason);
            }
            _actions.AddChild(wrap);
        }
        // Radial skill menu without a target: choose a skill, then aim (docs/DECISIONS_3.md §42).
        var radial = new Button
        {
            Text = "◎ Навыки\nменю",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(92, 52),
            TooltipText = "[R] Радиальное меню навыков: защита в центре, навыки от слабого к сильному",
            Name = "OpenRadial",
        };
        radial.AddThemeFontSizeOverride("font_size", 12);
        radial.Pressed += () => _bs.OpenSkillMenu(null, radial.GetGlobalRect().GetCenter() + new Vector2(0, -220));
        var rwrap = Ui.VBox(1);
        rwrap.AddChild(radial);
        var rh = Ui.Label("R", 11, Ui.TextDim);
        rh.HorizontalAlignment = HorizontalAlignment.Center;
        rwrap.AddChild(rh);
        _actions.AddChild(rwrap);
    }

    public string SkillTooltip(SkillDef s, Unit u)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(s.Description)) lines.Add(s.Description);
        lines.Add($"{Names.Of(s.Kind)} · {Names.Of(s.Targeting)}" + (s.Targeting is TargetingMode.Unit or TargetingMode.Cell ? $" · дальность {s.RangeMin}–{s.RangeMax}" : ""));
        lines.Add($"ОД {s.ApCost:0.#}" + string.Concat(s.ResourceCosts.Select(r => $" · {r.Resource} {r.Amount:0}")) + $" · подготовка {s.PrepTime:0.##} с · перезарядка {s.Cooldown:0.#} с");
        if (SkillText.IsSeries(s))
        {
            // A sequence: every hit with its multiplier, moment, condition and costs (docs/DECISIONS_3.md §45).
            var summary = SkillText.Summary(s);
            if (summary != "") lines.Add(summary);
            lines.AddRange(SkillText.StageLines(s).Select(l => "• " + l));
        }
        else
        {
            foreach (var st in s.Stages)
                lines.Add($"• {Names.Of(st.Area.Shape)}{(st.Delay > 0 ? $" через {st.Delay:0.##} с" : "")}: " + string.Join(", ", st.Effects.Select(e => e.Describe())));
        }
        if (s.Defense.Enabled) lines.Add($"Защита ({Names.Of(s.Defense.Kind)}): {s.Defense.Describe()}");
        if (u.DefenseSkill == s) lines.Add("Защитный навык юнита — центр радиального меню, клавиша B");
        return string.Join("\n", lines);
    }

    private void RefreshQueue()
    {
        var u = _bs.Selected;
        bool show = u != null && (Sim.CanPlayerCommand(u) || _bs.Admin);
        _queuePanel.Visible = show;
        if (!show) return;
        var proj = _bs.Projection;
        string key = u!.Id + "|" + string.Join(";", proj.Select(p => $"{p.Command.Id}:{(int)p.Command.Status}:{p.Estimate.Ap:0.0}:{p.WaitForApSeconds:0}:{p.Command.StatusText}")) + "|" + Sim.Tick / 10;
        if (key == _queueKey) return;
        _queueKey = key;
        Ui.ClearChildren(_queueList);
        _queueScroll.CustomMinimumSize = new Vector2(320, Math.Clamp(24 + proj.Count * 44, 30, 300));
        if (proj.Count == 0) _queueList.AddChild(Ui.Label("Пусто. Выберите действие внизу экрана.", 13, Ui.TextDim));
        for (int i = 0; i < proj.Count; i++)
        {
            var p = proj[i];
            var c = p.Command;
            int idx = i;
            var col = c.Status switch
            {
                CommandStatus.Active => Ui.Accent,
                CommandStatus.Waiting => Ui.Warn,
                _ => p.Estimate.Feasible ? Ui.Text : Ui.Bad,
            };
            string info = $"{p.Estimate.Ap:0.#} ОД";
            if (p.Estimate.Seconds > 0) info += $" · ~{p.Estimate.Seconds:0.#} с";
            if (p.WaitForApSeconds > 0.05 && !double.IsInfinity(p.WaitForApSeconds)) info += $" · ждёт ОД ≈{p.WaitForApSeconds:0.#} с";
            if (c.Status != CommandStatus.Pending) info += " · " + Names.Of(c.Status);
            if (!p.Estimate.Feasible && !string.IsNullOrEmpty(p.Estimate.Problem)) info += " · ⚠ " + p.Estimate.Problem;
            if (!string.IsNullOrEmpty(c.StatusText) && c.Status == CommandStatus.Waiting) info += " · " + c.StatusText;
            // Diagnostics: how the automatic target was chosen (docs/DECISIONS_3.md §49).
            if (!string.IsNullOrEmpty(c.TargetChoice) && (_bs.DiagnosticsVisible || _bs.Admin)) info += " · выбор цели: " + c.TargetChoice;
            var text = Ui.VBox(0, Ui.Label($"{i + 1}. {c.Describe(Sim)}", 13, col), Ui.Label(info, 11, Ui.TextDim, wrap: true).MinSize(200));
            var row = Ui.HBox(3);
            row.AddChild(text.Expand());
            bool pending = c.Status == CommandStatus.Pending;
            var up = Ui.Button("↑", () =>
            {
                Sim.Input.ReorderCommand(u, c.Id, idx - 1);
                OnQueueChanged();
            }, "Выше");
            up.Disabled = !pending || idx == 0;
            var down = Ui.Button("↓", () =>
            {
                Sim.Input.ReorderCommand(u, c.Id, idx + 1);
                OnQueueChanged();
            }, "Ниже");
            down.Disabled = !pending || idx == proj.Count - 1;
            row.AddChild(up);
            row.AddChild(down);
            row.AddChild(Ui.Button("✕", () =>
            {
                Sim.Input.RemoveCommand(u, c.Id);
                OnQueueChanged();
            }, c.Status == CommandStatus.Pending ? "Удалить" : "Отменить (выполненные действия не откатываются)"));
            _queueList.AddChild(row);
        }
        double reserved = u.ReservedAp;
        _queueFooter.Text = proj.Count == 0 ? "" : $"Резерв: {reserved:0.#} ОД · сейчас: {u.Ap:0.#} · остаток: {u.Ap - reserved:0.#}" +
                                                   (u.Ap - reserved < 0 ? " (команды подождут восстановления)" : "");
    }

    private void RefreshTooltip()
    {
        var cell = _bs.HoverCell;
        if (cell == null || IsMouseOverUi())
        {
            _tooltip.Visible = false;
            return;
        }
        _tooltipText.Text = CellText(cell, true);
        _tooltip.Visible = true;
        _tooltip.ResetSize();
        var m = GetViewport().GetMousePosition();
        var pos = m + new Vector2(22, 18);
        if (pos.X + _tooltip.Size.X > Size.X) pos.X = m.X - _tooltip.Size.X - 16;
        if (pos.Y + _tooltip.Size.Y > Size.Y) pos.Y = m.Y - _tooltip.Size.Y - 12;
        _tooltip.Position = pos;
    }

    private string CellText(Cell cell, bool full)
    {
        var sb = new System.Text.StringBuilder();
        var t = cell.Terrain;
        bool cellVisible = _bs.Admin || !Sim.FogOfWar || Sim.Vision.IsCellVisibleToPlayer(cell.Coord);
        sb.Append($"[b]{cell.DisplayName}[/b] {cell.Coord} · высота {cell.Height}");
        if (!cellVisible) sb.Append(" · [color=#8890a0]вне видимости[/color]");
        sb.Append('\n');
        if (!full) return sb.ToString();
        var u = _bs.Selected;
        var parts = new List<string>();
        if (u != null)
        {
            if (t.IsPassableFor(u.Def.MoveCategory) && !cell.Blocked)
                parts.Add($"вход {MovementRules.EntryCost(MoveContext.For(Sim, u), null, cell):0.#}");
            else
                parts.Add("[color=#ff8080]непроходимо[/color]");
        }
        if (cell.Cover > 0) parts.Add($"укрытие {cell.Cover:P0}");
        if (cell.TerrainBlocksLoS) parts.Add("закрывает обзор");
        if (cell.IsHazard) parts.Add("[color=#ffb060]опасно[/color]");
        if (cell.Terrain.DestructibleHp > 0) parts.Add($"прочность {cell.ObstacleHp}");
        if (parts.Count > 0) sb.Append(string.Join(" · ", parts)).Append('\n');
        if (!string.IsNullOrEmpty(cell.Description)) sb.Append($"[i]{cell.Description}[/i]\n");
        else if (!string.IsNullOrEmpty(t.Description)) sb.Append($"[color=#a0a6ae]{t.Description}[/color]\n");
        if (cellVisible)
            foreach (var e in cell.Effects)
            {
                var col = Ui.ColorTag(Ui.Parse(e.Def.Color, Ui.Text));
                string time = e.RemainingTicks > 0 ? $" {e.RemainingTicks * Sim.Dt:0.#} с" : e.Intrinsic ? " (постоянно)" : "";
                sb.Append($"[color={col}]◈ {e.Def.DisplayName}{(e.Level > 1 ? $" ур.{e.Level}" : "")}{time}[/color]\n");
            }
        var unit = _bs.HoverUnit;
        if (unit != null)
        {
            var f = _bs.Db.Faction(unit.FactionId);
            var tag = Ui.ColorTag(Ui.Parse(f?.Color, Ui.Text).Lightened(0.3f));
            if (Sim.Knowledge.IsFullyKnown(unit))
            {
                sb.Append($"[color={tag}]{ShapeGlyph(f?.Shape ?? FactionShape.Circle)} {unit.Name}[/color] HP {unit.Hp:0}/{unit.HpMax:0} · ОД {unit.Ap:0.#}\n");
                if (unit.Statuses.Count > 0) sb.Append(string.Join(", ", unit.Statuses.Select(s => s.LevelName).Distinct())).Append('\n');
            }
            else
            {
                // Enemies: only what the disclosure rules allow (docs/DECISIONS_2.md §24).
                var info = Sim.Knowledge.GetInfo(unit);
                sb.Append($"[color={tag}]{ShapeGlyph(f?.Shape ?? FactionShape.Circle)} {info.Name}[/color]");
                sb.Append(info.HealthField.Known ? $" HP {info.Hp:0}/{info.HpMax:0}" : " HP ?");
                sb.Append('\n');
                if (info.StatusesField.Known && info.Statuses.Count > 0) sb.Append(string.Join(", ", info.Statuses.Select(s => s.Name).Distinct())).Append('\n');
                if (info.IntentField.Known) sb.Append($"[color=#c0c8d0]{info.Intent}[/color]\n");
                if (info.PanelEnabled) sb.Append("[color=#8890a0]клик — сведения о противнике[/color]\n");
            }
            var line = BlockLine(unit);
            if (line.Length > 0) sb.Append($"[color=#ffb070]{line}[/color]\n");
            int n = _bs.StackOf(unit).Count;
            if (n > 1) sb.Append($"[color=#ffe8a0]На гексе {n} существ: повторный клик или Tab — следующий[/color]\n");
        }
        // Mode-specific details.
        if (_bs.Mode == InputMode.Move && _bs.HoverPath is { } p)
        {
            if (p.Found) sb.Append($"[color=#ffe090]Путь: {p.Path.Count} кл., {p.Cost * Sim.Rules.Ap.MovePerCost:0.#} ОД[/color]\n");
            else sb.Append($"[color=#ff8080]Недостижимо: {p.Reason}[/color]\n");
        }
        if ((_bs.Mode == InputMode.Skill || _bs.Mode == InputMode.Attack) && _bs.HoverSkillPreview is { } pv)
        {
            if (!pv.Valid) sb.Append($"[color=#ff8080]{pv.Problem}[/color]\n");
            foreach (var (tu, hit, notice, surprise, sector) in pv.Odds)
                sb.Append($"{tu.Name}: попадание {hit:P0}, заметит {notice:P0}, неожиданность {surprise:P0} ({Names.Of(sector)})\n");
            foreach (var w in pv.Warnings) sb.Append($"[color=#ffc060]⚠ {w}[/color]\n");
            sb.Append("[color=#8890a0]Предпросмотр не гарантирует результат: цели могут сместиться[/color]\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    private void RefreshDiag()
    {
        _diagPanel.Visible = _bs.DiagnosticsVisible;
        if (!_diagPanel.Visible) return;
        var lines = new List<string>
        {
            $"FPS {Engine.GetFramesPerSecond():0} · тик {Sim.Tick} · тиков/кадр {_bs.Driver.LastFrameTicks} · пропущено {_bs.Driver.DroppedTicks}",
            $"юнитов {Sim.AliveUnits.Count()} · эффектов {Sim.CellEffects.ActiveEffectCount} · синергий {Sim.CellEffects.SynergiesTriggered} · RNG {Sim.Rng.Draws}",
            $"путей {Sim.Pathfinder.TotalSearches} · LoS {Sim.Los.Checks} (кэш {Sim.Los.CacheHits}) · видимость {Sim.Vision.Recomputes}",
        };
        foreach (var kv in Sim.Profiler.RecentMsPerTick.OrderByDescending(k => k.Value)) lines.Add($"  {kv.Key}: {kv.Value:0.000} мс/тик");
        if (_bs.Admin && Sim.Damage.Recent.Count > 0)
        {
            // Damage breakdown of the last hits (TZ part 2 §11.8).
            lines.Add("Разбор урона (последние удары):");
            foreach (var (tick, a, t, r) in Sim.Damage.Recent.TakeLast(3))
            {
                string who = $"{Sim.GetUnit(a)?.Name ?? "—"} → {Sim.GetUnit(t)?.Name ?? "?"}";
                lines.Add($"  {tick * Sim.Dt:0.0} с {who}: {r.ShortBreakdown()}");
            }
        }
        _diagText.Text = string.Join("\n", lines);
    }
}
