using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// Player control of part 3 (docs/DECISIONS_3.md §39–§41): planning autopause, the interactive facing
/// arrow with one gesture for turning and routing, the dimmed map with reachable hexes and hazard
/// icons. Every gesture ends as an ordinary queued command.
/// </summary>
public partial class BattleScreen
{
    private sealed class Gesture
    {
        /// <summary>-1 — the head of the plan (end of the queue); otherwise the index of a route node.</summary>
        public int NodeIndex = -1;
        public int CommandId = -1;
        public HexCoord Anchor;
        public HexDirection AnchorFacing;
        public Vector2 StartMouse;
        public bool Moved;
        public bool RouteMode;
        public float Yaw;
        public HexDirection Dir;
        /// <summary>Aiming a skill with the arrow (skill/attack mode): the release confirms the target under the cursor.</summary>
        public bool Aim;
    }

    private FacingArrow _headArrow = null!;
    private readonly List<FacingArrow> _nodeArrows = new();
    private readonly List<(int CommandId, int Index, HexCoord Pos, HexDirection Facing)> _nodes = new();
    private Gesture? _gesture;
    private bool _autoPaused;
    private bool _planningResumed;
    private Dictionary<HexCoord, CellHazard>? _hazards;
    private (HexCoord From, double Ap, int Ver, long Tick)? _hazardKey;

    public bool GestureActive => _gesture != null;
    public bool GestureRoute => _gesture?.RouteMode == true;
    /// <summary>The planning autopause is holding the game (for tests and the HUD).</summary>
    public bool AutoPaused => _autoPaused && Driver.Paused;
    public FacingArrow HeadArrow => _headArrow;
    public IReadOnlyList<FacingArrow> NodeArrows => _nodeArrows;

    private void InitControl()
    {
        _headArrow = new FacingArrow { Name = "FacingArrow" };
        Board.AddChild(_headArrow);
    }

    // ------------------------------------------------------------------ planning autopause (§39)

    /// <summary>Pauses the game when planning starts, per the player's settings.</summary>
    public void PlanningPause(string why, Func<App.Settings, bool> enabled)
    {
        var s = Settings;
        if (!s.AutopauseEnabled || !enabled(s)) return;
        if (s.PauseOnlyAtPlanningStart && _planningResumed) return;
        if (Driver.Paused) return;
        Driver.Paused = true;
        _autoPaused = true;
        _hud.ShowBanner("Пауза: " + why, Ui.TextDim, 1.2f);
    }

    /// <summary>An order was confirmed: the autopause stays unless the player asked otherwise.</summary>
    private void OnOrderConfirmed()
    {
        if (Settings.KeepPauseAfterOrder || !_autoPaused || !Driver.Paused) return;
        Driver.Paused = false;
        _autoPaused = false;
    }

    /// <summary>The player toggled the pause by hand: the autopause no longer owns it.</summary>
    private void OnManualPause(bool paused)
    {
        _autoPaused = false;
        if (!paused && Selected != null) _planningResumed = true;
    }

    private void OnPlanningEnded() => _planningResumed = false;

    // ------------------------------------------------------------------ commands from gestures

    /// <summary>A move order with the player's routing mode (§41).</summary>
    public Command PlayerMove(HexCoord to)
    {
        var c = Command.Move(to);
        c.Routing = Settings.RouteMode;
        if (Settings.RouteMode == RouteMode.Custom) c.RouteWeights = Settings.RouteWeights.Clone();
        return c;
    }

    private bool CanTurn(Unit u) => !u.Flags.BlocksActions && !u.Flags.BlocksTurning;

    // ------------------------------------------------------------------ arrows

