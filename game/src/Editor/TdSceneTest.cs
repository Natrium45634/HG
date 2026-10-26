using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Part 4, stage 5 in the TD scene (docs/DECISIONS_4.md §64, §69): «Расстановка и зоны» — the battle mode, the
/// zones of the preparation (a click toggles a hex), the preset placement of the autotest; «Тестирование» — one
/// autobattle or a batch (seed = base + i, in the background) on a copy of the draft; reports go to user://reports.
/// </summary>
public partial class TdScenePanel
{
    /// <summary>The template a click puts into the preset placement (null — the click paints the zone).</summary>
    public string? PresetUnit { get; private set; }

    /// <summary>The last test result (text) and where it was saved.</summary>
    public string TestResult { get; private set; } = "";
    public string TestSaved { get; private set; } = "";
    /// <summary>A batch runs in the background.</summary>
    public bool TestRunning => _batch != null && !_batch.IsCompleted;

    private Task<(BatchReport, List<BattleReport>)>? _batch;
    private CancellationTokenSource? _batchCancel;
    private int _batchDone, _batchTotal;
    private int _runs = 10, _threads;
    private ulong _seed;
    private double _maxSeconds = 600;
    private bool _autoPlayer = true;
    private Label? _progress;
    private RichTextLabel? _resultText;

    /// <summary>The zone of deployment a click paints (part 6, §97); null — the zone of placement of part 4.</summary>
    public string? DeployZone { get; private set; }

    private DeploymentZoneDef? SelectedDeployZone => _sc?.DeploymentZones.FirstOrDefault(z => z.Id == DeployZone);

    /// <summary>The fighter the hexes of the zones are checked for (ТЗ ч. 6, 12.2); null — no check.</summary>
    public string? ZoneCheckUnit { get; private set; }
    private Dictionary<HexCoord, string?>? _zoneCheck;

    /// <summary>Per hex of the zones: null — the chosen fighter may be called there, otherwise why not (as the battle starts).</summary>
    public Dictionary<HexCoord, string?>? ZoneCheck
    {
        get
        {
            if (_zoneCheck != null || _sc == null || ZoneCheckUnit == null || Ctx.Db.Unit(ZoneCheckUnit) is not { } def) return _zoneCheck;
            var sim = DeploymentCheck.Battle(Ctx.Db, _sc);
            _zoneCheck = sim == null ? new Dictionary<HexCoord, string?>() : DeploymentCheck.For(sim, _sc, def);
            return _zoneCheck;
        }
    }

    public void SetZoneCheck(string? unit)
    {
        ZoneCheckUnit = unit;
        _zoneCheck = null;
        RefreshLibrary();
    }

    /// <summary>«Сохранить зону как пресет»: a copy into the library, offered to the scenarios of this map.</summary>
    public ZonePresetDef? SaveZonePreset()
    {
        if (_sc == null || SelectedDeployZone is not { } z) return null;
        int n = 1;
        string id;
        do id = $"zone_{_sc.Map}_{n++}"; while (Ctx.Db.Get<ZonePresetDef>(id) != null);
        var p = new ZonePresetDef { Id = id, Name = z.Name != "" ? z.Name : z.Id, Map = _sc.Map, Zone = ContentJson.Clone(z) };
        Ctx.Db.Add(p);
        Ctx.MarkDirty();
        _status.Text = $"Пресет зоны «{p.DisplayName}» [{p.Id}] сохранён в черновик (раздел «Пресеты зон»)";
        RefreshLibrary();
        return p;
    }

    /// <summary>A zone from a preset of this map: copied in, with an ID free in the scenario.</summary>
    public DeploymentZoneDef? AddZoneFromPreset(ZonePresetDef p)
    {
        if (_sc == null) return null;
        var z = ContentJson.Clone(p.Zone);
        string baseId = z.Id != "" ? z.Id : "zone";
        int n = 1;
        z.Id = baseId;
        while (_sc.DeploymentZones.Any(x => x.Id == z.Id)) z.Id = $"{baseId}_{++n}";
        _sc.DeploymentZones.Add(z);
        DeployZone = z.Id;
        PresetUnit = null;
        Edited();
        RefreshLibrary();
        RefreshProps();
        return z;
    }

