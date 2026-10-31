using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.Editor;
using HexTactics.Game.UI;

namespace HexTactics.Game.App;

/// <summary>A mission of a campaign launched now: the battle of its scenario records its result into the progress.</summary>
public sealed record CampaignLaunch(string Campaign, string Node, string Scenario);

/// <summary>The campaign of the battle being launched, and the last completion to show back on the screen of missions.</summary>
public static class CampaignSession
{
    public static CampaignLaunch? Pending { get; set; }
    public static (string Campaign, string Node, CampaignCompletion Done)? Last { get; set; }

    /// <summary>The launch for a battle of this scenario (taken once; another scenario leaves it).</summary>
    public static CampaignLaunch? Take(string scenario)
    {
        var p = Pending;
        if (p == null || p.Scenario != scenario) return null;
        Pending = null;
        return p;
    }

    public static string Dir => System.IO.Path.Combine(Main.Instance.Content.DataRoot, "campaigns");

    public static CampaignProgress Progress(CampaignDef c) => CampaignStore.Load(Dir, c.Id) ?? Campaigns.Start(c);
}

/// <summary>
/// «Кампании» (ТЗ ч. 6, 9.7; docs/DECISIONS_6.md §103): the campaigns, their missions with links and statuses on a scheme, the
/// description, why a mission is closed, its rewards (got or not), «▶ Играть», the result of the last battle and the choice of a
/// reward. No strategic map, base or economy.
/// </summary>
public partial class CampaignScreen : Control
{
    private readonly ContentDatabase _db = Main.Instance.Content.Published;
    private CampaignDef? _c;
    private CampaignProgress _p = new();
    private string _node = "";
    private VBoxContainer _list = null!;
    private VBoxContainer _detail = null!;
    private VBoxContainer _summary = null!;
    private RuleGraphCanvas _scheme = null!;

    public CampaignScreen(string campaign = "", string node = "")
    {
        _c = _db.Get<CampaignDef>(campaign) ?? _db.All<CampaignDef>().FirstOrDefault();
        _node = node;
    }

    public CampaignDef? Campaign => _c;
    public CampaignProgress Progress => _p;
    public string Node => _node;
    public RuleGraphCanvas Scheme => _scheme;

