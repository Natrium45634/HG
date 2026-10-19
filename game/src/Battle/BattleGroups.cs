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
/// Group control (docs/DECISIONS_3.md §44): Shift+click and box selection, saved groups (Ctrl+digit —
/// save, Alt+digit — select), the group command panel with order parameters, and the preview of the
/// individual end positions before the order is given.
/// </summary>
public partial class BattleScreen
{
    private readonly List<Unit> _group = new();
    private readonly Dictionary<int, List<int>> _savedGroups = new();
    private Vector2? _boxFrom;
    private bool _boxing;
    private GroupAssignment? _groupPreview;
    private (HexCoord Cell, int Target, GroupCommand Cmd, long Tick)? _groupPreviewKey;

    /// <summary>The selected group (two or more units); empty — single selection.</summary>
    public IReadOnlyList<Unit> Group => _group;
    public bool GroupActive => _group.Count >= 2;
    /// <summary>Parameters of the next group order (formation, queue mode, behaviour after the target dies…).</summary>
    public GroupOrder GroupTemplate { get; } = new() { Formation = "free" };
    /// <summary>A group command waiting for its target (point, unit or direction).</summary>
    public GroupCommand? GroupMode { get; private set; }
    public IReadOnlyDictionary<int, List<int>> SavedGroups => _savedGroups;
    public Rect2? BoxRect => _boxing && _boxFrom.HasValue ? new Rect2(_boxFrom.Value, _mouse - _boxFrom.Value).Abs() : null;
    public GroupOrderResult? LastGroupResult { get; private set; }

    public void SetGroup(IEnumerable<Unit> units)
    {
        var list = units.Where(u => u.Alive && Sim.CanPlayerCommand(u)).Distinct().OrderBy(u => u.Id).ToList();
        foreach (var v in Board.Views.Values) v.Selected = false;
        _group.Clear();
        GroupMode = null;
        if (list.Count == 0)
        {
            Select(null);
            return;
        }
        var primary = Selected != null && list.Contains(Selected) ? Selected : list[0];
        if (primary != Selected) Select(primary);
        if (list.Count >= 2) _group.AddRange(list);
        foreach (var u in list) Board.ViewOf(u).Selected = true;
        _hud.OnSelectionChanged();
    }

    /// <summary>Shift+click: adds or removes a unit of the group.</summary>
    public void ToggleInGroup(Unit u)
    {
        var current = GroupActive ? _group.ToList() : Selected != null ? new List<Unit> { Selected } : new List<Unit>();
        if (current.Contains(u)) current.Remove(u);
        else current.Add(u);
        SetGroup(current);
    }

    private void ClearGroup()
    {
        if (_group.Count == 0 && GroupMode == null) return;
        foreach (var u in _group)
            if (u != Selected && Board.Views.TryGetValue(u.Id, out var v))
                v.Selected = false;
        _group.Clear();
        GroupMode = null;
        _hud?.OnSelectionChanged();
    }

    // ------------------------------------------------------------------ saved groups

    public void SaveGroup(int slot)
    {
        var units = GroupActive ? _group.ToList() : Selected != null ? new List<Unit> { Selected } : new();
        if (units.Count == 0) return;
        _savedGroups[slot] = units.Select(u => u.Id).ToList();
        _hud.ShowBanner($"Группа {slot}: {units.Count} юн.", Ui.TextDim, 1.5f);
        _hud.OnSelectionChanged();
    }

    public void RecallGroup(int slot)
    {
        if (!_savedGroups.TryGetValue(slot, out var ids)) return;
        var units = ids.Select(Sim.GetUnit).Where(u => u != null && u.Alive).Cast<Unit>().ToList();
        if (units.Count == 0) return;
        SetGroup(units);
        PlanningPause($"группа {slot}", s => s.PauseOnSelect);
    }

    // ------------------------------------------------------------------ box selection

    /// <summary>LMB on empty ground with nothing else to do: a selection box may start.</summary>
    private void BeginBox(Vector2 at)
    {
        _boxFrom = at;
        _boxing = false;
    }

    private void UpdateBox(Vector2 at)
    {
        if (_boxFrom is { } from && !_boxing && from.DistanceTo(at) > 10) _boxing = true;
    }

