using System;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Призывы» (ТЗ ч. 6, 20.6; docs/DECISIONS_6.md §106): the fields of a description, its problems, a summary — who, where, how long,
/// what at the end, the AI, the limits — and the preview of the hexes of the placement around a caster: numbered in the order they
/// are taken, the first «количество» bright; the arrows turn the caster. Below — who summons by it (skills, passives, scenarios).
/// </summary>
public partial class SummonPanel : DefListPanel
{
    public SummonPanel(EditorContext ctx) : base(ctx, DefCategory.Summon, "Призывы суб-юнитов: кто, где, сколько живёт, лимиты и простой ИИ", null,
        () => new SummonDef { Name = "Новый призыв", Unit = "wolf" })
    {
    }

    /// <summary>The preview and the summary shown now (UI tests).</summary>
    public HexCanvas? Preview { get; private set; }
    public Label? Summary { get; private set; }

    protected override Control BuildDetail(Def d)
    {
        var def = (SummonDef)d;
        var box = Ui.VBox(6);
        var problems = Ui.VBox(2);
        problems.Name = "SummonProblems";
        var summary = Ui.Label("", 13, Ui.Text, wrap: true);
        summary.Name = "SummonSummary";
        Summary = summary;
        var canvas = new HexCanvas { Name = "SummonPreview", Radius = 4, ShowCasterGhost = true, CustomMinimumSize = new Vector2(0, 320) };
        Preview = canvas;
        var facing = HexDirection.E;
        var users = Ui.Label("", 12, Ui.TextDim, wrap: true);
        users.Name = "SummonUsers";
        void Refresh()
        {
            if (!IsInstanceValid(problems)) return;
            Ui.ClearChildren(problems);
            var list = SummonChecks.Check(Ctx.Db, def);
            problems.AddChild(Ui.Label(list.Count == 0 ? "✓ Призыв без ошибок" : $"Ошибок: {list.Count(x => x.Error)}, предупреждений: {list.Count(x => !x.Error)}", 13,
                list.Count == 0 ? Ui.Good : list.Any(x => x.Error) ? Ui.Bad : Ui.Warn));
            foreach (var x in list) problems.AddChild(Ui.Label((x.Error ? "⛔ " : "⚠ ") + x.Message, 12, x.Error ? Ui.Bad : Ui.Warn, wrap: true));
            summary.Text = SummonChecks.Summary(Ctx.Db, def);
            // The hexes of the placement: the caster at the centre, the hex of the skill three steps ahead.
            var anchor = new HexCoord(3, 0).Rotate((int)facing);
            var cells = SummonSystem.PatternOf(def, HexCoord.Zero, facing, anchor, anchor);
            canvas.Cells.Clear();
            canvas.Labels.Clear();
            canvas.Marked.Clear();
            canvas.Orientation = facing;
            canvas.Radius = Math.Clamp(cells.Select(c => HexCoord.Distance(HexCoord.Zero, c)).DefaultIfEmpty(1).Max() + 1, 3, 8);
            for (int i = 0; i < cells.Count; i++)
            {
                canvas.Cells[cells[i]] = i < def.Count ? new Color(0.45f, 0.85f, 0.55f) : new Color(0.3f, 0.45f, 0.35f);
                canvas.Labels[cells[i]] = (i + 1).ToString();
            }
            if (def.Placement is SummonPlacement.SelectedHex or SummonPlacement.AtTarget) canvas.Marked.Add(anchor);
            canvas.QueueRedraw();
            var skills = Ctx.Db.All<SkillDef>().Where(s => s.Stages.SelectMany(st => st.Effects).Any(e => e.Summon == def.Id)).ToList();
            var holders = Ctx.Db.All<UnitDef>().Where(u => u.Skills.Any(id => skills.Any(s => s.Id == id))).Select(u => u.DisplayName).ToList();
            var passives = Ctx.Db.All<PassiveDef>().Where(p => p.Effects.Any(e => e.Summon == def.Id)).Select(p => p.DisplayName).ToList();
            var scenarios = Ctx.Db.All<ScenarioDef>().Where(sc => sc.Triggers.Any(t => t.Actions.Any(a => a.Summon == def.Id))).Select(sc => sc.DisplayName).ToList();
            users.Text = skills.Count + passives.Count + scenarios.Count == 0
                ? "Никто не призывает по этому описанию: эффект «Призвать юнита» навыка или пассива, поле «Призыв»; действие сценария «Призвать юнитов»."
                : "Призывают: " + string.Join("; ", new[]
                {
                    skills.Count > 0 ? "навыки " + string.Join(", ", skills.Select(s => $"«{s.DisplayName}»")) + (holders.Count > 0 ? $" (у {string.Join(", ", holders)})" : "") : "",
                    passives.Count > 0 ? "пассивы " + string.Join(", ", passives.Select(p => $"«{p}»")) : "",
                    scenarios.Count > 0 ? "сценарии " + string.Join(", ", scenarios.Select(s => $"«{s}»")) : "",
                }.Where(x => x != ""));
        }
        canvas.DirectionClicked += dir =>
        {
            facing = dir;
            Refresh();
        };
        var grid = new PropertyGrid();
        grid.SetTarget(def, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(def);
            Refresh();
        };
        box.AddChild(problems);
        box.AddChild(summary);
        box.AddChild(Ui.Label("Расстановка: в центре — призыватель (стрелки поворачивают его взгляд), точка — гекс навыка; номера — порядок занятия гексов, " +
                              "яркие — первые по количеству. Занятые в бою гексы — по правилу «место занято».", 12, Ui.TextDim, wrap: true));
        box.AddChild(canvas);
        box.AddChild(users);
        box.AddChild(grid);
        Refresh();
        return box;
    }
}