    public override void _Ready()
    {
        Theme = Main.Instance.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore }.Also(r => r.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect)));
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 16);
        AddChild(margin);
        var root = Ui.VBox(8);
        margin.AddChild(root);
        var head = Ui.HBox(8);
        head.AddChild(Ui.Button("← Меню", () => Main.Instance.ShowMainMenu()).MinSize(120, 36));
        head.AddChild(Ui.Label("Кампании", 26, Ui.Accent).Expand());
        root.AddChild(head);
        var body = Ui.HBox(10).Expand(true, true);
        root.AddChild(body);

        var left = new ScrollContainer { CustomMinimumSize = new Vector2(300, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = Ui.VBox(4).Expand();
        _list.Name = "CampaignList";
        left.AddChild(_list);
        body.AddChild(left);

        var center = Ui.VBox(6).Expand(true, true);
        _scheme = new RuleGraphCanvas { Name = "CampaignMissionScheme", ReadOnly = true, SchemeTitles = true, MinimapEnabled = false };
        _scheme.NodeChosen += id =>
        {
            if (id != "" && id != _node) Choose(id);
        };
        center.AddChild(_scheme);
        _summary = Ui.VBox(2);
        _summary.Name = "CampaignSummary";
        center.AddChild(_summary);
        body.AddChild(center);

        var right = new ScrollContainer { CustomMinimumSize = new Vector2(400, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_right", 12);
        _detail = Ui.VBox(6).Expand();
        _detail.Name = "CampaignMission";
        pad.AddChild(_detail);
        right.AddChild(pad.Expand());
        body.AddChild(right);

        Open(_c);
    }

    public void Open(CampaignDef? c)
    {
        _c = c;
        _p = c == null ? new CampaignProgress() : CampaignSession.Progress(c);
        if (c != null && c.Node(_node) == null)
            _node = c.Nodes.FirstOrDefault(n => Campaigns.Status(c, _p, n) == CampaignNodeStatus.Available)?.Id ?? c.Nodes.FirstOrDefault()?.Id ?? "";
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshList();
        RefreshScheme();
        RefreshSummary();
        RefreshDetail();
    }

    private static string Icon(CampaignNodeStatus s) => s switch
    {
        CampaignNodeStatus.Completed => "✓",
        CampaignNodeStatus.Available => "●",
        _ => "○",
    };

    private static Color ColorOf(CampaignNodeStatus s) => s switch
    {
        CampaignNodeStatus.Completed => Ui.Good,
        CampaignNodeStatus.Available => Ui.Accent,
        _ => Ui.TextDim,
    };

    private void RefreshList()
    {
        Ui.ClearChildren(_list);
        var all = _db.All<CampaignDef>().ToList();
        if (all.Count == 0) _list.AddChild(Ui.Label("Кампаний нет — создайте их в редакторе («Кампании»).", 13, Ui.TextDim, wrap: true));
        foreach (var c in all)
        {
            var cc = c;
            var b = Ui.Button(c.DisplayName, () =>
            {
                _node = "";
                Open(cc);
            }, c.Description);
            b.Name = "Campaign_" + c.Id;
            b.ToggleMode = true;
            b.ButtonPressed = c == _c;
            b.Alignment = HorizontalAlignment.Left;
            _list.AddChild(b);
        }
        if (_c == null) return;
        _list.AddChild(Ui.Sep());
        _list.AddChild(Ui.Label("Задания:", 13, Ui.TextDim));
        foreach (var n in _c.Nodes)
        {
            var nn = n;
            var st = Campaigns.Status(_c, _p, n);
            var sc = _db.Get<ScenarioDef>(n.Scenario);
            var b = Ui.Button($"{Icon(st)} " + (n.StageLabel != "" ? n.StageLabel + ": " : "") + (sc?.DisplayName ?? n.Scenario), () => Choose(nn.Id),
                EdUtil.EnumLabel(st));
            b.Name = "Mission_" + n.Id;
            b.ToggleMode = true;
            b.ButtonPressed = n.Id == _node;
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.AddThemeColorOverride("font_color", ColorOf(st));
            _list.AddChild(b);
        }
        _list.AddChild(Ui.Sep());
        var reset = Ui.Button("Начать кампанию заново", () => Dialogs.Confirm(this, "Кампания", $"Сбросить прохождение «{_c.DisplayName}»? Полученные награды и флаги пропадут.", () =>
        {
            CampaignStore.Delete(CampaignSession.Dir, _c.Id);
            Open(_c);
        }, "Сбросить"));
        reset.Name = "CampaignReset";
        _list.AddChild(reset);
    }

    private void RefreshScheme()
    {
        _scheme.Marks.Clear();
        if (_c == null)
        {
            _scheme.Show(null);
            return;
        }
        foreach (var n in _c.Nodes) _scheme.Marks[n.Id] = ColorOf(Campaigns.Status(_c, _p, n));
        _scheme.Show(Campaigns.Scheme(_c, _p, _db));
    }

    private void RefreshSummary()
    {
        Ui.ClearChildren(_summary);
        if (_c == null) return;
        if (_c.Intro != "") _summary.AddChild(Ui.Label(_c.Intro, 13, Ui.TextDim, wrap: true));
        int done = _c.Nodes.Count(n => Campaigns.Completed(_p, n.Id));
        var parts = new List<string> { $"Пройдено {done} из {_c.Nodes.Count}" };
        if (_p.Medals.Count > 0) parts.Add("медали: " + string.Join(", ", _p.Medals));
        if (_p.Currency.Count > 0) parts.Add(string.Join(", ", _p.Currency.Select(kv => $"{kv.Key}: {kv.Value:0.##}")));
        if (_p.UnlockedUnits.Count > 0) parts.Add("открыты юниты: " + string.Join(", ", _p.UnlockedUnits.Select(u => _db.Unit(u)?.DisplayName ?? u)));
        if (_c.Cycles == CampaignCycles.Endless) parts.Add("бесконечная кампания");
        var line = Ui.Label(string.Join(" · ", parts), 14, Ui.Text, wrap: true);
        line.Name = "CampaignProgressLine";
        _summary.AddChild(line);
        foreach (var note in Campaigns.Reconcile(_c, _p)) _summary.AddChild(Ui.Label("⚠ " + note, 12, Ui.Warn, wrap: true));
    }

    public void Choose(string node)
    {
        _node = node;
        RefreshList();
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        Ui.ClearChildren(_detail);
        if (_c?.Node(_node) is not { } n) return;
        var st = Campaigns.Status(_c, _p, n);
        var sc = _db.Get<ScenarioDef>(n.Scenario);
        var variant = _db.Get<DifficultyVariantDef>(n.Variant);
        _detail.AddChild(Ui.Label((n.StageLabel != "" ? n.StageLabel + " · " : "") + (sc?.DisplayName ?? n.Scenario), 20, Ui.Accent, wrap: true));
        if (variant != null) _detail.AddChild(Ui.Label("Вариант: " + variant.DisplayName, 13, Ui.Warn));
        _detail.AddChild(Ui.Label(n.Description != "" ? n.Description : sc?.Description ?? "", 13, Ui.Text, wrap: true));
        var status = Ui.Label($"{Icon(st)} {EdUtil.EnumLabel(st)}" + (st == CampaignNodeStatus.Locked ? " — " + Campaigns.WhyLocked(_c, _p, n) : ""), 14, ColorOf(st), wrap: true);
        status.Name = "MissionStatus";
        _detail.AddChild(status);
        if (_p.Record(n.Id) is { } rec)
        {
            string Best(OutcomeMetric m) => rec.Best.TryGetValue(m, out var v) ? (m == OutcomeMetric.BaseHpRemaining ? $"{v:P0}" : $"{v:0.#}") : "—";
            _detail.AddChild(Ui.Label($"Попыток {rec.Attempts}, побед {rec.Victories}" + (rec.Victories > 0
                ? $" · лучшее: база {Best(OutcomeMetric.BaseHpRemaining)}, пропущено {Best(OutcomeMetric.Leaks)}, потери {Best(OutcomeMetric.Deaths)}, время {Best(OutcomeMetric.TimeToWin)} с"
                : ""), 12, Ui.TextDim, wrap: true));
        }
        if (n.Next.Count > 0)
            _detail.AddChild(Ui.Label("Далее: " + string.Join(", ", n.Next.Select(x => _c.Node(x) is { } nx ? (_db.Get<ScenarioDef>(nx.Scenario)?.DisplayName ?? nx.Id) : x)), 12, Ui.TextDim, wrap: true));

        // The rewards: what the set gives, and what this run has got.
        if (_db.Get<RewardSetDef>(n.RewardSet) is { } set)
        {
            _detail.AddChild(Ui.Sep());
            _detail.AddChild(Ui.Label("Награды:", 14, Ui.Accent));
            var lines = Campaigns.Preview(set);
            for (int i = 0; i < set.Entries.Count; i++)
            {
                var e = set.Entries[i];
                bool got = _p.Ledger.Any(g => g.Node == n.Id && g.Entry == e.Id);
                var l = Ui.Label((got ? "✓ " : "· ") + lines[i], 13, got ? Ui.Good : Ui.Text, wrap: true);
                l.Name = "Reward_" + e.Id;
                _detail.AddChild(l);
            }
        }
        foreach (var choice in _p.Pending.Where(x => x.Node == n.Id).ToList())
        {
            var cc = choice;
            _detail.AddChild(Ui.Label($"Выберите награду «{choice.Group}»:", 14, Ui.Warn));
            var row = new HFlowContainer();
            foreach (var id in choice.Entries)
            {
                var e = _db.Get<RewardSetDef>(choice.RewardSet)?.Entries.Find(x => x.Id == id);
                var eid = id;
                var b = Ui.Button(e?.Describe() ?? id, () =>
                {
                    var why = Campaigns.Choose(_db, _p, cc, eid);
                    CampaignStore.Save(CampaignSession.Dir, _p);
                    if (why != null) Dialogs.Info(this, "Награда", why);
                    RefreshAll();
                });
                b.Name = "Choose_" + id;
                row.AddChild(b);
            }
            _detail.AddChild(row);
        }

        // The result of the battle just played.
        if (CampaignSession.Last is { } last && last.Campaign == _c.Id && last.Node == n.Id)
        {
            var d = last.Done;
            _detail.AddChild(Ui.Sep());
            var res = Ui.Label((d.Victory ? "Победа" : "Поражение") + (d.Duplicate ? " (уже учтено)" : "") +
                               (d.Granted.Count > 0 ? "\nПолучено: " + string.Join(", ", d.Granted) : "") +
                               (d.NewlyAvailable.Count > 0 ? "\nОткрыто: " + string.Join(", ", d.NewlyAvailable.Select(x => _db.Get<ScenarioDef>(_c.Node(x)?.Scenario ?? "")?.DisplayName ?? x)) : "") +
                               (d.Skipped.Count > 0 ? "\nНе выдано: " + string.Join("; ", d.Skipped) : ""), 13, d.Victory ? Ui.Good : Ui.Bad, wrap: true);
            res.Name = "MissionResult";
            _detail.AddChild(res);
        }

        _detail.AddChild(Ui.Sep());
        var play = Ui.Button(st == CampaignNodeStatus.Completed ? "▶ Переиграть" : "▶ Играть", () => Play(n), st == CampaignNodeStatus.Locked ? Campaigns.WhyLocked(_c, _p, n) : "");
        play.Name = "CampaignPlay";
        play.Disabled = st == CampaignNodeStatus.Locked || sc == null;
        _detail.AddChild(play.MinSize(220, 44));
    }

    /// <summary>Launches the mission: its scenario (with its variant applied to a copy); the battle records its result.</summary>
    public void Play(CampaignNodeDef n)
    {
        if (_c == null || _db.Get<ScenarioDef>(n.Scenario) is not { } sc) return;
        var db = _db;
        if (_db.Get<DifficultyVariantDef>(n.Variant) is { } v)
        {
            var errors = MissionModifiers.Check(_db, sc, v).Where(p => p.Error).ToList();
            if (errors.Count > 0)
            {
                Dialogs.Info(this, "Задание не запускается", string.Join("\n", errors.Select(p => "• " + p.Message)));
                return;
            }
            (db, sc) = MissionModifiers.Prepare(_db, sc, v);
        }
        string campaign = _c.Id, node = n.Id;
        CampaignSession.Last = null;
        CampaignSession.Pending = new CampaignLaunch(campaign, node, sc.Id);
        Main.Instance.ShowBattle(sc, db, false, () => Main.Instance.ShowCampaigns(campaign, node));
    }
}