    /// <summary>Places the head arrow (end of the plan) and the arrival arrows of route nodes.</summary>
    private void UpdateArrows()
    {
        var u = Selected;
        bool show = u != null && u.Alive && Sim.CanPlayerCommand(u) && Settings.Gestures && !GroupActive && (Mode is InputMode.None or InputMode.Skill or InputMode.Attack || _gesture != null);
        _nodes.Clear();
        if (!show)
        {
            _headArrow.Hide();
            foreach (var a in _nodeArrows) a.Hide();
            return;
        }
        var rig = Board.Rig;
        float zoom = Math.Clamp(rig.Distance / 14f, 0.8f, 2.2f);
        var (head, headFacing) = PlanStart();
        int headCmd = -1;
        for (int i = 0; i < _proj.Count; i++)
        {
            var pc = _proj[i];
            if (pc.Command.Type != CommandType.Move || pc.Command.IsFinished) continue;
            if (i == _proj.Count - 1) headCmd = pc.Command.Id;
            else _nodes.Add((pc.Command.Id, i, pc.EndPos, pc.EndFacing));
        }
        var headCell = Sim.Map.Get(head);
        if (headCell == null)
        {
            _headArrow.Hide();
            return;
        }
        float yaw = _gesture is { NodeIndex: -1 } g && g.Moved && !g.RouteMode ? g.Yaw
            : Mode is InputMode.Skill or InputMode.Attack && _hover != null && _hover.Coord != head ? FacingArrow.YawTowards(Render.Board.Center(head), Render.Board.Center(_hover.Coord))
            : Render.Board.YawOf(headFacing);
        _headArrow.Show(head, Render.Board.Center(head, Render.Board.PickY(headCell)), yaw);
        _headArrow.SizeFactor = (float)Settings.ArrowSize;
        _headArrow.AnimSpeed = (float)Settings.UiAnimSpeed;
        _headArrow.ZoomScale = zoom;
        bool hover = _gesture == null && _hud != null && !_hud.IsMouseOverUi() && _headArrow.Hit(rig.Camera, _mouse, GrabPx);
        _headArrow.State = !CanTurn(u!) ? ArrowState.Invalid
            : _gesture is { NodeIndex: -1 } ? (_gesture.RouteMode ? ArrowState.Preview : ArrowState.Grabbed)
            : hover ? ArrowState.Hover
            : _proj.Count > 0 ? ArrowState.Arrival : ArrowState.Normal;

        while (_nodeArrows.Count < _nodes.Count)
        {
            var a = new FacingArrow { Name = "NodeArrow" + _nodeArrows.Count };
            Board.AddChild(a);
            _nodeArrows.Add(a);
        }
        for (int i = 0; i < _nodeArrows.Count; i++)
        {
            var a = _nodeArrows[i];
            if (i >= _nodes.Count)
            {
                a.Hide();
                continue;
            }
            var n = _nodes[i];
            var cell = Sim.Map.Get(n.Pos);
            if (cell == null)
            {
                a.Hide();
                continue;
            }
            float ny = _gesture != null && _gesture.NodeIndex == n.Index && _gesture.Moved ? _gesture.Yaw : Render.Board.YawOf(n.Facing);
            a.Show(n.Pos, Render.Board.Center(n.Pos, Render.Board.PickY(cell)), ny);
            a.SizeFactor = (float)Settings.ArrowSize * 0.85f;
            a.AnimSpeed = (float)Settings.UiAnimSpeed;
            a.ZoomScale = zoom;
            bool h = _gesture == null && _hud != null && !_hud.IsMouseOverUi() && a.Hit(rig.Camera, _mouse, GrabPx);
            a.State = _gesture != null && _gesture.NodeIndex == n.Index ? ArrowState.Grabbed : h ? ArrowState.Hover : ArrowState.Arrival;
        }
        _ = headCmd;
    }

    private float GrabPx => 22f * (float)Settings.UiScale;

    // ------------------------------------------------------------------ gesture (§40, §41)

    /// <summary>LMB pressed: starts a gesture if the press is on the head arrow or on a node arrow.</summary>
    private bool TryStartGesture(Vector2 mouse)
    {
        var u = Selected;
        if (u == null || !Settings.Gestures || !Sim.CanPlayerCommand(u)) return false;
        if (Mode is not (InputMode.None or InputMode.Skill or InputMode.Attack)) return false;
        bool aim = Mode != InputMode.None;
        var cam = Board.Rig.Camera;
        // A press closer to another creature's figure than to the arrow selects that creature.
        float other = float.MaxValue;
        foreach (var v in Board.Views.Values)
            if (v.Unit != u && v.Unit.Alive && v.Visible)
                other = Math.Min(other, cam.UnprojectPosition(v.Position + new Vector3(0, 0.3f, 0)).DistanceTo(mouse));
        bool Grab(FacingArrow a)
        {
            // Over another hex with a visible creature — that is a click on the creature.
            if (_hover != null && _hover.Coord != a.Cell && _hoverUnit != null && _hoverUnit != u) return false;
            float d = a.HitDistance(cam, mouse);
            return d <= GrabPx && d < other;
        }
        if (_headArrow.Visible && Grab(_headArrow))
        {
            var (pos, facing) = PlanStart();
            _gesture = new Gesture { NodeIndex = -1, Anchor = pos, AnchorFacing = facing, StartMouse = mouse, Yaw = Render.Board.YawOf(facing), Dir = facing, Aim = aim };
            return true;
        }
        if (aim) return false;
        for (int i = 0; i < _nodes.Count && i < _nodeArrows.Count; i++)
        {
            if (!Grab(_nodeArrows[i])) continue;
            var n = _nodes[i];
            _gesture = new Gesture { NodeIndex = n.Index, CommandId = n.CommandId, Anchor = n.Pos, AnchorFacing = n.Facing, StartMouse = mouse, Yaw = Render.Board.YawOf(n.Facing), Dir = n.Facing };
            return true;
        }
        return false;
    }

