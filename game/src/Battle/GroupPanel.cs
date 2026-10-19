using System;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// The bottom group command panel (docs/DECISIONS_3.md §44): the 14 commands, the order parameters (formation,
/// queue mode, behaviour after the target dies, automatic skills, facing, spread, lack of space) and the
/// saved groups. Shown instead of the single-unit action bar while a group is selected.
/// </summary>
public partial class GroupPanel : PanelContainer
{
    private readonly BattleScreen _bs;
    private Label _title = null!;
    private HFlowContainer _saved = null!;
    private string _savedKey = "";

    public GroupPanel(BattleScreen bs)
    {
        _bs = bs;
        Name = "GroupPanel";
    }

    private static readonly (GroupCommand Cmd, string Icon, string Hint)[] Commands =
    {
        (GroupCommand.Move, "➜", "Двигаться к точке: каждый получит свой гекс (ПКМ по гексу — то же)"),
        (GroupCommand.Attack, "⚔", "Атаковать цель: ближний бой — рядом, стрелки — на дистанции с обзором (ПКМ по врагу — то же)"),
        (GroupCommand.Skill, "★", "Навык по цели или гексу (навык — в параметрах)"),
        (GroupCommand.DefendPosition, "▣", "Оборонительная позиция: прийти и встать в стойку"),
        (GroupCommand.HoldPosition, "■", "Удерживать позицию: остановиться и держать стойку"),
        (GroupCommand.Follow, "⇢", "Следовать за юнитом"),
        (GroupCommand.Face, "↻", "Повернуться в направлении"),
        (GroupCommand.Disperse, "⁘", "Рассредоточиться (не ближе двух гексов друг к другу)"),
        (GroupCommand.Gather, "◎", "Собраться в области (компактно)"),
        (GroupCommand.Surround, "⬡", "Окружить цель"),
        (GroupCommand.StopAfterCurrent, "⏹", "Остановиться после текущего действия"),
        (GroupCommand.CancelQueue, "✕", "Отменить очередь (и выполняемый приказ)"),
        (GroupCommand.ClearQueue, "⌫", "Очистить ожидающие приказы"),
        (GroupCommand.AutoAttackPriority, "⌖", "Приоритет автоатаки (выбор — в параметрах)"),
    };

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", Ui.Box(Ui.Panel, Ui.Border, 8, 1, 8));
        var v = Ui.VBox(4);
        _title = Ui.Label("", 14, Ui.Accent);
        v.AddChild(_title);
        var cmds = new HFlowContainer();
        cmds.AddThemeConstantOverride("h_separation", 4);
        cmds.AddThemeConstantOverride("v_separation", 4);
        foreach (var (cmd, icon, hint) in Commands)
        {
            var b = new Button { Text = $"{icon} {Names.Of(cmd)}", FocusMode = FocusModeEnum.None, TooltipText = hint, Name = "Group_" + cmd };
            b.AddThemeFontSizeOverride("font_size", 12);
            var c = cmd;
            b.Pressed += () => _bs.StartGroupCommand(c);
            cmds.AddChild(b);
        }
        v.AddChild(cmds);

