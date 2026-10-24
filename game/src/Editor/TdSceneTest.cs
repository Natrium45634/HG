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

    private void ToggleZone(HexCoord c)
    {
        if (_sc == null || _map == null || !_map.Cells.Any(x => x.C == c)) return;
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

    private void DrawPlacement(OverlayLayer o)
    {
        if (_sc == null || _preview == null || Mode is not (TdMode.Placement or TdMode.Test)) return;
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
        var zone = btn("▦ Зона расстановки", () => SetPreset(null), "ЛКМ добавляет или убирает гекс зоны", "TdZoneBrush");
        zone.ToggleMode = true;
        zone.ButtonPressed = PresetUnit == null;
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
        RefreshLibrary();
        Hint();
    }

    private void BuildPlacementProps()
    {
        if (_sc == null) return;
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
        Spin("TdBatchThreads", "потоков (0 — все)", 0, 64, _threads, v => _threads = (int)v);
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
    }

    private void BuildTestProps()
    {
        _props.AddChild(Ui.Label("Результат теста", 15, Ui.Accent));
        _resultText = new RichTextLabel { Name = "TdTestResult", FitContent = true, SelectionEnabled = true, CustomMinimumSize = new Vector2(340, 200), Text = TestResult == "" ? "Запустите автотест или пакет." : TestResult };
        _resultText.AddThemeFontSizeOverride("normal_font_size", 13);
        _props.AddChild(_resultText);
        if (TestSaved != "") _props.AddChild(Ui.Label("Сохранено: " + TestSaved, 12, Ui.TextDim, wrap: true));
    }

    private bool CanTest()
    {
        if (_sc == null) return false;
        Ctx.Validate();
        RunCheck(false);
        var errors = _problems.Where(p => p.Severity == IssueSeverity.Error).ToList();
        if (errors.Count == 0) return true;
        Dialogs.Info(this, "Тест не запущен", "Критические ошибки:\n• " + string.Join("\n• ", errors.Take(8).Select(p => p.Message)));
        return false;
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
        int runs = _runs, threads = _threads;
        ulong seed = _seed;
        var opts = TestOptions();
        _batch = Task.Run(() => AutoBattle.Batch(db, sc, runs, seed, opts, threads, n => Interlocked.Exchange(ref _batchDone, n), token));
        _status.Text = $"Пакетный прогон: {runs} прогонов с seed {seed}…";
        RefreshLibrary();
    }

    private void PollTest()
    {
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
            TestResult = summary.ToText();
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