    /// <summary>World point under the mouse on the plane of the anchor hex top.</summary>
    private Vector3? PlanePoint(Vector2 mouse, HexCoord anchor)
    {
        var cam = Board.Rig.Camera;
        var cell = Sim.Map.Get(anchor);
        float y = cell == null ? 0 : Render.Board.PickY(cell);
        var o = cam.ProjectRayOrigin(mouse);
        var d = cam.ProjectRayNormal(mouse);
        if (Math.Abs(d.Y) < 1e-5f) return null;
        float t = (y - o.Y) / d.Y;
        return t < 0 ? null : o + d * t;
    }

    private void UpdateGesture(Vector2 mouse)
    {
        var g = _gesture;
        var u = Selected;
        if (g == null || u == null) return;
        if (!g.Moved && mouse.DistanceTo(g.StartMouse) < Settings.GestureDeadZone) return;
        g.Moved = true;
        var p = PlanePoint(mouse, g.Anchor);
        if (p == null) return;
        var center = Render.Board.Center(g.Anchor, p.Value.Y);
        float hexes = new Vector2(p.Value.X - center.X, p.Value.Z - center.Z).Length() / Mathf.Sqrt(3);
        float threshold = (float)Settings.RouteThreshold;
        if (g.Aim)
        {
            if (hexes > 0.05f) g.Yaw = FacingArrow.YawTowards(center, p.Value);
            return;
        }
        if (g.NodeIndex == -1 && !u.Flags.BlocksMovement)
        {
            if (!g.RouteMode && hexes > threshold)
            {
                g.RouteMode = true;
                PlanningPause("выбор точки маршрута", s => s.PauseOnTargeting);
            }
            else if (g.RouteMode && hexes < threshold * 0.7f)
            {
                g.RouteMode = false;
            }
        }
        if (hexes > 0.05f)
        {
            g.Yaw = FacingArrow.YawTowards(center, p.Value);
            g.Dir = FacingArrow.Snap(g.Yaw);
        }
        // The figure turns with the arrow when the arrow is at the unit (preview only).
        var view = Board.ViewOf(u);
        bool atUnit = g.NodeIndex == -1 && g.Anchor == (u.StepTo ?? u.Pos);
        view.PreviewYaw = !g.RouteMode && atUnit && CanTurn(u) ? Render.Board.YawOf(g.Dir) : null;
    }

    private void CancelGesture()
    {
        if (_gesture == null) return;
        _gesture = null;
        if (Selected != null) Board.ViewOf(Selected).PreviewYaw = null;
    }

    private void EndGesture()
    {
        var g = _gesture;
        var u = Selected;
        CancelGesture();
        if (g == null || u == null || !g.Moved) return;
        if (g.Aim)
        {
            // The arrow was pulled to the target: the same as a click on it.
            if (Mode is InputMode.Skill or InputMode.Attack) ClickSkill();
            return;
        }
        if (g.RouteMode)
        {
            CommitRoute(u, g);
            return;
        }
        if (!CanTurn(u))
        {
            _hud.ShowBanner($"{u.Name} не может поворачиваться", Ui.Bad);
            return;
        }
        if (g.Dir == g.AnchorFacing) return;
        // Turning: a route node keeps its facing after arrival; the plan's end gets the facing of its last move or a turn order.
        if (g.NodeIndex >= 0)
        {
            if (Sim.SetArrivalFacing(u, g.CommandId, g.Dir)) ConfirmFacing(u, g.Dir);
            return;
        }
        var last = _proj.Count > 0 ? _proj[^1].Command : null;
        if (last != null && last.Type == CommandType.Move && Sim.SetArrivalFacing(u, last.Id, g.Dir))
        {
            ConfirmFacing(u, g.Dir);
            return;
        }
        int keep = InsertIndex;
        InsertIndex = -1;
        SubmitAppend(Command.Turn(g.Dir));
        InsertIndex = keep;
    }