    private void ToggleZone(HexCoord c)
    {
        if (_sc == null || _map == null || !_map.Cells.Any(x => x.C == c)) return;
        if (SelectedDeployZone is { } dz)
        {
            if (!dz.Cells.Remove(c)) dz.Cells.Add(c);
            _status.Text = $"Зона развёртывания «{dz.Name}»: {(dz.Cells.Contains(c) ? "+" : "−")}{c}; гексов {dz.Cells.Count}";
            Edited();
            RefreshLibrary();
            return;
        }
        var z = _sc.Placement.ZoneCells;
        if (!z.Remove(c)) z.Add(c);
        _status.Text = $"Зона расстановки: {(z.Contains(c) ? "+" : "−")}{c}; гексов в зоне (с объектами): {_sc.Placement.ZoneHexes(_sc).Count}";
        Edited();
        RefreshLibrary();
    }

    private void TogglePreset(HexCoord c, string? unit)
    {
        if (_sc == null) return;
        var pre = _sc.Placement.Preset;
        var at = pre.FirstOrDefault(p => p.C == c);
        if (at != null)
        {
            pre.Remove(at);
            _status.Text = $"Убран из расстановки: «{Ctx.Db.Unit(at.Unit)?.DisplayName ?? at.Unit}» {c}";
        }
        else if (unit != null)
        {
            if (ObjectAt(c) is { } o)
            {
                _status.Text = $"Гекс занят объектом «{o.DisplayName}»";
                return;
            }
            var spawn = _sc.Objects.Where(x => x.Type == ScenarioObjectType.SpawnBase).OrderBy(x => HexCoord.Distance(x.Anchor, c)).FirstOrDefault();
            pre.Add(new ScenarioUnitDef { Unit = unit, C = c, Facing = spawn != null ? c.DirectionTo(spawn.Anchor) ?? HexDirection.W : HexDirection.W });
            var zone = _sc.Placement.ZoneHexes(_sc);
            _status.Text = $"В расстановку: «{Ctx.Db.Unit(unit)?.DisplayName ?? unit}» {c}" + (zone.Count > 0 && !zone.Contains(c) ? " (вне зоны — автотест поставит его всё равно)" : "");
        }
        else return;
        Edited();
        RefreshLibrary();
    }

    /// <summary>The colour of a zone of deployment (by its place in the list).</summary>
    private static Color ZoneColor(int i) => Color.FromHsv((0.47f + i * 0.13f) % 1f, 0.65f, 1f, 0.9f);

    /// <summary>
    /// The zones of deployment: a dashed outline in the colour of the zone that tells on the hex (the highest priority) —
    /// never a fill, so they do not look like an area of an attack; the selected zone brighter, its name at its first hex.
    /// </summary>
    private void DrawDeployZones(OverlayLayer o)
    {
        if (_sc == null || _preview == null || _sc.DeploymentZones.Count == 0) return;
        var zones = _sc.DeploymentZones;
        var hexes = zones.SelectMany(z => z.Cells).Distinct();
        foreach (var c in hexes)
        {
            var top = zones.Where(z => z.Cells.Contains(c)).OrderByDescending(z => z.Priority).First();
            var col = ZoneColor(zones.IndexOf(top));
            bool sel = top.Id == DeployZone;
            o.DashedRing(c, new Color(col, top.Active ? (sel ? 1f : 0.7f) : 0.35f), sel ? 0.09f : 0.06f, 0.86f);
            if (zones.Count(z => z.Cells.Contains(c)) > 1) o.Dot(c, new Color(col, 0.9f), 0.08f);
        }
        foreach (var z in zones.Where(z => z.Cells.Count > 0))
        {
            var cell = _preview.Map.Get(z.Cells[0]);
            _view.Labels.Add(Board.Center(z.Cells[0], (cell == null ? 0 : Board.SurfaceY(cell)) + 0.6f),
                (z.Name != "" ? z.Name : z.Id) + (z.Active ? "" : " (закрыта)"), ZoneColor(zones.IndexOf(z)), 15);
        }
    }

