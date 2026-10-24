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
/// Context menus of the TD scene (TZ §17.3, docs/DECISIONS_4.md §69): a spawn base — add a wave, assign the goal, a
/// test from this base (besides the settings, a group, a route, the route check, duplicate, delete); a route —
/// edit, join, fork, assign to a group, change the goal, check reachability, delete; a group of a wave — template,
/// count, intervals, route, modifiers, duplicate, disable.
/// </summary>
public partial class TdScenePanel
{
    private PopupMenu _routeMenu = null!;
    private PopupMenu _groupMenu = null!;
    private PopupMenu _pickMenu = null!;
    private string _menuRoute = "";
    private (WaveDef Wave, SpawnGroupDef Group)? _menuGroup;
    private List<string> _pickIds = new();
    private Action<string>? _picked;

    /// <summary>The menus live in the form (rebuilt with it), like the menu of objects.</summary>
    private void BuildMenus(Control root)
    {
        _routeMenu = new PopupMenu { Name = "TdRouteMenu" };
        _routeMenu.IdPressed += id => RouteCommand(_menuRoute, id);
        root.AddChild(_routeMenu);
        _groupMenu = new PopupMenu { Name = "TdGroupMenu" };
        _groupMenu.IdPressed += id =>
        {
            if (_menuGroup is { } mg) GroupCommand(mg.Wave, mg.Group, id);
        };
        root.AddChild(_groupMenu);
        _pickMenu = new PopupMenu { Name = "TdPickMenu" };
        _pickMenu.IdPressed += id =>
        {
            if (id >= 0 && id < _pickIds.Count) _picked?.Invoke(_pickIds[(int)id]);
        };
        root.AddChild(_pickMenu);
    }

    /// <summary>A list to choose from at the mouse (the goal, the route, the template); tests call <see cref="PickFor"/>.</summary>
    private void Pick(IEnumerable<(string Label, string Id)> items, Vector2 at, Action<string> chosen)
    {
        _pickMenu.Clear();
        _pickIds = new List<string>();
        foreach (var (label, id) in items)
        {
            _pickMenu.AddItem(label, _pickIds.Count);
            _pickIds.Add(id);
        }
        _picked = chosen;
        _pickMenu.Position = (Vector2I)at;
        _pickMenu.Popup();
    }

    /// <summary>UI tests: answers the open choice by an ID.</summary>
    public void PickFor(string id) => _picked?.Invoke(id);

    private IEnumerable<(string, string)> Goals() =>
        _sc!.Objects.Where(o => o.Spawn == null).Select(o => ($"{o.DisplayName} [{o.Id}]", o.Id));

    // ------------------------------------------------------------------ the route

    private RouteDef? RouteAt(HexCoord h) =>
        Route(SelectedRoute) is { } sel && sel.AllCells().Contains(h) ? sel : _sc!.Routes.FirstOrDefault(r => r.AllCells().Contains(h));

    private void OpenRouteMenu(RouteDef r, Vector2 at)
    {
        _menuRoute = r.Id;
        _routeMenu.Clear();
        _routeMenu.AddItem($"Изменить «{r.DisplayName}»", 1);
        _routeMenu.AddItem("Соединить с другим маршрутом…", 2);
        _routeMenu.AddItem("Создать развилку", 3);
        _routeMenu.AddItem("Назначить группе…", 4);
        _routeMenu.AddItem("Изменить конечную цель…", 5);
        _routeMenu.AddItem("Проверить достижимость", 6);
        _routeMenu.AddSeparator();
        _routeMenu.AddItem("Удалить маршрут", 7);
        _routeMenu.Position = (Vector2I)at;
        _routeMenu.Popup();
    }