        // Order parameters.
        var t = _bs.GroupTemplate;
        var opts = new HFlowContainer();
        opts.AddThemeConstantOverride("h_separation", 8);
        var formation = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Формация движения", Name = "GroupFormation" };
        var forms = _bs.Sim.Content.All<FormationDef>().ToList();
        for (int i = 0; i < forms.Count; i++) formation.AddItem(forms[i].DisplayName, i);
        int fi = forms.FindIndex(f => f.Id == t.Formation);
        if (fi >= 0) formation.Select(fi);
        formation.ItemSelected += i => t.Formation = forms[(int)i].Id;
        opts.AddChild(Labeled("Формация", formation));
        opts.AddChild(Labeled("Очередь", Enum(t.QueueMode, x => t.QueueMode = x)));
        opts.AddChild(Labeled("Цель погибла", Enum(t.OnTargetDeath, x => t.OnTargetDeath = x)));
        opts.AddChild(Labeled("Нет места", Enum(t.WhenNoSpace, x => t.WhenNoSpace = x)));
        opts.AddChild(Labeled("Автоатака", Enum(t.AutoPriority, x => t.AutoPriority = x)));
        var spread = new SpinBox { MinValue = 0, MaxValue = 10, Value = t.MaxSpread, TooltipText = "Допустимое расстояние от точки (0 — без ограничения)", Prefix = "разброс" };
        spread.ValueChanged += x => t.MaxSpread = (int)x;
        opts.AddChild(spread);
        opts.AddChild(Check("автовыбор навыков", t.AllowAutoSkills, x => t.AllowAutoSkills = x, "Маги выбирают навык с наибольшим ожидаемым уроном по цели"));
        opts.AddChild(Check("свой взгляд", t.IndividualFacing, x => t.IndividualFacing = x, "Индивидуальное направление вместо общего/формации"));
        opts.AddChild(Check("сохранить свои приказы", t.KeepIndividual, x => t.KeepIndividual = x, "Групповой приказ — после индивидуальных"));
        var skills = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Навык для команды «Навык»", Name = "GroupSkill" };
        skills.AddItem("— навык —", 0);
        var all = _bs.Sim.Content.All<SkillDef>().Where(s => s.Kind != SkillKind.Defense).OrderBy(s => s.DisplayName).ToList();
        for (int i = 0; i < all.Count; i++) skills.AddItem(all[i].DisplayName, i + 1);
        skills.ItemSelected += i => t.SkillId = i == 0 ? "" : all[(int)i - 1].Id;
        opts.AddChild(skills);
        v.AddChild(opts);

        var savedRow = Ui.HBox(4);
        savedRow.AddChild(Ui.Label("Группы (Ctrl+цифра — сохранить, Alt+цифра — выбрать):", 11, Ui.TextDim));
        _saved = new HFlowContainer();
        savedRow.AddChild(_saved.Expand());
        v.AddChild(savedRow);
        AddChild(v);
    }

    private static Control Labeled(string text, Control c)
    {
        var h = Ui.HBox(3);
        h.AddChild(Ui.Label(text + ":", 11, Ui.TextDim));
        h.AddChild(c);
        return h;
    }

    private static OptionButton Enum<T>(T value, Action<T> set) where T : struct, System.Enum
    {
        var o = new OptionButton { FocusMode = FocusModeEnum.None };
        var vals = System.Enum.GetValues<T>();
        for (int i = 0; i < vals.Length; i++) o.AddItem(Names.Of(vals[i]), i);
        o.Select(Array.IndexOf(vals, value));
        o.ItemSelected += i => set(vals[(int)i]);
        return o;
    }

    private static CheckButton Check(string text, bool value, Action<bool> set, string tip)
    {
        var c = new CheckButton { Text = text, ButtonPressed = value, FocusMode = FocusModeEnum.None, TooltipText = tip };
        c.AddThemeFontSizeOverride("font_size", 11);
        c.Toggled += x => set(x);
        return c;
    }

    public void Refresh()
    {
        _title.Text = $"Группа: {_bs.Group.Count} юн. — " + string.Join(", ", _bs.Group.Select(u => u.Name).Take(8)) + (_bs.Group.Count > 8 ? "…" : "")
                      + (_bs.GroupMode is { } m ? $" · {Names.Of(m)}: укажите цель" : "");
        string key = string.Join(";", _bs.SavedGroups.Select(kv => kv.Key + ":" + kv.Value.Count));
        if (key == _savedKey) return;
        _savedKey = key;
        Ui.ClearChildren(_saved);
        foreach (var (slot, ids) in _bs.SavedGroups.OrderBy(kv => kv.Key))
        {
            int s = slot;
            _saved.AddChild(Ui.Button($"{slot} ({ids.Count})", () => _bs.RecallGroup(s), $"Выбрать группу {slot} (Alt+{slot})"));
        }
    }
}