    private void ConfirmFacing(Unit u, HexDirection d)
    {
        _projKey = null;
        _hud.OnQueueChanged();
        OnOrderConfirmed();
        _hud.ShowBanner($"{u.Name}: взгляд после прибытия — {d.RuName()}", Ui.TextDim, 1.5f);
    }

    /// <summary>Gesture released in route mode: a move to the hex, an attack on a visible enemy, or an explanation.</summary>
    private void CommitRoute(Unit u, Gesture g)
    {
        if (_hover == null || _hover.Coord == g.Anchor) return;
        if (_hoverUnit != null && _hoverUnit != u && Sim.IsHostile(u, _hoverUnit) && UnitVisible(_hoverUnit))
        {
            OnEnemyPointed(u, _hoverUnit);
            return;
        }
        var path = _gesturePath;
        if (path == null || !path.Found)
        {
            string why = path?.Reason ?? "гекс недоступен";
            _hud.ShowBanner("Маршрут невозможен: " + why + (path?.Nearest is { } n ? $" — ближайшая доступная клетка {n} отмечена" : ""), Ui.Bad);
            return;
        }
        SubmitAppend(PlayerMove(_hover.Coord));
    }

    /// <summary>The player pointed at an enemy hex: the skill menu against it — an attack order, never a move onto the enemy (§42).</summary>
    private void OnEnemyPointed(Unit u, Unit enemy) => OpenSkillMenu(enemy, _mouse);

    // ------------------------------------------------------------------ radial skill menu (§42)

    /// <summary>
    /// Opens the skill menu (radial or list, per settings) for the selected unit. With a target every skill is
    /// checked against it (now / after approaching / impossible with the reason); without one a choice starts aiming.
    /// </summary>
    public void OpenSkillMenu(Unit? target, Vector2 at)
    {
        var u = Selected;
        if (u == null || !Sim.CanPlayerCommand(u)) return;
        PlanningPause("меню навыков", s => s.PauseOnSkillMenu);
        var (start, facing) = PlanStart();
        var actions = Sim.Planner.Actions(u);
        RadialItem Item(SkillDef s)
        {
            var a = actions.FirstOrDefault(x => x.Skill == s);
            bool enabled = a?.Enabled ?? true;
            string reason = a?.Reason ?? "";
            bool approach = false;
            if (enabled && target != null)
            {
                var chk = Sim.Planner.CheckAgainst(u, s, target, start, facing);
                // AP, resources, cooldown: the order waits by the queue rules (as part 1), so the skill stays available.
                enabled = chk.Now || chk.AfterApproach;
                approach = chk.AfterApproach && !chk.Now && chk.Reason.Contains("подойдёт");
                reason = chk.Reason;
            }
            string icon = !string.IsNullOrEmpty(s.Icon) ? s.Icon : s.DisplayName.Length > 0 ? s.DisplayName[..1] : "•";
            return new RadialItem(s, s.DisplayName, icon, enabled, reason, _hud.SkillTooltip(s, u), approach);
        }
        var (defense, first, second) = Sim.Planner.RadialSkills(u, RadialMenu.FirstCapacity);
        var defend = actions.FirstOrDefault(x => x.Key == "defend");
        RadialItem centre = defense != null
            ? Item(defense) with { Approach = false }
            : new RadialItem(null, "Стойка", "▣", defend?.Enabled ?? true, defend?.Reason ?? "",
                $"Защитная стойка: уклонение +{Sim.Rules.Ap.DefendEvasionBonus:P0}, блок +{Sim.Rules.Ap.DefendBlockBonus:P0} · {Sim.Rules.Ap.DefendCost:0.#} ОД", false);
        if (defense != null && target != null) centre = centre with { Enabled = actions.FirstOrDefault(x => x.Skill == defense)?.Enabled ?? true };
        var ring1 = first.Select(Item).ToList();
        var ring2 = second.Select(Item).ToList();
        string title = target != null ? $"{u.Name} → {target.Name}" : $"{u.Name}: навык";
        void Pick(RadialItem it) => OnSkillPicked(u, it, target);
        if (Settings.RadialMenu) _hud.Radial.Open(at, title, centre, ring1, ring2, Pick);
        else _hud.ShowSkillList(at, title, new[] { centre }.Concat(ring1).Concat(ring2).ToList(), Pick);
        if (target != null && ring1.Concat(ring2).All(i => !i.Enabled))
            _hud.ShowBanner($"{target.Name}: ни один навык не применить — {ring1.Concat(ring2).FirstOrDefault()?.Reason}. Подойдите, выберите позицию с обзором или отмените (Esc)", Ui.Warn, 5);
    }

