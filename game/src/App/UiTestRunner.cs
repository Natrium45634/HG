using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Battle;
using HexTactics.Game.Editor;

namespace HexTactics.Game.App;

/// <summary>
/// End-to-end UI tests driven by real input events (mouse clicks on hexes and buttons, keys).
/// Run: godot --path game -- --uitest=all (or a test name). Prints PASS/FAIL and exits with code 0/1.
/// Tests work on the in-memory draft only: nothing is published or written to the content folders.
/// </summary>
public partial class UiTestRunner : Node
{
    private readonly string _which;
    private readonly List<(string Name, Func<Task> Body)> _tests = new();
    private int _failed;
    private string _current = "";

    public UiTestRunner(string which)
    {
        // "--uitest" without a value arrives as "1": run everything.
        _which = string.IsNullOrEmpty(which) || which == "1" ? "all" : which;
    }

    public override void _Ready()
    {
        _tests.Add(("battle_select_and_move", BattleSelectAndMove));
        _tests.Add(("battle_waypoint_action", BattleWaypointAction));
        _tests.Add(("battle_skill_via_hotkey", BattleSkillViaHotkey));
        _tests.Add(("battle_ui_blocks_world_clicks", BattleUiBlocksWorld));
        // Part 3 (docs/DECISIONS_3.md): control of a single unit.
        _tests.Add(("battle_arrow_turn_and_route_gesture", BattleArrowGesture));
        _tests.Add(("battle_planning_autopause_and_cancel", BattlePlanningPauseAndCancel));
        _tests.Add(("battle_alternative_control_without_gestures", BattleAlternativeControl));
        _tests.Add(("battle_enemy_hex_radial_menu_and_aim", BattleRadialMenuAndAim));
        _tests.Add(("battle_auto_skill_and_aim_from_icon", BattleAutoSkillAndIconAim));
        _tests.Add(("battle_group_box_select_and_orders", BattleGroupOrders));
        _tests.Add(("battle_defense_skill_and_series", BattleDefenseSkillAndSeries));
        _tests.Add(("editor_skill_series_test_run", EditorSkillSeriesTestRun));
        _tests.Add(("battle_passives_and_talents_panel", BattlePassivesPanel));
        _tests.Add(("editor_talents_origins_used_by", EditorTalentsOrigins));
        _tests.Add(("editor_skill_mask_click", EditorSkillMaskClick));
        _tests.Add(("editor_map_paint", EditorMapPaint));
        _tests.Add(("editor_create_delete", EditorCreateDelete));
        _tests.Add(("editor_new_map_standard_template", EditorNewMapStandard));
        _tests.Add(("editor_save_map_as_template", EditorSaveAsTemplate));
        _tests.Add(("battle_fit_map_to_screen", BattleFitMap));
        _tests.Add(("battle_layers_melee_vs_flyer", BattleLayersMeleeVsFlyer));
        _tests.Add(("battle_stack_select_and_enemy_info", BattleStackAndEnemyInfo));
        _tests.Add(("battle_blocking_display", BattleBlockingDisplay));
        _tests.Add(("battle_shield_and_damage", BattleShieldAndDamage));
        _tests.Add(("editor_element_inheritance", EditorElementInheritance));
        _tests.Add(("battle_autoattack_panel_toggle", BattleAutoAttackPanel));
        _tests.Add(("battle_spawn_warning_and_objectives", BattleSpawnWarningObjectives));
        _tests.Add(("editor_route_tool", EditorRouteTool));
        _tests.Add(("editor_catalog_and_publish_gate", EditorCatalogAndGate));
        _tests.Add(("battle_models_animations_fallback", BattleModelsAndFallback));
        _tests.Add(("editor_asset_import", EditorAssetImport));
        _tests.Add(("editor_import_window_assign_requirements", EditorImportWindow));
        _tests.Add(("battle_projectiles_loop_vfx_icons", BattleProjectilesAndLoops));
        _tests.Add(("battle_speeds_and_pause_keep_every_event", BattleSpeedsAndPause));
        // Part 4 (docs/DECISIONS_4.md): the TD scene.
        _tests.Add(("editor_td_scene_bases_route_group_battle", EditorTdScene));
        _tests.Add(("editor_td_waves_library_presets_budget_battle", EditorTdWaves));
        _tests.Add(("editor_td_route_graph_fork_merge_battle", EditorTdRouteGraph));
        _tests.Add(("editor_category_tree_rules_and_pack_battle", EditorCategoryTree));
        // Last: it publishes into the sandbox user pack (Main runs UI tests on a fresh copy of user data).
        _tests.Add(("editor_library_publish_versions", EditorLibraryPublish));
        _tests.Add(("editor_package_export_import", EditorPackageExportImport));
        // The acceptance path of part 2 (TZ «Итоговый критерий»): everything through the game UI.
        _tests.Add(("admin_part3_series_skill_talent_publish_play", AdminPart3Flow));
        _tests.Add(("admin_full_flow_create_save_export_import_play", AdminFullFlow));
        _ = Run();
    }

