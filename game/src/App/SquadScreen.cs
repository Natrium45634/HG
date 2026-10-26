using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.Battle;
using HexTactics.Game.Editor;
using HexTactics.Game.UI;

namespace HexTactics.Game.App;

/// <summary>
/// The squad before a battle (ТЗ ч. 6, 2.3; docs/DECISIONS_6.md §96): the library of fighters on the left (an unavailable one
/// is disabled with its reason), the squad on the right (a required fighter cannot be removed), the summary by role and the
/// problems, presets — the scenario's and the player's own. «В бой» takes the squad into the battle (it is in the replay).
/// </summary>
public partial class SquadScreen : Control
{
    private readonly ScenarioDef _sc;
    private readonly ContentDatabase _db;
    private readonly bool _admin;
    private readonly Action? _onExit;
    private VBoxContainer _library = null!;
    private VBoxContainer _squad = null!;
    private Label _summary = null!;
    private Label _problems = null!;
    private Button _start = null!;
    private OptionButton _presets = null!;

    /// <summary>The squad being assembled (template IDs in order).</summary>
    public List<string> Squad { get; } = new();

    public SquadScreen(ScenarioDef sc, ContentDatabase db, bool admin, Action? onExit)
    {
        _sc = sc;
        _db = db;
        _admin = admin;
        _onExit = onExit;
        Name = "SquadScreen";
        Squad.AddRange(SquadRules.DefaultRoster(db, sc));
    }

    /// <summary>The player's own presets (outside the content): user data / player_squads.json.</summary>
    public static string PlayerPresetsPath => Path.Combine(Main.Instance.Content.DataRoot, "player_squads.json");

