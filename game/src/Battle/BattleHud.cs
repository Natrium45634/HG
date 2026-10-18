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

    private PanelContainer _actionPanel = null!;
    private HFlowContainer _actions = null!;
    private Label _modeHint = null!;
    private string _actionsKey = "";

    private PanelContainer _queuePanel = null!;
    private VBoxContainer _queueList = null!;
    private Label _queueFooter = null!;
    private string _queueKey = "";
    private CheckButton _appendToggle = null!;
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
        BuildLog();
        BuildTooltip();
        BuildBanner();
        BuildOutcome();
        BuildHelp();
        BuildMenu();
        BuildDiag();
        BuildPopup();
        OnSelectionChanged();
    }

    private T Track<T>(T c) where T : Control
    {
        _panels.Add(c);
        return c;
    }

    public bool IsMouseOverUi()
    {
        var m = GetViewport().GetMousePosition();
        foreach (var p in _panels)
            if (p.Visible && p.IsVisibleInTree() && p.GetGlobalRect().HasPoint(m))
                return true;
        return _popup.Visible;
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
                _bs.Driver.Paused = false;
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
        bar.AddChild(Ui.Button("Журнал", ToggleLog, "Показать/скрыть журнал (L)"));
        bar.AddChild(Ui.Button("?", ToggleHelp, "Справка (F1)", 34));
        bar.AddChild(Ui.Button("Меню", ToggleMenu, "Меню (Esc)"));
        var panel = Track(Ui.PanelBox(bar, null, 6));
        panel.SetAnchorsPreset(LayoutPreset.CenterTop);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.Position = new Vector2(0, 8);
        AddChild(panel);
        panel.Resized += () => panel.Position = new Vector2((Size.X - panel.Size.X) / 2, 8);
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
        outer.AddChild(head);
        outer.AddChild(_unitBox);
        _unitBox.AddChild(_unitSub);
        _unitBox.AddChild(_hp);
        _unitBox.AddChild(_ap);
        _unitBox.AddChild(_apInfo);
        _unitBox.AddChild(_resources);
        _unitBox.AddChild(_statuses);
        _unitBox.AddChild(_current);
        _unitBox.AddChild(_aiInfo);
        _unitBox.AddChild(Ui.Button("Характеристики (I)", ToggleInfo));
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
        btns.AddChild(Ui.Button("Удалить последнюю", () =>
        {
            var u = _bs.Selected;
            if (u != null && u.Queue.Count > 0) Sim.RemoveCommand(u, u.Queue.Items[^1].Id);
            OnQueueChanged();
        }, "Backspace"));
        btns.AddChild(Ui.Button("Очистить", () =>
        {
            if (_bs.Selected != null) Sim.ClearQueue(_bs.Selected);
            OnQueueChanged();
        }, "Delete"));
        body.AddChild(btns);
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
        var h = Ui.HBox(8);
        h.AddChild(Ui.Button("Заново", () => _bs.Restart()).MinSize(140, 40));
        h.AddChild(Ui.Button("Выйти", () => _bs.ExitBattle()).MinSize(140, 40));
        h.AddChild(Ui.Button("Осмотреться", () => _outcome!.Visible = false).MinSize(140, 40));
        v.AddChild(h);
        _outcome = Track(Ui.PanelBox(v, Ui.PanelLight, 24));
        _outcome.Visible = false;
        AddChild(_outcome);
    }

    private void BuildHelp()
    {
        var text = string.Join("\n", new[]
        {
            "Пробел — пауза/продолжить · Z/X или −/+ — скорость · . — один тик (на паузе)",
            "ЛКМ по своему юниту — выбрать и открыть меню действий · Tab — следующий юнит",
            "M — движение (несколько ЛКМ = маршрут) · T — поворот · F — атака · 1–9 — навыки",
            "B — защитная стойка · H — ожидание 2 с · Backspace — удалить последнюю команду · Delete — очистить",
            "ЛКМ по номеру точки маршрута — действие после этой точки (навык, поворот, стойка…)",
            "ПКМ — быстрое движение / атака по врагу · ПКМ/Esc — выйти из режима",
            "Shift при клике — заменить очередь вместо добавления (или наоборот, см. переключатель)",
            "Камера: WASD/стрелки, средняя кнопка мыши, край экрана · колесо/PgUp/PgDn — зум · Q/E — поворот",
            "I — характеристики · L — журнал · F3 — диагностика · Home — к выбранному юниту",
            "",
            "Обозначения: синяя линия — запланированный маршрут; жёлтая — предпросмотр пути;",
            "серые точки — пройденный путь; заливка цвета навыка — область; штриховка + «!» — угроза врага;",
            "серая штриховка — клетка закрыта препятствием; пунктир «?» — последнее известное место врага.",
            "Основание фишки: круг — отряд, ромб — враги, квадрат — нейтралы. Стрелка — направление взгляда.",
        });
        var v = Ui.VBox(8, Ui.Label("Управление и обозначения", 20, Ui.Accent), Ui.Label(text, 14, Ui.Text), Ui.Button("Закрыть", ToggleHelp));
        _help = Track(Ui.PanelBox(v, Ui.PanelLight, 18));
        _help.Visible = false;
        AddChild(_help);
    }

    private void BuildMenu()
    {
        var v = Ui.VBox(8, Ui.Label("Меню", 20, Ui.Accent));
        v.AddChild(Ui.Button("Продолжить", ToggleMenu).MinSize(240, 40));
        v.AddChild(Ui.Button("Начать заново", () => _bs.Restart()).MinSize(240, 40));
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

    public void ShowBanner(string text, Color color, float seconds = 4f)
    {
        _banner.Text = text;
        _banner.AddThemeColorOverride("font_color", color);
        _bannerTime = seconds;
    }

    public void ToggleLog() => _logPanel.Visible = !_logPanel.Visible;
    public void ToggleHelp() => _help.Visible = !_help.Visible;
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
            string hot = a.Key switch { "move" => " (M)", "turn" => " (T)", "attack" => " (F)", "defend" => " (B)", "wait" => " (H)", "info" => " (I)", _ => "" };
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
            if (_waypointIndex >= 0 && _waypointIndex < u.Queue.Count) Sim.RemoveCommand(u, u.Queue.Items[_waypointIndex].Id);
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
        _banner.Position = new Vector2(vp.X * 0.2f, 64);
        _banner.Size = new Vector2(vp.X * 0.6f, 40);
        _outcome.Position = (vp - _outcome.Size) / 2;
        _help.Position = (vp - _help.Size) / 2;
        _menu.Position = (vp - _menu.Size) / 2;
        _diagPanel.Position = new Vector2(vp.X - _diagPanel.Size.X - 10, vp.Y - _diagPanel.Size.Y - _actionPanel.Size.Y - 24);

        // Top bar.
        var d = _bs.Driver;
        _pauseBtn.Text = d.Paused ? "▶" : "‖";
        _pauseBtn.TooltipText = d.Paused ? "Продолжить (Пробел)" : "Пауза (Пробел)";
        for (int i = 0; i < _speedBtns.Count; i++) _speedBtns[i].ButtonPressed = !d.Paused && i == d.SpeedIndex;
        _time.Text = Ui.FormatTime(Sim.Time);
        _status.Text = d.Paused ? "ПАУЗА" : $"{d.Speed:0.##}×";
        _status.AddThemeColorOverride("font_color", d.Paused ? Ui.Warn : Ui.TextDim);

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
            _outcomeText.AddThemeColorOverride("font_color", Sim.Outcome == BattleOutcome.Victory ? Ui.Good : Ui.Bad);
            _outcome.Visible = true;
        }

        _refreshTimer -= dt;
        bool slow = _refreshTimer <= 0;
        if (slow) _refreshTimer = 0.2f;

        RefreshUnit(slow);
        RefreshActions(slow);
        RefreshQueue();
        RefreshTooltip();
        RefreshDiag();
    }

    private static string ShapeGlyph(FactionShape s) => s switch { FactionShape.Diamond => "◆", FactionShape.Square => "■", FactionShape.Triangle => "▲", _ => "●" };

    private void RefreshUnit(bool slow)
    {
        var u = _bs.Selected;
        _unitPanel.Visible = u != null;
        if (u == null) return;
        var faction = _bs.Db.Faction(u.FactionId);
        var fcol = Ui.Parse(faction?.Color, Ui.Text);
        _unitName.Text = $"{ShapeGlyph(faction?.Shape ?? FactionShape.Circle)} {u.Name}" + (u.Alive ? "" : " (погиб)");
        _unitName.AddThemeColorOverride("font_color", fcol.Lightened(0.25f));
        var rel = Sim.Options.PlayerFactions.Select(pf => _bs.Db.RelationOf(pf, u.FactionId)).FirstOrDefault();
        _unitSub.Text = $"{faction?.DisplayName ?? u.FactionId} · {Names.Of(rel)} · {u.Def.CreatureCategory} · взгляд: {u.Facing.RuName()}" + (u.AiControlled ? " · ИИ" : "");
        _hp.Set(u.Hp, u.HpMax, $"HP {u.Hp:0}/{u.HpMax:0}");
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
            var b = new Bar { FillColor = Ui.Parse(r.Def.Color, Ui.ApColor) }.MinSize(300, 14);
            b.Set(r.Current, r.Max, $"{r.Def.DisplayName} {r.Current:0}/{r.Max:0}" + (r.Regen != 0 ? $" (+{r.Regen:0.#}/с)" : ""));
            _resources.AddChild(b);
        }

        Ui.ClearChildren(_statuses);
        foreach (var s in u.Statuses)
        {
            var col = Ui.Parse(s.Def.Color, Ui.Text);
            string icon = string.IsNullOrEmpty(s.Def.Icon) ? "" : $"[{s.Def.Icon}] ";
            if (s.Def.Kind == StatusKind.Accumulating)
            {
                _statuses.AddChild(Ui.Label($"{icon}{s.LevelName}" + (s.LastGain > 0 || s.Points > 0 ? $" — {s.Points:0}/{s.Def.MaxPoints:0}" : ""), 13, col));
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
                _statuses.AddChild(Ui.Label($"{icon}{s.LevelName}{stacks}{time}", 13, col));
            }
        }
        var flags = new List<string>();
        if (u.Flags.BlocksActions) flags.Add("оглушён");
        if (u.Flags.BlocksMovement) flags.Add("обездвижен");
        if (u.Flags.BlocksSkills) flags.Add("немота");
        if (u.Flags.Blind) flags.Add("ослеплён");
        if (u.Flags.Invisible) flags.Add("невидим");
        if (u.Flags.Fear) flags.Add("страх");
        if (u.Defending) flags.Add("в стойке");
        if (flags.Count > 0) _statuses.AddChild(Ui.Label("Состояние: " + string.Join(", ", flags), 12, Ui.Warn));

        if (u.Casting != null && !u.Casting.Released)
            _current.Text = $"Готовит «{u.Casting.Skill.DisplayName}» — {u.Casting.Progress(Sim.Tick):P0}";
        else if (u.Queue.Current is { } c)
            _current.Text = $"{c.Describe(Sim)} — {Names.Of(c.Status)}{(string.IsNullOrEmpty(c.StatusText) ? "" : ": " + c.StatusText)}";
        else
            _current.Text = u.Alive ? "Ожидает приказов" : "";
        _aiInfo.Visible = u.AiControlled && (_bs.DiagnosticsVisible || _bs.Admin);
        _aiInfo.Text = $"ИИ ({u.AiProfile?.DisplayName}): {u.Ai.Explanation}";
        if (_showStats) RefreshStats();
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
            _statsBox.AddChild(row);
        }
        if (u.Passives.Count > 0) _statsBox.AddChild(Ui.Label("Пассивные: " + string.Join(", ", u.Passives.Select(p => p.DisplayName)), 12, Ui.TextDim, wrap: true));
        if (u.Def.Immunities.Count > 0) _statsBox.AddChild(Ui.Label("Иммунитеты: " + string.Join(", ", u.Def.Immunities), 12, Ui.TextDim, wrap: true));
    }

    private void RefreshActions(bool slow)
    {
        var u = _bs.Selected;
        _modeHint.Text = _bs.Mode == InputMode.None ? (u != null && Sim.CanPlayerCommand(u) ? "Выберите действие или ПКМ по клетке/врагу" : "") : _bs.Hint;
        _modeHint.Visible = _modeHint.Text != "";
        _actionPanel.Visible = u != null && Sim.CanPlayerCommand(u);
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
            string hot = a.Key switch { "move" => "M", "turn" => "T", "attack" => "F", "defend" => "B", "wait" => "H", _ => "" };
            if (a.Key.StartsWith("skill:")) hot = (++skillN).ToString();
            string icon = a.Skill != null && !string.IsNullOrEmpty(a.Skill.Icon) ? a.Skill.Icon + " " : a.Key switch { "move" => "➜ ", "turn" => "↻ ", "defend" => "▣ ", "wait" => "‖ ", _ => "" };
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
            bool active = (_bs.Mode == InputMode.Move && a.Key == "move") || (_bs.Mode == InputMode.Turn && a.Key == "turn") ||
                          (_bs.Mode == InputMode.Attack && a.Key == "attack") || (_bs.Mode == InputMode.Skill && a.Skill == _bs.ModeSkill && a.Key != "attack");
            if (active) b.AddThemeStyleboxOverride("normal", Ui.Box(new Color(0.34f, 0.30f, 0.20f), Ui.Accent, 5, 2, 8));
            else if (a.Enabled && a.Warning != "") b.AddThemeStyleboxOverride("normal", Ui.Box(new Color(0.24f, 0.22f, 0.17f), Ui.Warn, 5, 1, 8));
            var act = a;
            b.Pressed += () => _bs.Activate(act);
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
    }

    private string SkillTooltip(SkillDef s, Unit u)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(s.Description)) lines.Add(s.Description);
        lines.Add($"{Names.Of(s.Kind)} · {Names.Of(s.Targeting)}" + (s.Targeting is TargetingMode.Unit or TargetingMode.Cell ? $" · дальность {s.RangeMin}–{s.RangeMax}" : ""));
        lines.Add($"ОД {s.ApCost:0.#}" + string.Concat(s.ResourceCosts.Select(r => $" · {r.Resource} {r.Amount:0}")) + $" · подготовка {s.PrepTime:0.##} с · перезарядка {s.Cooldown:0.#} с");
        foreach (var st in s.Stages)
            lines.Add($"• {Names.Of(st.Area.Shape)}{(st.Delay > 0 ? $" через {st.Delay:0.##} с" : "")}: " + string.Join(", ", st.Effects.Select(e => e.Describe())));
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
            var text = Ui.VBox(0, Ui.Label($"{i + 1}. {c.Describe(Sim)}", 13, col), Ui.Label(info, 11, Ui.TextDim, wrap: true).MinSize(200));
            var row = Ui.HBox(3);
            row.AddChild(text.Expand());
            bool pending = c.Status == CommandStatus.Pending;
            var up = Ui.Button("↑", () =>
            {
                Sim.ReorderCommand(u, c.Id, idx - 1);
                OnQueueChanged();
            }, "Выше");
            up.Disabled = !pending || idx == 0;
            var down = Ui.Button("↓", () =>
            {
                Sim.ReorderCommand(u, c.Id, idx + 1);
                OnQueueChanged();
            }, "Ниже");
            down.Disabled = !pending || idx == proj.Count - 1;
            row.AddChild(up);
            row.AddChild(down);
            row.AddChild(Ui.Button("✕", () =>
            {
                Sim.RemoveCommand(u, c.Id);
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
                parts.Add($"вход {MovementRules.EntryCost(Sim.Rules, u.Def.MoveCategory, null, cell):0.#}");
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
            sb.Append($"[color={Ui.ColorTag(Ui.Parse(f?.Color, Ui.Text).Lightened(0.3f))}]{ShapeGlyph(f?.Shape ?? FactionShape.Circle)} {unit.Name}[/color] HP {unit.Hp:0}/{unit.HpMax:0} · ОД {unit.Ap:0.#}\n");
            if (unit.Statuses.Count > 0) sb.Append(string.Join(", ", unit.Statuses.Select(s => s.LevelName).Distinct())).Append('\n');
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
        _diagText.Text = string.Join("\n", lines);
    }
}