    public void RouteCommand(string routeId, long id)
    {
        var r = Route(routeId);
        if (r == null || _sc == null) return;
        var at = GetViewport().GetMousePosition();
        switch (id)
        {
            case 1:
                SetMode(TdMode.Routes);
                SelectedRoute = r.Id;
                SelectedNode = "";
                RefreshLibrary();
                RefreshProps();
                break;
            case 2:
                Pick(_sc.Routes.Where(x => x != r && x.Nodes.Count > 0).Select(x => (x.DisplayName, x.Id)), at, other =>
                {
                    if (Route(other) is { } b) JoinRoutes(r, b);
                });
                break;
            case 3:
                SetMode(TdMode.Routes);
                SelectedRoute = r.Id;
                RefreshLibrary();
                _status.Text = $"Развилка «{r.DisplayName}»: протяните ЛКМ от узла или от линии маршрута — новая ветка; конец на маршруте — слияние";
                break;
            case 4:
                Pick(_sc.Waves.SelectMany(w => w.Groups.Select(g => (W: w, G: g))).Select(x => ($"{x.W.Name}: {(x.G.Name != "" ? x.G.Name : x.G.Unit)} [{x.G.Id}]" + (x.G.Route == r.Id ? " ✓" : ""), x.G.Id)), at, gid =>
                {
                    var g = _sc.Waves.SelectMany(w => w.Groups).FirstOrDefault(x => x.Id == gid);
                    if (g == null) return;
                    g.Route = r.Id;
                    _status.Text = $"Группа «{(g.Name != "" ? g.Name : g.Id)}» идёт по «{r.DisplayName}»";
                    Edited();
                    RefreshLibrary();
                });
                break;
            case 5:
                Pick(Goals(), at, target =>
                {
                    r.Target = target;
                    _status.Text = $"Конечная цель «{r.DisplayName}»: «{Obj(target)?.DisplayName ?? target}»";
                    Edited(true);
                    RefreshLibrary();
                });
                break;
            case 6:
            {
                var problems = _map == null ? new List<ScenarioProblem>() : ScenarioObjectsCheck.Route(Ctx.Db, _sc, _map, r);
                int errs = problems.Count(p => p.Severity == IssueSeverity.Error);
                _status.Text = errs == 0 ? $"«{r.DisplayName}»: цель «{Obj(r.Target)?.DisplayName ?? "—"}» достижима" + (problems.Count > 0 ? $" (замечаний {problems.Count})" : "")
                    : $"«{r.DisplayName}»: ошибок {errs} — " + string.Join("; ", problems.Where(p => p.Severity == IssueSeverity.Error).Take(3).Select(p => p.Message));
                RunCheck(false);
                break;
            }
            case 7:
            {
                var uses = ChangeImpact.OfRoute(_sc, r.Id);
                void Do()
                {
                    _sc.Routes.Remove(r);
                    if (SelectedRoute == r.Id) SelectedRoute = "";
                    Edited(true);
                    RefreshLibrary();
                    RefreshProps();
                }
                if (uses.Count == 0) Do();
                else Dialogs.Confirm(this, "Удалить маршрут", $"Маршрут «{r.DisplayName}» используют: {string.Join("; ", uses.Select(u => u.Text))}. Ссылки станут ошибками. Удалить?", Do, "Удалить");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ the group of a wave

    private void OpenGroupMenu(WaveDef w, SpawnGroupDef g, Vector2 at)
    {
        _menuGroup = (w, g);
        _groupMenu.Clear();
        _groupMenu.AddItem("Изменить шаблон…", 1);
        _groupMenu.AddItem("Изменить количество…", 2);
        _groupMenu.AddItem("Изменить интервалы…", 3);
        _groupMenu.AddItem("Назначить маршрут…", 4);
        _groupMenu.AddItem("Назначить модификаторы", 5);
        _groupMenu.AddItem("Дублировать группу", 6);
        _groupMenu.AddItem(g.Enabled ? "Отключить группу" : "Включить группу", 7);
        _groupMenu.Position = (Vector2I)at;
        _groupMenu.Popup();
    }

    public void GroupCommand(WaveDef w, SpawnGroupDef g, long id)
    {
        if (_sc == null) return;
        var at = GetViewport().GetMousePosition();
        void Done(string text)
        {
            _status.Text = text;
            Edited();
            RefreshLibrary();
            RefreshProps();
        }
        switch (id)
        {
            case 1:
                Pick(Ctx.Db.All<UnitDef>().Where(u => u.Faction == w.Faction).OrderBy(u => u.DisplayName, StringComparer.CurrentCulture)
                    .Select(u => ($"{u.DisplayName} [{u.Id}]" + (u.Id == ContentRef.IdOf(g.Unit) ? " ✓" : ""), u.Id)), at, uid =>
                {
                    g.Unit = uid;
                    Done($"Шаблон группы: «{Ctx.Db.Unit(uid)?.DisplayName}»");
                });
                break;
            case 2:
                Dialogs.Prompt(this, "Количество", $"Врагов в группе «{(g.Name != "" ? g.Name : g.Id)}»", g.Count.ToString(), s =>
                {
                    if (int.TryParse(s, out var n) && n > 0) Done($"Количество: {g.Count = n}");
                });
                break;
            case 3:
                Dialogs.Prompt(this, "Интервалы", "Интервал между врагами, задержка от начала волны (через пробел), с",
                    $"{g.Interval.ToString(System.Globalization.CultureInfo.InvariantCulture)} {g.Delay.ToString(System.Globalization.CultureInfo.InvariantCulture)}", s =>
                    {
                        var parts = s.Replace(',', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        if (parts.Length > 0 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out var i) && i >= 0) g.Interval = i;
                        if (parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var d) && d >= 0) g.Delay = d;
                        Done($"Интервал {g.Interval:0.##} с, задержка {g.Delay:0.##} с");
                    });
                break;
            case 4:
                Pick(_sc.Routes.Select(r => (r.DisplayName + (r.Id == g.Route ? " ✓" : ""), r.Id)), at, rid =>
                {
                    g.Route = rid;
                    Done($"Маршрут группы: «{Route(rid)?.DisplayName}»");
                });
                break;
            case 5:
                SetMode(TdMode.Waves);
                SelectedWave = w;
                SelectedGroup = g;
                if (g.StatMods.Count == 0) g.StatMods.Add(new StatModDef { Stat = Stats.MoveSpeed });
                Done("Модификаторы экземпляров группы — в свойствах справа («Модификаторы экземпляров»); шаблон не меняется");
                break;
            case 6:
            {
                var copy = ContentJson.Clone(g);
                int n = _sc.Waves.Sum(x => x.Groups.Count) + 1;
                string gid;
                do gid = $"group_{n++}"; while (_sc.Waves.Any(x => x.Groups.Any(y => y.Id == gid)));
                copy.Id = gid;
                copy.FromBudget = false;
                w.Groups.Insert(w.Groups.IndexOf(g) + 1, copy);
                SelectedWave = w;
                SelectedGroup = copy;
                Done($"Группа продублирована: [{copy.Id}]");
                break;
            }
            case 7:
                g.Enabled = !g.Enabled;
                Done(g.Enabled ? "Группа включена" : "Группа отключена: в бою не появляется");
                break;
        }
    }

    // ------------------------------------------------------------------ the spawn base: a test from it

    /// <summary>An autobattle with only the groups of this spawn base (a copy — the scene does not change).</summary>
    public void TestFromBase(ScenarioObjectDef spawn)
    {
        if (!CanTest()) return;
        var db = Ctx.Db.Clone();
        var sc = ContentJson.Clone(_sc!);
        foreach (var w in sc.Waves)
        {
            foreach (var g in w.Groups.Where(g => g.SpawnBase != spawn.Id && !g.ExtraBases.Contains(spawn.Id))) g.Enabled = false;
            if (w.Budget.Enabled && w.Budget.SpawnBase != spawn.Id && !w.Budget.Pinned) w.Budget.Enabled = false;
        }
        var r = AutoBattle.Run(db, sc, _seed == 0 ? 1 : _seed, TestOptions());
        LastReports = new List<BattleReport> { r };
        TestResult = $"Тест с базы «{spawn.DisplayName}» (только её группы):\n" + r.ToText(db);
        SetMode(TdMode.Test);
        _status.Text = $"Тест с базы «{spawn.DisplayName}»: {r.Outcome}, дошли до цели {r.EnemiesReachedGoal}";
    }

    private void AddWaveAt(ScenarioObjectDef spawn)
    {
        var w = new WaveDef { Name = $"Волна {_sc!.Waves.Count + 1}", Start = WaveStart.Time, Time = 3 + 20 * _sc.Waves.Count };
        _sc.Waves.Add(w);
        SelectedWave = w;
        SelectedGroup = null;
        SetMode(TdMode.Waves);
        AddGroup(spawn);
    }

    private void AssignGoal(ScenarioObjectDef spawn, string target)
    {
        int n = 0;
        foreach (var r in _sc!.Routes.Where(r => r.Spawn == spawn.Id))
        {
            r.Target = target;
            n++;
        }
        _status.Text = $"Цель маршрутов базы «{spawn.DisplayName}» ({n}): «{Obj(target)?.DisplayName ?? target}»";
        Edited(true);
        RefreshLibrary();
    }
}