    /// <summary>The check for a chosen fighter: a diamond — it may stand there; hatched — it may not (the reasons on the left).</summary>
    private void DrawZoneCheck(OverlayLayer o)
    {
        if (ZoneCheck is not { } check) return;
        foreach (var (c, why) in check)
            if (why == null) o.Diamond(c, new Color(0.4f, 1f, 0.6f, 0.85f), 0.16f);
            else o.HatchHex(c, new Color(1f, 0.4f, 0.3f, 0.6f), stripes: 4);
    }

    private void DrawPlacement(OverlayLayer o)
    {
        if (_sc == null || _preview == null || Mode is not (TdMode.Placement or TdMode.Test)) return;
        DrawDeployZones(o);
        DrawZoneCheck(o);
        foreach (var c in _sc.Placement.ZoneHexes(_sc)) o.FillHex(c, new Color(0.35f, 0.65f, 1f, 0.2f), 0.9f);
        foreach (var c in _sc.Placement.ZoneCells) o.RingHex(c, new Color(0.5f, 0.8f, 1f, 0.7f), 0.05f);
        foreach (var p in _sc.Placement.Preset)
        {
            o.RingHex(p.C, new Color(0.4f, 1f, 0.55f, 0.9f), 0.09f, 0.8f);
            o.Arrow(p.C, p.Facing, new Color(0.4f, 1f, 0.55f, 0.9f), 0.5f);
            var cell = _preview.Map.Get(p.C);
            _view.Labels.Add(Board.Center(p.C, (cell == null ? 0 : Board.SurfaceY(cell)) + 0.9f), Ctx.Db.Unit(p.Unit)?.DisplayName ?? p.Unit, new Color(0.6f, 1f, 0.7f), 18);
        }
    }