    private bool EndBox(Vector2 at)
    {
        var from = _boxFrom;
        bool was = _boxing;
        _boxFrom = null;
        _boxing = false;
        if (!was || from == null) return false;
        var rect = new Rect2(from.Value, at - from.Value).Abs();
        var cam = Board.Rig.Camera;
        var picked = Sim.Units.Where(u => u.Alive && Sim.CanPlayerCommand(u) && UnitVisible(u) && rect.HasPoint(cam.UnprojectPosition(Board.UnitPos(u) + new Vector3(0, 0.3f, 0)))).ToList();
        if (picked.Count == 0) return true;
        SetGroup(picked);
        PlanningPause("выбрана группа", s => s.PauseOnSelect);
        return true;
    }

    // ------------------------------------------------------------------ group commands

    /// <summary>A button of the group panel: immediate commands are given at once, the others wait for a target.</summary>
    public void StartGroupCommand(GroupCommand c)
    {
        if (!GroupActive) return;
        switch (c)
        {
            case GroupCommand.HoldPosition:
            case GroupCommand.Disperse:
            case GroupCommand.StopAfterCurrent:
            case GroupCommand.CancelQueue:
            case GroupCommand.ClearQueue:
            case GroupCommand.AutoAttackPriority:
                IssueGroup(c, null, null, null);
                return;
        }
        GroupMode = c;
        ExitMode();
        PlanningPause("выбор цели группы", s => s.PauseOnTargeting);
        _hint = c switch
        {
            GroupCommand.Attack or GroupCommand.Surround => "Группа: ЛКМ по врагу",
            GroupCommand.Follow => "Группа: ЛКМ по союзнику, за которым следовать",
            GroupCommand.Face => "Группа: ЛКМ по гексу в нужном направлении",
            GroupCommand.Skill => "Группа: ЛКМ по врагу или гексу (навык — из параметров)",
            _ => "Группа: ЛКМ по точке назначения — гексы распределятся автоматически",
        };
        _hud.OnModeChanged();
    }

    public void CancelGroupMode()
    {
        GroupMode = null;
        _hint = "";
        _groupPreview = null;
        _hud.OnModeChanged();
    }

    /// <summary>Gives the group order with the template parameters.</summary>
    public GroupOrderResult? IssueGroup(GroupCommand c, HexCoord? point, Unit? target, HexDirection? facing)
    {
        if (!GroupActive) return null;
        var t = GroupTemplate;
        var o = new GroupOrder
        {
            Command = c, Point = point, TargetUnitId = target?.Id ?? -1, Facing = facing ?? t.Facing, IndividualFacing = t.IndividualFacing,
            Formation = c == GroupCommand.Gather && t.Formation == "free" ? "compact" : t.Formation, QueueMode = t.QueueMode, KeepIndividual = t.KeepIndividual,
            ExcludeTags = t.ExcludeTags.ToList(), AllowAutoSkills = t.AllowAutoSkills, OnTargetDeath = t.OnTargetDeath, MaxSpread = t.MaxSpread,
            WhenNoSpace = t.WhenNoSpace, Routing = Settings.RouteMode, AutoPriority = t.AutoPriority, SkillId = t.SkillId,
        };
        var r = Sim.IssueGroupOrder(_group, o);
        LastGroupResult = r;
        GroupMode = null;
        _hint = "";
        _projKey = null;
        _hud.OnQueueChanged();
        _hud.OnModeChanged();
        _hud.ShowBanner(r.Message + (r.Partial ? " — " + string.Join("; ", r.Skipped.Take(3).Select(s => $"{Sim.GetUnit(s.Unit)?.Name}: {s.Reason}")) : ""),
            r.Partial ? Ui.Warn : Ui.TextDim, r.Partial ? 5 : 2);
        if (r.Ordered.Count > 0) OnOrderConfirmed();
        return r;
    }

    /// <summary>LMB while a group command waits for its target.</summary>
    private void GroupClick()
    {
        if (GroupMode is not { } c || _hover == null) return;
        var enemy = _hoverUnit != null && _group.Count > 0 && Sim.IsHostile(_group[0], _hoverUnit) && UnitVisible(_hoverUnit) ? _hoverUnit : null;
        switch (c)
        {
            case GroupCommand.Attack:
            case GroupCommand.Surround:
                if (enemy == null)
                {
                    _hud.ShowBanner("Укажите врага", Ui.Warn, 2);
                    return;
                }
                IssueGroup(c, _hover.Coord, enemy, null);
                break;
            case GroupCommand.Follow:
                if (_hoverUnit == null || enemy != null)
                {
                    _hud.ShowBanner("Укажите союзника", Ui.Warn, 2);
                    return;
                }
                IssueGroup(c, null, _hoverUnit, null);
                break;
            case GroupCommand.Face:
            {
                var centre = GroupCentre();
                var d = centre.DirectionTo(_hover.Coord) ?? FacingArrow.Snap(FacingArrow.YawTowards(Render.Board.Center(centre), Render.Board.Center(_hover.Coord)));
                IssueGroup(c, null, null, d);
                break;
            }
            case GroupCommand.Skill:
                IssueGroup(c, _hover.Coord, enemy, null);
                break;
            default:
                // An enemy hex is an attack for the whole group, never a destination.
                if (enemy != null) IssueGroup(GroupCommand.Attack, _hover.Coord, enemy, null);
                else IssueGroup(c, _hover.Coord, null, null);
                break;
        }
    }

