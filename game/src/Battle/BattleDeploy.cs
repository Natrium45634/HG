using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Editor;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// Part 6, stage 6.2 on the battle screen (ТЗ ч. 6, 3.6–3.8, 11.4–11.5; docs/DECISIONS_6.md §97): calling a fighter of the
/// squad — a card of the ribbon → a hex of the zone (the allowed ones outlined, the forbidden hatched with the reason) →
/// the facing (the cursor's sector or 1–6) → a click or Enter. Esc / ПКМ go one step back without any cost. The drag of
/// a route never places anyone: the call is a mode of its own. Retreat with a confirmation, save and load of the battle.
/// </summary>
public partial class BattleScreen
{
    /// <summary>The entry of the squad being called (−1 — none).</summary>
    public int DeployEntry { get; private set; } = -1;
    /// <summary>The hex chosen for it, waiting for the facing and the confirmation (null — the hex is not chosen yet).</summary>
    public HexCoord? DeployCell { get; private set; }
    public HexDirection DeployDirection { get; private set; } = HexDirection.E;
    public bool Deploying => DeployEntry >= 0;

    /// <summary>The text of the active tool (always visible while calling, 11.5).</summary>
    public string DeployHint
    {
        get
        {
            if (!Deploying) return "";
            var e = Sim.Squad.Entries[DeployEntry];
            string name = _db.Unit(e.Template)?.DisplayName ?? e.Template;
            if (DeployCell == null)
            {
                string? why = _hover != null ? Sim.Deployment.CellProblem(e, _hover.Coord) : null;
                return $"Вызов «{name}» (цена {Sim.Deployment.CostOf(e):0.#}): ЛКМ — гекс зоны" +
                       (why != null && _hover != null ? $" · здесь нельзя: {why}" : "") + " · Esc/ПКМ — отмена без затрат";
            }
            var def = _db.Unit(e.Template);
            string facing = def?.Deployment?.Facing switch
            {
                DeployFacing.Auto => "направление — к врагу (профиль бойца)",
                DeployFacing.Fixed => "направление задано профилем бойца",
                _ => "направление — курсором (или 1–6)",
            };
            return $"«{name}» в {DeployCell}: {facing}, ЛКМ/Enter — вызвать · Esc/ПКМ — другой гекс";
        }
    }

    /// <summary>A card of the ribbon: a ready fighter enters the mode of calling (again — leaves it); one on the field is selected.</summary>
    public void ChooseDeploy(int entry)
    {
        if (entry < 0 || entry >= Sim.Squad.Entries.Count) return;
        var e = Sim.Squad.Entries[entry];
        if (DeployEntry == entry)
        {
            CancelDeploy();
            return;
        }
        if (e.State == RosterState.Deployed && Sim.GetUnit(e.Unit) is { Alive: true } onField)
        {
            CancelDeploy();
            Select(onField);
            return;
        }
        if (Sim.Deployment.CanDeploy(e) is { } why)
        {
            _hud.ShowBanner("Вызвать нельзя: " + why, Ui.Bad, 2.5f);
            return;
        }
        CancelGesture();
        ExitMode();
        PlaceTemplate = null;
        Relocating = null;
        DeployEntry = entry;
        DeployCell = null;
        PlanningPause("выбор места вызова", s => s.PauseOnTargeting);
    }

    public void CancelDeploy()
    {
        DeployEntry = -1;
        DeployCell = null;
    }

    /// <summary>Esc / ПКМ while calling: from the facing back to the hex, from the hex out of the mode. True — handled.</summary>
    private bool DeployStepBack()
    {
        if (!Deploying) return false;
        if (DeployCell != null) DeployCell = null;
        else CancelDeploy();
        return true;
    }

    /// <summary>ЛКМ while calling: choose the hex, then confirm.</summary>
    private void DeployClick()
    {
        if (DeployCell != null)
        {
            ConfirmDeploy();
            return;
        }
        if (_hover == null) return;
        PickDeployCell(_hover.Coord);
    }

    /// <summary>Chooses the hex of the call (the checks of the simulation); true — chosen, the facing is next.</summary>
    public bool PickDeployCell(HexCoord c)
    {
        if (!Deploying) return false;
        var e = Sim.Squad.Entries[DeployEntry];
        var why = Sim.Deployment.CanDeploy(e) ?? Sim.Deployment.CellProblem(e, c);
        if (why != null)
        {
            _hud.ShowBanner("Сюда вызвать нельзя: " + why, Ui.Bad, 2.5f);
            return false;
        }
        DeployCell = c;
        DeployDirection = Sim.Placement.FacingFrom(c);
        return true;
    }

    public void SetDeployFacing(HexDirection d) => DeployDirection = d;