    // ------------------------------------------------------------------ automatic use and aiming from the skill icon (§43)

    private ActionInfo? _skillPress;
    private Vector2 _skillPressFrom;
    private bool _skillPressDragged;
    private bool _skillPressReleased;
    /// <summary>Mouse position from every input event (the GUI keeps the motion while a button is held).</summary>
    private Vector2 _rawMouse;

    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                _rawMouse = mm.Position;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                _rawMouse = mb.Position;
                if (!mb.Pressed && _skillPress != null) _skillPressReleased = true;
                break;
        }
    }

    /// <summary>The skill can be used without aiming (its target is chosen by its rules).</summary>
    public static bool CanAuto(SkillDef s) => s.UseMode is SkillUseMode.Auto or SkillUseMode.Both;

    private static bool NeedsNoTarget(SkillDef s) =>
        s.Targeting == TargetingMode.Self && s.Stages.All(st => st.Area.Shape is AreaShape.Single or AreaShape.Radius or AreaShape.Ring or AreaShape.Cross or AreaShape.Global);

    /// <summary>An automatic use order: the target is chosen at execution by the skill's rules.</summary>
    public Command AutoSkillCommand(Unit u, SkillDef s)
    {
        var c = u.BasicAttack == s && s.Targeting == TargetingMode.Unit ? new Command { Type = CommandType.Attack } : Command.Skill(s.Id, null);
        c.SkillId = s.Id;
        c.AutoTarget = true;
        c.Approach = true;
        return c;
    }

    /// <summary>LMB down on a skill icon: the press is tracked; dragging onto the map turns into aiming.</summary>
    public void BeginSkillPress(ActionInfo a)
    {
        if (Selected == null || a.Skill == null || !a.Enabled) return;
        _skillPress = a;
        _skillPressFrom = _rawMouse;
        _skillPressDragged = false;
        _skillPressReleased = false;
    }

    /// <summary>A click on a skill icon (no drag): automatic use when the skill allows it, otherwise aiming as before.</summary>
    public void SkillClick(ActionInfo a)
    {
        if (_skillPressDragged) return;
        _skillPress = null;
        var u = Selected;
        if (u == null || a.Skill == null) return;
        if (!a.Enabled)
        {
            Activate(a);
            return;
        }
        var s = a.Skill;
        if (CanAuto(s) && !NeedsNoTarget(s))
        {
            ExitMode();
            SubmitAppend(AutoSkillCommand(u, s));
            _hud.ShowBanner($"«{s.DisplayName}»: цель выберется автоматически ({s.AutoTarget})", Ui.TextDim, 2.5f);
            return;
        }
        Activate(a);
    }

    /// <summary>Follows a press started on a skill icon: dragged onto the map — aim; released there — confirm the target.</summary>
    private void UpdateSkillPress()
    {
        if (_skillPress == null) return;
        var mouse = _rawMouse;
        bool down = !_skillPressReleased;
        if (!_skillPressDragged && mouse.DistanceTo(_skillPressFrom) > 12 && !_hud.IsOverUi(mouse))
        {
            _skillPressDragged = true;
            EnterSkill(_skillPress.Skill!);
        }
        if (_skillPressDragged)
        {
            _mouse = mouse;
            if (!down)
            {
                _skillPress = null;
                _skillPressDragged = false;
                _forcedHover = null;
                UpdateHover();
                if (!_hud.IsOverUi(mouse) && Mode is InputMode.Skill or InputMode.Attack) ClickSkill();
            }
            return;
        }
        if (!down) _skillPress = null;
    }

    /// <summary>A skill of the menu was chosen: an order against the target, the defence, or aiming.</summary>
    private void OnSkillPicked(Unit u, RadialItem it, Unit? target)
    {
        if (Selected != u) return;
        if (it.Skill == null)
        {
            SubmitAppend(Command.Defend());
            return;
        }
        var s = it.Skill;
        // The unit's defensive skill is the «defend» order: its stance holds until another order (docs/DECISIONS_3.md §45).
        if (s == u.DefenseSkill)
        {
            SubmitAppend(Command.Defend());
            return;
        }
        if (s.Kind == SkillKind.Defense || (target == null && s.Targeting == TargetingMode.Self && s.Stages.All(st => st.Area.Shape is AreaShape.Single or AreaShape.Radius or AreaShape.Ring or AreaShape.Cross or AreaShape.Global)))
        {
            SubmitAppend(Command.Skill(s.Id, null));
            return;
        }
        if (target == null)
        {
            // From the menu without a target: «automatic» skills pick their target themselves, the others are aimed.
            if (CanAuto(s)) SubmitAppend(AutoSkillCommand(u, s));
            else EnterSkill(s);
            return;
        }
        SubmitAppend(CommandAgainst(u, s, target));
    }

    /// <summary>The order of a skill against a unit, whatever the skill's aiming (unit, cell, direction).</summary>
    public Command CommandAgainst(Unit u, SkillDef s, Unit target)
    {
        var (start, _) = PlanStart();
        Command c = u.BasicAttack == s && s.Targeting == TargetingMode.Unit
            ? Command.Attack(target.Id)
            : s.Targeting switch
            {
                TargetingMode.Unit => Command.Skill(s.Id, null, target.Id),
                TargetingMode.Cell => Command.Skill(s.Id, target.Pos),
                _ => Command.Skill(s.Id, null, -1, start.DirectionTo(target.Pos) ?? u.Facing),
            };
        c.Approach = true;
        return c;
    }

    /// <summary>Adds a command at the end of the plan (the gesture always continues the plan).</summary>
    private void SubmitAppend(Command c)
    {
        var u = Selected;
        if (u == null) return;
        var err = Sim.Enqueue(u, c, -1, false);
        _projKey = null;
        if (err != null) _hud.ShowBanner("Команда не принята: " + err, Ui.Bad);
        else OnOrderConfirmed();
        _hud.OnQueueChanged();
    }

    // ------------------------------------------------------------------ dimmed map, reachable hexes, hazards (§41)

    private PathResult? _gesturePath;
    private (HexCoord From, HexCoord To, int Ver, long Tick, RouteMode Mode)? _gesturePathKey;

    /// <summary>Muted map while choosing a point or a target: grey wash over the hex tops (figures stay coloured).</summary>
    private void DimMap(OverlayLayer o)
    {
        float a = (float)Math.Clamp(Settings.DimStrength, 0, 1);
        if (a <= 0.01f) return;
        var col = new Color(0.32f, 0.33f, 0.36f, 0.75f * a);
        foreach (var c in Sim.Map.Cells) o.FillHex(c.Coord, col, 0.995f, 0.022f);
    }

    /// <summary>Route planning preview from the plan's end: reachable hexes, hazards, the path to the hovered hex.</summary>
    private void DrawRoutePlanning(OverlayLayer o, Unit u, HexCoord start, HexDirection startFacing)
    {
        DimMap(o);
        double freeAp = Math.Max(0, _proj.Count > 0 && InsertIndex < 0 ? _proj[^1].RemainingNoRegen : u.FreeAp);
        var key = (start, Math.Round(freeAp, 1), Sim.Map.Version, Sim.Tick / 10);
        if (_reachKey != key || _reach == null)
        {
            _reach = Sim.Planner.ReachableWithAp(u, start, freeAp);
            _reachKey = key;
        }
        var hl = Ui.Parse(Settings.HighlightColor, new Color(0.37f, 0.82f, 0.42f));
        foreach (var c in _reach.Keys)
        {
            if (c == start) continue;
            o.FillHex(c, new Color(hl.R, hl.G, hl.B, 0.30f), 0.9f, 0.03f);
            o.RingHex(c, new Color(hl.R, hl.G, hl.B, 0.75f), 0.035f, 0.9f, 0.032f);
        }
        // Current hex: its own white ring.
        o.RingHex(start, new Color(1, 1, 1, 0.95f), 0.08f, 0.96f);
        if (Settings.ShowHazards) DrawHazards(o, u, start, freeAp);

        _gesturePath = null;
        if (_hover == null || _hover.Coord == start) return;
        var to = _hover.Coord;
        if (_hoverUnit != null && _hoverUnit != u && Sim.IsHostile(u, _hoverUnit) && UnitVisible(_hoverUnit))
        {
            // Enemy hex: an attack order, not a destination.
            o.RingHex(to, new Color(1f, 0.3f, 0.25f), 0.12f, 0.9f);
            _labels.Add(LabelPos(to, 0.95f), "⚔ атаковать", new Color(1f, 0.55f, 0.45f), 28);
            return;
        }
        var pk = (start, to, Sim.Map.Version, Sim.Tick / 5, Settings.RouteMode);
        if (_gesturePathKey != pk)
        {
            var probe = PlayerMove(to);
            bool future = start != u.Pos && start != (u.StepTo ?? u.Pos);
            _gesturePathCached = Sim.Planner.Route(u, probe, start, startFacing, to, ignoreUnits: future, computeNearest: true);
            _gesturePathKey = pk;
        }
        _gesturePath = _gesturePathCached;
        var path = _gesturePath;
        if (path == null) return;
        if (path.Found)
        {
            var last = start;
            var col = new Color(1f, 0.85f, 0.35f, 0.95f);
            foreach (var c in path.Path)
            {
                o.Line(last, c, col, 0.09f);
                last = c;
            }
            foreach (var c in path.Path) o.Dot(c, col, 0.08f);
            o.RingHex(to, Ui.Accent, 0.13f, 0.97f);
            double ap = path.Cost * Sim.Rules.Ap.MovePerCost;
            double secs = path.StepCosts.Sum(c => c / Math.Max(0.01, u.Stat(Stats.MoveSpeed)));
            bool shortAp = ap > freeAp + 1e-9;
            if (Settings.ShowRouteCost)
                _labels.Add(LabelPos(to, 0.85f), $"{ap:0.#} ОД · {secs:0.#} с" + (shortAp ? " ◷" : ""), shortAp ? Ui.Warn : new Color(1f, 0.92f, 0.6f), 30);
            if (_diag && Settings.ShowRouteCost)
            {
                // Admin: the cost of every step (docs/DECISIONS_3.md §49).
                for (int i = 0; i < path.Path.Count; i++)
                    _labels.Add(LabelPos(path.Path[i], 0.35f), $"{path.StepCosts[i]:0.#}", new Color(0.8f, 0.9f, 1f), 20);
            }
        }
        else
        {
            o.RingHex(to, new Color(1f, 0.25f, 0.2f, 0.95f), 0.1f);
            var b = LabelPos(to, -0.45f);
            o.Strip(b + new Vector3(-0.35f, 0, -0.35f), b + new Vector3(0.35f, 0, 0.35f), 0.1f, new Color(1f, 0.25f, 0.2f), true);
            o.Strip(b + new Vector3(0.35f, 0, -0.35f), b + new Vector3(-0.35f, 0, 0.35f), 0.1f, new Color(1f, 0.25f, 0.2f), true);
            _labels.Add(LabelPos(to, 0.9f), path.Reason, Ui.Bad, 22);
            if (path.Nearest.HasValue)
            {
                o.DashedRing(path.Nearest.Value, new Color(0.4f, 1f, 0.5f, 0.95f));
                _labels.Add(LabelPos(path.Nearest.Value, 0.7f), "ближайшая", new Color(0.5f, 1f, 0.6f), 24);
            }
        }
    }

    private PathResult? _gesturePathCached;

    /// <summary>Hazard marks around the reachable area: icon + pattern, never colour alone.</summary>
    private void DrawHazards(OverlayLayer o, Unit u, HexCoord start, double freeAp)
    {
        var key = (start, Math.Round(freeAp, 1), Sim.Map.Version, Sim.Tick / 10);
        if (_hazardKey != key || _hazards == null)
        {
            _hazardKey = key;
            _hazards = new Dictionary<HexCoord, CellHazard>();
            var threats = Sim.Planner.Threats(u);
            var cells = new HashSet<HexCoord>(_reach?.Keys ?? Enumerable.Empty<HexCoord>());
            foreach (var c in cells.ToList())
            foreach (var n in Sim.Map.InRadius(c, 1))
                cells.Add(n.Coord);
            foreach (var c in cells)
            {
                var cell = Sim.Map.Get(c);
                if (cell == null || c == start) continue;
                if (!_admin && Sim.FogOfWar && !Sim.Vision.IsCellVisibleToPlayer(c) && !(_reach?.ContainsKey(c) ?? false)) continue;
                var h = Sim.Planner.HazardsOf(u, cell, threats);
                if (h != CellHazard.None) _hazards[c] = h;
            }
        }
        // Zones (detection, auto-attacks) are wide: their pattern everywhere, their icons only on the route and under the cursor.
        var onRoute = new HashSet<HexCoord>(_gesturePathCached?.Path ?? new List<HexCoord>());
        if (_hover != null) onRoute.Add(_hover.Coord);
        const CellHazard zones = CellHazard.Detection | CellHazard.AutoAttack | CellHazard.Ally | CellHazard.Enemy;
        foreach (var (c, hz) in _hazards)
        {
            var h = hz;
            if (h.HasFlag(CellHazard.Damage)) o.HatchHex(c, new Color(1f, 0.45f, 0.15f, 0.7f));
            if (h.HasFlag(CellHazard.AutoAttack)) o.DashedRing(c, new Color(1f, 0.3f, 0.3f, 0.75f), 0.05f, 0.8f);
            else if (h.HasFlag(CellHazard.Detection)) o.DashedRing(c, new Color(0.8f, 0.55f, 1f, 0.6f), 0.04f, 0.82f);
            if (h.HasFlag(CellHazard.Ally) || h.HasFlag(CellHazard.Enemy))
                o.RingHex(c, h.HasFlag(CellHazard.Enemy) ? new Color(1f, 0.3f, 0.25f, 0.9f) : new Color(0.5f, 0.75f, 1f, 0.9f), 0.06f, 0.7f);
            string icons = HazardIcons(onRoute.Contains(c) ? h : h & ~zones);
            if (icons != "") _labels.Add(LabelPos(c, 0.25f), icons, HazardColor(h), 22);
        }
    }

    public static string HazardIcons(CellHazard h)
    {
        var s = new System.Text.StringBuilder();
        if (h.HasFlag(CellHazard.Damage)) s.Append('♨');
        if (h.HasFlag(CellHazard.Detection)) s.Append('◉');
        if (h.HasFlag(CellHazard.AutoAttack)) s.Append('⚔');
        if (h.HasFlag(CellHazard.NeedsAbility)) s.Append('✦');
        if (h.HasFlag(CellHazard.Obstacle)) s.Append('▩');
        if (h.HasFlag(CellHazard.WrongLayer)) s.Append('⇅');
        if (h.HasFlag(CellHazard.CostChanged)) s.Append('±');
        return s.ToString();
    }

    public static string HazardText(CellHazard h)
    {
        var l = new List<string>();
        if (h.HasFlag(CellHazard.Damage)) l.Add("♨ урон от эффекта клетки");
        if (h.HasFlag(CellHazard.Detection)) l.Add("◉ зона обнаружения врага");
        if (h.HasFlag(CellHazard.AutoAttack)) l.Add("⚔ зона автоатаки врага");
        if (h.HasFlag(CellHazard.NeedsAbility)) l.Add("✦ нужна особая способность (полёт, плавание)");
        if (h.HasFlag(CellHazard.Obstacle)) l.Add("▩ препятствие");
        if (h.HasFlag(CellHazard.Ally)) l.Add("● занято союзником");
        if (h.HasFlag(CellHazard.Enemy)) l.Add("● занято врагом");
        if (h.HasFlag(CellHazard.WrongLayer)) l.Add("⇅ недоступный слой");
        if (h.HasFlag(CellHazard.CostChanged)) l.Add("± изменённая стоимость");
        return string.Join("\n", l);
    }

    private static Color HazardColor(CellHazard h) =>
        h.HasFlag(CellHazard.Damage) ? new Color(1f, 0.6f, 0.3f)
        : h.HasFlag(CellHazard.AutoAttack) ? new Color(1f, 0.45f, 0.45f)
        : h.HasFlag(CellHazard.Detection) ? new Color(0.85f, 0.65f, 1f)
        : new Color(0.85f, 0.88f, 0.92f);

    /// <summary>Preview of the sector of view while turning with the arrow.</summary>
    private void DrawFacingPreview(OverlayLayer o, Unit u, Gesture g)
    {
        var p = u.Def.Perception;
        var col = new Color(1f, 0.9f, 0.5f, 0.22f);
        int range = (int)Math.Min(6, Math.Floor(u.Stat(Stats.DetectionRange) + 1e-9));
        foreach (var dir in p.FacingsOf(g.Dir))
        {
            var area = new AreaDef { Shape = AreaShape.Cone, Size = Math.Max(1, range), IncludeCenter = false };
            foreach (var ac in AreaResolver.Resolve(area, g.Anchor, dir, Sim.Map)) o.FillHex(ac.Cell, col, 0.85f, 0.03f);
        }
        _labels.Add(LabelPos(g.Anchor, 1.05f), g.Dir.RuName(), new Color(1f, 0.92f, 0.6f), 26);
    }
}