    private HexCoord GroupCentre()
    {
        double q = _group.Average(u => (double)u.Pos.Q), r = _group.Average(u => (double)u.Pos.R);
        return new HexCoord((int)Math.Round(q), (int)Math.Round(r));
    }

    /// <summary>Preview of the end positions of the group under the cursor (numbered rings and lines from the units).</summary>
    private void DrawGroupPreview(OverlayLayer o)
    {
        foreach (var u in _group)
        {
            var p = Board.UnitPos(u);
            o.Circle(p + new Vector3(0, 0.03f, 0), 0.62f, 0.05f, new Color(0.5f, 1f, 0.7f, 0.85f));
        }
        if (GroupMode is not { } c || _hover == null) return;
        var enemy = _hoverUnit != null && Sim.IsHostile(_group[0], _hoverUnit) && UnitVisible(_hoverUnit) ? _hoverUnit : null;
        bool attack = c is GroupCommand.Attack or GroupCommand.Surround || (enemy != null && c is GroupCommand.Move or GroupCommand.Gather or GroupCommand.DefendPosition);
        if (c is GroupCommand.Follow or GroupCommand.Face or GroupCommand.Skill) return;
        DimMap(o);
        var key = (_hover.Coord, enemy?.Id ?? -1, c, Sim.Tick / 10);
        if (_groupPreviewKey != key)
        {
            _groupPreviewKey = key;
            var formation = Sim.Content.Get<FormationDef>(c == GroupCommand.Gather && GroupTemplate.Formation == "free" ? "compact" : GroupTemplate.Formation);
            _groupPreview = attack && enemy != null
                ? (c == GroupCommand.Surround
                    ? Sim.Groups.AssignMove(_group, enemy.Pos, new FormationDef { Kind = FormationKind.Surround, Radius = 1 }, 0, enemy.Pos)
                    : Sim.Groups.AssignAttack(_group.Where(u => Sim.Groups.RoleOf(u) != UnitRole.Support).ToList(), enemy,
                        u => Sim.GroupOrders.AttackSkillOf(u, enemy, new GroupOrder { AllowAutoSkills = GroupTemplate.AllowAutoSkills })))
                : Sim.Groups.AssignMove(_group, _hover.Coord, formation, GroupTemplate.MaxSpread);
        }
        var a = _groupPreview;
        if (a == null) return;
        if (enemy != null) o.RingHex(enemy.Pos, new Color(1f, 0.3f, 0.25f), 0.12f, 0.9f);
        else o.RingHex(_hover.Coord, Ui.Accent, 0.1f, 0.97f);
        int i = 0;
        foreach (var u in _group)
        {
            i++;
            if (!a.Cells.TryGetValue(u.Id, out var cell))
            {
                _labels.Add(Board.UnitPos(u) + new Vector3(0, 1.6f, 0), "нет места", Ui.Warn, 22);
                continue;
            }
            var col = new Color(0.5f, 1f, 0.7f, 0.9f);
            o.FillHex(cell, new Color(col.R, col.G, col.B, 0.28f), 0.85f, 0.035f);
            o.RingHex(cell, col, 0.06f, 0.85f);
            o.Line(u.Pos, cell, new Color(col.R, col.G, col.B, 0.5f), 0.035f);
            _labels.Add(LabelPos(cell, 0.5f), i.ToString(), Colors.White, 26);
            if (a.Facings.TryGetValue(u.Id, out var f) && (GroupTemplate.Facing.HasValue || enemy != null)) o.Arrow(cell, GroupTemplate.Facing ?? f, new Color(0.5f, 1f, 0.7f), 0.55f);
        }
        if (a.Partial) _labels.Add(LabelPos(_hover.Coord, 1.0f), a.Note, Ui.Warn, 24);
    }
}