    /// <summary>Confirms the call. The simulation checks everything again; a refusal spends nothing and says why (3.7).</summary>
    public Unit? ConfirmDeploy()
    {
        if (!Deploying || DeployCell is not { } c) return null;
        int entry = DeployEntry;
        var u = Sim.Input.Deploy(entry, c, DeployDirection, out var why);
        if (u == null)
        {
            _hud.ShowBanner("Вызов не состоялся: " + why + " — очки не списаны", Ui.Bad, 3f);
            DeployCell = null;
            if (Sim.Deployment.CanDeploy(Sim.Squad.Entries[entry]) != null) CancelDeploy();
            return null;
        }
        CancelDeploy();
        Select(u, byPlayer: false);
        _hud.ShowBanner($"Вызван «{u.Name}» (−{Sim.Squad.Entries[entry].Cost:0.#}, осталось {Sim.Deployment.Points:0.#})", Ui.Good, 2.5f);
        return u;
    }

    /// <summary>The facing follows the cursor: the sector of the hex under it around the chosen hex.</summary>
    private void UpdateDeployFacing()
    {
        if (DeployCell is not { } c || _hover == null || _hover.Coord == c) return;
        if (c.DirectionTo(_hover.Coord) is { } d) DeployDirection = d;
    }

    /// <summary>Keys while calling: 1–6 — the facing, Enter — confirm. True — handled.</summary>
    private bool DeployKey(InputEventKey k)
    {
        if (!Deploying) return false;
        if (k.Keycode is Key.Enter or Key.KpEnter)
        {
            if (DeployCell != null) ConfirmDeploy();
            return true;
        }
        if (DeployCell != null && k.Keycode >= Key.Key1 && k.Keycode <= Key.Key6 && !k.CtrlPressed && !k.AltPressed)
        {
            DeployDirection = (HexDirection)(int)(k.Keycode - Key.Key1);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The preview of the call (11.4): the hexes of the zone with a dashed outline and a dot — never filled like an area of
    /// an attack; the forbidden ones hatched; the hex under the cursor ringed; the chosen hex with the arrow of the facing.
    /// </summary>
    private void DrawDeploy(OverlayLayer o, float pulse)
    {
        if (!Deploying) return;
        var e = Sim.Squad.Entries[DeployEntry];
        var ok = new Color(0.35f, 0.95f, 0.85f, 0.85f);
        var bad = new Color(1f, 0.45f, 0.35f, 0.55f);
        foreach (var c in DeployCells())
        {
            if (Sim.Deployment.CellProblem(e, c) == null)
            {
                o.DashedRing(c, ok, 0.06f, 0.86f);
                o.Dot(c, new Color(ok, 0.6f), 0.07f);
            }
            else o.HatchHex(c, bad, stripes: 4);
        }
        if (DeployCell is { } chosen)
        {
            o.RingHex(chosen, new Color(0.4f, 1f, 0.9f, 0.75f + 0.25f * pulse), 0.1f, 0.95f, 0.05f, true);
            // The facing the fighter will get: the chosen one, or the one of its profile (auto / fixed).
            var def = _db.Unit(e.Template);
            var facing = def != null ? Sim.Deployment.FacingFor(def, chosen, DeployDirection) : DeployDirection;
            if (def?.Deployment?.Facing is null or DeployFacing.Manual)
                for (int i = 0; i < 6; i++)
                    if ((HexDirection)i != facing) o.Arrow(chosen, (HexDirection)i, new Color(0.6f, 0.8f, 0.9f, 0.25f), 0.45f);
            o.Arrow(chosen, facing, new Color(0.45f, 1f, 0.9f, 0.95f), 0.85f);
            return;
        }
        if (_hover == null) return;
        bool can = Sim.Deployment.CellProblem(e, _hover.Coord) == null;
        o.RingHex(_hover.Coord, can ? new Color(0.4f, 1f, 0.9f, 0.7f + 0.3f * pulse) : new Color(1f, 0.35f, 0.3f, 0.85f), 0.1f, 0.95f, 0.05f, true);
    }

    /// <summary>The hexes a call may go to: the zones of deployment of the scenario, or the zone of placement.</summary>
    private IEnumerable<HexCoord> DeployCells()
    {
        var zones = _scenario.DeploymentZones;
        if (zones.Count == 0) return Sim.Placement.Zone;
        return zones.Where(z => Sim.IsPlayerFaction(z.Faction)).SelectMany(z => z.Cells).Distinct();
    }

    /// <summary>«Отозвать» the selected fighter: with a confirmation when the rules ask for it (3.8).</summary>
    public void RetreatSelected(bool confirmed = false)
    {
        if (Selected is not { } u) return;
        if (Sim.Deployment.CanRetreat(u) is { } why)
        {
            _hud.ShowBanner("Отозвать нельзя: " + why, Ui.Bad, 2.5f);
            return;
        }
        if (Sim.Deployment.Rules.ConfirmRetreat && !confirmed)
        {
            Dialogs.Confirm(_hud, "Отозвать бойца", $"Отозвать «{u.Name}»? {Sim.Deployment.PointsName}: вернётся {Sim.Deployment.RefundOf(u):0.#}; " +
                $"повторный вызов — через {Sim.Deployment.CooldownOf(u.Def):0.#} с.", () => RetreatSelected(true), "Отозвать");
            return;
        }
        double refund = Sim.Deployment.RefundOf(u);
        if (!Sim.Input.Retreat(u, out why))
        {
            _hud.ShowBanner("Отозвать нельзя: " + why, Ui.Bad, 2.5f);
            return;
        }
        Select(null, byPlayer: false);
        _hud.ShowBanner($"«{u.Name}» отозван (+{refund:0.#})", Ui.Text, 2.5f);
    }

    // ------------------------------------------------------------------ interactive objects (part 6, §100)

    /// <summary>The selected unit uses an object (ПКМ by the object's hex or the button of the unit panel).</summary>
    public bool InteractWith(string objectId)
    {
        if (Selected is not { } u) return false;
        if (!Sim.Input.Interact(u, objectId, out var why))
        {
            _hud.ShowBanner("Нельзя: " + why, Ui.Bad, 2.5f);
            return false;
        }
        var o = _scenario.Object(objectId);
        _hud.ShowBanner($"{u.Name}: «{o?.Interaction?.Prompt}» — {o?.DisplayName}", Ui.Text, 2f);
        return true;
    }

    /// <summary>ПКМ by a hex of an object with an interaction, the selected unit of the player within reach: uses it. True — handled.</summary>
    private bool TryInteractClick()
    {
        if (_hover == null || Selected is not { } u || !Sim.CanPlayerCommand(u)) return false;
        var o = _scenario.Objects.FirstOrDefault(x => x.Interaction != null && x.Occupies(_hover.Coord));
        if (o == null || Sim.Interactive.CanInteract(u, o.Id) != null) return false;
        return InteractWith(o.Id);
    }

    /// <summary>The state of the objects with states over them, and «⚙» over those the selected unit can use.</summary>
    private void DrawObjectLabels()
    {
        var u = Selected;
        foreach (var o in _scenario.Objects)
        {
            if (!Sim.Map.Contains(o.Anchor)) continue;
            string text = "";
            if (o.States != null && Sim.Interactive.CurrentState(o.Id) is { } st)
                text = $"{o.DisplayName}: {st.DisplayName}" + (Sim.Interactive.PendingOf(o.Id) is { Length: > 0 } p ? $" → {o.States.Get(p)?.DisplayName ?? p} (ждёт)" : "");
            if (u != null && o.Interaction != null && Sim.CanPlayerCommand(u) && Sim.Interactive.CanInteract(u, o.Id) == null)
                text = (text == "" ? o.DisplayName : text) + $"\n⚙ {o.Interaction.Prompt} (ПКМ)";
            if (text == "") continue;
            if (Sim.FogOfWar && !_admin && !Sim.Vision.IsCellVisibleToPlayer(o.Anchor)) continue;
            _labels.Add(LabelPos(o.Anchor, 1.1f), text, new Color(0.8f, 0.95f, 1f), 20);
        }
    }

    // ------------------------------------------------------------------ the battle saved in the middle (T28)

    public static string SavesDir => ProjectSettings.GlobalizePath("user://saves");

    /// <summary>Saves the battle as it is now: the record so far with the content — loading plays it back to this tick.</summary>
    public string SaveBattle()
    {
        var save = Replay!.SaveAt(_snapshot!, Sim);
        Directory.CreateDirectory(SavesDir);
        var path = Path.Combine(SavesDir, $"{_snapshot!.Scenario}_{DateTime.Now:yyyyMMdd_HHmmss}.hexsave");
        File.WriteAllText(path, save.ToJson());
        return path;
    }

    /// <summary>The saved battles, the newest first.</summary>
    public static List<string> SavedBattles() =>
        Directory.Exists(SavesDir) ? Directory.GetFiles(SavesDir, "*.hexsave").OrderByDescending(File.GetLastWriteTimeUtc).ToList() : new List<string>();

    /// <summary>A battle loaded from a save: played back to its tick, it goes on paused.</summary>
    public static BattleScreen Load(string path, bool admin, Action? onExit)
    {
        var save = BattleReplay.FromJson(File.ReadAllText(path)) ?? throw new InvalidDataException("файл не читается как сохранение боя");
        var content = save.SavedContent() ?? throw new InvalidDataException("в сохранении нет контента");
        var snap = BattleSnapshot.FromReplay(save, content);
        return new BattleScreen(snap, admin, onExit) { Loaded = save };
    }

    /// <summary>The save this battle was loaded from (null — a new battle).</summary>
    public BattleReplay? Loaded { get; private init; }
    /// <summary>How the load went (empty — not loaded).</summary>
    public string LoadText { get; private set; } = "";
}
