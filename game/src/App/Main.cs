using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.Battle;
using HexTactics.Game.Editor;
using HexTactics.Game.UI;

namespace HexTactics.Game.App;

/// <summary>
/// Application root: owns content, settings and the current screen (menu, battle, editor).
/// Command line (after "--"): --auto=battle|editor|menu --scenario=id --run=seconds --speed=index
/// --shot=path.png --frames=N --tab=name --select=n --mode=... (used for automated screenshots/tests).
/// </summary>
public partial class Main : Node
{
    public static Main Instance { get; private set; } = null!;

    /// <summary>The version of the game (project settings → application/config/version).</summary>
    public static string Version => ProjectSettings.GetSetting("application/config/version", "1.0.0").AsString();

    public ContentManager Content { get; private set; } = null!;
    public Settings Settings { get; private set; } = null!;
    public Theme Theme { get; private set; } = null!;
    public Dictionary<string, string> Args { get; } = new();

    private Node? _screen;
    private int _frame;
    private int _shotFrame = -1;

    public override void _Ready()
    {
        Instance = this;
        // The same numbers and percents in every text on every system (docs/DECISIONS_3.md §54): the formatting does not
        // follow the OS regional settings — a Windows build must read like the tested one.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            var s = a.TrimStart('-');
            int eq = s.IndexOf('=');
            if (eq > 0) Args[s[..eq]] = s[(eq + 1)..];
            else Args[s] = "1";
        }
        // UI tests run in a sandbox of user data (fresh each run): they publish content and toggle favourites.
        string? sandbox = null;
        if (Args.ContainsKey("uitest"))
        {
            sandbox = "uitest";
            var dir = ProjectSettings.GlobalizePath("user://uitest");
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
            Settings.FileName = "user://uitest/settings.json";
        }
        Settings = Settings.Load();
        ApplyWindowSettings();
        Ui.InstallFontFallback();
        Theme = Ui.BuildTheme();
        Content = new ContentManager(sandbox);
        Content.Load();

        if (Args.ContainsKey("selftest"))
        {
            RunSelfTest();
            return;
        }
        if (Args.TryGetValue("uitest", out var uitest))
        {
            ShowMainMenu();
            AddChild(new UiTestRunner(uitest));
            return;
        }
        string auto = Args.GetValueOrDefault("auto", "menu");
        if (Args.TryGetValue("shot", out _)) _shotFrame = int.Parse(Args.GetValueOrDefault("frames", "45"));
        switch (auto)
        {
            case "battle":
            {
                var sc = Content.Published.Get<ScenarioDef>(Args.GetValueOrDefault("scenario", "valley_ambush"));
                if (sc != null) ShowBattle(sc, Content.Published, Args.ContainsKey("admin"));
                else ShowMainMenu();
                break;
            }
            case "editor":
                ShowEditor();
                break;
            default:
                ShowMainMenu();
                break;
        }
    }

    /// <summary>
    /// Smoke test of a build: content loads without errors and every scenario simulates 30 s.
    /// Prints a report and exits with code 0/1 (used for exported builds and CI).
    /// </summary>
    private void RunSelfTest()
    {
        int failures = 0;
        GD.Print($"SELFTEST: HexTactics {Version}");
        GD.Print($"SELFTEST: контент — {Content.SourceDescription}, ошибок {Content.ErrorCount}");
        foreach (var i in Content.LoadIssues.Where(i => i.Severity == HexTactics.Core.Content.IssueSeverity.Error)) GD.Print("  " + i);
        if (Content.ErrorCount > 0) failures++;
        // Resources must be inside the build and readable (models are also parsed — the same path as in battle).
        int files = 0, models = 0;
        foreach (var a in Content.Published.All<HexTactics.Core.Content.AssetDef>().Where(a => a.HasFile))
        {
            files++;
            var problem = HexTactics.Core.Content.AssetValidator.FileProblem(a, Render.Assets.Read(a.Path));
            if (problem == null && a.Kind == HexTactics.Core.Content.AssetKind.Model)
            {
                var node = Render.Assets.Model(a);
                if (node == null) problem = Render.Assets.Problems.GetValueOrDefault(a.Id, "модель не загружена");
                else
                {
                    models++;
                    node.Free();
                }
            }
            if (problem != null)
            {
                failures++;
                GD.PrintErr($"SELFTEST: ресурс «{a.Id}»: {problem}");
            }
        }
        GD.Print($"SELFTEST: ресурсы — файлов {files}, моделей загружено {models}");
        foreach (var sc in Content.Published.All<ScenarioDef>())
        {
            try
            {
                var sim = HexTactics.Core.Sim.ScenarioSystem.Create(Content.Published, sc, 1);
                foreach (var u in sim.Units) u.AiControlled = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                sim.RunSeconds(30);
                GD.Print($"SELFTEST: «{sc.DisplayName}» 30 с за {sw.ElapsedMilliseconds} мс, юнитов {sim.AliveUnits.Count()}/{sim.Units.Count}, событий {sim.Log.TotalCount}, итог {sim.Outcome}");
            }
            catch (Exception e)
            {
                failures++;
                GD.PrintErr($"SELFTEST: «{sc.DisplayName}» ошибка: {e.Message}");
            }
        }
        GD.Print(failures == 0 ? "SELFTEST: OK" : $"SELFTEST: FAILED ({failures})");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    public void ApplyWindowSettings()
    {
        if (DisplayServer.GetName() == "headless") return;
        DisplayServer.WindowSetMode(Settings.Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        GetTree().Root.ContentScaleFactor = (float)Settings.UiScale;
    }

    private void SetScreen(Node screen)
    {
        if (_screen != null)
        {
            RemoveChild(_screen);
            _screen.QueueFree();
        }
        _screen = screen;
        AddChild(screen);
    }

    public void ShowMainMenu() => SetScreen(new MainMenu());

    public void ShowBattle(ScenarioDef scenario, ContentDatabase db, bool admin, Action? onExit = null) =>
        SetScreen(new BattleScreen(scenario, db, admin, onExit));

    /// <summary>A prepared battle screen: a restart from its snapshot, an unsafe test.</summary>
    public void ShowBattle(BattleScreen screen) => SetScreen(screen);

    public void ShowEditor() => SetScreen(new EditorScreen());

    public override void _Process(double delta)
    {
        _frame++;
        if (_shotFrame >= 0 && _frame == _shotFrame)
        {
            var path = Args["shot"];
            var img = GetViewport().GetTexture().GetImage();
            img.SavePng(path);
            GD.Print("Скриншот: " + path);
            if (!Args.ContainsKey("keep")) GetTree().Quit();
        }
    }
}
