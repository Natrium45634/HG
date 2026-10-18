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
        left.AddChild(Ui.Spacer(0, 18));
        left.AddChild(Ui.Button("Редактор (админ-режим)", () => app.ShowEditor(), "Карты, юниты, навыки, эффекты, правила и сценарии без кода").MinSize(300, 44));
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
        right.AddChild(_scenarioList);
        _details = Ui.Label("", 14, Ui.TextDim, wrap: true).MinSize(380, 90);
        right.AddChild(_details);
        _play = Ui.Button("▶ Играть", Play).MinSize(380, 48);
        _play.Disabled = true;
        right.AddChild(_play);
        right.AddChild(Ui.Label("Пробел — пауза · ЛКМ по юниту — меню действий · ПКМ — быстрое движение/атака", 12, Ui.TextDim, wrap: true).MinSize(380));
        root.AddChild(Ui.PanelBox(right, null, 22).MinSize(430, 0));

        foreach (var sc in app.Content.Published.All<ScenarioDef>())
        {
            var s = sc;
            var b = Ui.Button(sc.DisplayName, () => Choose(s)).MinSize(380, 38);
            b.Alignment = HorizontalAlignment.Left;
            _scenarioList.AddChild(b);
        }
        var first = app.Content.Published.All<ScenarioDef>().FirstOrDefault();
        if (first != null) Choose(first);
    }

    private void RestoreDamaged()
    {
        var restored = Main.Instance.Content.RestoreDamaged();
        var integrity = Main.Instance.Content.Integrity;
        Editor.Dialogs.Info(this, "Восстановление", $"Восстановлено файлов: {restored.Count}.\n" + (integrity?.Describe() ?? ""));
        Main.Instance.ShowMainMenu();
    }

    private void Choose(ScenarioDef sc)
    {
        _chosen = sc;
        var map = Main.Instance.Content.Published.Get<MapDef>(sc.Map);
        _details.Text = $"{sc.Description}\nКарта: {map?.DisplayName ?? sc.Map} · юнитов: {sc.Units.Count} · волн: {sc.Waves.Count}\n" +
                        $"Победа: {string.Join("; ", sc.Victory.Select(v => v.Describe()))}";
        _play.Disabled = false;
    }

    private void Play()
    {
        if (_chosen == null) return;
        Main.Instance.ShowBattle(_chosen, Main.Instance.Content.Published, false);
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