    private void BuildPlacementLibrary(Func<string, Action, string, string, Button> btn)
    {
        if (_sc == null) return;
        var pl = _sc.Placement;
        _library.AddChild(Ui.Label("Клик по гексу:", 13, Ui.TextDim));
        var zone = btn("▦ Зона расстановки", () =>
        {
            DeployZone = null;
            SetPreset(null);
        }, "ЛКМ добавляет или убирает гекс зоны", "TdZoneBrush");
        zone.ToggleMode = true;
        zone.ButtonPressed = PresetUnit == null && DeployZone == null;
        // Part 6 (§97): the zones of deployment for the calls during the battle — several, with tags, phase and priority.
        _library.AddChild(Ui.Label("Зоны развёртывания (вызов за очки):", 13, Ui.TextDim));
        for (int i = 0; i < _sc.DeploymentZones.Count; i++)
        {
            var dz = _sc.DeploymentZones[i];
            string id = dz.Id;
            var zb = btn($"◇ {(dz.Name != "" ? dz.Name : dz.Id)} ({dz.Cells.Count})" + (dz.Active ? "" : " · закрыта"), () =>
            {
                PresetUnit = null;
                DeployZone = DeployZone == id ? null : id;
                RefreshLibrary();
                RefreshProps();
                Hint();
            }, "ЛКМ по гексу добавляет или убирает его в этой зоне; свойства зоны — справа", "TdDeployZone_" + id);
            zb.ToggleMode = true;
            zb.ButtonPressed = DeployZone == id;
            zb.AddThemeColorOverride("font_color", ZoneColor(i));
        }
        btn("+ Зона развёртывания", () =>
        {
            int n = 1;
            while (_sc.DeploymentZones.Any(z => z.Id == $"zone_{n}")) n++;
            _sc.DeploymentZones.Add(new DeploymentZoneDef { Id = $"zone_{n}", Name = $"Зона {n}" });
            DeployZone = $"zone_{n}";
            PresetUnit = null;
            Edited();
            RefreshLibrary();
            RefreshProps();
            Hint();
        }, "Новая зона; затем ЛКМ по гексам карты", "TdDeployZoneAdd");
        if (SelectedDeployZone is { } selz)
            btn($"✗ Удалить зону «{(selz.Name != "" ? selz.Name : selz.Id)}»", () =>
            {
                _sc.DeploymentZones.Remove(selz);
                DeployZone = null;
                Edited();
                RefreshLibrary();
                RefreshProps();
                Hint();
            }, "Ссылки триггеров на неё покажет проверка", "TdDeployZoneRemove");
        if (SelectedDeployZone != null) btn("💾 Сохранить зону как пресет", () => SaveZonePreset(), "В библиотеку («Пресеты зон»): её можно взять в другой сценарий этой карты", "TdZoneSavePreset");
        var presets = Ctx.Db.All<ZonePresetDef>().Where(p => p.Map == _sc.Map).ToList();
        if (presets.Count > 0)
        {
            var pick = new OptionButton { Name = "TdZoneFromPreset", FocusMode = FocusModeEnum.None };
            pick.AddItem("+ Зона из пресета…");
            foreach (var p in presets) pick.AddItem($"{p.DisplayName} ({p.Zone.Cells.Count} гекс.)");
            pick.ItemSelected += i =>
            {
                if (i > 0) AddZoneFromPreset(presets[(int)i - 1]);
            };
            _library.AddChild(pick);
        }
        var eff = EffectiveGameRules.Resolve(Ctx.Db, _sc);
        if (_sc.DeploymentZones.Count > 0 && !eff.Deployment.Enabled)
            _library.AddChild(Ui.Label("⚠ Вызов за очки выключен в правилах режима сценария — зоны развёртывания не действуют.", 12, Ui.Warn, wrap: true));
        // Which hexes take a chosen fighter, and why not the others (ТЗ ч. 6, 12.2).
        _library.AddChild(Ui.Label("Проверить гексы для бойца:", 13, Ui.TextDim));
        var who = new OptionButton { Name = "TdZoneCheckUnit", FocusMode = FocusModeEnum.None };
        who.AddItem("— не проверять");
        var pool = (_sc.Squad is { Enabled: true } ? SquadRules.PoolOf(Ctx.Db, _sc) : Ctx.Db.All<UnitDef>().Where(u => u.Faction == "player")).ToList();
        foreach (var u in pool) who.AddItem(u.DisplayName);
        int at = pool.FindIndex(u => u.Id == ZoneCheckUnit);
        who.Selected = at + 1;
        who.ItemSelected += i => SetZoneCheck(i <= 0 ? null : pool[(int)i - 1].Id);
        _library.AddChild(who);
        if (ZoneCheck is { } check)
        {
            int ok = check.Values.Count(v => v == null);
            var reasons = check.Values.Where(v => v != null).GroupBy(v => v!).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})");
            var sum = Ui.Label($"Можно: {ok} гекс.; нельзя: {check.Count - ok}" + (check.Count > ok ? " — " + string.Join("; ", reasons) : ""), 12, ok > 0 ? Ui.Text : Ui.Bad, wrap: true);
            sum.Name = "TdZoneCheckSummary";
            _library.AddChild(sum);
        }
        var unreachable = DeploymentCheck.Unreachable(Ctx.Db, _sc);
        if (_sc.DeploymentZones.Count > 0)
        {
            var reach = Ui.Label(unreachable.Count == 0 ? "✓ Из каждой зоны можно дойти до цели" : string.Join("\n", unreachable.Select(u => "⚠ " + u)), 12, unreachable.Count == 0 ? Ui.Good : Ui.Warn, wrap: true);
            reach.Name = "TdZoneReach";
            _library.AddChild(reach);
        }
        _library.AddChild(Ui.Label("Расстановка по умолчанию (автотест):", 13, Ui.TextDim));
        var units = pl.Units.Select(u => u.Unit).Concat(Ctx.Db.All<UnitDef>().Where(u => u.Faction == "player").Select(u => u.Id)).Distinct().ToList();
        foreach (var id in units)
        {
            var ud = Ctx.Db.Unit(id);
            if (ud == null) continue;
            string uid = id;
            bool avail = pl.Units.Any(u => u.Unit == id);
            var b = btn($"⚑ {ud.DisplayName}{(avail ? "" : " (не в списке доступных)")}", () => SetPreset(uid), "ЛКМ ставит его в расстановку по умолчанию, ПКМ убирает", "TdPresetPick_" + id);
            b.ToggleMode = true;
            b.ButtonPressed = PresetUnit == id;
        }
        _library.AddChild(Ui.Sep());
        _library.AddChild(Ui.Label($"Зона: {pl.ZoneHexes(_sc).Count} гекс. (своих {pl.ZoneCells.Count}) · в расстановке: {pl.Preset.Count}", 12, Ui.TextDim, wrap: true));
        btn("✗ Очистить гексы зоны", () =>
        {
            pl.ZoneCells.Clear();
            Edited();
            RefreshLibrary();
        }, "Зоны объектов остаются", "TdZoneClear");
        btn("✗ Очистить расстановку", () =>
        {
            pl.Preset.Clear();
            Edited();
            RefreshLibrary();
        }, "", "TdPresetClear");
    }

    private void SetPreset(string? unit)
    {
        PresetUnit = unit;
        if (unit != null) DeployZone = null;
        RefreshLibrary();
        Hint();
    }

    private void BuildPlacementProps()
    {
        if (_sc == null) return;
        // The selected zone of deployment first: its tags, phase and priority are what a click is about now.
        if (SelectedDeployZone is { } dz)
        {
            _props.AddChild(Ui.Label($"Зона развёртывания «{(dz.Name != "" ? dz.Name : dz.Id)}»", 15, Ui.Accent));
            var zg = Grid(dz, RefreshLibrary);
            zg.Name = "TdDeployZoneGrid";
            _props.AddChild(zg);
            _props.AddChild(Ui.Label("На пересечении зон действуют ограничения всех; приоритет — порядок проверки и причина отказа. " +
                                     "Открыть и закрыть зону в бою — действия триггера «Открыть/Закрыть зону развёртывания».", 12, Ui.TextDim, wrap: true));
            _props.AddChild(Ui.Sep());
        }
        _props.AddChild(Ui.Label("Режим боя и подготовка", 15, Ui.Accent));
        var mode = new OptionButton { Name = "TdBattleMode", FocusMode = FocusModeEnum.None };
        var modes = Enum.GetValues<BattleMode>();
        foreach (var m in modes) mode.AddItem(EdUtil.EnumLabel(m));
        mode.Selected = Array.IndexOf(modes, _sc.Mode);
        mode.ItemSelected += i =>
        {
            _sc.Mode = modes[i];
            _status.Text = "Режим боя: " + EdUtil.EnumLabel(_sc.Mode);
            Edited();
        };
        _props.AddChild(mode);
        var grid = Grid(_sc.Placement, RefreshLibrary);
        grid.Choices[nameof(PlacementDef.ZoneObjects)] = () => ObjectChoices(_ => true);
        grid.Name = "TdPlacementGrid";
        _props.AddChild(grid);
        _props.AddChild(Ui.Label("Альтернативные цели врагов и «база недоступна через» — в свойствах сцены (группа «Автобой»).", 12, Ui.TextDim, wrap: true));
    }

    // ------------------------------------------------------------------ tests

    private void BuildTestLibrary(Func<string, Action, string, string, Button> btn)
    {
        if (_sc == null) return;
        if (_seed == 0) _seed = _sc.Seed != 0 ? _sc.Seed : 1;
        btn("▶ Автотест: 1 прогон", RunAutoTest, "Без игрока: защитники по профилям ИИ и расстановке по умолчанию", "TdAutoTest");
        _library.AddChild(Ui.Sep());
        _library.AddChild(Ui.Label("Пакетный прогон:", 13, Ui.TextDim));
        SpinBox Spin(string name, string prefix, double min, double max, double value, Action<double> set)
        {
            var sb = new SpinBox { Name = name, MinValue = min, MaxValue = max, Value = value, Prefix = prefix, CustomMinimumSize = new Vector2(200, 0) };
            sb.ValueChanged += v => set(v);
            _library.AddChild(sb);
            return sb;
        }
        Spin("TdBatchRuns", "прогонов", 1, 1000, _runs, v => _runs = (int)v);
        Spin("TdBatchSeed", "seed от", 1, 1_000_000_000, _seed, v => _seed = (ulong)v);
        Spin("TdBatchThreads", "потоков (0 — из правил)", 0, 64, _threads, v => _threads = (int)v);
        Spin("TdTestMaxSeconds", "не дольше, с", 10, 7200, _maxSeconds, v => _maxSeconds = v);
        var ai = new CheckBox { Text = "Защитники по профилям ИИ", ButtonPressed = _autoPlayer, Name = "TdTestAutoPlayer" };
        ai.Toggled += on => _autoPlayer = on;
        _library.AddChild(ai);
        var run = btn(TestRunning ? "■ Остановить пакет" : "▶▶ Пакетный прогон", () =>
        {
            if (TestRunning) _batchCancel?.Cancel();
            else RunBatch();
        }, "N прогонов с seed = база + i; результат не зависит от числа потоков", "TdBatch");
        _progress = Ui.Label(TestRunning ? $"Идёт: {_batchDone}/{_batchTotal}" : "", 12, Ui.TextDim, wrap: true);
        _progress.Name = "TdBatchProgress";
        _library.AddChild(_progress);
        _library.AddChild(Ui.Sep());
        btn("⟲ Проверить реплей…", PickReplay, "Реплей боя (меню боя → «Сохранить реплей»): тот же seed и действия игрока по тикам — совпал ли конец и что изменилось в контенте", "TdReplay");
    }

    private static string ReplaysDir => ProjectSettings.GlobalizePath("user://replays");

    private void PickReplay()
    {
        System.IO.Directory.CreateDirectory(ReplaysDir);
        var fd = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, CurrentDir = ReplaysDir,
            Filters = new[] { "*.hexreplay ; Реплей боя" }, Title = "Реплей боя",
        };
        fd.FileSelected += path =>
        {
            CheckReplay(path);
            fd.QueueFree();
        };
        fd.Canceled += fd.QueueFree;
        AddChild(fd);
        fd.PopupCentered(new Vector2I(820, 520));
    }

    /// <summary>Plays a replay back on its saved content and on the current draft: does the end match, what changed.</summary>
    public void CheckReplay(string path)
    {
        BattleReplay? rep;
        try
        {
            rep = BattleReplay.FromJson(System.IO.File.ReadAllText(path));
        }
        catch (Exception e)
        {
            rep = null;
            TestResult = $"Реплей не прочитан: {e.Message}";
        }
        if (rep != null)
        {
            var lines = new List<string> { $"Реплей «{System.IO.Path.GetFileName(path)}»: сценарий «{rep.Scenario}», seed {rep.Seed}, тиков {rep.EndTick}, действий игрока {rep.Entries.Count}" };
            try
            {
                if (rep.Content != null) lines.Add("На сохранённом контенте: " + rep.Play().Text);
                var draft = rep.Play(Ctx.Db.Clone());
                lines.Add("На текущем черновике: " + draft.Text);
            }
            catch (Exception e)
            {
                lines.Add($"Воспроизведение прервано: {e.GetType().Name}: {e.Message}");
            }
            TestResult = string.Join("\n", lines);
        }
        _status.Text = TestResult.Split('\n')[0];
        RefreshProps();
    }

    private void BuildTestProps()
    {
        _props.AddChild(Ui.Label("Результат теста", 15, Ui.Accent));
        _resultText = new RichTextLabel { Name = "TdTestResult", FitContent = true, SelectionEnabled = true, CustomMinimumSize = new Vector2(340, 200), Text = TestResult == "" ? "Запустите автотест или пакет." : TestResult };
        _resultText.AddThemeFontSizeOverride("normal_font_size", 13);
        _props.AddChild(_resultText);
        if (TestSaved != "") _props.AddChild(Ui.Label("Сохранено: " + TestSaved, 12, Ui.TextDim, wrap: true));
        AddPerformance();
    }

    private bool CanTest()
    {
        if (_sc == null) return false;
        Ctx.Validate();
        RunCheck(false);
        var errors = _problems.Where(p => p.Severity == IssueSeverity.Error).ToList();
        if (errors.Count == 0) return true;
        // ТЗ ч. 5, 26.4: an explicit «небезопасный тест» without the player, with the ignored errors first in its report.
        Dialogs.Confirm(this, "Тест не запущен", "Критические ошибки:\n• " + string.Join("\n• ", errors.Take(8).Select(p => p.Message)) +
            "\n\nМожно провести «небезопасный тест»: один прогон без игрока, ошибки — в начале отчёта, сбой симуляции не роняет редактор.", RunUnsafeTest, "Небезопасный тест");
        return false;
    }

    /// <summary>One run despite the errors (the draft is not changed).</summary>
    public void RunUnsafeTest()
    {
        if (_sc == null) return;
        var db = Ctx.Db.Clone();
        var sc = db.Get<ScenarioDef>(_sc.Id) ?? ContentJson.Clone(_sc);
        var res = UnsafeTest.Run(db, sc, _maxSeconds, _seed);
        TestResult = res.Text + (res.Report != null ? "\n\n" + res.Report.ToText(db) : "");
        TestSaved = res.Report != null ? AutoBattle.Save(ReportsDir, res.Report, db) : "";
        _status.Text = $"Небезопасный тест: ошибок проигнорировано {res.Ignored.Count}, {(res.Exceptions.Count > 0 ? "сбои: " + res.Exceptions.Count : "без сбоев")}";
        RefreshProps();
    }

    private AutoBattleOptions TestOptions() => new() { MaxSeconds = _maxSeconds, AutoPlayer = _autoPlayer, PlacePreset = true };

    private static string ReportsDir => ProjectSettings.GlobalizePath("user://reports");

    /// <summary>One autobattle on a copy of the draft (the content does not change).</summary>
    public void RunAutoTest()
    {
        if (!CanTest()) return;
        var db = Ctx.Db.Clone();
        var sc = db.Get<ScenarioDef>(_sc!.Id) ?? ContentJson.Clone(_sc);
        var started = Time.GetTicksMsec();
        var r = AutoBattle.Run(db, sc, _seed, TestOptions());
        LastReports = new List<BattleReport> { r };
        TestResult = r.ToText(db);
        TestSaved = AutoBattle.Save(ReportsDir, r, db);
        _status.Text = $"Автотест: {r.Outcome}, волн {r.WavesCompleted}/{r.WavesTotal}, {r.Seconds:0.#} с боя за {Time.GetTicksMsec() - started} мс";
        RefreshProps();
    }

    /// <summary>A batch in the background; the progress and the summary appear when ready.</summary>
    public void RunBatch()
    {
        if (TestRunning || !CanTest()) return;
        var db = Ctx.Db.Clone();
        var sc = db.Get<ScenarioDef>(_sc!.Id) ?? ContentJson.Clone(_sc);
        _batchDone = 0;
        _batchTotal = _runs;
        _batchCancel = new CancellationTokenSource();
        var token = _batchCancel.Token;
        int runs = _runs, threads = _threads > 0 ? _threads : Ctx.Db.Rules.Performance.ParallelRuns;
        ulong seed = _seed;
        var opts = TestOptions();
        _batch = Task.Run(() => AutoBattle.Batch(db, sc, runs, seed, opts, threads, n => Interlocked.Exchange(ref _batchDone, n), token));
        _status.Text = $"Пакетный прогон: {runs} прогонов с seed {seed}…";
        RefreshLibrary();
    }

    private void PollTest()
    {
        PollProfile();
        if (_batch == null) return;
        if (!_batch.IsCompleted)
        {
            if (_progress != null && IsInstanceValid(_progress)) _progress.Text = $"Идёт: {_batchDone}/{_batchTotal}";
            return;
        }
        var t = _batch;
        _batch = null;
        if (t.IsFaulted)
        {
            TestResult = "Ошибка пакетного прогона: " + t.Exception?.GetBaseException().Message;
            _status.Text = TestResult;
        }
        else
        {
            var (summary, reports) = t.Result;
            LastReports = reports;
            TestResult = summary.ToText() + CompareBatches(summary);
            TestSaved = AutoBattle.Save(ReportsDir, summary, reports);
            _status.Text = $"Пакет: {summary.RunsCount} прогонов, побед {summary.WinRate:P0}, волн в среднем {summary.MeanWaves:0.#}";
        }
        if (Mode == TdMode.Test)
        {
            RefreshLibrary();
            RefreshProps();
        }
    }
}