    public static List<SquadPresetDef> PlayerPresets()
    {
        try
        {
            return File.Exists(PlayerPresetsPath) ? ContentJson.Deserialize<List<SquadPresetDef>>(File.ReadAllText(PlayerPresetsPath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            GD.PushWarning("Пресеты отряда игрока не прочитаны: " + e.Message);
            return new();
        }
    }

    public override void _Ready()
    {
        Theme = Main.Instance.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore }.Also(r => r.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect)));
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 24);
        AddChild(margin);
        var root = Ui.VBox(10);
        margin.AddChild(root);
        root.AddChild(Ui.Label("Отряд — " + _sc.DisplayName, 26, Ui.Accent));
        var rules = Ui.Label(RulesText(), 14, Ui.TextDim, wrap: true);
        rules.Name = "SquadRules";
        root.AddChild(rules);

        var cols = Ui.HBox(16).Expand(true, true);
        root.AddChild(cols);
        var left = Ui.VBox(6).Expand(true, true);
        left.AddChild(Ui.Label("Бойцы", 18, Ui.Accent));
        var libScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _library = Ui.VBox(4).Expand();
        _library.Name = "SquadLibrary";
        libScroll.AddChild(_library);
        left.AddChild(libScroll);
        cols.AddChild(Ui.PanelBox(left, null, 12).Expand(true, true));

        var right = Ui.VBox(6).Expand(true, true);
        right.AddChild(Ui.Label("Отряд", 18, Ui.Accent));
        var sqScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _squad = Ui.VBox(4).Expand();
        _squad.Name = "SquadList";
        sqScroll.AddChild(_squad);
        right.AddChild(sqScroll);
        _summary = Ui.Label("", 14, Ui.Text, wrap: true);
        _summary.Name = "SquadSummary";
        right.AddChild(_summary);
        _problems = Ui.Label("", 14, Ui.Bad, wrap: true);
        _problems.Name = "SquadProblems";
        right.AddChild(_problems);
        cols.AddChild(Ui.PanelBox(right, null, 12).Expand(true, true));

        var bottom = Ui.HBox(8);
        _presets = new OptionButton { Name = "SquadPresets" };
        bottom.AddChild(Ui.Label("Пресет", 14, Ui.TextDim));
        bottom.AddChild(_presets.MinSize(240));
        var load = Ui.Button("Загрузить", LoadSelectedPreset, "Набрать отряд из пресета: недоступные бойцы пропускаются (причина — в журнале экрана)");
        load.Name = "SquadLoadPreset";
        bottom.AddChild(load);
        var save = Ui.Button("Сохранить пресет…", () => Dialogs.Prompt(this, "Сохранить пресет отряда", "Название", "Мой отряд", SavePreset),
            "В ваших данных (не в контенте игры)");
        save.Name = "SquadSavePreset";
        bottom.AddChild(save);
        bottom.AddChild(Ui.Spacer(0, 0, expandH: true));
        bottom.AddChild(Ui.Button("Назад", () =>
        {
            if (_onExit != null) _onExit();
            else Main.Instance.ShowMainMenu();
        }).MinSize(120, 40));
        _start = Ui.Button("▶ В бой", Start, "Отряд уходит в бой; в реплее записан");
        _start.Name = "SquadStart";
        _start.AddThemeColorOverride("font_color", Ui.Good);
        bottom.AddChild(_start.MinSize(160, 40));
        root.AddChild(bottom);
        FillPresets();
        Refresh();
    }

    private string RulesText()
    {
        var s = _sc.Squad ?? new ScenarioSquadDef();
        var parts = new List<string> { s.MaxSize > 0 ? $"бойцов {s.MinSize}–{s.MaxSize}" : $"бойцов от {s.MinSize}" };
        if (s.RequiredTags.Count > 0) parts.Add("нужны: " + string.Join(", ", s.RequiredTags));
        if (s.AllowedTags.Count > 0) parts.Add("только: " + string.Join(", ", s.AllowedTags));
        if (s.ForbiddenTags.Count > 0) parts.Add("нельзя: " + string.Join(", ", s.ForbiddenTags));
        if (s.Limits.Count > 0) parts.Add("лимиты: " + string.Join(", ", s.Limits));
        parts.Add(s.DuplicatesAllowed ? "повторы разрешены" : "без повторов");
        var mode = EffectiveGameRules.Resolve(_db, _sc);
        parts.Add("на поле в начале: " + EdUtil.EnumLabel(mode.SquadStart).ToLowerInvariant());
        return string.Join(" · ", parts);
    }

    private void FillPresets()
    {
        _presets.Clear();
        foreach (var p in _db.All<SquadPresetDef>()) _presets.AddItem(p.DisplayName);
        foreach (var p in PlayerPresets()) _presets.AddItem(p.DisplayName + " (мой)");
    }

    private SquadPresetDef? PresetAt(int i)
    {
        var all = _db.All<SquadPresetDef>().Concat(PlayerPresets()).ToList();
        return i >= 0 && i < all.Count ? all[i] : null;
    }

    public void Add(string id)
    {
        if (SquadRules.WhyNot(_db, _sc, Squad, id) != null) return; // the same check as the battle: nothing slips in
        Squad.Add(id);
        Refresh();
    }

    public void RemoveAt(int i)
    {
        if (SquadRules.WhyKeep(_sc, Squad, i) != null) return;
        Squad.RemoveAt(i);
        Refresh();
    }

    public void Move(int i, int d)
    {
        int j = i + d;
        if (i < 0 || j < 0 || i >= Squad.Count || j >= Squad.Count) return;
        (Squad[i], Squad[j]) = (Squad[j], Squad[i]);
        Refresh();
    }

    /// <summary>The fighters of a preset that fit (the required ones stay).</summary>
    public List<string> LoadPreset(SquadPresetDef p)
    {
        var skipped = new List<string>();
        Squad.Clear();
        Squad.AddRange((_sc.Squad ?? new ScenarioSquadDef()).StartingRoster.Where(id => _db.Unit(id) != null));
        var unclaimed = Squad.ToList();
        foreach (var id in p.Units)
        {
            if (unclaimed.Remove(id)) continue;
            var why = SquadRules.WhyNot(_db, _sc, Squad, id);
            if (why == null) Squad.Add(id);
            else skipped.Add($"«{_db.Unit(id)?.DisplayName ?? id}»: {why}");
        }
        Refresh();
        return skipped;
    }

    private void LoadSelectedPreset()
    {
        if (PresetAt(_presets.Selected) is not { } p) return;
        var skipped = LoadPreset(p);
        if (skipped.Count > 0) Dialogs.Info(this, "Пресет набран не полностью", string.Join("\n", skipped));
    }

    public void SavePreset(string name)
    {
        if (name.Trim().Length == 0) return;
        var list = PlayerPresets();
        list.RemoveAll(x => x.Name == name.Trim());
        list.Add(new SquadPresetDef { Id = "player_" + ContentIO.SafeFileName(name.Trim()).ToLowerInvariant(), Name = name.Trim(), Units = Squad.ToList(), DuplicatesAllowed = true });
        Directory.CreateDirectory(Path.GetDirectoryName(PlayerPresetsPath)!);
        File.WriteAllText(PlayerPresetsPath, ContentJson.Serialize(list));
        FillPresets();
    }

    private void Refresh()
    {
        if (!IsInstanceValid(_library)) return;
        Ui.ClearChildren(_library);
        foreach (var u in SquadRules.PoolOf(_db, _sc))
        {
            var why = SquadRules.WhyNot(_db, _sc, Squad, u.Id);
            double level = PowerCache.Evaluate(_db, u).Level;
            string id = u.Id;
            var b = Ui.Button($"{u.DisplayName} · {EdUtil.EnumLabel(u.Role)} · ур. {level:0.#}" + (why != null ? "  — " + why : ""), () => Add(id),
                why ?? "Добавить в отряд");
            b.Name = "SquadAdd_" + u.Id;
            b.Disabled = why != null;
            b.Alignment = HorizontalAlignment.Left;
            _library.AddChild(b);
        }
        Ui.ClearChildren(_squad);
        for (int i = 0; i < Squad.Count; i++)
        {
            int index = i;
            var u = _db.Unit(Squad[i]);
            var keep = SquadRules.WhyKeep(_sc, Squad, i);
            var row = Ui.HBox(4);
            row.Name = "SquadRow_" + i;
            row.AddChild(Ui.Label($"{i + 1}. {u?.DisplayName ?? Squad[i]}" + (keep != null ? " (обязательный)" : ""), 15, keep != null ? Ui.Accent : Ui.Text).Expand());
            row.AddChild(Ui.Button("↑", () => Move(index, -1), "Выше"));
            row.AddChild(Ui.Button("↓", () => Move(index, +1), "Ниже"));
            var rm = Ui.Button("✕", () => RemoveAt(index), keep ?? "Убрать из отряда");
            rm.Name = "SquadRemove_" + i;
            rm.Disabled = keep != null;
            row.AddChild(rm);
            _squad.AddChild(row);
        }
        var roles = SquadRules.Roles(_db, Squad);
        var s = _sc.Squad ?? new ScenarioSquadDef();
        _summary.Text = $"Бойцов {Squad.Count}" + (s.MaxSize > 0 ? $" из {s.MaxSize}" : "") +
                        (roles.Count > 0 ? " · " + string.Join(", ", roles.Select(r => $"{r.Role} ×{r.Count}")) : "");
        var problems = SquadRules.Check(_db, _sc, Squad);
        _problems.Text = string.Join("\n", problems.Select(p => "✗ " + p));
        _start.Disabled = problems.Count > 0;
        _start.TooltipText = problems.Count > 0 ? "Сначала исправьте: " + string.Join("; ", problems) : "Отряд уходит в бой; в реплее записан";
    }

    public void Start()
    {
        if (SquadRules.Check(_db, _sc, Squad).Count > 0) return;
        Main.Instance.ShowBattle(new BattleScreen(_sc, _db, _admin, _onExit) { Roster = Squad.ToList() });
    }
}
