using System;
using System.Collections.Generic;
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
        _ = Run();
    }

    private async Task Run()
    {
        await Frames(3);
        int ran = 0;
        foreach (var (name, body) in _tests)
        {
            if (_which != "all" && _which != name) continue;
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
        await Key(Godot.Key.Escape); // close the action menu if open
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

    // ------------------------------------------------------------------ editor tests

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
}
