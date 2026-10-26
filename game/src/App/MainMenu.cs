using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.App;

/// <summary>Main menu: play a scenario, open the administrative editor, settings, quit.</summary>
public partial class MainMenu : Control
{
    private VBoxContainer _scenarioList = null!;
    private Label _details = null!;
    private ScenarioDef? _chosen;
    private Button _play = null!;
    private OptionButton _variant = null!;
    private ScrollContainer _listScroll = null!;

    public override void _Process(double delta)
    {
        // The list takes what is left of the height (the rest of the column stays visible).
        float h = GetViewportRect().Size.Y;
        float want = Math.Min(_scenarioList.GetCombinedMinimumSize().Y, Math.Max(160, h * 0.42f));
        if (Math.Abs(_listScroll.CustomMinimumSize.Y - want) > 1) _listScroll.CustomMinimumSize = new Vector2(380, want);
    }
    private List<DifficultyVariantDef> _variants = new();

    public override void _Ready()
    {
        var app = Main.Instance;
        Theme = app.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore }.Also(r => r.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect)));
        AddChild(new MenuBackdrop { MouseFilter = MouseFilterEnum.Ignore }.Also(r => r.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect)));

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var root = Ui.HBox(24);
        center.AddChild(root);

        // Left: title + main buttons.
        var left = Ui.VBox(12);
        left.AddChild(Ui.Label("ГЕКС-ТАКТИКА", 44, Ui.Accent));
        left.AddChild(Ui.Label("тактика в реальном времени с паузой", 16, Ui.TextDim));
        var version = Ui.Label("версия " + Main.Version, 13, Ui.TextDim);
        version.Name = "VersionLabel";
        left.AddChild(version);
        left.AddChild(Ui.Spacer(0, 18));
        left.AddChild(Ui.Button("Редактор (админ-режим)", () => app.ShowEditor(), "Карты, юниты, навыки, эффекты, правила и сценарии без кода").MinSize(300, 44));
        var load = Ui.Button("Загрузить бой", () => Battle.SavedBattlesDialog.Open(this, false, null), "Бои, сохранённые посреди схватки (часть 6)").MinSize(300, 40);
        load.Name = "MenuLoadBattle";
        left.AddChild(load);
        left.AddChild(Ui.Button("Настройки", OpenSettings).MinSize(300, 44));
        left.AddChild(Ui.Button("Папка пользовательского контента", () => app.Content.OpenUserFolder()).MinSize(300, 36));
        left.AddChild(Ui.Button("Выход", () => GetTree().Quit()).MinSize(300, 44));
        left.AddChild(Ui.Spacer(0, 10));
        var src = Ui.Label("Контент: " + app.Content.SourceDescription, 13, Ui.TextDim, wrap: true).MinSize(300);
        left.AddChild(src);
        if (app.Content.ErrorCount > 0)
        {
            var warn = Ui.Label($"⚠ В контенте {app.Content.ErrorCount} ошибок — откройте редактор → «Проверка».", 13, Ui.Bad, wrap: true).MinSize(300);
            left.AddChild(warn);
        }
        if (app.Content.Integrity is { HasManifest: true, Ok: false } integrity)
        {
            left.AddChild(Ui.Label("⚠ Проверка целостности: " + integrity.Describe(), 13, Ui.Bad, wrap: true).MinSize(300));
            left.AddChild(Ui.Button("Восстановить из резервной копии", RestoreDamaged, "Повреждённые и отсутствующие файлы берутся из копии опубликованного состояния").MinSize(300, 34));
        }
        root.AddChild(Ui.PanelBox(left, null, 22));

        // A crash during the previous publication was rolled back at start: tell the admin once.
        if (app.Content.RecoveryMessage is { Length: > 0 } msg && !app.Args.ContainsKey("uitest"))
        {
            app.Content.RecoveryMessage = null;
            Callable.From(() => Editor.Dialogs.Info(this, "Восстановление после сбоя", msg)).CallDeferred();
        }

        // Right: scenarios.
        var right = Ui.VBox(8);
        right.AddChild(Ui.Label("Сценарии", 20, Ui.Text));
        _scenarioList = Ui.VBox(6);
        // Many scenarios scroll: «Играть» and the variant stay on the screen at any size of the window.
        var listScroll = new ScrollContainer { Name = "ScenarioScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        listScroll.AddChild(_scenarioList.Expand());
        right.AddChild(listScroll);
        _listScroll = listScroll;
        _details = Ui.Label("", 14, Ui.TextDim, wrap: true).MinSize(380, 90);
        right.AddChild(_details);
        // Part 6 (§99): the variants of the scenario — the same map with sets of modifiers on top.
        _variant = new OptionButton { Name = "MenuVariant", CustomMinimumSize = new Vector2(380, 0), TooltipText = "Вариант сценария: обычная или с модификаторами" };
        _variant.ItemSelected += _ => ShowVariant();
        right.AddChild(_variant);
        _play = Ui.Button("▶ Играть", Play).MinSize(380, 48);
        _play.Disabled = true;
        right.AddChild(_play);
        right.AddChild(Ui.Label("Пробел — пауза · ЛКМ по юниту — выбор и стрелка взгляда: короткий жест — поворот, длинный — маршрут · ПКМ — быстрый приказ, во время жеста — отмена · Esc — шаг назад", 12, Ui.TextDim, wrap: true).MinSize(380));
        root.AddChild(Ui.PanelBox(right, null, 22).MinSize(430, 0));

        // Drafts marked «неполный» are kept in the library but are not played.
        foreach (var sc in app.Content.Published.All<ScenarioDef>().Where(x => !x.Incomplete))
        {
            var s = sc;
            var b = Ui.Button(sc.DisplayName, () => Choose(s)).MinSize(380, 38);
            b.Alignment = HorizontalAlignment.Left;
            _scenarioList.AddChild(b);
        }
        var first = app.Content.Published.All<ScenarioDef>().FirstOrDefault(x => !x.Incomplete);
        if (first != null) Choose(first);
    }

    private void RestoreDamaged()
    {
        var restored = Main.Instance.Content.RestoreDamaged();
        var integrity = Main.Instance.Content.Integrity;
        Editor.Dialogs.Info(this, "Восстановление", $"Восстановлено файлов: {restored.Count}.\n" + (integrity?.Describe() ?? ""));
        Main.Instance.ShowMainMenu();
    }

    private void Choose(ScenarioDef sc) => Choose(sc, keepVariant: false);

    private void Choose(ScenarioDef sc, bool keepVariant)
    {
        int keep = _variant.Selected;
        _chosen = sc;
        var map = Main.Instance.Content.Published.Get<MapDef>(sc.Map);
        _details.Text = $"{sc.Description}\nКарта: {map?.DisplayName ?? sc.Map} · юнитов: {sc.Units.Count} · волн: {sc.Waves.Count}\n" +
                        $"Победа: {string.Join("; ", sc.Victory.Select(v => v.Describe()))}";
        _play.Disabled = false;
        var db = Main.Instance.Content.Published;
        _variants = db.All<DifficultyVariantDef>().Where(v => v.Scenario == sc.Id).ToList();
        _variant.Clear();
        _variant.AddItem("Обычная");
        foreach (var v in _variants) _variant.AddItem(v.DisplayName + (v.UnlockedBy != "" ? $" (после «{db.Get<ScenarioDef>(v.UnlockedBy)?.DisplayName ?? v.UnlockedBy}»)" : ""));
        _variant.Visible = _variants.Count > 0;
        _variant.Selected = keepVariant ? Math.Clamp(keep, 0, _variants.Count) : 0;
        _variant.TooltipText = "Вариант сценария: обычная или с модификаторами";
        if (Variant is { } vv)
        {
            // One line here (the menu must keep «Играть» on the screen); every value — in the hint of the list.
            var rows = MissionModifiers.Preview(db, sc, vv);
            _details.Text += $"\nВариант «{vv.DisplayName}»: изменений {rows.Count}" + (vv.RecommendedPower > 0 ? $" · рекомендуемая сила {vv.RecommendedPower:0.#}" : "") + " (подробно — в подсказке списка)";
            _variant.TooltipText = string.Join("\n", rows.Select(r => $"{r.Field}: {r.Base} → {r.Effective}"));
        }
    }

    private DifficultyVariantDef? Variant => _variant.Selected > 0 && _variant.Selected <= _variants.Count ? _variants[_variant.Selected - 1] : null;

    private void ShowVariant()
    {
        if (_chosen == null) return;
        Choose(_chosen, keepVariant: true);
    }

    private void Play()
    {
        if (_chosen == null) return;
        var db = Main.Instance.Content.Published;
        if (Variant is { } v)
        {
            // The variant is checked before the battle (ТЗ ч. 6, 5.8) and applied to a copy: the scenario stays as it is.
            var errors = MissionModifiers.Check(db, _chosen, v).Where(p => p.Error).ToList();
            if (errors.Count > 0)
            {
                Editor.Dialogs.Info(this, "Вариант не запускается", string.Join("\n", errors.Select(p => "• " + p.Message)));
                return;
            }
            var (vdb, vsc) = MissionModifiers.Prepare(db, _chosen, v);
            Main.Instance.ShowBattle(vsc, vdb, false);
            return;
        }
        Main.Instance.ShowBattle(_chosen, db, false);
    }

    private void OpenSettings()
    {
        var dlg = new SettingsDialog();
        AddChild(dlg);
        dlg.PopupCentered(new Vector2I(560, 620));
    }
}

/// <summary>Decorative hex pattern behind the menu.</summary>
public partial class MenuBackdrop : Control
{
    public override void _Draw()
    {
        float r = 46;
        float w = Mathf.Sqrt(3) * r;
        for (int row = -1; row < Size.Y / (r * 1.5f) + 2; row++)
        for (int col = -1; col < Size.X / w + 2; col++)
        {
            var c = new Vector2(col * w + (row % 2 == 0 ? 0 : w / 2), row * r * 1.5f);
            var pts = new Vector2[7];
            for (int i = 0; i < 7; i++)
            {
                float a = Mathf.DegToRad(60 * i - 30);
                pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (r - 3);
            }
            float k = (Mathf.Sin(col * 0.7f + row * 0.4f) + 1) * 0.5f;
            DrawPolyline(pts, new Color(1, 1, 1, 0.025f + 0.03f * k), 1.5f);
        }
    }
}

public static class NodeExt
{
    public static T Also<T>(this T n, System.Action<T> a)
    {
        a(n);
        return n;
    }
}
