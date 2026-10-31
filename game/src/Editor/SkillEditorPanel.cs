using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Skill editor (ТЗ 16.3): hex test field with a test unit in the centre, six direction arrows,
/// click cells to include/exclude them from the area (stored as data: canonical mask facing east),
/// side panel to assign effects to the selected cell, stage editing, skill properties, preview
/// and a test battle. Template shapes can be converted to a mask for manual editing.
/// </summary>
public partial class SkillEditorPanel : DefListPanel
{
    private SkillDef? _skill;
    private int _stage;
    private HexDirection _dir = HexDirection.E;
    private int _radius = 5;
    private HexCoord? _selected;
    private HexCanvas _canvas = null!;
    private OptionButton _stageSelect = null!;
    private TabContainer _tabs = null!;
    private VBoxContainer _cellTab = null!;
    private VBoxContainer _stageTab = null!;
    private VBoxContainer _skillTab = null!;
    private VBoxContainer _previewTab = null!;
    private Label _status = null!;
    private readonly Action<SkillDef> _test;

    public SkillEditorPanel(EditorContext ctx, Action<SkillDef> test) : base(ctx, DefCategory.Skill, "Навыки", null, NewSkill)
    {
        _test = test;
    }

    private static Def NewSkill() => new SkillDef
    {
        Name = "Новый навык",
        Targeting = TargetingMode.Direction,
        Stages = new()
        {
            new SkillStageDef
            {
                Area = new AreaDef { Shape = AreaShape.Mask, Cells = new() { new MaskCellDef { Offset = new HexCoord(1, 0) } } },
                Effects = new() { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } },
            },
        },
    };

    private SkillStageDef? Stage => _skill != null && _stage >= 0 && _stage < _skill.Stages.Count ? _skill.Stages[_stage] : null;

    protected override Control BuildDetail(Def d)
    {
        _skill = (SkillDef)d;
        if (_stage >= _skill.Stages.Count) _stage = 0;
        _selected = null;

        var root = Ui.HBox(10).Expand();
        // ---- left: field and toolbar
        var left = Ui.VBox(6).Expand(true, true);
        var bar = new HFlowContainer();
        bar.AddThemeConstantOverride("h_separation", 6);
        _stageSelect = new OptionButton { FocusMode = FocusModeEnum.None };
        _stageSelect.ItemSelected += i =>
        {
            _stage = (int)i;
            _selected = null;
            RefreshAll();
        };
        bar.AddChild(Ui.Label("Этап:", 14, Ui.TextDim));
        bar.AddChild(_stageSelect);
        bar.AddChild(Ui.Button("+ этап", AddStage, "Добавить этап (своя область, эффекты и задержка)"));
        bar.AddChild(Ui.Button("− этап", RemoveStage, "Удалить текущий этап"));
        bar.AddChild(Ui.Spacer(12));
        bar.AddChild(Ui.Label("Поле:", 14, Ui.TextDim));
        var rad = new SpinBox { MinValue = 2, MaxValue = 12, Value = _radius, CustomMinimumSize = new Vector2(70, 0) };
        rad.ValueChanged += v =>
        {
            _radius = (int)v;
            RefreshCanvas();
        };
        bar.AddChild(rad);
        bar.AddChild(Ui.Button("В маску", ConvertToMask, "Преобразовать шаблонную форму в набор клеток для ручного редактирования"));
        bar.AddChild(Ui.Button("Очистить", ClearMask, "Убрать все клетки маски"));
        left.AddChild(bar);
        _canvas = new HexCanvas();
        _canvas.CellClicked += OnCellClicked;
        _canvas.DirectionClicked += dir =>
        {
            _dir = dir;
            RefreshCanvas();
            RefreshPreview();
        };
        left.AddChild(_canvas);
        _status = Ui.Label("", 13, Ui.TextDim, wrap: true);
        left.AddChild(_status);
        left.AddChild(Ui.Label("ЛКМ по клетке — включить/выбрать, повторный ЛКМ по выбранной — выключить, ПКМ — выключить. " +
                               "Стрелки — направление навыка (маска хранится для направления «восток» и поворачивается). " +
                               "Белая точка — у клетки есть собственные эффекты.", 12, Ui.TextDim, wrap: true));
        root.AddChild(left);

        // ---- right: tabs
        _tabs = new TabContainer { CustomMinimumSize = new Vector2(430, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        _cellTab = Tab("Клетка");
        _stageTab = Tab("Этап");
        _skillTab = Tab("Навык");
        _seriesTab = Tab("Серия");
        _previewTab = Tab("Превью и тест");
        root.AddChild(_tabs);

        Callable.From(RefreshAll).CallDeferred();
        return root;
    }

    private VBoxContainer Tab(string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        var v = Ui.VBox(6).Expand();
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddChild(v);
        scroll.AddChild(pad.Expand());
        _tabs.AddChild(scroll);
        return v;
    }

    private void Changed()
    {
        if (_skill != null) OnEdited(_skill);
        RefreshCanvas();
        RefreshPreview();
        if (_tabs.CurrentTab != 3) RefreshSeriesTab();
    }

    private void RefreshAll()
    {
        if (_skill == null || !IsInstanceValid(_canvas)) return;
        _stageSelect.Clear();
        for (int i = 0; i < _skill.Stages.Count; i++)
        {
            var st = _skill.Stages[i];
            _stageSelect.AddItem($"{i + 1}{(string.IsNullOrEmpty(st.Name) ? "" : " · " + st.Name)}{(st.Delay > 0 ? $" (+{st.Delay:0.##} с)" : "")}");
        }
        if (_skill.Stages.Count > 0) _stageSelect.Selected = _stage;
        RefreshCanvas();
        RefreshCellTab();
        RefreshStageTab();
        RefreshSkillTab();
        RefreshSeriesTab();
        RefreshPreview();
    }

    // ------------------------------------------------------------------ canvas

    private bool AnchorAtCaster => _skill == null || _skill.UsesChosenDirection;

    private void RefreshCanvas()
    {
        if (_skill == null || !IsInstanceValid(_canvas)) return;
        var st = Stage;
        _canvas.Radius = _radius;
        _canvas.Orientation = _dir;
        _canvas.Cells.Clear();
        _canvas.Marked.Clear();
        _canvas.Labels.Clear();
        _canvas.SelectedCell = _selected;
        _canvas.AccentColor = Ui.Parse(_skill.Color, Ui.Accent);
        int range = Math.Max(1, _skill.RangeMax);
        _canvas.ShowCasterGhost = !AnchorAtCaster;
        _canvas.CasterCell = AnchorAtCaster ? HexCoord.Zero : _dir.Opposite().Offset() * Math.Min(range, _radius);
        var col = Ui.Parse(_skill.Color, Ui.Accent);
        if (st != null)
        {
            foreach (var (off, mask, _) in AreaResolver.CanonicalOffsets(st.Area))
            {
                var c = off.Rotate((int)_dir);
                if (c.Length > _radius) continue;
                _canvas.Cells[c] = new Color(col.R, col.G, col.B, 0.85f);
                if (mask?.Effects.Count > 0) _canvas.Marked.Add(c);
            }
            if (st.Area.Shape == AreaShape.Chain)
                for (int j = 1; j <= st.Area.ChainJumps; j++)
                {
                    var c = HexDirection.E.Offset().Rotate((int)_dir) * Math.Min(_radius, j * st.Area.ChainRange);
                    _canvas.Labels[c] = $"прыжок {j}";
                }
            if (st.Area.Shape == AreaShape.Global) _status.Text = "Глобальная область: все клетки карты";
        }
        string shape = st == null ? "нет этапов" : Names.Of(st.Area.Shape);
        _status.Text = $"Этап {_stage + 1}: форма «{shape}», клеток {_canvas.Cells.Count}, направление {_dir.RuName()}. " +
                       (AnchorAtCaster ? "Центр — исполнитель." : $"Центр — точка применения; исполнитель (полупрозрачный) на дальности {range}.");
        _canvas.QueueRedraw();
    }

    private HexCoord Canonical(HexCoord rotated) => rotated.Rotate(-(int)_dir);

    private void OnCellClicked(HexCoord c, MouseButton button)
    {
        var st = Stage;
        if (st == null) return;
        if (st.Area.Shape != AreaShape.Mask)
        {
            if (st.Area.Shape is AreaShape.Chain or AreaShape.Global)
            {
                _status.Text = "Цепь и глобальная область не редактируются по клеткам — выберите другую форму на вкладке «Этап».";
                return;
            }
            ConvertToMask();
        }
        var off = Canonical(c);
        var existing = st.Area.Cells.FirstOrDefault(m => m.Offset == off);
        if (button == MouseButton.Right)
        {
            if (existing != null) st.Area.Cells.Remove(existing);
            if (_selected == c) _selected = null;
        }
        else if (existing == null)
        {
            st.Area.Cells.Add(new MaskCellDef { Offset = off });
            _selected = c;
        }
        else if (_selected == c)
        {
            st.Area.Cells.Remove(existing);
            _selected = null;
        }
        else
        {
            _selected = c;
        }
        Changed();
        RefreshCellTab();
        _tabs.CurrentTab = 0;
    }

    private void ConvertToMask()
    {
        var st = Stage;
        if (st == null || st.Area.Shape == AreaShape.Mask) return;
        if (st.Area.Shape is AreaShape.Chain or AreaShape.Global) return;
        var cells = AreaResolver.CanonicalOffsets(st.Area).Select(x => new MaskCellDef { Offset = x.Offset }).ToList();
        string was = Names.Of(st.Area.Shape);
        st.Area.Shape = AreaShape.Mask;
        st.Area.Cells = cells;
        Changed();
        RefreshStageTab();
        _status.Text = $"Форма «{was}» преобразована в маску из {cells.Count} клеток — теперь клетки можно включать и выключать.";
    }

    private void ClearMask()
    {
        var st = Stage;
        if (st == null) return;
        if (st.Area.Shape != AreaShape.Mask) st.Area.Shape = AreaShape.Mask;
        st.Area.Cells.Clear();
        _selected = null;
        Changed();
        RefreshCellTab();
        RefreshStageTab();
    }

    private void AddStage()
    {
        if (_skill == null) return;
        _skill.Stages.Add(new SkillStageDef { Name = "Этап " + (_skill.Stages.Count + 1), Delay = 0.5, Area = new AreaDef { Shape = AreaShape.Single } });
        _stage = _skill.Stages.Count - 1;
        OnEdited(_skill);
        RefreshAll();
    }

    private void RemoveStage()
    {
        if (_skill == null || _skill.Stages.Count == 0) return;
        _skill.Stages.RemoveAt(_stage);
        _stage = Math.Max(0, _stage - 1);
        OnEdited(_skill);
        RefreshAll();
    }

    // ------------------------------------------------------------------ side panel

    private static readonly (EffectType Type, string Label)[] QuickEffects =
    {
        (EffectType.Damage, "Урон"), (EffectType.Heal, "Лечение"), (EffectType.ApplyStatus, "Состояние"), (EffectType.Push, "Толчок"),
        (EffectType.Pull, "Притяжение"), (EffectType.ChangeFacing, "Поворот цели"), (EffectType.ModifyResource, "Ресурс"), (EffectType.ModifyAp, "ОД"),
        (EffectType.AddCellEffect, "Эффект клетки"), (EffectType.ChangeTerrain, "Местность"), (EffectType.DestroyObstacle, "Разрушение"), (EffectType.Summon, "Призыв"),
    };

    private SkillEffectDef MakeEffect(EffectType t)
    {
        var e = new SkillEffectDef { Type = t };
        switch (t)
        {
            case EffectType.Heal:
                e.Filter = TargetFilterDef.AlliesAndSelf();
                e.RequiresHit = false;
                e.Amount = 15;
                break;
            case EffectType.ApplyStatus:
                e.Status = Ctx.Db.Ids(DefCategory.Status).FirstOrDefault() ?? "";
                e.Amount = 1;
                break;
            case EffectType.AddCellEffect:
                e.CellEffect = Ctx.Db.Ids(DefCategory.CellEffect).FirstOrDefault() ?? "";
                break;
            case EffectType.ChangeTerrain:
                e.Terrain = Ctx.Db.Ids(DefCategory.Terrain).FirstOrDefault() ?? "";
                break;
            case EffectType.Summon:
                e.SummonUnit = Ctx.Db.Ids(DefCategory.Unit).FirstOrDefault() ?? "";
                e.Duration = 20;
                break;
            case EffectType.ModifyResource:
                e.Resource = Ctx.Db.Ids(DefCategory.Resource).FirstOrDefault() ?? "";
                e.Amount = -10;
                break;
            case EffectType.ModifyAp:
                e.Amount = -2;
                break;
        }
        return e;
    }

    private void RefreshCellTab()
    {
        if (!IsInstanceValid(_cellTab)) return;
        Ui.ClearChildren(_cellTab);
        var st = Stage;
        if (st == null)
        {
            _cellTab.AddChild(Ui.Label("Добавьте этап.", 14, Ui.TextDim));
            return;
        }
        if (_selected == null)
        {
            _cellTab.AddChild(Ui.Label("Выберите клетку на поле, чтобы назначить ей эффекты.", 14, Ui.TextDim, wrap: true));
        }
        else if (st.Area.Shape == AreaShape.Mask)
        {
            var off = Canonical(_selected.Value);
            var mc = st.Area.Cells.FirstOrDefault(m => m.Offset == off);
            if (mc == null)
            {
                _cellTab.AddChild(Ui.Label("Клетка не входит в область.", 14, Ui.TextDim));
            }
            else
            {
                _cellTab.AddChild(Ui.Label($"Клетка {_selected.Value} (в маске: {off}, при направлении «восток»)", 15, Ui.Accent, wrap: true));
                _cellTab.AddChild(Ui.Label("Эффекты только этой клетки (дополнительно к общим эффектам этапа):", 13, Ui.TextDim, wrap: true));
                var quick = new HFlowContainer();
                foreach (var (t, label) in QuickEffects)
                {
                    var tt = t;
                    quick.AddChild(Ui.Button("+ " + label, () =>
                    {
                        mc.Effects.Add(MakeEffect(tt));
                        Changed();
                        RefreshCellTab();
                    }));
                }
                _cellTab.AddChild(quick);
                var grid = new PropertyGrid { LabelWidth = 170 };
                grid.SetTarget(mc, Ctx.Db);
                grid.Changed += Changed;
                _cellTab.AddChild(grid);
            }
        }
        _cellTab.AddChild(Ui.Sep());
        _cellTab.AddChild(Ui.Label("Общие эффекты этапа (все клетки области) — редактируются на вкладке «Этап»:", 13, Ui.TextDim, wrap: true));
        if (st.Effects.Count == 0) _cellTab.AddChild(Ui.Label("нет", 13, Ui.TextDim));
        for (int i = 0; i < st.Effects.Count; i++) _cellTab.AddChild(Ui.Label($"{i + 1}. {st.Effects[i].Describe()} — {st.Effects[i].Filter}", 13, Ui.Text, wrap: true));
        var quickStage = new HFlowContainer();
        foreach (var (t, label) in QuickEffects)
        {
            var tt = t;
            quickStage.AddChild(Ui.Button("+ " + label, () =>
            {
                st.Effects.Add(MakeEffect(tt));
                Changed();
                RefreshCellTab();
                RefreshStageTab();
            }, "Добавить общий эффект этапа"));
        }
        _cellTab.AddChild(quickStage);
    }

    private void RefreshStageTab()
    {
        if (!IsInstanceValid(_stageTab)) return;
        Ui.ClearChildren(_stageTab);
        var st = Stage;
        if (st == null) return;
        _stageTab.AddChild(Ui.Label("Порядок применения внутри этапа: урон/лечение → состояния → перемещение/направление/ресурсы → " +
                                    "прерывания → изменения клеток → призыв → реакции. Внутри группы — по порядку в списке (↑/↓).", 12, Ui.TextDim, wrap: true));
        var grid = new PropertyGrid { LabelWidth = 190 };
        grid.SetTarget(st, Ctx.Db);
        grid.Changed += () =>
        {
            Changed();
            RefreshCellTab();
        };
        grid.PropertyChanged += (o, p) =>
        {
            if (p is nameof(AreaDef.Shape) or nameof(SkillStageDef.Name) or nameof(SkillStageDef.Delay)) Callable.From(RefreshAll).CallDeferred();
        };
        _stageTab.AddChild(grid);
    }

    private void RefreshSkillTab()
    {
        if (!IsInstanceValid(_skillTab) || _skill == null) return;
        Ui.ClearChildren(_skillTab);
        var grid = new PropertyGrid { LabelWidth = 200 };
        grid.Skip.Add(nameof(SkillDef.Stages));
        grid.SetTarget(_skill, Ctx.Db);
        grid.Changed += Changed;
        grid.PropertyChanged += (o, p) =>
        {
            if (p is nameof(SkillDef.Targeting) or nameof(SkillDef.RangeMax)) RefreshCanvas();
        };
        _skillTab.AddChild(grid);
    }

    private void RefreshPreview()
    {
        if (!IsInstanceValid(_previewTab) || _skill == null) return;
        Ui.ClearChildren(_previewTab);
        var s = _skill;
        var lines = new List<string>
        {
            $"«{s.DisplayName}» — {Names.Of(s.Kind)}, наведение: {Names.Of(s.Targeting)}",
            $"Стоимость: {s.ApCost:0.#} ОД" + string.Concat(s.ResourceCosts.Select(r => $", {r.Resource} {r.Amount:0.#}")),
            s.Targeting is TargetingMode.Cell or TargetingMode.Unit ? $"Дальность: {s.RangeMin}–{s.RangeMax}{(s.Ranged ? " (+ бонус высоты)" : "")}, линия видимости: {(s.RequiresLoS ? "нужна" : "не нужна")}" : "Дальность: от исполнителя",
            $"Подготовка {s.PrepTime:0.##} с · восстановление {s.RecoveryTime:0.##} с · перезарядка {s.Cooldown:0.#} с",
            $"Прерывание: уроном {(s.Interrupt.ByDamage ? $"{s.Interrupt.ChanceOnDamage:P0}" : "нет")}, контролем {(s.Interrupt.ByControl ? "да" : "нет")}, возврат {s.Interrupt.Refund:P0}",
            "",
            $"Направление превью: {_dir.RuName()}",
        };
        for (int i = 0; i < s.Stages.Count; i++)
        {
            var st = s.Stages[i];
            var cells = AreaResolver.CanonicalOffsets(st.Area);
            lines.Add($"Этап {i + 1}{(st.Delay > 0 ? $" (через {st.Delay:0.##} с)" : "")}: {Names.Of(st.Area.Shape)}, клеток {cells.Count}" + (st.MaxTargets > 0 ? $", макс. целей {st.MaxTargets}" : ""));
            foreach (var e in st.Effects) lines.Add($"   • все клетки: {e.Describe()} → {e.Filter}{(e.Chance < 1 ? $", шанс {e.Chance:P0}" : "")}");
            foreach (var (off, mask, _) in cells)
                if (mask != null && mask.Effects.Count > 0)
                    lines.Add($"   • клетка {off.Rotate((int)_dir)}: " + string.Join("; ", mask.Effects.Select(e => e.Describe())));
        }
        _previewTab.AddChild(Ui.Label(string.Join("\n", lines), 13, Ui.Text, wrap: true));
        // ТЗ ч. 6, 19.5: who the skill reaches and why — the filter of each effect and the rule of friendly fire in force.
        var ffKind = s.Targeting == TargetingMode.Unit && s.Stages.All(st => AreaResolver.CanonicalOffsets(st.Area).Count <= 1) ? FfKind.Direct : FfKind.Area;
        var ff = FriendlyFireSystem.Preview(Ctx.Db, null, ffKind, s.Stages.SelectMany(st => st.Effects).ToList(), (s.FriendlyFire, "навык"));
        var ffLabel = Ui.Label("Кого заденет:\n   " + string.Join("\n   ", ff), 13, Ui.TextDim, wrap: true);
        ffLabel.Name = "FriendlyFirePreview";
        _previewTab.AddChild(ffLabel);
        var issues = Ctx.IssuesOf(s).ToList();
        if (Ctx.Db.DisabledSkills.TryGetValue(s.Id, out var why))
            _previewTab.AddChild(Ui.Label("✗ Навык отключён до исправления ошибок: " + why, 13, Ui.Bad, wrap: true));
        foreach (var i in issues)
            _previewTab.AddChild(Ui.Label($"{(i.Severity == IssueSeverity.Error ? "✗" : "⚠")} {i.Message}", 13, i.Severity == IssueSeverity.Error ? Ui.Bad : Ui.Warn, wrap: true));
        if (issues.Count == 0) _previewTab.AddChild(Ui.Label("✓ Ошибок нет", 13, Ui.Good));
        _previewTab.AddChild(Ui.Button("▶ Тест в бою (полигон, черновик)", () =>
        {
            Ctx.Validate();
            if (Ctx.Db.DisabledSkills.ContainsKey(s.Id))
            {
                Dialogs.Info(this, "Навык с ошибками", "Исправьте ошибки навыка перед тестом — неработающий навык не запускается.");
                return;
            }
            _test(s);
        }).MinSize(0, 40));
        _previewTab.AddChild(Ui.Label("Тест запускает песочницу с вашим навыком у тестового юнита. Опубликованный контент не меняется.", 12, Ui.TextDim, wrap: true));
    }
}