    private async Task Run()
    {
        await Frames(3);
        int ran = 0;
        foreach (var (name, body) in _tests)
        {
            // One name, several names separated by commas, or «all».
            if (_which != "all" && !_which.Split(',', StringSplitOptions.TrimEntries).Contains(name)) continue;
            ran++;
            _current = name;
            try
            {
                // A test that stops progressing fails instead of hanging the run.
                var task = body();
                ulong started = Time.GetTicksMsec();
                while (!task.IsCompleted)
                {
                    if (Time.GetTicksMsec() - started > 120_000) throw new TimeoutException("тест не завершился за 120 с");
                    await Frames(1);
                }
                await task;
                GD.Print($"PASS {name}");
            }
            catch (Exception e)
            {
                _failed++;
                GD.PrintErr($"FAIL {name}: {e.Message}");
            }
        }
        if (ran == 0)
        {
            _failed++;
            GD.PrintErr($"FAIL: нет теста '{_which}'. Доступны: {string.Join(", ", _tests.Select(t => t.Item1))}");
        }
        GD.Print(_failed == 0 ? $"UI TESTS: ALL PASSED ({ran})" : $"UI TESTS: {_failed} FAILED");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    // ------------------------------------------------------------------ helpers

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void Check(bool cond, string message)
    {
        if (!cond) throw new Exception(message);
    }

    private void Mouse(Vector2 pos, MouseButton button)
    {
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = button, Pressed = true });
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = button, Pressed = false });
    }

    private async Task Click(Vector2 pos, MouseButton button = MouseButton.Left)
    {
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
        await Frames(2);
        Mouse(pos, button);
        await Frames(3);
    }

    private async Task Key(Key k, bool shift = false)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true, ShiftPressed = shift });
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false, ShiftPressed = shift });
        await Frames(3);
    }

    private static IEnumerable<T> Find<T>(Node root) where T : Node
    {
        foreach (var c in root.GetChildren())
        {
            if (c is T t) yield return t;
            foreach (var x in Find<T>(c)) yield return x;
        }
    }

    private async Task ClickButton(string text)
    {
        var b = Find<Button>(GetTree().Root).FirstOrDefault(x => x.IsVisibleInTree() && !x.Disabled && x.Text.Contains(text, StringComparison.Ordinal));
        Check(b != null, $"кнопка «{text}» не найдена");
        await Click(b!.GetGlobalRect().GetCenter());
    }

    private async Task PressButton(string text)
    {
        var b = Find<Button>(GetTree().Root).LastOrDefault(x => x.IsVisibleInTree() && !x.Disabled && x.Text.Contains(text, StringComparison.Ordinal));
        Check(b != null, $"кнопка «{text}» не найдена");
        b!.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
    }

    private BattleScreen Battle()
    {
        var bs = Find<BattleScreen>(GetTree().Root).FirstOrDefault();
        Check(bs != null, "экран боя не открыт");
        return bs!;
    }

    private static Vector2 HexScreen(BattleScreen bs, HexCoord c)
    {
        var cell = bs.Sim.Map.Get(c);
        var p = Render.Board.Center(c, Render.Board.PickY(cell!));
        return bs.Board.Rig.Camera.UnprojectPosition(p);
    }

    private async Task<BattleScreen> OpenBattle(string scenario)
    {
        var db = Main.Instance.Content.Published;
        Main.Instance.ShowBattle(db.Get<ScenarioDef>(scenario)!, db, false);
        await Frames(6);
        return Battle();
    }

    // ------------------------------------------------------------------ battle tests

    private async Task BattleSelectAndMove()
    {
        var bs = await OpenBattle("sandbox");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(knight));
        await Frames(10);
        await Click(HexScreen(bs, knight.Pos));
        Check(bs.Selected == knight, "клик по юниту не выбрал его");
        await Key(Godot.Key.M);
        Check(bs.Mode == InputMode.Move, "M не включил режим движения");
        var target = knight.Pos + new HexCoord(2, 0);
        await Click(HexScreen(bs, target));
        Check(knight.Queue.Count == 1 && knight.Queue.Items[0].Type == CommandType.Move, "клик по гексу не добавил движение");
        await Key(Godot.Key.Space);
        Check(!bs.Driver.Paused, "пробел не снял паузу");
        for (int i = 0; i < 200 && knight.Pos != target; i++) bs.Sim.Step();
        Check(knight.Pos == target, $"юнит не дошёл до {target}, стоит в {knight.Pos}");
        await Key(Godot.Key.Space);
        Check(bs.Driver.Paused, "пробел не поставил паузу");
    }

    private async Task BattleWaypointAction()
    {
        var bs = await OpenBattle("sandbox");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        bs.Select(knight);
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(knight));
        await Frames(8);
        await Key(Godot.Key.M);
        var a = knight.Pos + new HexCoord(2, 0);
        var b = knight.Pos + new HexCoord(4, -1);
        await Click(HexScreen(bs, a));
        await Click(HexScreen(bs, b));
        await Click(Vector2.Zero, MouseButton.Right); // leave move mode
        Check(bs.Mode == InputMode.None, "ПКМ не вышел из режима");
        Check(knight.Queue.Count == 2, $"ожидалось 2 точки маршрута, в очереди {knight.Queue.Count}");
        // Insert a turn after the first waypoint (the waypoint menu calls Activate with an insert index).
        var turn = bs.Sim.Planner.Actions(knight).First(x => x.Key == "turn");
        bs.Activate(turn, 1);
        Check(bs.Mode == InputMode.Turn, "не включился режим поворота после точки");
        await Click(HexScreen(bs, a + new HexCoord(0, -1)));
        var types = knight.Queue.Items.Select(c => c.Type).ToList();
        Check(types.SequenceEqual(new[] { CommandType.Move, CommandType.Turn, CommandType.Move }), "порядок очереди: " + string.Join(",", types));
    }

    private async Task BattleSkillViaHotkey()
    {
        var bs = await OpenBattle("sandbox");
        var mage = bs.Sim.Units.First(u => u.Def.Id == "mage");
        bs.Select(mage);
        bs.Board.Rig.FocusOn(Render.Board.Center(mage.Pos + new HexCoord(3, 0)));
        await Frames(8);
        await Key(Godot.Key.Key1);
        Check(bs.Mode == InputMode.Skill && bs.ModeSkill?.Id == "fireball", "клавиша 1 не выбрала первый навык (огненный шар)");
        var target = mage.Pos + new HexCoord(4, 0);
        await Click(HexScreen(bs, target));
        Check(mage.Queue.Count == 1 && mage.Queue.Items[0].SkillId == "fireball" && mage.Queue.Items[0].TargetCell == target, "навык не поставлен в очередь");
        for (int i = 0; i < 60; i++) bs.Sim.Step();
        Check(bs.Sim.Map.Get(target)!.HasEffect("fire") || bs.Sim.Log.Entries.Any(e => e.Type == SimEventType.CastReleased), "огненный шар не сработал");
    }

    private async Task BattleUiBlocksWorld()
    {
        var bs = await OpenBattle("sandbox");
        var mage = bs.Sim.Units.First(u => u.Def.Id == "mage");
        bs.Select(mage);
        await Frames(4);
        await Key(Godot.Key.M);
        // Click on the top bar (over the map) must not create a move command.
        var top = Find<Button>(GetTree().Root).First(x => x.IsVisibleInTree() && x.Text.Contains("Журнал"));
        var pos = top.GetGlobalRect().GetCenter();
        Mouse(pos, MouseButton.Left);
        await Frames(3);
        Check(mage.Queue.Count == 0, "клик по панели интерфейса прошёл в карту");
    }

    // ------------------------------------------------------------------ part 3: control (docs/DECISIONS_3.md §39–§41)

    /// <summary>LMB drag through several mouse moves (press, motions, release), optionally with RMB before the release.</summary>
    private async Task Drag(Vector2 from, Vector2 to, int steps = 8, bool rightBeforeRelease = false)
    {
        Input.ParseInputEvent(new InputEventMouseMotion { Position = from, GlobalPosition = from });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = from, GlobalPosition = from, ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames(2);
        for (int i = 1; i <= steps; i++)
        {
            var p = from.Lerp(to, (float)i / steps);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = MouseButtonMask.Left });
            await Frames(1);
        }
        if (rightBeforeRelease)
        {
            Input.ParseInputEvent(new InputEventMouseButton { Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Right, Pressed = true });
            Input.ParseInputEvent(new InputEventMouseButton { Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Right, Pressed = false });
            await Frames(2);
        }
        Input.ParseInputEvent(new InputEventMouseButton { Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames(3);
    }

    private static Vector2 ArrowScreen(BattleScreen bs, Render.FacingArrow a) => bs.Board.Rig.Camera.UnprojectPosition(a.GrabPoint());

    private async Task BattleArrowGesture()
    {
        var bs = await OpenBattle("sandbox");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        var mage = bs.Sim.Units.First(u => u.Def.Id == "mage");
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(knight));
        await Frames(10);
        // Selecting by click while the game runs pauses it (planning autopause).
        bs.Select(null);
        bs.Resume();
        await Click(HexScreen(bs, mage.Pos));
        Check(bs.Selected == mage, "клик не выбрал мага");
        Check(bs.Driver.Paused && bs.AutoPaused, "выбор юнита не поставил игру на паузу");
        await Click(HexScreen(bs, knight.Pos));
        Check(bs.Selected == knight, "клик не выбрал рыцаря");
        long tick = bs.Sim.Tick;
        await Click(HexScreen(bs, knight.Pos)); // repeated click: nothing changes, time does not run
        await Frames(5);
        Check(bs.Selected == knight && bs.Driver.Paused && bs.Sim.Tick == tick, "повторный клик по выделенному юниту что-то изменил");
        Check(bs.HeadArrow.Visible, "стрелка взгляда не показана");

        // Short gesture: turning (a free, instant turn order by default).
        var start = ArrowScreen(bs, bs.HeadArrow);
        var west = HexScreen(bs, knight.Pos + HexDirection.W.Offset());
        var unitScreen = HexScreen(bs, knight.Pos);
        var turnTo = unitScreen + (west - unitScreen) * 0.6f;
        await Drag(start, turnTo);
        Check(knight.Queue.Count == 1 && knight.Queue.Items[0].Type == CommandType.Turn && knight.Queue.Items[0].Direction == HexDirection.W,
            "короткий жест не создал приказ поворота на запад: " + string.Join(", ", knight.Queue.Items.Select(c => c.Describe(bs.Sim))));
        bs.Sim.Step();
        Check(knight.Facing == HexDirection.W, "юнит не повернулся");

        // Long gesture: a route to a hex three steps away.
        var target = knight.Pos + new HexCoord(3, 0);
        await Drag(ArrowScreen(bs, bs.HeadArrow), HexScreen(bs, target), 12);
        Check(knight.Queue.Count == 1 && knight.Queue.Items[0].Type == CommandType.Move && knight.Queue.Items[0].TargetCell == target,
            "длинный жест не построил маршрут: " + string.Join(", ", knight.Queue.Items.Select(c => c.Describe(bs.Sim))) + $" · сообщение: {bs.Hud.LastBanner} · курсор над {bs.HoverCell?.Coord}, цель {target}");
        Check(knight.Queue.Items[0].Routing == RouteMode.MinCost, "приказ движения без режима маршрута игрока");
        await Frames(3);
        Check(bs.HeadArrow.Cell == target, "стрелка не появилась в конечной точке маршрута");

        // At the end point: a short gesture sets the facing after arrival.
        var endScreen = HexScreen(bs, target);
        var sw = HexScreen(bs, target + HexDirection.SW.Offset());
        await Drag(ArrowScreen(bs, bs.HeadArrow), endScreen + (sw - endScreen) * 0.6f);
        Check(knight.Queue.Items[0].ArrivalFacing == HexDirection.SW, "направление после прибытия не задано");

        // Another long gesture continues the route (several points).
        var second = target + new HexCoord(0, 2);
        await Drag(ArrowScreen(bs, bs.HeadArrow), HexScreen(bs, second), 12);
        Check(knight.Queue.Count == 2 && knight.Queue.Items[1].TargetCell == second, "вторая точка маршрута не добавлена");

        // RMB during the gesture cancels it.
        await Drag(ArrowScreen(bs, bs.HeadArrow), HexScreen(bs, second + new HexCoord(2, 0)), 10, rightBeforeRelease: true);
        Check(knight.Queue.Count == 2 && !bs.GestureActive, "ПКМ не отменил жест");

        // The route is executed in game time; the unit ends facing the chosen direction at the node.
        bs.Resume();
        for (int i = 0; i < 400 && knight.Queue.Count > 1; i++) bs.Sim.Step();
        Check(knight.Pos == target && knight.Facing == HexDirection.SW || knight.Queue.Count <= 1, "узел маршрута не выполнен");
    }

    private async Task BattlePlanningPauseAndCancel()
    {
        var bs = await OpenBattle("sandbox");
        var s = Main.Instance.Settings;
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        var mage = bs.Sim.Units.First(u => u.Def.Id == "mage");
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(knight));
        await Frames(8);
        try
        {
            // No pause on selection when it is switched off.
            s.PauseOnSelect = false;
            bs.Resume();
            await Click(HexScreen(bs, mage.Pos));
            Check(bs.Selected == mage && !bs.Driver.Paused, "выбор поставил паузу при выключенной настройке");
            // Targeting pauses; an order keeps the pause by default.
            await Key(Godot.Key.M);
            Check(bs.Driver.Paused && bs.AutoPaused, "режим движения не поставил паузу");
            await Click(HexScreen(bs, mage.Pos + new HexCoord(2, 0)));
            Check(mage.Queue.Count == 1 && bs.Driver.Paused, "после приказа пауза должна сохраниться");
            await Key(Godot.Key.Escape);
            // «Remove the pause after the order».
            s.KeepPauseAfterOrder = false;
            await Key(Godot.Key.M);
            Check(bs.Driver.Paused, "режим движения не поставил паузу");
            await Click(HexScreen(bs, mage.Pos + new HexCoord(2, 1)));
            Check(mage.Queue.Count == 2 && !bs.Driver.Paused, "автопауза не снята после приказа");
            await Key(Godot.Key.Escape);
            // «Only at the start of planning»: after the player resumes, the same planning does not pause again.
            s.KeepPauseAfterOrder = true;
            s.PauseOnlyAtPlanningStart = true;
            bs.TogglePause();
            bs.TogglePause();
            Check(!bs.Driver.Paused, "пауза не снята");
            await Key(Godot.Key.M);
            Check(!bs.Driver.Paused, "повторная автопауза в том же планировании");
            await Key(Godot.Key.Escape);

            // Cancel: Backspace removes the last pending point, Delete — the pending orders, the executing one stays.
            bs.Driver.Paused = true;
            bs.Sim.ClearQueue(mage);
            bs.Sim.Enqueue(mage, Command.Move(mage.Pos + new HexCoord(2, 0)));
            bs.Sim.Enqueue(mage, Command.Move(mage.Pos + new HexCoord(2, 2)));
            await Key(Godot.Key.Backspace);
            Check(mage.Queue.Count == 1, "Backspace не удалил последнюю точку");
            bs.Sim.ClearQueue(mage);
            bs.Sim.Enqueue(mage, Command.Move(mage.Pos + new HexCoord(3, 0)));
            bs.Sim.Enqueue(mage, Command.Move(mage.Pos + new HexCoord(3, 2)));
            // A step already under way finishes first (slow frames let the battle run further before this point).
            for (long t = 0; t < bs.Sim.TicksFor(3) && mage.Queue.Items[0].Status == CommandStatus.Pending; t++) bs.Sim.Step();
            Check(mage.Queue.Items[0].Status is CommandStatus.Active or CommandStatus.Waiting, "первый приказ не начался: " + mage.Queue.Items[0].Status);
            await Key(Godot.Key.Delete);
            Check(mage.Queue.Count == 1 && mage.Queue.Items[0].Status != CommandStatus.Pending, "Delete удалил выполняемый приказ или не удалил ожидающие");
            await Key(Godot.Key.Delete, shift: true);
            Check(mage.Queue.Count == 0, "Shift+Delete не отменил всё");

            // Esc chain: leave the mode → clear the selection → game menu.
            await Key(Godot.Key.M);
            Check(bs.Mode == InputMode.Move, "M не включил движение");
            await Key(Godot.Key.Escape);
            Check(bs.Mode == InputMode.None && bs.Selected == mage, "первый Esc должен выйти из режима");
            await Key(Godot.Key.Escape);
            Check(bs.Selected == null, "второй Esc должен снять выделение");
        }
        finally
        {
            s.PauseOnSelect = true;
            s.KeepPauseAfterOrder = true;
            s.PauseOnlyAtPlanningStart = false;
        }
    }

    private async Task BattleAlternativeControl()
    {
        var bs = await OpenBattle("sandbox");
        var s = Main.Instance.Settings;
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        try
        {
            s.Gestures = false;
            bs.Select(knight);
            bs.Board.Rig.FocusOn(bs.Board.UnitPos(knight));
            await Frames(8);
            Check(!bs.HeadArrow.Visible, "без жестов стрелка не должна захватываться");
            // Select → «Движение» → hex.
            await ClickButton("Движение");
            Check(bs.Mode == InputMode.Move, "кнопка «Движение» не включила режим");
            var to = knight.Pos + new HexCoord(2, 0);
            await Click(HexScreen(bs, to));
            Check(knight.Queue.Count == 1 && knight.Queue.Items[0].TargetCell == to, "клик по гексу не создал движение");
            // A wall cannot be chosen as the end point.
            var wall = bs.Sim.Map.Cells.FirstOrDefault(c => !c.Terrain.IsPassableFor("walk") && c.Terrain.Kind != TerrainKind.Pit);
            if (wall != null)
            {
                bs.Board.Rig.FocusOn(Render.Board.Center(wall.Coord));
                await Frames(6);
                await Click(HexScreen(bs, wall.Coord));
                Check(knight.Queue.Count == 1, "непроходимый гекс выбран как конечная точка");
            }
            await Key(Godot.Key.Escape);
            // Select → «Атака» → enemy (the enemy hex is an attack, not a move).
            var enemy = bs.Sim.Units.First(u => u.Alive && bs.Sim.IsHostile(knight, u));
            bs.Board.Rig.FocusOn(bs.Board.UnitPos(enemy));
            await Frames(6);
            await Key(Godot.Key.M);
            await Click(HexScreen(bs, enemy.Pos));
            Check(bs.Hud.Radial.IsOpen, "указание врага не открыло меню навыков");
            var basic = bs.Hud.Radial.Buttons.First(b => b.Name == "Radial_" + knight.BasicAttack!.Id);
            await Click(basic.GetGlobalRect().GetCenter());
            Check(knight.Queue.Items.Last().Type == CommandType.Attack, "указание врага в режиме движения не стало атакой");
            Check(knight.Queue.Items.All(c => c.TargetCell != enemy.Pos || c.Type != CommandType.Move), "юнит пытается занять гекс врага");
        }
        finally
        {
            s.Gestures = true;
        }
    }

    private async Task BattleRadialMenuAndAim()
    {
        var bs = await OpenBattle("sandbox");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        var mage = bs.Sim.Units.First(u => u.Def.Id == "mage");
        var orc = bs.Sim.Units.First(u => u.Alive && bs.Sim.IsHostile(knight, u) && u.Def.Id.Contains("orc"));
        bs.Select(knight);
        bs.Board.Rig.FocusOn((bs.Board.UnitPos(knight) + bs.Board.UnitPos(orc)) / 2);
        await Frames(10);

        // The gesture released on the enemy hex opens the skill menu, never a move onto it.
        await Drag(ArrowScreen(bs, bs.HeadArrow), HexScreen(bs, orc.Pos), 14);
        var radial = bs.Hud.Radial;
        Check(radial.IsOpen, "гекс врага не открыл радиальное меню: " + bs.Hud.LastBanner);
        Check(knight.Queue.Count == 0, "указание врага создало приказ до выбора навыка");
        Check(radial.CentreItem != null && (radial.CentreItem.Skill == null || radial.CentreItem.Skill.Kind == SkillKind.Defense), "в центре должен быть защитный навык");
        var (_, first, _) = bs.Sim.Planner.RadialSkills(knight, HexTactics.Game.Battle.RadialMenu.FirstCapacity);
        Check(radial.First.Select(i => i.Skill!.Id).SequenceEqual(first.Select(s => s.Id)), "порядок первого кольца не совпадает с порядком «от слабого к сильному»");
        // The first slot is at 12 o'clock and the next goes clockwise.
        var b0 = radial.Buttons.First(b => b.Name == "Radial_" + radial.First[0].Skill!.Id).GetGlobalRect().GetCenter();
        var b1 = radial.Buttons.First(b => b.Name == "Radial_" + radial.First[1].Skill!.Id).GetGlobalRect().GetCenter();
        string centreName = "Radial_" + (radial.CentreItem!.Skill?.Id ?? "stance");
        var c = radial.Buttons.First(b => b.Name == centreName).GetGlobalRect().GetCenter();
        Check(b0.Y < c.Y - 40 && Math.Abs(b0.X - c.X) < 8, "первый навык должен быть на 12 часах");
        Check(b1.X > b0.X, "навыки должны идти по часовой стрелке");
        // Choose the basic attack: the knight approaches and never takes the enemy hex.
        await Click(radial.Buttons.First(b => b.Name == "Radial_" + knight.BasicAttack!.Id).GetGlobalRect().GetCenter());
        Check(!radial.IsOpen && knight.Queue.Count == 1 && knight.Queue.Items[0].Type == CommandType.Attack && knight.Queue.Items[0].Approach,
            "выбор навыка не создал приказ атаки с подходом");
        bool entered = false;
        for (int i = 0; i < 300 && orc.Hp >= orc.HpMax && knight.Queue.Count > 0; i++)
        {
            bs.Sim.Step();
            entered |= knight.Pos == orc.Pos;
        }
        Check(!entered && HexCoord.Distance(knight.Pos, orc.Pos) == 1, $"рыцарь должен подойти вплотную, не занимая гекс врага (стоит в {knight.Pos}, враг {orc.Pos})");

        // The second ring appears when a skill asks for it.
        bs.Select(mage);
        var meteor = bs.Db.Skill("meteor")!;
        var ring = meteor.Ring;
        try
        {
            meteor.Ring = MenuRing.Second;
            bs.OpenSkillMenu(null, new Vector2(800, 450));
            await Frames(3);
            Check(radial.Second.Any(i => i.Skill == meteor), "второе кольцо не появилось");
            await Key(Godot.Key.Escape);
            Check(!radial.IsOpen, "Esc не закрыл меню");
        }
        finally
        {
            meteor.Ring = ring;
        }

        // An aimed skill: choose it, pull the arrow to the target cell — the order is fixed on release.
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(mage));
        await Frames(6);
        await Key(Godot.Key.Key1);
        Check(bs.Mode == InputMode.Skill && bs.ModeSkill?.Id == "fireball", "клавиша 1 не выбрала огненный шар");
        var cell = mage.Pos + new HexCoord(4, 0);
        await Drag(ArrowScreen(bs, bs.HeadArrow), HexScreen(bs, cell), 12);
        Check(mage.Queue.Count == 1 && mage.Queue.Items[0].SkillId == "fireball" && mage.Queue.Items[0].TargetCell == cell, "прицельная стрелка не задала цель навыка");

        // An invalid target is rejected: chain lightning (enemies only) on an ally.
        await Key(Godot.Key.Key3);
        Check(bs.ModeSkill?.Id == "chain_lightning", "клавиша 3 не выбрала цепную молнию");
        await Click(HexScreen(bs, knight.Pos));
        Check(mage.Queue.Count == 1, "навык по союзнику принят, хотя допустимы только враги");
    }

    private async Task BattleAutoSkillAndIconAim()
    {
        var bs = await OpenBattle("arena_duel");
        var scout = bs.Sim.Units.First(u => u.Def.Id == "archer");
        // The test needs the enemies in sight and in reach: bring them closer (setup only).
        var enemies = bs.Sim.Units.Where(u => u.Alive && bs.Sim.IsHostile(scout, u)).ToList();
        for (int i = 0; i < enemies.Count; i++)
        {
            var free = bs.Sim.Map.Cells.Where(c => c.Occupants.Count == 0 && c.Terrain.IsPassableFor("walk") && c.Terrain.Kind != TerrainKind.Pit
                    && HexCoord.Distance(c.Coord, scout.Pos) is >= 3 and <= 5)
                .OrderBy(c => c.Coord.Q).ThenBy(c => c.Coord.R).Skip(i * 2).FirstOrDefault();
            if (free != null) Teleport(bs, enemies[i], free.Coord);
        }
        // Auto-attacks of this battle only (the rules object is shared with the published content).
        foreach (var u in bs.Sim.Units) bs.Sim.AutoAttacks.DisabledUnits.Add(u.Id);
        bs.Select(scout);
        bs.Board.Rig.FocusOn((bs.Board.UnitPos(scout) + bs.Board.UnitPos(enemies[0])) / 2);
        await Frames(10);
        Button IconOf(string text) => Find<Button>(GetTree().Root).First(b => b.IsVisibleInTree() && b.Text.Contains(text));

        // A click on the icon: automatic use, the target is chosen by the skill's rules at execution.
        await Click(IconOf("Точный выстрел").GetGlobalRect().GetCenter());
        Check(scout.Queue.Count == 1 && scout.Queue.Items[0].AutoTarget, "клик по иконке не создал автоматическое применение: " + bs.Hud.LastBanner
            + " · кнопки: " + string.Join(" | ", Find<Button>(GetTree().Root).Where(b => b.IsVisibleInTree() && b.Text.Contains("Точный")).Select(b => b.GetPath() + " dis=" + b.Disabled + " rect=" + b.GetGlobalRect()))
            + " · очередь: " + string.Join(", ", scout.Queue.Items.Select(c => c.Describe(bs.Sim))) + " · выбран: " + bs.Selected?.Name);
        var proj = bs.Sim.Planner.ProjectQueue(scout);
        Check(proj[0].Estimate.Problem.Contains("цель сейчас"), "оценка не показала, какую цель выберут правила");
        bs.Resume();
        for (int i = 0; i < 600 && scout.Queue.Count > 0; i++) bs.Sim.Step();
        bs.Driver.Paused = true;
        var cmd = bs.Sim.Log.Entries.Any(e => e.Text.Contains("Точный выстрел") && e.Text.Contains("цель"));
        Check(cmd, "в журнале нет выбора цели автоматическим навыком");
        Check(enemies.Any(e => e.Hp < e.HpMax), "автоматический выстрел никого не задел");

        // A drag from the icon onto an enemy: aimed at that very enemy.
        await Frames(4);
        var target = enemies.OrderByDescending(e => e.Hp).First(e => e.Alive);
        bs.Board.Rig.FocusOn((bs.Board.UnitPos(scout) + bs.Board.UnitPos(target)) / 2);
        await Frames(8);
        foreach (var c in scout.Queue.Items.ToList()) bs.Sim.RemoveCommand(scout, c.Id);
        await Drag(IconOf("Точный выстрел").GetGlobalRect().GetCenter(), HexScreen(bs, target.Pos), 14);
        Check(scout.Queue.Count == 1 && !scout.Queue.Items[0].AutoTarget && scout.Queue.Items[0].TargetUnitId == target.Id,
            "протягивание от иконки к врагу не нацелило навык на него: " + string.Join(", ", scout.Queue.Items.Select(c => c.Describe(bs.Sim))) + " · " + bs.Hud.LastBanner
            + $" · режим {bs.Mode} {bs.ModeSkill?.Id} · курсор над {bs.HoverCell?.Coord}, цель {target.Pos}, мышь {GetViewport().GetMousePosition()}");

        // The hotkey of a «both» skill: the first press aims, the second uses it automatically.
        foreach (var c in scout.Queue.Items.ToList()) bs.Sim.RemoveCommand(scout, c.Id);
        await Key(Godot.Key.Key1);
        Check(bs.Mode == InputMode.Skill && bs.ModeSkill?.Id == "aimed_shot", "первое нажатие должно включить прицел");
        await Key(Godot.Key.Key1);
        Check(scout.Queue.Count == 1 && scout.Queue.Items[0].AutoTarget, "второе нажатие должно применить навык автоматически");
    }

    private async Task BattleGroupOrders()
    {
        var bs = await OpenBattle("sandbox");
        var heroes = bs.Sim.Units.Where(u => u.Alive && bs.Sim.CanPlayerCommand(u)).ToList();
        Check(heroes.Count >= 3, "в песочнице должно быть не меньше трёх героев");
        bs.Select(null);
        bs.Board.Rig.FocusOn(heroes.Select(h => bs.Board.UnitPos(h)).Aggregate((a, b) => a + b) / heroes.Count);
        await Frames(10);
        // Box selection around all heroes.
        var pts = heroes.Select(h => bs.Board.Rig.Camera.UnprojectPosition(bs.Board.UnitPos(h) + new Vector3(0, 0.3f, 0))).ToList();
        var from = new Vector2(pts.Min(p => p.X) - 40, pts.Min(p => p.Y) - 40);
        var to = new Vector2(pts.Max(p => p.X) + 40, pts.Max(p => p.Y) + 40);
        await Drag(from, to, 8);
        Check(bs.GroupActive && bs.Group.Count == heroes.Count, $"рамка выделила {bs.Group.Count} из {heroes.Count}");
        Check(bs.Hud.GroupPanel.Visible, "панель групповых команд не показана");

        // «Move to a point»: one hex for the order, distinct hexes for the units.
        await ClickButton("Двигаться к точке");
        Check(bs.GroupMode == GroupCommand.Move, "команда не ждёт точку");
        var point = heroes[0].Pos + new HexCoord(5, 0);
        await Click(HexScreen(bs, point));
        var r = bs.LastGroupResult;
        Check(r != null && r.Ordered.Count == heroes.Count, "приказ не отдан всем: " + r?.Message);
        var ends = heroes.Select(h => h.Queue.Items.Last(c => c.Type == CommandType.Move).TargetCell!.Value).ToList();
        Check(ends.Distinct().Count() == ends.Count, "конечные гексы совпадают: " + string.Join(", ", ends));
        Check(ends.Contains(point), "на сам гекс приказа никто не назначен");

        // Saved groups: Ctrl+1, clear, Alt+1.
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Key1, PhysicalKeycode = Godot.Key.Key1, Pressed = true, CtrlPressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Key1, PhysicalKeycode = Godot.Key.Key1, Pressed = false, CtrlPressed = true });
        await Frames(3);
        await Key(Godot.Key.Escape);
        Check(!bs.GroupActive, "Esc не снял групповое выделение");
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Key1, PhysicalKeycode = Godot.Key.Key1, Pressed = true, AltPressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.Key1, PhysicalKeycode = Godot.Key.Key1, Pressed = false, AltPressed = true });
        await Frames(3);
        Check(bs.GroupActive && bs.Group.Count == heroes.Count, "Alt+1 не вернул группу");

        // RMB on an enemy: a group attack; support stays out of melee; nobody targets the enemy hex as a destination.
        var enemy = bs.Sim.Units.First(u => u.Alive && bs.Sim.IsHostile(heroes[0], u));
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(enemy));
        await Frames(6);
        await Click(HexScreen(bs, enemy.Pos), MouseButton.Right);
        var ra = bs.LastGroupResult;
        Check(ra != null && ra.Ordered.Count > 0, "групповая атака не отдана");
        Check(heroes.All(h => h.Queue.Items.All(c => c.Type != CommandType.Move || c.TargetCell != enemy.Pos)), "кто-то идёт на гекс врага");
        var support = heroes.Where(h => bs.Sim.Groups.RoleOf(h) == UnitRole.Support).ToList();
        Check(support.All(sp => ra!.Skipped.Any(s => s.Unit == sp.Id)), "поддержка не должна идти в ближний бой");

        // Shift+click removes a unit from the group.
        var last = heroes[^1];
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(last));
        await Frames(6);
        var pos = HexScreen(bs, last.Pos);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true });
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = false, ShiftPressed = true });
        await Frames(3);
        Check(!bs.Group.Contains(last) && bs.Group.Count == heroes.Count - 1 || heroes.Count == 2, "Shift+клик не убрал юнита из группы");
    }

    // ------------------------------------------------------------------ editor tests

    /// <summary>
    /// Part 3, stage 5: the defensive skill in the centre of the radial menu holds a stance (shown in the unit
    /// panel) until another order; a series of hits shows its progress and every hit in the log.
    /// </summary>
    private async Task BattleDefenseSkillAndSeries()
    {
        var bs = await OpenBattle("sandbox");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        bs.Select(knight);
        bs.Board.Rig.FocusOn(bs.Board.UnitPos(knight));
        await Frames(6);
        bs.OpenSkillMenu(null, new Vector2(800, 450));
        await Frames(3);
        var radial = bs.Hud.Radial;
        Check(radial.IsOpen && radial.CentreItem?.Skill?.Id == "shield_wall", "в центре меню рыцаря должна быть «Стена щитов»");
        await Click(radial.Buttons.First(b => b.Name == "Radial_shield_wall").GetGlobalRect().GetCenter());
        Check(knight.Queue.Count == 1 && knight.Queue.Items[0].Type == CommandType.Defend, "центр меню не дал приказ защиты");
        for (int i = 0; i < 20; i++) bs.Sim.Step();
        Check(knight.Stance?.Skill.Id == "shield_wall", "стойка не началась");
        await Frames(20);
        Check(Find<Label>(bs.Hud).Any(l => l.Visible && l.Text.Contains("«Стена щитов»")), "панель юнита не показывает защиту");
        // Another order ends the stance («until another order»).
        bs.Sim.Enqueue(knight, Command.Turn(knight.Facing.Rotate(1)));
        for (int i = 0; i < 20; i++) bs.Sim.Step();
        Check(knight.Stance == null, "новый приказ не завершил стойку");

        // «Три удара мечом» against an enemy: three hits in the log, each with its multiplier.
        var orc = bs.Sim.Units.First(u => u.Alive && bs.Sim.IsHostile(knight, u) && u.Def.Id.Contains("orc"));
        var cmd = bs.CommandAgainst(knight, bs.Db.Skill("triple_strike")!, orc);
        var err = bs.Sim.Enqueue(knight, cmd);
        Check(err == null, "приказ «Три удара мечом» не принят: " + err);
        bool sawProgress = false;
        for (int i = 0; i < 400 && !bs.Sim.Log.Entries.Any(e => e.Text.Contains("удар 3 из 3")) && orc.Alive; i++)
        {
            bs.Sim.Step();
            if (i % 5 == 0)
            {
                await Frames(1);
                sawProgress |= Find<Label>(bs.Hud).Any(l => l.Visible && l.Text.Contains("«Три удара мечом»: удар"));
            }
        }
        var hits = bs.Sim.Log.Entries.Where(e => e.Type == SimEventType.SkillHit && e.Text.Contains("Три удара мечом")).Select(e => e.Text).ToList();
        Check(hits.Count == 3 || !orc.Alive && hits.Count >= 1, "в журнале нет трёх ударов: " + string.Join(" | ", hits));
        Check(hits.Count == 0 || hits[0].Contains("70 %"), "первый удар должен быть 70 %: " + hits.FirstOrDefault());
        Check(sawProgress, "панель юнита не показывала ход серии");
    }

    /// <summary>
    /// Part 3, stage 6: the unit panel lists passives and talents with their source and unambiguous texts; the
    /// stat rows explain every source; a talent changes the damage through the common formula.
    /// </summary>
    private async Task BattlePassivesPanel()
    {
        var bs = await OpenBattle("slime_bog");
        var slime = bs.Sim.Units.First(u => u.Def.Id == "slime");
        var elf = bs.Sim.Units.First(u => u.Def.Id == "elf_ranger");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        // Own unit: talents with their source and unambiguous texts.
        bs.Select(elf);
        await Frames(4);
        bs.Hud.ToggleInfo();
        await Frames(20);
        var labels = Find<Label>(bs.Hud).Where(l => l.IsVisibleInTree()).Select(l => l.Text).ToList();
        Check(labels.Any(t => t.Contains("Родство с природной магией") && t.Contains("талант") && t.Contains("раса «Эльф»")), "панель не показывает талант и его источник: " +
            string.Join(" | ", labels.Where(t => t.Length > 0).Take(40)));
        Check(labels.Any(t => t.Contains("на 25 % больше урона") && t.Contains("nature_magic")), "нет однозначного текста таланта");
        bs.Hud.ToggleInfo();
        // An enemy: its passives are part of what the player learns (part 2) — once scanned the panel shows −75 % to water.
        bs.Sim.Knowledge.Reveal(slime);
        bs.Select(slime, byPlayer: false);
        await Frames(20);
        labels = Find<Label>(bs.Hud).Where(l => l.IsVisibleInTree()).Select(l => l.Text).ToList();
        Check(labels.Any(t => t.Contains("Водный") && t.Contains("-75 %")), "раскрытая панель врага не показывает сопротивление −75 %: " +
            string.Join(" | ", labels.Where(t => t.Contains("опротивл")).Take(5)));
        Check(slime.PassiveInstances.Any(x => x.Def.Id == "bog_mist" && x.SourceKind == PassiveSource.Scenario), "нет сценарного пассива");
        // The elf's talent through the damage pipeline: nature magic +25 %, the other attacks unchanged.
        Check(elf.PassiveInstances.Any(x => x.Def.Id == "nature_affinity" && x.Source.Contains("Эльф")), "талант расы не получен от происхождения");
        double r1 = bs.Sim.Passives.OutgoingPercent(elf, slime, "earth", bs.Db.Skill("thorn_lash"), "");
        double r2 = bs.Sim.Passives.OutgoingPercent(elf, slime, "piercing", bs.Db.Skill("aimed_shot"), "");
        Check(Math.Abs(r1 - 25) < 1e-6 && Math.Abs(r2) < 1e-6, $"талант действует неверно: природа {r1}, выстрел {r2}");
        // The knight's aura: allies around get armor, the stat row explains where it comes from.
        for (int i = 0; i < 20; i++) bs.Sim.Step();
        var mage = bs.Sim.Units.First(u => u.Def.Id == "mage");
        bool near = HexCoord.Distance(mage.Pos, knight.Pos) <= 2;
        var br = mage.Stats.Explain(Stats.Armor);
        Check(!near || br.Items.Any(i => i.C.Source.Contains("Аура стража")), "в разборе брони нет ауры стража");
        bs.Select(mage);
        bs.Hud.ToggleInfo();
        await Frames(20);
        var rows = Find<HBoxContainer>(bs.Hud).Where(h => h.IsVisibleInTree() && !string.IsNullOrEmpty(h.TooltipText)).ToList();
        Check(!near || rows.Any(h => h.TooltipText.Contains("Аура стража") && h.TooltipText.Contains("Итог")), "строка характеристики не объясняет источники");
        bs.Hud.ToggleInfo();
    }

    /// <summary>The editor: talents in their own list, the player's text, and every object that uses the talent.</summary>
    private async Task EditorTalentsOrigins()
    {
        await OpenEditor("talents");
        var panel = Find<PassivePanel>(GetTree().Root).First();
        panel.Select("nature_affinity");
        await Frames(6);
        var preview = Find<Label>(panel).FirstOrDefault(l => l.Name == "PassivePreview");
        Check(preview != null && preview.Text.Contains("на 25 % больше урона") && preview.Text.Contains("nature_magic"), "нет текста таланта: " + preview?.Text);
        var used = Find<Button>(panel).Where(b => b.Name.ToString().StartsWith("UsedBy_")).ToList();
        Check(used.Any(b => b.Name == "UsedBy_elf") && used.Any(b => b.Name == "UsedBy_elf_ranger"), "«Где используется» не показывает происхождение и юнитов: " + string.Join(", ", used.Select(b => b.Name)));
        // A jump to the origin.
        await Click(used.First(b => b.Name == "UsedBy_elf").GetGlobalRect().GetCenter());
        await Frames(8);
        Check(Find<OriginPanel>(GetTree().Root).Any(), "переход к происхождению не открыл раздел «Происхождения»");
    }

    /// <summary>The skill editor's «Серия» tab: the hits in order, and a run on the test map with every hit.</summary>
    private async Task EditorSkillSeriesTestRun()
    {
        await OpenEditor("skills");
        var panel = Find<SkillEditorPanel>(GetTree().Root).First();
        panel.Select("triple_strike");
        await Frames(6);
        var tabs = Find<TabContainer>(panel).First(t => t.GetTabCount() >= 5);
        tabs.CurrentTab = 3;
        await Frames(4);
        Check(Find<Button>(panel).Any(b => b.Name == "SeriesStage_2" && b.Text.Contains("Удар 3") && b.Text.Contains("120 %")), "на вкладке «Серия» нет третьего удара 120 %");
        var run = Find<Button>(panel).First(b => b.Name == "SeriesTestRun");
        // The tab scrolls: bring the button into view before clicking it like a player would.
        for (Node? n = run.GetParent(); n != null; n = n.GetParent())
            if (n is ScrollContainer sc)
            {
                sc.EnsureControlVisible(run);
                break;
            }
        await Frames(3);
        await Click(run.GetGlobalRect().GetCenter());
        await Frames(4);
        Check(Find<Label>(panel).Any(l => l.Text.StartsWith("Удар 3 →")), "прогон не показал третий удар");
        Check(Find<Label>(panel).Any(l => l.Text.StartsWith("Исполнитель:")), "нет итога прогона");
        // A hit of the list opens its stage.
        await Click(Find<Button>(panel).First(b => b.Name == "SeriesStage_1").GetGlobalRect().GetCenter());
        await Frames(4);
        Check(tabs.CurrentTab == 1, "щелчок по удару не открыл вкладку этапа");
    }

    private async Task<EditorScreen> OpenEditor(string tab)
    {
        Main.Instance.Args["tab"] = tab;
        Main.Instance.ShowEditor();
        await Frames(8);
        var ed = Find<EditorScreen>(GetTree().Root).FirstOrDefault();
        Check(ed != null, "редактор не открыт");
        return ed!;
    }

    private async Task EditorSkillMaskClick()
    {
        await OpenEditor("skills");
        var panel = Find<SkillEditorPanel>(GetTree().Root).First();
        panel.Select("quake");
        await Frames(6);
        var skill = Main.Instance.Content.Draft!.Skill("quake")!;
        int before = skill.Stages[0].Area.Cells.Count;
        var canvas = Find<HexCanvas>(GetTree().Root).First();
        // Cell (0,-2) relative to the centre is not in the quake mask.
        var (x, y) = HexLayout.ToPlane(new HexCoord(0, -2), CanvasHexSize(canvas));
        var p = canvas.GetGlobalRect().Position + canvas.Size / 2 + new Vector2((float)x, (float)y);
        await Click(p);
        Check(skill.Stages[0].Area.Cells.Count == before + 1, $"клик по клетке не добавил её в маску ({before} → {skill.Stages[0].Area.Cells.Count})");
        await Click(p);
        Check(skill.Stages[0].Area.Cells.Count == before, "повторный клик по выбранной клетке не убрал её");
        // Direction arrow rotates the preview, the stored mask stays canonical.
        var offsets = skill.Stages[0].Area.Cells.Select(c => c.Offset).ToList();
        var (ax, ay) = HexLayout.ToPlane(HexDirection.W.Offset() * (canvas.Radius + 1), CanvasHexSize(canvas));
        await Click(canvas.GetGlobalRect().Position + canvas.Size / 2 + new Vector2((float)ax, (float)ay));
        Check(canvas.Orientation == HexDirection.W, "стрелка не сменила направление");
        Check(skill.Stages[0].Area.Cells.Select(c => c.Offset).SequenceEqual(offsets), "маска изменилась при повороте превью");
    }

    private static float CanvasHexSize(HexCanvas c)
    {
        int n = 2 * c.Radius + 4;
        float byW = c.Size.X / (Mathf.Sqrt(3) * n);
        float byH = c.Size.Y / (1.5f * n + 0.5f);
        return Math.Max(6, Math.Min(byW, byH));
    }

    private async Task EditorMapPaint()
    {
        await OpenEditor("maps");
        var panel = Find<MapEditorPanel>(GetTree().Root).First();
        panel.Select("arena");
        await Frames(30); // let the layout settle (side tabs resize the viewport)
        var vp = Find<BoardViewport>(GetTree().Root).First();
        var map = Main.Instance.Content.Draft!.Get<MapDef>("arena")!;
        var cellDef = map.Cells.First(c => c.C == new HexCoord(2, 2));
        string before = cellDef.T;
        // Choose "Трава" in the palette and paint the cell.
        var opt = Find<OptionButton>(panel).First(o => o.ItemCount > 5 && o.GetItemText(0).Contains("["));
        int grassIdx = Enumerable.Range(0, opt.ItemCount).First(i => opt.GetItemText(i).Contains("[grass]"));
        opt.Select(grassIdx);
        var cell = vp.Board.Map!.Get(new HexCoord(2, 2))!;
        var world = Render.Board.Center(cell.Coord, Render.Board.PickY(cell));
        var local = vp.Board.Rig.Camera.UnprojectPosition(world);
        await Click(vp.GetGlobalRect().Position + local);
        Check(cellDef.T == "grass", $"кисть не изменила местность ({before} → {cellDef.T}); курсор над {vp.HoverHex}, экран {vp.GetGlobalRect()} локально {local}");
        Check(Main.Instance.Content.DraftDirty, "черновик не помечен изменённым");
    }

    private async Task EditorCreateDelete()
    {
        await OpenEditor("statuses");
        var db = Main.Instance.Content.Draft!;
        int before = db.Table(DefCategory.Status).Count;
        await ClickButton("Создать");
        Check(db.Table(DefCategory.Status).Count == before + 1, "«Создать» не добавил объект");
        var panel = Find<DefListPanel>(GetTree().Root).First();
        var created = panel.Current!;
        Check(db.Exists(DefCategory.Status, created.Id), "созданный объект не в черновике");
        await ClickButton("Удалить");
        var dlg = Find<ConfirmationDialog>(GetTree().Root).LastOrDefault();
        Check(dlg != null, "нет диалога подтверждения удаления");
        dlg!.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames(3);
        Check(db.Table(DefCategory.Status).Count == before, "удаление не сработало");
        // Deleting something referenced shows its dependents.
        panel.Select("poison");
        await Frames(3);
        await ClickButton("Удалить");
        var dlg2 = Find<ConfirmationDialog>(GetTree().Root).LastOrDefault(d => d.Visible);
        Check(dlg2 != null && dlg2.DialogText.Contains("ссылаются"), "не показаны зависимости перед удалением");
        dlg2!.EmitSignal(AcceptDialog.SignalName.Canceled);
        await Frames(2);
        Check(db.Exists(DefCategory.Status, "poison"), "отмена удалила объект");
    }

    // ------------------------------------------------------------------ part 2

    private async Task EditorNewMapStandard()
    {
        await OpenEditor("maps");
        var db = Main.Instance.Content.Draft!;
        int before = db.Table(DefCategory.Map).Count;
        await ClickButton("Создать");
        Check(db.Table(DefCategory.Map).Count == before + 1, "«Создать» не добавил карту");
        var panel = Find<MapEditorPanel>(GetTree().Root).First();
        var map = (MapDef)panel.Current!;
        Check(map.TemplateId == MapTemplates.StandardId, $"новая карта не из стандартного шаблона ({map.TemplateId})");
        Check(map.Cells.Count == 80, $"стандартная карта должна иметь 80 клеток, а не {map.Cells.Count}");
    }

    private async Task EditorSaveAsTemplate()
    {
        await OpenEditor("maps");
        var db = Main.Instance.Content.Draft!;
        var panel = Find<MapEditorPanel>(GetTree().Root).First();
        panel.Select("arena");
        await Frames(6);
        int before = db.Table(DefCategory.MapTemplate).Count;
        await ClickButton("Сохранить как шаблон");
        var dlg = Find<ConfirmationDialog>(GetTree().Root).LastOrDefault(d => d.Visible);
        Check(dlg != null, "нет диалога «Сохранить как шаблон»");
        dlg!.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames(3);
        Check(db.Table(DefCategory.MapTemplate).Count == before + 1, "шаблон не добавлен");
        var t = db.All<MapTemplateDef>().Last();
        Check(t.Cells.Count == db.Get<MapDef>("arena")!.Cells.Count, "в шаблоне другая форма");
        Check(!t.BuiltIn && t.Group == MapTemplates.UserGroup, "шаблон не пользовательский");
        foreach (var info in Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible).ToList()) info.EmitSignal(AcceptDialog.SignalName.Confirmed);
    }

    private async Task BattleFitMap()
    {
        var bs = await OpenBattle("arena_duel");
        await Frames(10);
        var rig = bs.Board.Rig;
        for (int i = 0; i < 4; i++) await Key(Godot.Key.Pageup);
        await Frames(30);
        float zoomed = rig.Distance;
        await Key(Godot.Key.V);
        await Frames(60);
        Check(rig.AutoFit, "V не включил вписывание");
        Check(rig.Distance > zoomed * 1.2f, $"после V камера не отдалилась ({zoomed:0.0} → {rig.Distance:0.0})");
        var vp = GetViewport().GetVisibleRect().Size;
        var ins = Find<BattleHud>(GetTree().Root).First().WorkInsets();
        foreach (var c in bs.Sim.Map.Cells)
        {
            var p = rig.Camera.UnprojectPosition(Render.Board.Center(c.Coord, Render.Board.PickY(c)));
            Check(p.X >= ins.Left - 4 && p.X <= vp.X - ins.Right + 4 && p.Y >= ins.Top - 4 && p.Y <= vp.Y - ins.Bottom + 4,
                $"клетка {c.Coord} вне рабочей области: {p}, область {ins}");
        }
    }

    private async Task BattleLayersMeleeVsFlyer()
    {
        var bs = await OpenBattle("facility_breach");
        Check(bs.Sim.Map.Count == 80, "карта технокомплекса должна быть 10 × 8");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        var archer = bs.Sim.Units.First(u => u.Def.Id == "archer");
        var drone = bs.Sim.Units.First(u => u.Def.Id == "drone");
        Check(drone.Layer == "air", "дрон не в воздушном слое");
        // Bring the drone next to the knight (test setup), then try a melee attack by clicking on it.
        bs.Sim.Map.Get(drone.Pos)!.Occupants.Remove(drone);
        var near = knight.Pos + new HexCoord(1, 0);
        bs.Sim.Map.Get(near)!.Occupants.Add(drone);
        typeof(Unit).GetProperty("Pos")!.SetValue(drone, near);
        bs.Select(knight);
        await Frames(10);
        await Key(Godot.Key.F);
        await Click(HexScreen(bs, near));
        Check(knight.Queue.Count == 0, "ближняя атака по летящему не должна попасть в очередь");
        await Key(Godot.Key.Escape);
        var shot = bs.Sim.Planner.Actions(archer).First(a => a.Key == "attack");
        Check(bs.Sim.Skills.Validate(archer, shot.Skill!, null, drone.Id, null, archer.Pos, archer.Facing).Reason != FailureReason.Invalid,
            "дальняя атака должна допускать воздушную цель");
    }

    private static void Teleport(BattleScreen bs, Unit u, HexCoord to)
    {
        bs.Sim.Map.Get(u.Pos)!.Occupants.Remove(u);
        bs.Sim.Map.Get(to)!.Occupants.Add(u);
        typeof(Unit).GetProperty("Pos")!.SetValue(u, to);
        bs.Sim.Vision.Recompute();
        bs.Sim.Knowledge.Update();
    }

    private static void Confirm(Window? dlg)
    {
        Check(dlg != null, "диалог не открыт");
        dlg!.EmitSignal(AcceptDialog.SignalName.Confirmed);
    }

    private ConfirmationDialog? Dialog(string title) =>
        Find<ConfirmationDialog>(GetTree().Root).LastOrDefault(d => d.Visible && d.Title == title);

    /// <summary>
    /// Library (docs/DECISIONS_2.md §31): favourites filter, mass tag, publication with usages
    /// (keep old instances for some, update the rest), versions and history, pinned old instance,
    /// dependencies view, and a simulated crash during publication that is rolled back.
    /// </summary>
    private async Task EditorLibraryPublish()
    {
        var cm = Main.Instance.Content;
        await OpenEditor("skills");
        var panel = Find<DefListPanel>(GetTree().Root).First();
        var list = Find<ItemList>(panel).First();

        // Favourites + filter.
        panel.Select("cleave");
        await Frames(4);
        await ClickButton("★ Избранное");
        Check(cm.IsFavorite(cm.Draft!.Skill("cleave")!), "«★ Избранное» не отметил объект");
        var filter = Find<OptionButton>(panel).First(o => o.ItemCount > 3 && o.GetItemText(3) == "Избранное");
        filter.Select(3);
        filter.EmitSignal(OptionButton.SignalName.ItemSelected, 3);
        await Frames(3);
        Check(list.ItemCount == 1 && list.GetItemText(0).Contains("★"), $"фильтр «Избранное» показывает {list.ItemCount} объектов");
        filter.Select(0);
        filter.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        await Frames(3);

        // Mass operation: a tag on two selected objects.
        int i1 = Enumerable.Range(0, list.ItemCount).First(i => list.GetItemText(i).Contains("[cleave]"));
        int i2 = Enumerable.Range(0, list.ItemCount).First(i => list.GetItemText(i).Contains("[quake]"));
        list.DeselectAll();
        list.Select(i1, false);
        list.Select(i2, false);
        var mass = Find<MenuButton>(panel).First(m => m.Text.StartsWith("Массово"));
        mass.GetPopup().EmitSignal(PopupMenu.SignalName.IdPressed, 1);
        await Frames(3);
        var prompt = Dialog("Добавить тег");
        Check(prompt != null, "нет диалога тега");
        Find<LineEdit>(prompt!).First().Text = "melee_aoe";
        Confirm(prompt);
        await Frames(3);
        Check(cm.Draft!.Skill("cleave")!.HasTag("melee_aoe") && cm.Draft.Skill("quake")!.HasTag("melee_aoe"), "массовое добавление тега не сработало");

        // Change the skill and publish: usages are listed; the enemies keep the old instance.
        var cleave = cm.Draft!.Skill("cleave")!;
        string oldDescription = cleave.Description;
        cleave.Description = "Новая версия рассечения";
        cm.DraftDirty = true;
        await ClickButton("Сохранить");
        var pub = Dialog("Опубликовать изменения");
        Check(pub != null, "нет диалога публикации");
        Check(LabelVisible("v1 → v2"), "диалог должен показать смену версии");
        var boxes = Find<CheckBox>(pub!).Where(b => !b.Text.StartsWith("Принимаю")).ToList();
        Check(boxes.Count >= 3, $"должны быть показаны использования рассечения (найдено {boxes.Count})");
        foreach (var b in boxes) b.ButtonPressed = b.Text.Contains("Рыцарь") || b.Text.Contains("knight");
        // Warnings of the shipped content (a pylon that does not move…) must be accepted explicitly.
        var accept = Find<CheckBox>(pub!).FirstOrDefault(b => b.Text.StartsWith("Принимаю"));
        Check(accept != null, "предупреждения должны требовать подтверждения");
        Confirm(pub);
        await Frames(3);
        Check(Dialog("Опубликовать изменения") != null && cm.Published.Skill("cleave")!.Version == 1, "без подтверждения предупреждений публикация не выполняется");
        foreach (var d in Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible && d is not ConfirmationDialog).ToList()) d.QueueFree();
        accept!.ButtonPressed = true;
        Confirm(pub);
        await Frames(4);
        var published = cm.Published;
        Check(published.Skill("cleave")!.Version == 2, "версия не увеличилась");
        Check(published.Unit("knight")!.Skills.Contains("cleave"), "рыцарь должен перейти на новую версию");
        Check(published.Unit("orc_brute")!.Skills.Contains("cleave@1") && published.Unit("titan")!.Skills.Contains("cleave@1"), "враги должны сохранить старый экземпляр");
        Check(published.Get<SkillDef>("cleave@1")!.Description == oldDescription, "старый экземпляр должен браться из истории");
        Check(cm.Library.Versions(DefCategory.Skill, "cleave").SequenceEqual(new[] { 1 }), "в истории должна быть v1");
        Check(cm.Library.CheckIntegrity().Ok, "после публикации целостность должна быть в порядке");
        Check(cm.ErrorCount == 0, "после публикации не должно быть ошибок: " + string.Join("; ", cm.LoadIssues.Where(i => i.Severity == IssueSeverity.Error).Take(3)));
        Check(InfoVisible("v1 → v2"), "итог публикации должен назвать версии");
        foreach (var d in Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible).ToList()) d.QueueFree();
        await Frames(2);

        // Dependencies and history of the skill.
        panel = Find<DefListPanel>(GetTree().Root).First();
        panel.Select("cleave");
        await Frames(4);
        await ClickButton("Связи…");
        Check(LabelVisible("Используется в") && LabelVisible("закреплено на v1"), "окно связей должно показать закреплённые использования");
        Confirm(Dialog("Связи: " + cleave.DisplayName + " [cleave]"));
        await Frames(2);
        await ClickButton("Версии…");
        await PressButton("Различия"); // inside a dialog window: press directly
        Check(LabelVisible("v1 → текущая"), "окно версий должно показать различия");
        foreach (var d in Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible).ToList()) d.QueueFree();
        await Frames(2);

        // A battle uses the pinned old instance for the titan.
        var bs = await OpenBattle("titan_lair");
        var titan = bs.Sim.Units.First(u => u.Def.Id == "titan");
        Check(bs.Sim.Content.Get<SkillDef>(titan.Def.Skills.First(x => x.StartsWith("cleave")))!.Description == oldDescription, "титан в бою должен использовать старую версию навыка");

        // Crash during publication (fault injection): the pack is rolled back, the draft is kept.
        await OpenEditor("skills");
        var skills = File.ReadAllText(System.IO.Path.Combine(cm.UserRoot, "skills.json"));
        cm.Draft!.Skill("cleave")!.Description = "Версия, которую прервёт сбой";
        cm.DraftDirty = true;
        int writes = 0;
        cm.FaultInjection = _ =>
        {
            if (++writes == 2) throw new IOException("имитация сбоя питания");
        };
        await ClickButton("Сохранить");
        var pub2 = Dialog("Опубликовать изменения");
        Check(pub2 != null, "нет диалога публикации");
        Find<CheckBox>(pub2!).First(b => b.Text.StartsWith("Принимаю")).ButtonPressed = true;
        Confirm(pub2);
        await Frames(4);
        cm.FaultInjection = null;
        Check(InfoVisible("Пакет возвращён"), "должно быть сообщение об откате");
        Check(File.ReadAllText(System.IO.Path.Combine(cm.UserRoot, "skills.json")) == skills, "файл навыков должен быть восстановлен");
        Check(!File.Exists(System.IO.Path.Combine(cm.UserRoot, ContentLibrary.JournalFile)), "журнал должен быть снят после отката");
        Check(cm.Library.CheckIntegrity().Ok, "после отката целостность должна быть в порядке");
        Check(cm.Draft!.Skill("cleave")!.Description == "Версия, которую прервёт сбой", "черновик не должен теряться");
        foreach (var d in Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible).ToList()) d.QueueFree();
    }

    /// <summary>
    /// Editor without programming (docs/DECISIONS_2.md §33): an operation added from the catalog menu,
    /// objective hexes picked on the board, the catalog reference, and the publication gate — a cycle
    /// of triggers blocks it until the scenario is fixed or marked as an incomplete draft.
    /// </summary>
    private async Task EditorCatalogAndGate()
    {
        var cm = Main.Instance.Content;
        await OpenEditor("statuses");
        var panel = Find<DefListPanel>(GetTree().Root).First();
        panel.Select("burning");
        await Frames(6);
        var burning = cm.Draft!.Status("burning")!;
        int before = burning.Levels[0].OnTick.Count;
        // The «+ из каталога» of the level's periodic operations (the condition lists have their own).
        var menu = Find<MenuButton>(panel).FirstOrDefault(m => m.IsVisibleInTree() && m.Text.StartsWith("+ из каталога")
            && m.GetPopup().GetChildren().OfType<PopupMenu>().Any(g => Enumerable.Range(0, g.ItemCount).Any(i => g.GetItemText(i) == "Изменить накопление")));
        Check(menu != null, "в списке операций должна быть кнопка «+ из каталога»");
        var groups = menu!.GetPopup().GetChildren().OfType<PopupMenu>().ToList();
        var states = groups.FirstOrDefault(g => Enumerable.Range(0, g.ItemCount).Any(i => g.GetItemText(i) == "Изменить накопление"));
        Check(states != null, $"в меню каталога нет «Изменить накопление» (групп {groups.Count})");
        int idx = Enumerable.Range(0, states!.ItemCount).First(i => states.GetItemText(i) == "Изменить накопление");
        states.EmitSignal(PopupMenu.SignalName.IdPressed, states.GetItemId(idx));
        await Frames(3);
        Check(burning.Levels[0].OnTick.Count == before + 1 && burning.Levels[0].OnTick[^1].Type == EffectType.ModifyAccumulation,
            "операция из каталога должна добавиться в список");
        burning.Levels[0].OnTick.RemoveAt(burning.Levels[0].OnTick.Count - 1);

        // The catalog reference.
        await OpenEditor("catalog");
        Check(LabelVisible("Каталог операций и условий") && LabelVisible("Изменить накопление") && LabelVisible("Босс сменил фазу"), "справочник каталога");

        // Objective hexes picked on the board.
        await OpenEditor("scenarios");
        var sp = Find<ScenarioEditorPanel>(GetTree().Root).First();
        sp.Select("gate_defense");
        await Frames(30);
        var sc = cm.Draft!.Get<ScenarioDef>("gate_defense")!;
        var core = sc.Objectives.First(o => o.Id == "core");
        await ClickButton("Клетки цели");
        var objOpt = Find<OptionButton>(sp).FirstOrDefault(o => o.ItemCount > 0 && o.GetItemText(0).Contains("[core]"));
        Check(objOpt != null, "нет списка целей у инструмента «Клетки цели»");
        objOpt!.Select(0);
        var vp = Find<BoardViewport>(sp).First();
        var targetCell = vp.Board.Map!.Cells.FirstOrDefault(c => c.Terrain.IsPassableFor("walk") && !core.Cells.Contains(c.Coord) && c.Coord.Q + c.Coord.R / 2 == 4);
        Check(targetCell != null, "нет подходящего гекса");
        var target = targetCell!.Coord;
        var cell = vp.Board.Map.Get(target)!;
        var local = vp.Board.Rig.Camera.UnprojectPosition(Render.Board.Center(target, Render.Board.PickY(cell)));
        await Click(vp.GetGlobalRect().Position + local);
        Check(core.Cells.Contains(target), $"клик по гексу должен добавить его в цель (курсор над {vp.HoverHex}, нужен {target})");
        await Click(vp.GetGlobalRect().Position + local);
        Check(!core.Cells.Contains(target), "повторный клик убирает гекс из цели");

        // Publication gate: a cycle of triggers is a critical error.
        sc.Triggers.Add(new TriggerDef { Name = "А", Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 5 }, Actions = { new TriggerActionDef { Type = TriggerActionType.ActivateTrigger, Text = "Б" } } });
        sc.Triggers.Add(new TriggerDef { Name = "Б", Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 9 }, Actions = { new TriggerActionDef { Type = TriggerActionType.ActivateTrigger, Text = "А" } } });
        cm.DraftDirty = true;
        await ClickButton("Сохранить");
        Check(Dialog("Публикация заблокирована") != null && LabelVisible("Циклические триггеры"), "цикл триггеров должен блокировать публикацию");
        Check(Dialog("Опубликовать изменения") == null, "окно публикации не должно открываться");
        Confirm(Dialog("Публикация заблокирована"));
        await Frames(3);
        // Marked as an incomplete draft, the scenario no longer blocks (and is not played).
        sc.Incomplete = true;
        await ClickButton("Сохранить");
        var pub = Dialog("Опубликовать изменения");
        Check(pub != null && LabelVisible("Черновики"), "черновик не блокирует публикацию");
        pub!.EmitSignal(ConfirmationDialog.SignalName.Canceled);
        await Frames(2);
        sc.Triggers.RemoveRange(sc.Triggers.Count - 2, 2);
        sc.Incomplete = false;
    }

    /// <summary>
    /// TZ §16 п. 20–21: the pylon shows its imported model and plays the clips of its animation roles;
    /// a damaged model file falls back to the base figure with a reported problem and the battle goes on;
    /// operations and events play visual effects.
    /// </summary>
    private async Task BattleModelsAndFallback()
    {
        Render.Assets.Reset();
        var bs = await OpenBattle("titan_lair");
        var pylon = bs.Sim.Units.First(u => u.Def.Id == "pylon");
        var view = bs.Board.ViewOf(pylon);
        Check(view.UsesModel, "пилон должен отображаться импортированной моделью: " + view.ModelProblem);
        Check(view.Role == AnimRole.Spawn && view.CurrentClip == "Spawn", $"сначала — клип появления (роль {view.Role}, клип «{view.CurrentClip}»)");
        for (int i = 0; i < bs.Sim.TicksFor(0.7); i++) bs.Sim.Step();
        await Frames(3);
        Check(view.Role == AnimRole.Idle && view.CurrentClip == "Idle", $"затем — ожидание (роль {view.Role}, клип «{view.CurrentClip}»)");
        bs.Sim.Combat.DealDamage(null, pylon, 5, "physical", null, "тест", null, DamageSource.Other);
        await Frames(2);
        Check(view.Role == AnimRole.Hit && view.CurrentClip == "Hit", "получение урона — клип «Hit»");
        // The knight has no model: its roles are procedural.
        var knight = bs.Board.ViewOf(bs.Sim.Units.First(u => u.Def.Id == "knight"));
        Check(!knight.UsesModel && knight.CurrentClip == "", "у фишки анимации процедурные");

        // Visual effects of operations and events.
        int played = Render.Vfx.Played;
        bs.Sim.Combat.ApplyGlobal(new SkillEffectDef { Type = EffectType.PlayVfx, Text = "spark_burst" }, null, pylon.Pos, "тест");
        bs.Sim.Combat.ApplyGlobal(new SkillEffectDef { Type = EffectType.PlayVfx, Text = "нет_такого" }, null, pylon.Pos, "тест");
        await Frames(3);
        Check(Render.Vfx.Played >= played + 2, "эффекты (и эффект по умолчанию для неизвестного ID) должны проигрываться");

        // A damaged model file in the user pack: the base figure, a reported problem, the battle goes on.
        var cm = Main.Instance.Content;
        string damaged = System.IO.Path.Combine(cm.UserRoot, "assets", "models", "crystal_pylon.glb");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(damaged)!);
        File.WriteAllBytes(damaged, System.Text.Encoding.ASCII.GetBytes("glTF this file was cut in half"));
        try
        {
            Render.Assets.Reset();
            bs = await OpenBattle("titan_lair");
            pylon = bs.Sim.Units.First(u => u.Def.Id == "pylon");
            view = bs.Board.ViewOf(pylon);
            Check(!view.UsesModel, "повреждённая модель не должна использоваться");
            Check(view.ModelProblem.Contains("повреждён") || view.ModelProblem.Contains("GLB"), "должна быть указана причина: " + view.ModelProblem);
            Check(Render.Assets.Problems.ContainsKey("crystal_pylon"), "проблема должна попасть в список ресурсов");
            for (int i = 0; i < 100; i++) bs.Sim.Step();
            await Frames(3);
            Check(bs.Sim.Units.Count(u => u.Alive) > 0 && bs.IsInsideTree(), "бой продолжается");
            Check(AssetValidator.CheckFiles(cm.Published, Render.Assets.Read).Any(i => i.Source == "Asset:crystal_pylon"), "проверка файлов сообщает о повреждении");
        }
        finally
        {
            File.Delete(damaged);
            Render.Assets.Reset();
        }
    }

    /// <summary>The editor imports a model (checked, copied into the pack, clips mapped) and rejects a script.</summary>
    private async Task EditorAssetImport()
    {
        var cm = Main.Instance.Content;
        string dir = ProjectSettings.GlobalizePath("user://uitest/import");
        Directory.CreateDirectory(dir);
        string model = System.IO.Path.Combine(dir, "Scout Drone.glb");
        File.WriteAllBytes(model, Render.Assets.Read("assets/models/crystal_pylon.glb")!);
        string script = System.IO.Path.Combine(dir, "evil.gd");
        File.WriteAllText(script, "extends Node\nfunc _ready(): OS.execute(\"rm\", [\"-rf\", \"/\"])");
        string next = model;
        Dialogs.AutoPath = _ => next;
        try
        {
            await OpenEditor("assets");
            await ClickButton("Импорт файла…");
            // The same bytes as the shipped pylon: reuse is offered; here a separate resource is wanted.
            var reuse = Find<CheckBox>(ImportDialog()!).First(c => c.Name == "ImportReuse");
            Check(reuse.Visible && reuse.ButtonPressed && reuse.Text.Contains("crystal_pylon"), "совпадающий файл должен предлагаться: " + reuse.Text);
            reuse.ButtonPressed = false;
            await ConfirmImport();
            Check(Find<AcceptDialog>(GetTree().Root).Any(d => d.Visible && d.Title == "Ресурс импортирован"),
                "модель должна импортироваться: " + string.Join(" | ", Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible).Select(d => d.Title + ": " + d.DialogText)));
            var def = cm.Draft!.Get<AssetDef>("scout_drone");
            Check(def != null && def.Kind == AssetKind.Model && def.Clips.Contains("Idle") && def.Animations.Any(a => a.Role == AnimRole.Death && a.Clip == "Death"),
                "ресурс должен описать модель, клипы и роли анимаций");
            Check(File.Exists(System.IO.Path.Combine(cm.UserRoot, "assets", "models", "scout_drone.glb")), "файл должен быть скопирован в пакет");
            Check(Render.Assets.Model(def) != null, "импортированная модель должна загружаться");
            CloseDialogs();
            await Frames(2);
            next = script;
            int before = cm.Draft!.All<AssetDef>().Count();
            await ClickButton("Импорт файла…");
            Check(ImportText().Contains("исполняемый код"), "скрипт должен быть отклонён: " + ImportText());
            await ConfirmImport();
            Check(Dialog(AssetImportDialog.Title) != null, "окно импорта отклонённого файла не должно закрываться по «Создать ресурс»");
            Check(cm.Draft.All<AssetDef>().Count() == before, "отклонённый файл не создаёт ресурс");
            CloseDialogs();
        }
        finally
        {
            Dialogs.AutoPath = null;
        }
    }

    // ------------------------------------------------------------------ part 3, stage 7

    private ConfirmationDialog? ImportDialog() => Find<ConfirmationDialog>(GetTree().Root).LastOrDefault(d => d.Visible && d.Name == "AssetImportDialog");

    private string ImportText()
    {
        var d = ImportDialog() ?? Find<ConfirmationDialog>(GetTree().Root).LastOrDefault(x => x.Visible && x.Name == "AssetCheckDialog");
        return d == null ? "(окна импорта нет)" : string.Join("\n", Find<Label>(d).Where(l => l.IsVisibleInTree()).Select(l => l.Text));
    }

    private async Task ConfirmImport()
    {
        var d = ImportDialog();
        Check(d != null, "окно импорта не открылось");
        d!.EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frames(4);
    }

    private static void SelectOption(OptionButton o, Func<string, bool> match)
    {
        for (int i = 0; i < o.ItemCount; i++)
            if (match(o.GetItemText(i)))
            {
                o.Select(i);
                o.EmitSignal(OptionButton.SignalName.ItemSelected, i);
                return;
            }
        throw new Exception("нет пункта в списке: " + string.Join(" | ", Enumerable.Range(0, o.ItemCount).Select(o.GetItemText)));
    }

    /// <summary>
    /// Part 3, stage 7 (docs/DECISIONS_3.md §47): the import window analyses generated PNG/TGA/OBJ/WAV files against the
    /// limits, reduces a too-large image, assigns the resource to a skill, a terrain and a unit sound; a script is
    /// refused; the requirements page exports TXT and HTML; «Проверить файл» only analyses.
    /// </summary>
    private async Task EditorImportWindow()
    {
        var cm = Main.Instance.Content;
        string dir = ProjectSettings.GlobalizePath("user://uitest/import3");
        Directory.CreateDirectory(dir);
        var big = Image.CreateEmpty(2048, 2048, false, Image.Format.Rgba8);
        big.Fill(new Color(0.2f, 0.6f, 1f, 0.8f));
        string png = System.IO.Path.Combine(dir, "Big Icon.png");
        big.SavePng(png);
        // TGA 4×4, 32 bit, by hand (the engine reads TGA but does not write it).
        var tga = new byte[18 + 4 * 4 * 4];
        tga[2] = 2; tga[12] = 4; tga[14] = 4; tga[16] = 32; tga[17] = 8;
        for (int i = 18; i < tga.Length; i++) tga[i] = (byte)(i % 4 == 3 ? 255 : 90 + i % 60);
        string tgaPath = System.IO.Path.Combine(dir, "hex_tile.tga");
        File.WriteAllBytes(tgaPath, tga);
        string obj = System.IO.Path.Combine(dir, "pillar.obj");
        File.WriteAllText(obj, "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nv 0 0 1\nv 1 0 1\nv 1 1 1\nv 0 1 1\n" +
                               "f 1 2 3 4\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n");
        string wav = System.IO.Path.Combine(dir, "clang.wav");
        File.WriteAllBytes(wav, Render.Assets.Read("assets/sounds/chime.wav")!);
        string next = png;
        Dialogs.AutoPath = _ => next;
        try
        {
            await OpenEditor("assets");
            var db = cm.Draft!;
            // 1. A 2048 image as a skill icon: larger than the maximum → reduce → assign to «Щит веры».
            await ClickButton("Импорт файла…");
            var dlg = ImportDialog();
            Check(dlg != null, "окно импорта не открылось");
            Check(ImportText().Contains("2048×2048") && ImportText().Contains("альфа-канал: есть"), "окно должно показать размеры и альфа-канал: " + ImportText());
            var assign = Find<OptionButton>(dlg!).First(o => o.Name == "ImportAssign");
            SelectOption(assign, t => t.StartsWith("Навык «Щит веры» — иконка"));
            await Frames(2);
            var purpose = Find<OptionButton>(dlg!).First(o => o.Name == "ImportPurpose");
            Check(purpose.GetSelectedId() == (int)TexturePurpose.SkillIcon, "назначение должно стать «иконка навыка» по выбранному объекту");
            Check(ImportText().Contains("больше максимума 1024×1024"), "иконка 2048 больше максимума: " + ImportText());
            var resize = Find<CheckBox>(dlg!).First(c => c.Name == "ImportResize");
            Check(resize.Visible, "должно предлагаться уменьшение");
            resize.ButtonPressed = true;
            await Frames(2);
            Check(ImportText().Contains("1024×1024") && ImportText().Contains("Уменьшено"), "после уменьшения — 1024×1024: " + ImportText());
            Check(Find<TextureRect>(dlg!).Any(t => t.Name == "ImportPreviewImage" && t.Texture != null), "должен быть предпросмотр изображения");
            await ConfirmImport();
            var icon = db.Get<AssetDef>(db.Skill("aegis")!.IconAsset);
            Check(icon != null && icon.Width == 1024 && icon.Path.EndsWith(".png") && icon.Purpose == TexturePurpose.SkillIcon, "иконка навыка не назначена: " + db.Skill("aegis")!.IconAsset);
            Check(Render.Assets.Texture(icon) is { } t1 && t1.GetWidth() == 1024, "уменьшенная иконка должна загружаться");
            CloseDialogs();
            await Frames(2);

            // 2. A TGA as a hex top texture.
            next = tgaPath;
            await ClickButton("Импорт файла…");
            Check(ImportText().Contains("4×4") && ImportText().Contains("TGA"), "TGA: " + ImportText());
            SelectOption(Find<OptionButton>(ImportDialog()!).First(o => o.Name == "ImportAssign"), t => t.StartsWith("Местность «Платформа» — текстура верха") || t.StartsWith("Местность «Пол» — текстура верха"));
            await Frames(2);
            Check(Find<OptionButton>(ImportDialog()!).First(o => o.Name == "ImportPurpose").GetSelectedId() == (int)TexturePurpose.HexSurface, "назначение — поверхность гекса");
            Check(ImportText().Contains("меньше рекомендуемого"), "маленькая текстура — совет: " + ImportText());
            await ConfirmImport();
            var terrain = db.All<TerrainDef>().First(x => x.Id is "platform" or "floor" && x.Look.TopTexture != "");
            Check(Render.Assets.Texture(db.Get<AssetDef>(terrain.Look.TopTexture)) != null, "TGA должна загружаться как текстура верха");
            CloseDialogs();
            await Frames(2);

            // 3. OBJ — static geometry; the engine counts the triangles in the preview.
            next = obj;
            await ClickButton("Импорт файла…");
            Check(ImportText().Contains("треугольников 12"), "OBJ: 6 четырёхугольников = 12 треугольников: " + ImportText());
            Check(Find<Label>(ImportDialog()!).Any(l => l.Name == "ImportEngineStats" && l.Text.Contains("треугольников 12")), "движок должен прочитать модель: " + ImportText());
            await ConfirmImport();
            var pillar = db.All<AssetDef>().First(a => a.SourceFile == "pillar.obj");
            Check(Render.Assets.Model(pillar) != null, "OBJ должна загружаться");
            CloseDialogs();
            await Frames(2);

            // 4. A sound for the knight's hits.
            next = wav;
            await ClickButton("Импорт файла…");
            Check(ImportText().Contains("Звук:") && ImportText().Contains("Гц"), "WAV: " + ImportText());
            SelectOption(Find<OptionButton>(ImportDialog()!).First(o => o.Name == "ImportAssign"), t => t.StartsWith("Юнит «Рыцарь» — звук получения урона"));
            // The bytes are the shipped chime: the existing resource is offered and taken (no duplicate file).
            Check(Find<CheckBox>(ImportDialog()!).Any(c => c.Name == "ImportReuse" && c.Visible && c.ButtonPressed), "совпадающий звук должен предлагаться");
            await ConfirmImport();
            Check(db.Unit("knight")!.Visual.HitSound == "chime", "звук рыцаря не назначен: " + db.Unit("knight")!.Visual.HitSound);
            CloseDialogs();
            await Frames(2);

            // 4b. glTF with its own texture next to it: the reference is found and the file goes into the pack.
            Directory.CreateDirectory(System.IO.Path.Combine(dir, "tex"));
            big.Resize(64, 64);
            big.SavePng(System.IO.Path.Combine(dir, "tex", "skin.png"));
            string gltf = System.IO.Path.Combine(dir, "statue.gltf");
            File.WriteAllText(gltf, "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"name\":\"root\"}],\"images\":[{\"uri\":\"tex/skin.png\"}]}");
            next = gltf;
            await ClickButton("Импорт файла…");
            Check(!ImportText().Contains("tex/skin.png") || !ImportText().Contains("✗"), "внешний файл рядом с моделью должен находиться: " + ImportText());
            await ConfirmImport();
            var statue = db.All<AssetDef>().FirstOrDefault(a => a.SourceFile == "statue.gltf");
            Check(statue != null, "glTF не импортирован: " + ImportText());
            Check(File.Exists(System.IO.Path.Combine(cm.UserRoot, System.IO.Path.GetDirectoryName(statue!.Path)!, "tex", "skin.png")), "текстура модели должна быть скопирована в пакет");
            CloseDialogs();
            await Frames(2);
            File.WriteAllText(gltf, "{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"uri\":\"tex/lost.png\"}]}");
            await ClickButton("Импорт файла…");
            Check(ImportText().Contains("lost.png"), "отсутствующий файл модели должен быть назван: " + ImportText());
            CloseDialogs();
            await Frames(2);

            // 5. «Проверить файл» — only the analysis.
            int count = db.All<AssetDef>().Count();
            next = png;
            await ClickButton("Проверить файл…");
            Check(ImportText().Contains("2048×2048"), "проверка файла должна показать анализ: " + ImportText());
            CloseDialogs();
            await Frames(2);
            Check(db.All<AssetDef>().Count() == count, "проверка не создаёт ресурс");

            // 6. The requirements page: exports and the summary.
            await OpenEditor("requirements");
            Check(Find<Label>(GetTree().Root).Any(l => l.Name == "AssetSummary" && l.Text.Contains("Ресурсов:")), "сводка ресурсов");
            string txt = System.IO.Path.Combine(dir, "req.txt"), html = System.IO.Path.Combine(dir, "req.html");
            next = txt;
            await ClickButton("Экспорт TXT…");
            CloseDialogs();
            next = html;
            await ClickButton("Экспорт HTML…");
            CloseDialogs();
            Check(File.Exists(txt) && File.ReadAllText(txt).Contains("ТРЕБОВАНИЯ К РЕСУРСАМ") && File.ReadAllText(txt).Contains("Иконка навыка | 128–256 | 1024×1024"), "TXT требований");
            Check(File.Exists(html) && File.ReadAllText(html).Contains("<table>") && File.ReadAllText(html).Contains("Маркеры анимации"), "HTML требований");
        }
        finally
        {
            Dialogs.AutoPath = null;
            CloseDialogs();
        }
    }

    /// <summary>
    /// Part 3, stage 7: a fireball flies to its target as an arc projectile (the hit numbers wait for it), the fire on a
    /// hex runs its cyclic effect without duplicates, the radial menu shows a skill's image icon.
    /// </summary>
    private async Task BattleProjectilesAndLoops()
    {
        Render.Assets.Reset();
        var bs = await OpenBattle("sandbox");
        var mage = bs.Sim.Units.First(u => u.Def.Id == "mage");
        var foe = bs.Sim.Units.Where(u => u.Alive && bs.Sim.IsHostile(mage, u)).OrderBy(u => u.Pos.DistanceTo(mage.Pos)).First();
        bs.Sim.Knowledge.Reveal(foe);
        var err = bs.Sim.Enqueue(mage, bs.CommandAgainst(mage, bs.Db.Skill("fireball")!, foe));
        Check(err == null, "приказ «Огненный шар» не принят: " + err);
        bool flew = false, lagged = false;
        for (int i = 0; i < 600 && !flew; i++)
        {
            bs.Sim.Step();
            if (bs.Sim.Log.Entries.Any(e => e.Type == SimEventType.StageResolved && e.DamageType == "fireball"))
            {
                await Frames(1);
                lagged = bs.LaggedHits > 0 || lagged;
                flew = Find<Node3D>(bs.Board).Any(n => n.Name.ToString().StartsWith("Projectile"));
            }
        }
        Check(flew, "снаряд огненного шара не появился");
        Check(lagged, "числа урона должны ждать прилёта снаряда");
        await Frames(40);
        Check(!Find<Node3D>(bs.Board).Any(n => n.Name.ToString() == "Projectile"), "снаряд должен исчезнуть после попадания");
        Check(bs.LaggedHits == 0, "отложенные попадания должны быть показаны");

        // The fire on a hex: its cyclic effect, not duplicated by a second ignition.
        var cell = bs.Sim.Map.Get(mage.Pos)!;
        var fire = bs.Db.CellEffect("fire")!;
        foreach (var c in new[] { mage.Pos }.Concat(mage.Pos.Neighbors()).Select(bs.Sim.Map.Get).Where(c => c != null))
            if (bs.Sim.CellEffects.Add(c!, fire, 1, 5, -1, 0, 0) != null)
            {
                cell = c!;
                break;
            }
        await Frames(4);
        int loops = Find<Node3D>(bs.Board.Effects).Count(n => n.HasMeta("vfx") && (string)n.GetMeta("vfx") == "fire_loop");
        Check(loops == 1, $"циклический эффект огня должен быть один (найдено {loops})");

        // The radial menu: «Щит веры» with its image icon (if the unit knows it).
        var holder = bs.Sim.Units.FirstOrDefault(u => u.Alive && u.Def.Skills.Contains("aegis") && bs.Sim.IsPlayerFaction(u.FactionId));
        if (holder != null)
        {
            bs.Select(holder);
            await Frames(3);
            bs.OpenSkillMenu(null, new Vector2(800, 450));
            await Frames(3);
            var b = bs.Hud.Radial.Buttons.FirstOrDefault(x => x.Name == "Radial_aegis");
            Check(b != null && b.Icon != null, "у «Щита веры» в радиальном меню должна быть иконка-изображение");
            bs.Hud.Radial.Close();
        }
        Render.Assets.Reset();
    }

    /// <summary>
    /// Part 3, stage 8 (TZ §23.11): the real battle screen driven by the keys — pause, resume, faster, slower — gives
    /// exactly the battle of a headless run with the same orders; the screen receives every event; the hits held for
    /// projectiles are all shown.
    /// </summary>
    private async Task BattleSpeedsAndPause()
    {
        var db = Main.Instance.Content.Published;
        var sc = db.Get<ScenarioDef>("valley_ambush")!;
        var bs = await OpenBattle("valley_ambush");
        var reference = ScenarioSystem.Create(db, sc, null, false);
        reference.ConsumeAutopause();
        void Orders(Simulation sim)
        {
            var knight = sim.Units.First(u => u.Def.Id == "knight");
            var mage = sim.Units.First(u => u.Def.Id == "mage");
            var foe = sim.Units.Where(x => x.Alive && sim.IsHostile(knight, x)).OrderBy(x => HexCoord.Distance(x.Pos, knight.Pos)).First();
            sim.Enqueue(knight, new Command { Type = CommandType.Skill, SkillId = "triple_strike", TargetUnitId = foe.Id, Approach = true });
            sim.Enqueue(mage, new Command { Type = CommandType.Skill, SkillId = "fireball", TargetUnitId = foe.Id, Approach = true });
        }
        Check(bs.Sim.Tick == 0, "бой должен стоять на паузе до начала проверки");
        Orders(bs.Sim);
        Orders(reference);
        var keys = new[] { Godot.Key.Space, Godot.Key.Equal, Godot.Key.Equal, Godot.Key.Space, Godot.Key.Space, Godot.Key.Minus, Godot.Key.Equal, Godot.Key.Equal, Godot.Key.Minus };
        var speeds = new HashSet<int>();
        int pausedFrames = 0;
        if (bs.Driver.Paused) await Key(Godot.Key.Space);
        for (int i = 0; i < 54; i++)
        {
            if (i % 6 == 0) await Key(keys[i / 6 % keys.Length]);
            else await Frames(1);
            if (bs.Driver.Paused)
            {
                pausedFrames++;
                long t0 = bs.Sim.Tick;
                await Frames(2);
                Check(bs.Sim.Tick == t0, "на паузе бой не должен идти");
                if (i % 6 >= 3) bs.Resume(); // an autopause or our pause: the player continues
            }
            speeds.Add(bs.Driver.SpeedIndex);
        }
        bs.Driver.Paused = true;
        await Frames(2);
        long ticks = bs.Sim.Tick;
        Check(ticks > bs.Sim.TicksFor(5), $"бой почти не шёл ({ticks} тиков)");
        Check(speeds.Count >= 2 && pausedFrames > 0, $"скорость и пауза должны меняться (скоростей {speeds.Count}, кадров паузы {pausedFrames})");
        for (long k = reference.Tick; k < ticks; k++) reference.Step();
        Check(reference.Log.TotalCount == bs.Sim.Log.TotalCount, $"событий в бою {bs.Sim.Log.TotalCount}, в эталоне {reference.Log.TotalCount}");
        var a = bs.Sim.Log.Entries.Select(e => $"{e.Tick}|{e.Type}|{e.Text}").ToList();
        var b = reference.Log.Entries.Select(e => $"{e.Tick}|{e.Type}|{e.Text}").ToList();
        int diff = Enumerable.Range(0, Math.Min(a.Count, b.Count)).FirstOrDefault(i => a[i] != b[i], -1);
        Check(diff < 0, diff < 0 ? "" : $"бой разошёлся с эталоном: «{a[diff]}» ≠ «{b[diff]}»");
        Check(bs.EventsSeen == bs.Sim.Log.TotalCount, $"экран получил {bs.EventsSeen} событий из {bs.Sim.Log.TotalCount}");
        await Frames(60);
        Check(bs.LaggedHits == 0, "показ попаданий, ждавших снаряд, не завершился");
        GD.Print($"скорости и пауза: тиков {ticks}, событий {bs.Sim.Log.TotalCount}, скоростей {speeds.Count}, кадров паузы {pausedFrames}");
    }

    /// <summary>Drags the left button through several points (a gesture that bends).</summary>
    private async Task DragPath(IReadOnlyList<Vector2> points, int stepsPerLeg = 4)
    {
        var from = points[0];
        Input.ParseInputEvent(new InputEventMouseMotion { Position = from, GlobalPosition = from });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = from, GlobalPosition = from, ButtonIndex = MouseButton.Left, Pressed = true });
        await Frames(2);
        for (int leg = 1; leg < points.Count; leg++)
            for (int i = 1; i <= stepsPerLeg; i++)
            {
                var p = points[leg - 1].Lerp(points[leg], (float)i / stepsPerLeg);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = MouseButtonMask.Left });
                await Frames(1);
            }
        var to = points[^1];
        Input.ParseInputEvent(new InputEventMouseButton { Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames(3);
    }

    /// <summary>Scrolls the button into view (inside a scrolled panel) and clicks it.</summary>
    private async Task ClickInView(Control c)
    {
        for (Node? n = c.GetParent(); n != null; n = n.GetParent())
            if (n is ScrollContainer sc)
            {
                sc.EnsureControlVisible(c);
                break;
            }
        await Frames(3);
        await Click(c.GetGlobalRect().GetCenter());
    }

    /// <summary>Waits until the smooth camera stops (framing a new map eases in over several frames).</summary>
    private async Task CameraSettled(Render.CameraRig rig)
    {
        var last = rig.Camera.GlobalPosition;
        for (int i = 0; i < 240; i++)
        {
            await Frames(1);
            var now = rig.Camera.GlobalPosition;
            if (now.DistanceTo(last) < 0.0005f) return;
            last = now;
        }
    }

    private async Task ClickHex(BoardViewport vp, HexCoord c)
    {
        var cell = vp.Board.Map!.Get(c)!;
        var local = vp.Board.Rig.Camera.UnprojectPosition(Render.Board.Center(c, Render.Board.PickY(cell)));
        await Click(vp.GetGlobalRect().Position + local);
    }

    /// <summary>
    /// The final criterion of part 3 (TZ §23.6–7) through the editor: a new multi-hit skill from the template with
    /// its own multiplier per hit and a test run, a conditional talent with the player's text, a unit that has both;
    /// saved to the library; in a battle on the published content the talent changes the stat only while its
    /// condition holds and the skill strikes three times with its multipliers.
    /// </summary>
    private async Task AdminPart3Flow()
    {
        var cm = Main.Instance.Content;
        // 1. The skill: «Создать», the «Три удара» template, the second hit at 90 %, a run on the test map.
        await OpenEditor("skills");
        await ClickButton("Создать");
        var panel = Find<SkillEditorPanel>(GetTree().Root).First();
        var skill = Current<SkillDef>(panel);
        skill.Name = "Тройной выпад";
        skill.Targeting = TargetingMode.Unit;
        skill.RangeMax = 1;
        skill.ApCost = 2;
        panel.Select(skill);
        await Frames(4);
        var tabs = Find<TabContainer>(panel).First(t => t.GetTabCount() >= 5);
        tabs.CurrentTab = 3;
        await Frames(4);
        await ClickButton("Шаблон «Три удара»");
        Check(skill.Stages.Count == 3 && skill.Stages.Select(x => x.DamageMultiplier).SequenceEqual(new[] { 0.7, 0.85, 1.2 }), "шаблон должен дать три удара 70/85/120 %");
        foreach (var st in skill.Stages) st.Area = new AreaDef { Shape = AreaShape.Single };
        skill.Stages[1].DamageMultiplier = 0.9;
        panel.Select(skill);
        await Frames(4);
        tabs = Find<TabContainer>(panel).First(t => t.GetTabCount() >= 5);
        tabs.CurrentTab = 3;
        await Frames(4);
        var run = Find<Button>(panel).First(b => b.Name == "SeriesTestRun");
        for (Node? n = run.GetParent(); n != null; n = n.GetParent())
            if (n is ScrollContainer sc)
            {
                sc.EnsureControlVisible(run);
                break;
            }
        await Frames(3);
        await Click(run.GetGlobalRect().GetCenter());
        await Frames(4);
        Check(Find<Label>(panel).Any(l => l.Text.StartsWith("Удар 3 →")), "прогон нового навыка должен показать третий удар");

        // 2. The talent: +6 armor while the owner's health is at most 50 %.
        await OpenEditor("talents");
        await ClickButton("Создать");
        var tp = Find<PassivePanel>(GetTree().Root).First();
        var talent = Current<PassiveDef>(tp);
        talent.Name = "Закалка ветерана";
        talent.Kind = PassiveKind.Talent;
        talent.Modifiers.Add(new ModifierDef { Target = ModifierTarget.Stat, Stat = Stats.Armor, Value = 6, Mode = ModifierMode.FlatBefore });
        talent.When = new UnitConditionDef { Type = UnitConditionType.HpFraction, Subject = ConditionSubject.Self, Compare = CompareMode.AtMost, Value = 0.5 };
        tp.Select(talent);
        await Frames(4);
        var text = Find<Label>(tp).FirstOrDefault(l => l.Name == "PassivePreview")?.Text ?? "";
        Check(text.Contains("Закалка ветерана") && text.Contains("Условие"), "текст для игрока должен показать талант и его условие: " + text);

        // 3. A unit with both.
        await OpenEditor("units");
        await ClickButton("Создать");
        var unit = Current<UnitDef>(Find<DefListPanel>(GetTree().Root).First());
        unit.Name = "Ветеран";
        unit.Faction = "player";
        unit.BasicAttack = "strike";
        unit.Skills.Add(skill.Id);
        unit.Passives.Add(talent.Id);
        unit.Stats[Stats.HpMax] = 100;
        unit.Stats[Stats.Armor] = 2;

        // 4. Save to the library.
        await ClickButton("Сохранить");
        var pub = Dialog("Опубликовать изменения");
        Check(pub != null, "окно публикации должно открыться (без критических ошибок)");
        if (Find<CheckBox>(pub!).FirstOrDefault(b => b.Text.StartsWith("Принимаю")) is { } accept) accept.ButtonPressed = true;
        Confirm(pub);
        await Frames(4);
        CloseDialogs();
        var db = cm.Published;
        Check(db.Get<SkillDef>(skill.Id) != null && db.Get<PassiveDef>(talent.Id) != null && db.Get<UnitDef>(unit.Id) != null, "навык, талант и юнит должны быть в библиотеке");

        // 5. A battle on the published content.
        var map = db.Get<MapDef>("arena")!;
        var sim = new Simulation(db, map, new SimOptions { Seed = 9, PlayerFactions = new() { "player" }, AiFactions = new(), FogOfWar = false });
        var cell = sim.Map.Cells.First(c => c.Terrain.IsPassableFor("walk") && c.Terrain.AllowsPlacement && c.Occupants.Count == 0 &&
                                            sim.Map.Get(c.Coord.Neighbor(HexDirection.E)) is { } n && n.Terrain.IsPassableFor("walk") && n.Terrain.AllowsPlacement && n.Occupants.Count == 0);
        var vet = sim.Spawn(db.Get<UnitDef>(unit.Id)!, null, cell.Coord, HexDirection.E);
        var foe = sim.Spawn(db.Unit("orc_brute")!, null, cell.Coord.Neighbor(HexDirection.E), HexDirection.W);
        sim.RunSeconds(1);
        double calm = vet.Stat(Stats.Armor);
        sim.Combat.DealDamage(null, vet, 60, "true", null, "тест", null, DamageSource.Other);
        sim.RunSeconds(1);
        double hurt = vet.Stat(Stats.Armor);
        Check(Math.Abs(hurt - calm - 6) < 1e-6, $"талант: броня {calm} → {hurt}, ожидалось +6 при здоровье ≤ 50 %");
        Check(sim.Enqueue(vet, Command.Skill(skill.Id, null, foe.Id)) == null, "приказ нового навыка не принят");
        for (long i = 0; i < sim.TicksFor(10) && sim.Log.Entries.Count(e => e.Type == SimEventType.SkillHit && e.Text.Contains("Тройной выпад")) < 3; i++) sim.Step();
        var hits = sim.Log.Entries.Where(e => e.Type == SimEventType.SkillHit && e.Text.Contains("Тройной выпад")).Select(e => e.Text).ToList();
        Check(hits.Count == 3, "в бою должно быть три удара: " + string.Join(" | ", hits));
        Check(hits[0].Contains("70 %") && hits[1].Contains("90 %") && hits[2].Contains("120 %"), "множители ударов: " + string.Join(" | ", hits));
        GD.Print("часть 3, итоговый путь: " + string.Join(" | ", hits) + $"; броня {calm} → {hurt}");
    }

    private static T Current<T>(DefListPanel p) where T : Def => (T)p.Current!;

    /// <summary>
    /// The final criterion of part 2: an administrator creates a map, its terrain, an element and a
    /// damage type, a skill from the catalog, an enemy with an auto-attack and a scenario; saves to the
    /// library; exports the scenario package; removes the objects; imports the package back; and plays it.
    /// </summary>
    private async Task AdminFullFlow()
    {
        var cm = Main.Instance.Content;
        string pkgPath = System.IO.Path.Combine(ProjectSettings.GlobalizePath("user://uitest/packages"), "admin_flow.hexpack");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pkgPath)!);
        Dialogs.AutoPath = _ => pkgPath;
        try
        {
            // 1. Map from the standard template, terrain painted with the brush.
            await OpenEditor("maps");
            await ClickButton("Создать");
            var mapPanel = Find<MapEditorPanel>(GetTree().Root).First();
            var map = Current<MapDef>(mapPanel);
            map.Name = "Поле испытаний";
            await Frames(30);
            var brush = Find<OptionButton>(mapPanel).First(o => o.ItemCount > 5 && o.GetItemText(0).Contains("["));
            brush.Select(Enumerable.Range(0, brush.ItemCount).First(i => brush.GetItemText(i).Contains("[lava]")));
            var vp = Find<BoardViewport>(mapPanel).First();
            var lavaCell = map.Cells.First(c => c.C.Q + c.C.R / 2 == 5 && c.C.R == 7).C;
            await ClickHex(vp, lavaCell);
            Check(map.Cells.First(c => c.C == lavaCell).T == "lava", "кисть должна нарисовать лаву");
            string mapId = map.Id;

            // 2. Element (inherits fire) and a damage type of it.
            await OpenEditor("elements");
            await ClickButton("Создать");
            var element = Current<ElementDef>(Find<DefListPanel>(GetTree().Root).First());
            element.Name = "Плазма";
            element.Parent = "fire";
            await OpenEditor("damage");
            await ClickButton("Создать");
            var dtype = Current<DamageTypeDef>(Find<DefListPanel>(GetTree().Root).First());
            dtype.Name = "Плазменный ожог";
            dtype.Kind = DamageCategory.Elemental;
            dtype.Element = element.Id;

            // 3. A skill: its damage operation comes from the catalog.
            await OpenEditor("skills");
            await ClickButton("Создать");
            var skillPanel = Find<DefListPanel>(GetTree().Root).First();
            var skill = Current<SkillDef>(skillPanel);
            skill.Name = "Плазменный заряд";
            skill.Targeting = TargetingMode.Unit;
            skill.RangeMax = 3;
            skill.ApCost = 1;
            if (skill.Stages.Count == 0) skill.Stages.Add(new SkillStageDef());
            skill.Stages[0].Area = new AreaDef { Shape = AreaShape.Single };
            skill.Stages[0].Effects.Clear();
            var op = (SkillEffectDef)OperationCatalog.Abilities.First(e => e.Name == "Нанести урон").Create();
            op.DamageType = dtype.Id;
            op.Amount = 6;
            skill.Stages[0].Effects.Add(op);

            // 4. An enemy with that skill as an auto-attack.
            await OpenEditor("enemies");
            await ClickButton("Создать");
            var enemy = Current<UnitDef>(Find<DefListPanel>(GetTree().Root).First());
            enemy.Name = "Плазменный дрон";
            enemy.BasicAttack = skill.Id;
            enemy.AiProfile = "aggressive";
            enemy.Stats[Stats.HpMax] = 60;
            enemy.Stats[Stats.MoveSpeed] = 2;
            enemy.AutoAttacks.Add(new AutoAttackDef { Skill = skill.Id, Interval = 1 });

            // 5. A scenario on the new map: units placed by clicks on the board.
            await OpenEditor("scenarios");
            await ClickButton("Создать");
            var sp = Find<ScenarioEditorPanel>(GetTree().Root).First();
            var sc = Current<ScenarioDef>(sp);
            sc.Name = "Испытание плазмы";
            sc.Map = mapId;
            sc.Limits.FogOfWar = false;
            sp.Select(sc);
            await Frames(30);
            var svp = Find<BoardViewport>(sp).First();
            var unitOpt = Find<OptionButton>(sp).First(o => Enumerable.Range(0, o.ItemCount).Any(i => o.GetItemText(i).StartsWith("Плазменный дрон")));
            await ClickButton("Поставить");
            var playerStart = map.Markers.First(m => m.Type == MarkerType.PlayerStart).C;
            var enemyStart = map.Markers.First(m => m.Type == MarkerType.EnemySpawn).C;
            unitOpt.Select(Enumerable.Range(0, unitOpt.ItemCount).First(i => unitOpt.GetItemText(i).StartsWith("Рыцарь")));
            await ClickHex(svp, playerStart);
            unitOpt.Select(Enumerable.Range(0, unitOpt.ItemCount).First(i => unitOpt.GetItemText(i).StartsWith("Плазменный дрон")));
            await ClickHex(svp, enemyStart);
            Check(sc.Units.Count == 2 && sc.Units.Any(u => u.Unit == enemy.Id) && sc.Units.Any(u => u.Unit == "knight"), $"юниты должны расставляться кликом ({sc.Units.Count})");

            // 6. Save to the library (warnings accepted).
            await ClickButton("Сохранить");
            var pub = Dialog("Опубликовать изменения");
            Check(pub != null, "окно публикации должно открыться (без критических ошибок)");
            if (Find<CheckBox>(pub!).FirstOrDefault(b => b.Text.StartsWith("Принимаю")) is { } accept) accept.ButtonPressed = true;
            Confirm(pub);
            await Frames(4);
            CloseDialogs();
            Check(cm.Published.Get<ScenarioDef>(sc.Id) != null && cm.Published.Get<UnitDef>(enemy.Id) != null, "сценарий и враг должны быть в библиотеке");

            // 7. Export the scenario package (with every dependency).
            await OpenEditor("scenarios");
            sp = Find<ScenarioEditorPanel>(GetTree().Root).First();
            sp.Select(sc.Id);
            await Frames(4);
            await ClickButton("Пакет…");
            Confirm(Dialog("Экспорт пакета"));
            await Frames(4);
            CloseDialogs();
            var pkg = ContentPackage.Read(File.ReadAllBytes(pkgPath));
            Check(pkg.Ok && new[] { sc.Id, mapId, enemy.Id, skill.Id, dtype.Id, element.Id }.All(id => pkg.Objects.Any(o => o.Id == id)), "в пакете должны быть все созданные объекты");

            // 8. Remove them from the library, then import the package back.
            var draft = cm.Draft!;
            draft.Table(DefCategory.Scenario).Remove(sc.Id);
            draft.Table(DefCategory.Map).Remove(mapId);
            draft.Table(DefCategory.Unit).Remove(enemy.Id);
            draft.Table(DefCategory.Skill).Remove(skill.Id);
            draft.Table(DefCategory.DamageType).Remove(dtype.Id);
            draft.Table(DefCategory.Element).Remove(element.Id);
            cm.DraftDirty = true;
            await ClickButton("Импорт…");
            var preview = Dialog("Импорт: предпросмотр");
            Check(preview != null && LabelVisible("Новые объекты"), "предпросмотр импорта");
            Confirm(preview);
            await Frames(5);
            Check(Find<AcceptDialog>(GetTree().Root).Any(d => d.Visible && d.Title == "Импорт: отчёт"), "отчёт об импорте");
            CloseDialogs();
            Check(new[] { sc.Id }.All(id => draft.Get<ScenarioDef>(id) != null) && draft.Get<ElementDef>(element.Id)?.Parent == "fire", "объекты должны вернуться");
            await ClickButton("Сохранить");
            pub = Dialog("Опубликовать изменения");
            if (Find<CheckBox>(pub!).FirstOrDefault(b => b.Text.StartsWith("Принимаю")) is { } accept2) accept2.ButtonPressed = true;
            Confirm(pub);
            await Frames(4);
            CloseDialogs();

            // 9. Play: the drone auto-attacks with the new damage type.
            var db = cm.Published;
            Main.Instance.ShowBattle(db.Get<ScenarioDef>(sc.Id)!, db, admin: true);
            await Frames(8);
            var bs = Battle();
            var drone = bs.Sim.Units.First(u => u.Def.Id == enemy.Id);
            var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
            Teleport(bs, drone, knight.Pos + new HexCoord(2, 0));
            foreach (var u in bs.Sim.Units) u.AiControlled = true;
            bool Done() => bs.Sim.Log.Entries.Any(e => e.Type == SimEventType.Damage && e.DamageType == dtype.Id)
                           && bs.Sim.Log.Entries.Any(e => e.SourceUnit == drone.Id && e.Text.Contains("автоатака"));
            for (int i = 0; i < 400 && !Done(); i++) bs.Sim.Step();
            Check(bs.Sim.Log.Entries.Any(e => e.Type == SimEventType.Damage && e.DamageType == dtype.Id),
                $"дрон должен наносить плазменный урон в бою: дрон {drone.Pos} ОД {drone.Ap:0.#}, рыцарь {knight.Pos}, отключено: {string.Join(",", db.DisabledSkills.Keys)}; " +
                string.Join(" | ", bs.Sim.Log.Entries.Where(e => e.SourceUnit == drone.Id || e.TargetUnit == drone.Id).Select(e => e.Type + ":" + e.Text).TakeLast(8)) +
                "; ошибки навыка: " + string.Join(" | ", ContentValidator.Validate(db.Clone()).Where(i => i.Source == "Skill:" + skill.Id).Select(i => i.Message)));
            Check(bs.Sim.Log.Entries.Any(e => e.SourceUnit == drone.Id && e.Text.Contains("автоатака")), "через автоатаку");
        }
        finally
        {
            Dialogs.AutoPath = null;
        }
    }

    private void CloseDialogs()
    {
        foreach (var d in Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible).ToList())
        {
            d.Hide();
            d.QueueFree();
        }
    }

    /// <summary>
    /// Packages (docs/DECISIONS_2.md §32): a scenario exported with all dependencies, imported back
    /// with an ID conflict resolved as a copy; a damaged package is rejected; an object without its
    /// dependencies imports only as an incomplete draft.
    /// </summary>
    private async Task EditorPackageExportImport()
    {
        var cm = Main.Instance.Content;
        string dir = ProjectSettings.GlobalizePath("user://uitest/packages");
        Directory.CreateDirectory(dir);
        string pkgPath = System.IO.Path.Combine(dir, "gate.hexpack");
        string next = pkgPath;
        Dialogs.AutoPath = _ => next;
        try
        {
            await OpenEditor("scenarios");
            var panel = Find<DefListPanel>(GetTree().Root).First();
            panel.Select("gate_defense");
            await Frames(4);
            await ClickButton("Пакет…");
            Check(LabelVisible("вместе с зависимостями"), "окно экспорта должно показать зависимости");
            Confirm(Dialog("Экспорт пакета"));
            await Frames(4);
            Check(File.Exists(pkgPath), "пакет не записан");
            Check(InfoVisible("сохранён"), "нет сообщения об экспорте");
            CloseDialogs();
            var pkg = ContentPackage.Read(File.ReadAllBytes(pkgPath));
            Check(pkg.Ok && pkg.Objects.Any(o => o.Id == "gate_defense") && pkg.Objects.Any(o => o.Category == DefCategory.Map) && pkg.Objects.Any(o => o.Id == "goblin"),
                "пакет должен содержать сценарий, карту и врагов");

            // The library changes after the export: the scenario is gone, the goblin differs.
            var draft = cm.Draft!;
            draft.Table(DefCategory.Scenario).Remove("gate_defense");
            draft.Unit("goblin")!.Name = "Гоблин (изменён)";
            cm.DraftDirty = true;
            await ClickButton("Импорт…");
            var preview = Dialog("Импорт: предпросмотр");
            Check(preview != null, "нет предпросмотра импорта");
            Check(LabelVisible("Конфликты ID") && LabelVisible("[goblin]"), "предпросмотр должен показать конфликт ID гоблина");
            var choice = Find<OptionButton>(preview!).First(o => o.ItemCount == 3 && o.GetItemText(1).StartsWith("Копия"));
            choice.Select(1);
            Confirm(preview);
            await Frames(5);
            Check(InfoVisible("копия"), "отчёт импорта должен назвать копии");
            CloseDialogs();
            var sc = draft.Get<ScenarioDef>("gate_defense");
            Check(sc != null, "сценарий должен вернуться");
            Check(draft.Unit("goblin")!.Name == "Гоблин (изменён)", "существующий объект не должен перезаписываться");
            Check(draft.Unit("goblin_copy") != null && draft.Unit("goblin_copy")!.Name != "Гоблин (изменён)", "копия должна получить новый ID и содержимое пакета");
            Check(sc!.Waves.SelectMany(w => w.Units).Any(u => u.Unit == "goblin_copy") || sc.Waves.SelectMany(w => w.Groups).Any(g => g.Unit == "goblin_copy"),
                "ссылки сценария должны перейти на копию");

            // A damaged package is rejected without changes.
            var bytes = File.ReadAllBytes(pkgPath);
            for (int i = bytes.Length / 2; i < bytes.Length / 2 + 64; i++) bytes[i] ^= 0x5A;
            next = System.IO.Path.Combine(dir, "damaged.hexpack");
            File.WriteAllBytes(next, bytes);
            int before = draft.AllDefs().Count();
            await ClickButton("Импорт…");
            Check(InfoVisible("не прошёл проверку") || InfoVisible("отклонён") || InfoVisible("Ошибка импорта"), "повреждённый пакет должен быть отклонён");
            Check(draft.AllDefs().Count() == before, "черновик не должен меняться");
            CloseDialogs();

            // A single object without its dependencies: only as an incomplete draft.
            next = System.IO.Path.Combine(dir, "lonely.json");
            File.WriteAllText(next, """
            { "formatVersion": 4, "kind": "def", "category": "Unit",
              "item": { "id": "lonely_knight", "name": "Одинокий рыцарь", "faction": "player", "basicAttack": "missing_strike" } }
            """);
            await ClickButton("Импорт…");
            preview = Dialog("Импорт: предпросмотр");
            Check(preview != null && LabelVisible("Отсутствуют зависимости"), "предпросмотр должен показать отсутствующую зависимость");
            Confirm(preview);
            await Frames(4);
            Check(draft.Unit("lonely_knight") == null, "без отметки черновика импорт не выполняется");
            Check(InfoVisible("только как черновик"), "должно быть объяснение, почему импорт не выполнен");
            foreach (var d in Find<AcceptDialog>(GetTree().Root).Where(d => d.Visible && d is not ConfirmationDialog).ToList()) d.QueueFree();
            await Frames(2);
            preview = Dialog("Импорт: предпросмотр");
            Check(preview != null, "предпросмотр должен остаться открытым");
            Find<CheckBox>(preview!).First(c => c.Text.StartsWith("Импортировать как черновик")).ButtonPressed = true;
            Confirm(preview);
            await Frames(5);
            Check(draft.Unit("lonely_knight") is { Incomplete: true }, "объект должен импортироваться с отметкой «неполный»");
            CloseDialogs();
        }
        finally
        {
            Dialogs.AutoPath = null;
        }
    }

    private bool InfoVisible(string text) =>
        Find<AcceptDialog>(GetTree().Root).Any(d => d.Visible && d.DialogText.Contains(text, StringComparison.Ordinal));

    private bool LabelVisible(string text) =>
        Find<Label>(GetTree().Root).Any(l => l.IsVisibleInTree() && l.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>Three goblins on one hex: click selects one and opens the strip, a repeated click and Tab cycle; the panel shows only known facts.</summary>
    private async Task BattleStackAndEnemyInfo()
    {
        var bs = await OpenBattle("checkpoint_hold");
        var goblins = bs.Sim.Units.Where(u => u.Def.Id == "goblin").ToList();
        Check(goblins.Count == 3 && goblins.All(g => g.Pos == goblins[0].Pos), "три гоблина должны стоять на одном гексе");
        var hex = goblins[0].Pos;
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        Teleport(bs, knight, hex + new HexCoord(-1, 0));
        bs.Board.Rig.FocusOn(Render.Board.Center(hex));
        await Frames(12);
        Check(goblins.All(g => bs.UnitVisible(g)), "гоблины должны быть видны рыцарю");
        Check(bs.Board.ViewOf(goblins[0]).StackCount == 3, "раскладка гекса должна знать о трёх существах (×3)");

        var at = HexScreen(bs, hex);
        await Click(at);
        var first = bs.Selected;
        Check(first != null && goblins.Contains(first), "клик по гексу должен выбрать гоблина");
        Check(Find<Button>(GetTree().Root).Count(b => b.IsVisibleInTree() && b.Text.Contains("Гоблин")) == 3, "полоска выбора должна показать трёх гоблинов");
        await Click(at);
        var second = bs.Selected;
        Check(second != null && second != first && goblins.Contains(second), "повторный клик должен выбрать следующего на гексе");
        await Key(Godot.Key.Tab);
        var third = bs.Selected;
        Check(third != null && third != first && third != second && goblins.Contains(third), "Tab должен листать существ на гексе");

        // Information panel: name, health and intent are seen; resistances and skills are not observed yet.
        await Frames(15);
        Check(LabelVisible("Намерение"), "панель врага должна показывать намерение");
        Check(LabelVisible("Сопротивления: ?"), "сопротивления не должны раскрываться до наблюдения");
        Check(!LabelVisible("Характеристики"), "полные характеристики врага не показываются");
        var info = bs.Sim.Knowledge.GetInfo(third!);
        Check(info.NameField.Known && info.HealthField.Known && !info.ResistancesField.Known, "раскрытие: имя и здоровье видны, сопротивления — нет");
    }

    /// <summary>A goblin walks past the knight: "Блок 1/3" on the knight, "заблокирован" on the goblin.</summary>
    private async Task BattleBlockingDisplay()
    {
        var bs = await OpenBattle("checkpoint_hold");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        var goblin = bs.Sim.Units.First(u => u.Def.Id == "goblin");
        Teleport(bs, goblin, knight.Pos + new HexCoord(1, 0));
        bs.Sim.Enqueue(goblin, Command.Move(knight.Pos + new HexCoord(-3, 0)), -1, true);
        for (int i = 0; i < 40 && bs.Sim.Blocking.BlockerOf(goblin) == null; i++) bs.Sim.Step();
        Check(bs.Sim.Blocking.BlockerOf(goblin) == knight, "рыцарь должен заблокировать гоблина");
        bs.Select(knight);
        await Frames(15);
        Check(bs.Board.ViewOf(knight).BlockText.StartsWith("Блок 1/3"), $"у рыцаря должна быть подпись «Блок 1/3», а не «{bs.Board.ViewOf(knight).BlockText}»");
        Check(bs.Board.ViewOf(goblin).BlockText == "заблокирован", "у гоблина должна быть подпись «заблокирован»");
        Check(LabelVisible("Блок 1/3 — держит: Гоблин"), "панель рыцаря должна показывать, кого он держит");
    }

    /// <summary>The priestess shields the knight: the HP bar shows the shield, a hit is absorbed by it first.</summary>
    private async Task BattleShieldAndDamage()
    {
        var bs = await OpenBattle("facility_breach");
        var knight = bs.Sim.Units.First(u => u.Def.Id == "knight");
        var priestess = bs.Sim.Units.First(u => u.Def.Id == "priestess");
        Check(priestess.KnowsSkill("aegis"), "у жрицы должен быть «Щит веры»");
        Check(bs.Sim.Enqueue(priestess, Command.Skill("aegis", null, knight.Id), -1, true) == null, "щит не встал в очередь");
        for (int i = 0; i < 200 && knight.ShieldTotal <= 0; i++) bs.Sim.Step();
        Check(knight.ShieldTotal > 0, "рыцарь не получил щит");
        bs.Select(knight);
        await Frames(15);
        Check(Find<UI.Bar>(GetTree().Root).Any(b => b.IsVisibleInTree() && b.Caption.Contains("щит")), "полоса здоровья должна показывать щит");
        double hp = knight.Hp, shield = knight.ShieldTotal;
        bs.Sim.Combat.DealDamage(null, knight, 10, "magic", null, "тест");
        Check(knight.Hp == hp && knight.ShieldTotal < shield, "урон должен сначала уйти в щит");
        var last = bs.Sim.Damage.Recent[^1].Result;
        Check(last.Shielded > 0 && last.Steps.Any(s => s.Name == "щиты"), "разбор урона должен содержать шаг «щиты»");
    }

    /// <summary>The element editor shows which fields are inherited and from whom.</summary>
    private async Task EditorElementInheritance()
    {
        await OpenEditor("elements");
        var panel = Find<DefListPanel>(GetTree().Root).First();
        panel.Select("ice");
        await Frames(6);
        Check(LabelVisible("унаследовано от «Вода»"), "редактор должен показать поле, унаследованное ледом от воды");
        Check(LabelVisible("задано здесь"), "редактор должен отличать собственные поля");
    }

    /// <summary>The archer auto-attacks a goblin in range; the panel shows it and the toggle turns it off.</summary>
    private async Task BattleAutoAttackPanel()
    {
        var bs = await OpenBattle("checkpoint_hold");
        var archer = bs.Sim.Units.First(u => u.Def.Id == "archer");
        var goblin = bs.Sim.Units.First(u => u.Def.Id == "goblin");
        Teleport(bs, goblin, archer.Pos + new HexCoord(3, 0));
        for (int i = 0; i < 60 && bs.Sim.AutoAttacks.StatesOf(archer).Sum(s => s.Fired) == 0; i++) bs.Sim.Step();
        Check(bs.Sim.AutoAttacks.StatesOf(archer).Sum(s => s.Fired) > 0, "лучник должен атаковать автоматически");
        bs.Select(archer);
        await Frames(15);
        Check(LabelVisible("Автоатака"), "панель юнита должна показывать автоатаку");
        await ClickButton("Автоатака: вкл");
        Check(bs.Sim.AutoAttacks.DisabledUnits.Contains(archer.Id), "кнопка должна выключать автоатаки юнита");
        int fired = bs.Sim.AutoAttacks.StatesOf(archer).Sum(s => s.Fired);
        for (int i = 0; i < 80; i++) bs.Sim.Step();
        Check(bs.Sim.AutoAttacks.StatesOf(archer).Sum(s => s.Fired) == fired, "выключенная автоатака не должна срабатывать");
    }

    /// <summary>Gate defence: the objectives panel lists the goals; a warning line is active before the first wave and gone after the spawn.</summary>
    private async Task BattleSpawnWarningObjectives()
    {
        var bs = await OpenBattle("gate_defense");
        await Frames(10);
        Check(LabelVisible("Удержать ядро") && LabelVisible("Отразить 3 волны"), "панель целей должна показывать цели сценария");
        var ws = bs.Sim.Scenario.Warnings;
        var w0 = bs.Sim.Scenario.Waves[0];
        Check(w0.Planned.Count == 4, "первая волна должна запланировать 4 появления");
        var first = ws.OfSpawn(w0.Planned[0].Id)!;
        Check(first.Path.Count > 5, "линия должна идти по маршруту");
        for (int i = 0; i < 400 && !ws.IsActive(first, bs.Sim.Tick); i++) bs.Sim.Step();
        Check(ws.IsActive(first, bs.Sim.Tick) && ws.VisibleToPlayer(first), "маркер должен быть виден до появления");
        await Frames(4);
        while (bs.Sim.Tick < first.VanishTick) bs.Sim.Step();
        Check(!ws.IsActive(first, bs.Sim.Tick), "маркер исчезает за секунду до появления");
        Check(!bs.Sim.Units.Any(u => u.Wave == 0), "враг ещё не появился");
        for (int i = 0; i < 40 && !bs.Sim.Units.Any(u => u.Wave == 0); i++) bs.Sim.Step();
        Check(bs.Sim.Units.Any(u => u.Wave == 0), "враг должен появиться через секунду после исчезновения линии");
        await Frames(10);
        Check(LabelVisible("пропущено 0/3"), "прогресс обороны должен быть виден");
    }

    /// <summary>Map editor: the route tool adds points to a new route by clicking hexes.</summary>
    private async Task EditorRouteTool()
    {
        await OpenEditor("maps");
        var panel = Find<MapEditorPanel>(GetTree().Root).First();
        panel.Select("arena");
        await Frames(30);
        var map = Main.Instance.Content.Draft!.Get<MapDef>("arena")!;
        int routes = map.Routes.Count;
        var tool = Find<Button>(panel).FirstOrDefault(b => b.Text == "Маршрут" && b.IsVisibleInTree());
        Check(tool != null, "нет кнопки инструмента «Маршрут»");
        await Click(tool!.GetGlobalRect().GetCenter());
        await Frames(5);
        Check(map.Routes.Count == routes + 1, $"инструмент «Маршрут» должен создать маршрут (нажата: {tool.ButtonPressed}, маршрутов {map.Routes.Count})");
        var vp = Find<BoardViewport>(GetTree().Root).First();
        foreach (var c in new[] { new HexCoord(2, 2), new HexCoord(4, 2) })
        {
            var cell = vp.Board.Map!.Get(c)!;
            var local = vp.Board.Rig.Camera.UnprojectPosition(Render.Board.Center(cell.Coord, Render.Board.PickY(cell)));
            await Click(vp.GetGlobalRect().Position + local);
        }
        var route = map.Routes[^1];
        Check(route.Points.Count == 2 && route.Points[0] == new HexCoord(2, 2) && route.Points[1] == new HexCoord(4, 2), $"точки маршрута: {string.Join(" ", route.Points)}");
        Check(Main.Instance.Content.DraftDirty, "черновик не помечен изменённым");
    }
    /// <summary>
    /// Part 4, stage 1 through the editor: a new TD scene — a spawn base and a player base placed by clicks, a hex
    /// added to the base by Shift+click, a route drawn by dragging the left button (a gap filled, the short line kept
    /// and reported, then «Продлить до цели»), a group bound to the base and the route, the object menu by the right
    /// button, a duplicate with a new ID; the battle on the draft: enemies leave the spawn base along the route and
    /// damage the base.
    /// </summary>
    private async Task EditorTdScene()
    {
        await OpenEditor("td");
        var panel = Find<TdScenePanel>(GetTree().Root).First();
        await ClickButton("Создать");
        await Frames(10);
        var sc = panel.Scene;
        Check(sc != null && sc.Name == "Новая TD-сцена", "новая TD-сцена не создана");
        sc = sc!;
        sc.Map = "td_pass";
        panel.Select(sc);
        await Frames(20);
        panel = Find<TdScenePanel>(GetTree().Root).First();
        var vp = panel.View;
        Check(vp.Board.Map != null && vp.Board.Map.Contains(new HexCoord(12, 4)), "карта «Перевал» не показана");
        HexCoord Off(int col, int row) => new(col - row / 2, row);
        await CameraSettled(vp.Board.Rig);
        var spawnAt = Off(0, 3);
        var baseAt = Off(14, 4);

        // Objects: place by the library buttons and clicks on hexes.
        await Click(Find<Button>(panel).First(b => b.Name == "TdPlace_SpawnBase").GetGlobalRect().GetCenter());
        await ClickHex(vp, spawnAt);
        await Click(Find<Button>(panel).First(b => b.Name == "TdPlace_PlayerBase").GetGlobalRect().GetCenter());
        await ClickHex(vp, baseAt);
        Check(sc.Objects.Count == 2, $"объектов {sc.Objects.Count}, ожидалось 2");
        var spawn = sc.Objects.First(o => o.Type == ScenarioObjectType.SpawnBase);
        var bas = sc.Objects.First(o => o.Type == ScenarioObjectType.PlayerBase);
        Check(spawn.Anchor == spawnAt && spawn.Spawn != null, "база спавна не на месте или без компонента «Спавн»");
        Check(bas.Anchor == baseAt && bas.Health is { Main: true }, "база игрока не на месте или не главная");
        // A second hex of the base: Shift + left click next to it (select mode).
        await Click(Find<Button>(panel).First(b => b.Name == "TdPlace_None").GetGlobalRect().GetCenter());
        await ClickHex(vp, baseAt);
        Check(panel.SelectedObject == bas.Id, "клик по базе не выбрал её");
        var extra = baseAt + new HexCoord(1, 0);
        var cell = vp.Board.Map!.Get(extra)!;
        var pos = vp.GetGlobalRect().Position + vp.Board.Rig.Camera.UnprojectPosition(Render.Board.Center(extra, Render.Board.PickY(cell)));
        Input.ParseInputEvent(new InputEventMouseMotion { Position = pos, GlobalPosition = pos });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true });
        Input.ParseInputEvent(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = false, ShiftPressed = true });
        await Frames(4);
        Check(bas.Cells().Count == 2 && bas.Occupies(extra), $"Shift+ЛКМ должен добавить гекс базы (гексов {bas.Cells().Count})");
        await Frames(4);
        Check(vp.Board.Views.Values.Any(v => v.Unit.ObjectId == bas.Id && v.Unit.Footprint!.Count == 2), "предпросмотр не показывает базу из двух гексов");

        // Routes: drag from the spawn base to the middle of the pass — the line is kept, the check says it is short.
        await Click(Find<Button>(panel).First(b => b.Name == "TdMode_Routes").GetGlobalRect().GetCenter());
        Vector2 Scr(HexCoord c) => vp.GetGlobalRect().Position + vp.Board.Rig.Camera.UnprojectPosition(Render.Board.Center(c, Render.Board.PickY(vp.Board.Map!.Get(c)!)));
        var mid = Off(4, 4);
        await CameraSettled(vp.Board.Rig);
        await Drag(Scr(spawnAt), Scr(mid), 3);
        Check(sc.Routes.Count == 1, $"протяжка от базы спавна должна создать маршрут (маршрутов {sc.Routes.Count}; {panel.StatusText})");
        var route = sc.Routes[0];
        var path = route.MainPath();
        Check(path.Count >= 4 && path[0] == spawnAt && path[^1] == mid, $"линия маршрута: {string.Join(" ", path)}");
        for (int i = 1; i < path.Count; i++) Check(HexCoord.Distance(path[i - 1], path[i]) == 1, $"разрыв между {path[i - 1]} и {path[i]}");
        Check(route.Spawn == spawn.Id && route.Target == bas.Id, "маршрут не связан с базой спавна и главной базой");
        Check(panel.Problems.Any(p => p.Object == route.Id && p.Severity == IssueSeverity.Error && p.Message.Contains("не доходит до цели")),
            "короткий маршрут должен быть отмечен «не доходит до цели», а не обрезан или перенаправлен");
        var drawn = path;
        await Click(Find<Button>(panel).First(b => b.Name == "TdExtend").GetGlobalRect().GetCenter());
        path = route.MainPath();
        Check(path.Take(drawn.Count).SequenceEqual(drawn) && bas.Cells().Any(c => HexCoord.Distance(c, path[^1]) == 1), "«Продлить до цели» не довёл маршрут до базы");
        Check(!panel.Problems.Any(p => p.Object == route.Id && p.Severity == IssueSeverity.Error), "после продления у маршрута остались ошибки: "
            + string.Join("; ", panel.Problems.Where(p => p.Object == route.Id).Select(p => p.Message)));

        // Waves: a group of the spawn base gets its route.
        await Click(Find<Button>(panel).First(b => b.Name == "TdMode_Waves").GetGlobalRect().GetCenter());
        await ClickHex(vp, spawnAt);
        await Click(Find<Button>(panel).First(b => b.Name == "TdAddGroup").GetGlobalRect().GetCenter());
        var g = sc.Waves.SelectMany(w => w.Groups).SingleOrDefault();
        Check(g != null && g.SpawnBase == spawn.Id && g.Route == route.Id && g.Unit != "", "группа не привязана к базе спавна и маршруту");
        g!.Count = 3;
        g.AiProfile = "route_raider";
        sc.Waves[0].Time = 1;

        // The object menu (right button) and a duplicate with a new ID.
        await Click(Scr(spawnAt), MouseButton.Right);
        var menu = Find<PopupMenu>(panel).First(m => m.Name == "TdObjectMenu");
        var items = Enumerable.Range(0, menu.ItemCount).Select(i => menu.GetItemText(i)).ToList();
        Check(menu.Visible && items.Contains("Добавить группу") && items.Contains("Создать маршрут") && items.Contains("Дублировать") && items.Contains("Удалить"),
            "ПКМ по базе спавна должна открыть её меню: " + string.Join(", ", items));
        menu.Hide();
        panel.MenuCommand(spawn.Id, 4);
        var copy = sc.Objects.Last();
        Check(sc.Objects.Count == 3 && copy.Id != spawn.Id && copy.Type == ScenarioObjectType.SpawnBase && copy.Spawn != null, "дубликат должен получить новый ID");
        panel.MenuCommand(copy.Id, 6);
        await Frames(3);
        Check(sc.Objects.Count == 2, "удаление дубликата без ссылок должно пройти сразу");
        panel.RunCheck(true);
        Check(!panel.Problems.Any(p => p.Severity == IssueSeverity.Error), "в готовой сцене есть ошибки: " + string.Join("; ", panel.Problems.Where(p => p.Severity == IssueSeverity.Error).Select(p => p.Message)));

        // Battle on the draft: enemies leave the spawn base along the route and damage the base.
        await Click(Find<Button>(panel).First(b => b.Name == "TdRun").GetGlobalRect().GetCenter());
        await Frames(10);
        var bs = Battle();
        var b = bs.Sim.Structures.Of(bas.Id);
        Check(b != null && b.Footprint!.Count == 2, "в бою нет базы из двух гексов");
        Check(bs.Board.Views.TryGetValue(b!.Id, out var bv) && bv.Unit.IsStructure, "база не показана на поле");
        for (int i = 0; i < 20 * 120 && b.Hp >= b.HpMax; i++) bs.Sim.Step();
        var enemies = bs.Sim.Units.Where(u => u.Group == g.Id).ToList();
        Check(enemies.Count == 3 && enemies.All(u => u.RouteId == route.Id), $"враги группы: {enemies.Count}, маршрут {string.Join(",", enemies.Select(u => u.RouteId))}");
        Check(b.Hp < b.HpMax, "дошедшие враги должны нанести урон базе");
        await Frames(10);
        Main.Instance.Content.Draft!.Table(DefCategory.Scenario).Remove(sc.Id);
    }
    /// <summary>
    /// Part 4, stage 2 through the editor: the «Волны» mode of «Оборона перевала» — the template library filtered by
    /// search, a card adds a group bound to the chosen spawn base and its route; «Сохранить как пресет» and a group
    /// from the preset; a wave by budget — «Подобрать», «Закрепить» (groups appear and stay); the schedule; in battle
    /// the waves panel shows the progress and why an appearance waits (the group's limit of the living).
    /// </summary>
    private async Task EditorTdWaves()
    {
        await OpenEditor("td");
        var panel = Find<TdScenePanel>(GetTree().Root).First();
        panel.Select("td_pass");
        await Frames(20);
        panel = Find<TdScenePanel>(GetTree().Root).First();
        var sc = panel.Scene!;
        var draft = Main.Instance.Content.Draft!;
        await CameraSettled(panel.View.Board.Rig);
        await Click(Find<Button>(panel).First(b => b.Name == "TdMode_Waves").GetGlobalRect().GetCenter());
        await ClickHex(panel.View, sc.Object("spawn_1")!.Anchor);
        Check(panel.SelectedObject == "spawn_1", "клик по базе спавна в режиме «Волны» должен выбрать её");
        await Click(Find<Button>(panel).First(b => b.Text.StartsWith("▾ Стая")).GetGlobalRect().GetCenter());
        Check(panel.SelectedWave?.Name == "Стая", "волна «Стая» не выбрана");

        // The library: a search leaves the wolf card; the card adds a group to the chosen wave and base.
        var search = Find<LineEdit>(panel).First(l => l.Name == "TdTplSearch");
        search.Text = "волк";
        search.EmitSignal(LineEdit.SignalName.TextChanged, "волк");
        await Frames(3);
        var cards = Find<Button>(panel).Where(b => b.Name.ToString().StartsWith("TdTpl_") && b.IsVisibleInTree()).ToList();
        Check(cards.Count == 1 && cards[0].Name == "TdTpl_wolf" && cards[0].Text.Contains("угроза"), "поиск «волк» должен оставить карточку волка с угрозой: " + string.Join(", ", cards.Select(c => c.Name)));
        int groupsBefore = sc.Waves[1].Groups.Count;
        await ClickInView(cards[0]);
        var g = sc.Waves[1].Groups.Last();
        Check(sc.Waves[1].Groups.Count == groupsBefore + 1 && g.Unit == "wolf" && g.SpawnBase == "spawn_1" && g.Route == "route_1", $"карточка должна добавить группу волка у пещеры по её маршруту ({g.Unit}, {g.SpawnBase}, {g.Route})");

        // A preset from the group, then a group from the preset.
        g.Count = 2;
        g.SubgroupSize = 1;
        g.SubgroupPause = 2;
        Check(panel.SelectedGroup == g, "новая группа должна быть выбрана");
        await ClickInView(Find<Button>(panel).First(b => b.Name == "TdSavePreset"));
        var preset = draft.All<GroupPresetDef>().LastOrDefault();
        Check(preset != null && preset.Template.Unit == "wolf" && preset.Template.SpawnBase == "" && preset.Template.SubgroupSize == 1, "пресет не сохранён или хранит базу сцены");
        await ClickInView(Find<Button>(panel).First(b => b.Name == "TdPreset_" + preset!.Id));
        var fromPreset = sc.Waves[1].Groups.Last();
        Check(fromPreset != g && fromPreset.Preset == preset.Id && fromPreset.Unit == "wolf" && fromPreset.SpawnBase == "spawn_1" && fromPreset.Id != g.Id, "группа из пресета не создана");

        // A wave by budget: «Подобрать» shows the pick, «Закрепить» makes it groups.
        var w = sc.Waves[2];
        w.Budget = new WaveBudgetDef { Enabled = true, Budget = 12, MinUnits = 2, MaxUnits = 6, SpawnBase = "spawn_1", Route = "route_1", Interval = 1 };
        await ClickInView(Find<Button>(panel).First(b => b.Text.StartsWith("▾ Таран")));
        await ClickInView(Find<Button>(panel).First(b => b.Name == "TdBudgetPick"));
        var preview = Find<Label>(panel).First(l => l.Name == "TdBudgetPreview");
        Check(preview.Text.Contains(" из 12"), "«Подобрать» должен показать состав и затраты: " + preview.Text);
        await ClickInView(Find<Button>(panel).First(b => b.Name == "TdBudgetPin"));
        var picked = w.Groups.Where(x => x.FromBudget).ToList();
        Check(w.Budget.Pinned && picked.Count > 0 && picked.All(x => x.SpawnBase == "spawn_1"), "«Закрепить» должен записать подобранные группы");
        string pinnedText = string.Join(",", picked.Select(x => x.ToString()));
        Check(LabelVisible("Таран:"), "расписание должно показывать волну «Таран»");

        // Battle on the draft: a limit of the living holds the scouts back; the panel says why.
        sc.Waves[0].Groups[0].MaxAlive = 1;
        await Click(Find<Button>(panel).First(b => b.Name == "TdRun").GetGlobalRect().GetCenter());
        await Frames(10);
        var bs = Battle();
        var w0 = bs.Sim.Scenario.Waves[0];
        for (int i = 0; i < 20 * 30 && !w0.Planned.Any(p => p.WaitReason.Contains("лимит живых группы")); i++) bs.Sim.Step();
        await Frames(10);
        Check(LabelVisible("Волна «Разведка»") && LabelVisible("ждёт: лимит живых группы"), "панель волн должна показать прогресс и причину ожидания");
        var w2 = bs.Sim.Scenario.Waves[2];
        Check(string.Join(",", w2.Def.Groups.Where(x => x.FromBudget).Select(x => x.ToString())) == pinnedText && w2.Budget == null, "в бою закреплённый состав не должен подбираться заново");
        // Restore the shipped scene in the draft for the following tests.
        draft.Table(DefCategory.Scenario).Remove("td_pass");
        draft.Add(ContentJson.Clone(Main.Instance.Content.Published.Get<ScenarioDef>("td_pass")!));
        draft.Table(DefCategory.GroupPreset).Remove(preset.Id);
        ContentValidator.Validate(draft);
    }
    /// <summary>
    /// Part 4, stage 3 through the editor: on «Перевал» a detour drawn by a bending drag from a hex of the route to a
    /// further hex of it makes a fork and a merge (the crossing of nothing else is created); a click on the fork
    /// selects it with its rule and exits; with «по очереди» the enemies of the first wave take both exits in battle.
    /// </summary>
    private async Task EditorTdRouteGraph()
    {
        await OpenEditor("td");
        var panel = Find<TdScenePanel>(GetTree().Root).First();
        panel.Select("td_pass");
        await Frames(20);
        panel = Find<TdScenePanel>(GetTree().Root).First();
        var sc = panel.Scene!;
        var vp = panel.View;
        await CameraSettled(vp.Board.Rig);
        await Click(Find<Button>(panel).First(b => b.Name == "TdMode_Routes").GetGlobalRect().GetCenter());
        await ClickInView(Find<Button>(panel).First(b => b.Name == "TdRoute_route_1"));
        Check(panel.SelectedRoute == "route_1", "маршрут не выбран");
        var r = sc.Route("route_1")!;
        HexCoord Off(int col, int row) => new(col - row / 2, row);
        Vector2 Scr(HexCoord c) => vp.GetGlobalRect().Position + vp.Board.Rig.Camera.UnprojectPosition(Render.Board.Center(c, Render.Board.PickY(vp.Board.Map!.Get(c)!)));
        var forkAt = Off(4, 5);
        var mergeAt = Off(7, 6);
        Check(r.MainPath().Contains(forkAt) && r.MainPath().Contains(mergeAt), "ожидаемые гексы не на маршруте");
        int nodes = r.Nodes.Count;
        await DragPath(new[] { Scr(forkAt), Scr(Off(4, 6)), Scr(Off(5, 7)), Scr(Off(7, 7)), Scr(mergeAt) });
        var fork = RouteGraph.NodeAt(r, forkAt);
        var merge = RouteGraph.NodeAt(r, mergeAt);
        Check(fork != null && fork.Kind == RouteNodeKind.Fork && merge != null && merge.Kind == RouteNodeKind.Merge,
            $"протяжка обхода должна создать развилку и слияние ({panel.StatusText}; узлы {string.Join(" ", r.Nodes.Select(n => $"{n.Id}{n.C}:{n.Kind}"))})");
        var exits = r.Out(fork!.Id).ToList();
        Check(exits.Count == 2 && exits.Any(e => e.Cells.Contains(Off(5, 7))), "у развилки должно быть два выхода, один — через обход");
        Check(r.MainPath().SequenceEqual(sc.Route("route_1")!.MainPath()) && r.MainPath()[^1] == Off(13, 3), "основная линия не должна измениться");
        Check(!panel.Problems.Any(p => p.Object == r.Id && p.Severity == IssueSeverity.Error), "граф с обходом должен пройти проверку: " + string.Join("; ", panel.Problems.Where(p => p.Object == r.Id).Select(p => p.Message)));
        // A click on the fork selects it: its rule and both exits are shown.
        await ClickHex(vp, forkAt);
        Check(panel.SelectedNode == fork.Id, $"клик по развилке должен выбрать узел ({panel.SelectedNode})");
        Check(Find<UI.PropertyGrid>(panel).Any(g => g.Name == "TdNodeGrid") && exits.All(e => Find<UI.PropertyGrid>(panel).Any(g => g.Name == "TdEdgeGrid_" + e.Id)), "свойства узла и выходов не показаны");
        fork.Rule = RouteForkRule.RoundRobin;

        // Battle: the scouts split over both exits.
        await Click(Find<Button>(panel).First(b => b.Name == "TdRun").GetGlobalRect().GetCenter());
        await Frames(10);
        var bs = Battle();
        var scouts = new List<Unit>();
        for (int i = 0; i < 20 * 40; i++)
        {
            bs.Sim.Step();
            scouts = bs.Sim.Units.Where(u => u.Group == "scouts").ToList();
            if (scouts.Count == 4 && scouts.All(u => !u.Alive || u.Nav!.Taken.Any(id => exits.Any(e => e.Id == id)))) break;
        }
        await Frames(5);
        foreach (var e in exits)
            Check(scouts.Any(u => u.Nav!.Taken.Contains(e.Id)), $"по выходу {e.Id} никто не пошёл: " + string.Join(" | ", scouts.Select(u => string.Join(">", u.Nav!.Taken))));
        var draft = Main.Instance.Content.Draft!;
        draft.Table(DefCategory.Scenario).Remove("td_pass");
        draft.Add(ContentJson.Clone(Main.Instance.Content.Published.Get<ScenarioDef>("td_pass")!));
        ContentValidator.Validate(draft);
        Check(nodes == 2, "в исходном маршруте два узла");
    }
    /// <summary>
    /// Part 4, stage 4 through the UI: «Категории существ» shows the tree path of «Волк-вожак»; the unit editor lists
    /// the rules of the categories with their sources and states («Слизь-мимик» inherits the forbidden standard attack,
    /// «Кислотная слизь» excludes it); in «Ночная охота стаи» the leader's aura reaches the pack, «Сила стаи» counts the
    /// wolves, the night speeds the wolves up and the panel shows the time of day until dawn.
    /// </summary>
    private async Task EditorCategoryTree()
    {
        await OpenEditor("origins");
        var origins = Find<OriginPanel>(GetTree().Root).First();
        origins.Select("wolf_leader_cat");
        await Frames(6);
        Check(LabelVisible("Дерево: Сущность → Живые → Звери → Волк → Волк-вожак"), "путь в дереве категорий не показан");
        Check(Find<Button>(origins).Any(b => b.IsVisibleInTree() && b.Text == "Волк-вожак (основная)"), "шаблон вожака не показан в своей категории");

        await OpenEditor("enemies");
        var units = Find<UnitEditorPanel>(GetTree().Root).First();
        units.Select("slime_mimic");
        await Frames(8);
        string Rules() => string.Join("\n", Find<Label>(units).Where(l => l.Name.ToString().Contains("CatRule") && l.IsVisibleInTree()).Select(l => l.Text));
        Check(Rules().Contains("✓ запрет: стандартная атака — «Слизи»"), "у мимика должен действовать унаследованный запрет:\n" + Rules());
        units.Select("slime");
        await Frames(8);
        Check(Rules().Contains("✗ запрет: стандартная атака — «Слизи» (исключено в «Кислотная слизь»)"), "у кислотной слизи запрет должен быть исключён:\n" + Rules());
        units.Select("wolf_leader");
        await Frames(8);
        Check(Rules().Contains("✓ пассив «Вой вожака» — «Волк-вожак»") && Rules().Contains("«Ночной охотник» — «Волк» (условно"), "правила вожака:\n" + Rules());
        Check(LabelVisible("Теги от категорий: creature, living, beast, pack"), "теги категорий не показаны");

        var bs = await OpenBattle("pack_hunt");
        await Frames(10);
        var sim = bs.Sim;
        var leader = sim.Units.First(u => u.Def.Id == "wolf_leader");
        var wolf = sim.Units.First(u => u.Def.Id == "wolf");
        var mimic = sim.Units.First(u => u.Def.Id == "slime_mimic");
        sim.RunSeconds(0.6);
        Check(wolf.PassiveInstances.Any(p => p.Def.Id == "leader_howl" && p.AuraOwner == leader.Id), "аура вожака не дошла до волка");
        var strength = leader.PassiveInstances.First(p => p.Def.Id == "pack_strength");
        Check(strength.Scaled.Values.Single().Value >= 10, $"«Сила стаи» должна считать волков рядом (+{strength.Scaled.Values.Single().Value} %)");
        Check(mimic.BasicAttack == null && mimic.Skills.Any(s => s.Id == "ambush_lash"), "мимик: без стандартной атаки, с ударом из засады");
        double nightSpeed = wolf.Stat(Stats.MoveSpeed);
        await Frames(5);
        Check(LabelVisible("Время суток: ночь"), "панель должна показать время суток");
        sim.Scenario.SetVariable("time_of_day", "day");
        sim.RunSeconds(0.6);
        await Frames(5);
        Check(wolf.Stat(Stats.MoveSpeed) < nightSpeed - 1e-6 && LabelVisible("Время суток: день"), $"днём волки медленнее ({nightSpeed} → {wolf.Stat(Stats.MoveSpeed)})");
    }
}
