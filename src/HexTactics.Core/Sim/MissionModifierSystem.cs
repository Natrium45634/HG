using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>
/// The changes of units of the applied modifiers of a mission (ТЗ ч. 6, 5.3–5.5; docs/DECISIONS_6.md §99): a stat — an
/// instance modifier of the common engine (the order of the engine: base → scenario → the sets in order → the effects of the
/// battle), a status — the ordinary status. From «С секунды» to «До секунды» of the battle; a unit that appears later
/// (a wave) gets them when it appears. The share of health is kept when the maximum changes.
/// </summary>
public sealed class MissionModifierSystem
{
    private readonly Simulation _sim;
    private readonly List<MissionModifierDef> _mods = new();
    private readonly HashSet<(int Mod, int Unit)> _applied = new();
    private readonly HashSet<int> _ended = new();

    public MissionModifierSystem(Simulation sim) => _sim = sim;

    /// <summary>The changes of units in force in this battle.</summary>
    public IReadOnlyList<MissionModifierDef> Modifiers => _mods;

    /// <summary>The variant of the battle ("" — none).</summary>
    public string Variant { get; private set; } = "";

    internal void Load(ScenarioDef sc)
    {
        Variant = sc.AppliedVariant;
        _mods.AddRange(sc.AppliedModifiers);
        if (_mods.Count > 0) _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Модификаторы миссии: {_mods.Count}" + (Variant != "" ? $" (вариант «{Variant}»)" : ""));
        Update();
    }

    private static string Source(MissionModifierDef m, int i) => "модификатор миссии: " + (m.Title != "" ? m.Title : "#" + i);

    private bool InWindow(MissionModifierDef m)
    {
        double t = _sim.Scenario.BattleTime;
        return t >= m.ActiveFrom - 1e-9 && (m.ActiveUntil <= 0 || t < m.ActiveUntil - 1e-9);
    }

    /// <summary>The tick: start what is due, end what has run out, reach the units that appeared.</summary>
    internal void Update()
    {
        for (int i = 0; i < _mods.Count; i++)
        {
            var m = _mods[i];
            if (_ended.Contains(i)) continue;
            if (m.ActiveUntil > 0 && _sim.Scenario.BattleTime >= m.ActiveUntil - 1e-9)
            {
                End(i, m);
                continue;
            }
            if (!InWindow(m)) continue;
            foreach (var u in _sim.Units)
            {
                if (!u.Alive || u.IsStructure || _applied.Contains((i, u.Id))) continue;
                if (!MissionModifiers.Matches(_sim.Content, m.Selector, u.Def, u.FactionId, u.Wave, u.Group, u.SpawnBase)) continue;
                _applied.Add((i, u.Id));
                Apply(u, m, i);
            }
        }
    }

    private void Apply(Unit u, MissionModifierDef m, int i)
    {
        switch (m.Op)
        {
            case MissionModOp.Stat when m.Stat.Stat != "":
                KeepHealth(u, () => u.InstanceMods.Add((ContentJson.Clone(m.Stat), Source(m, i))));
                break;
            case MissionModOp.Status when _sim.Content.Status(m.Status) is { } sd:
                _sim.Statuses.Apply(u, sd, m.StatusPoints, 0, -1, force: true);
                break;
            case MissionModOp.Scale:
                _sim.Scales.Change(u, m.Delta, $"модификатор миссии «{m.Title}»", null);
                break;
        }
    }

    private void End(int i, MissionModifierDef m)
    {
        _ended.Add(i);
        string src = Source(m, i);
        foreach (var (mi, uid) in _applied.Where(x => x.Mod == i).ToList())
        {
            _applied.Remove((mi, uid));
            if (_sim.GetUnit(uid) is not { Alive: true } u) continue;
            if (m.Op == MissionModOp.Stat) KeepHealth(u, () => u.InstanceMods.RemoveAll(x => x.Source == src));
            else if (m.Op == MissionModOp.Status && u.Statuses.FirstOrDefault(s => s.Def.Id == m.Status) is { } inst)
                _sim.Statuses.Remove(u, inst, "модификатор миссии закончился");
        }
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Модификатор миссии «{m.Title}» закончился");
    }

    private void KeepHealth(Unit u, Action change)
    {
        double share = u.HpMax > 0 ? u.Hp / u.HpMax : 1;
        change();
        _sim.Statuses.RecalculateModifiers(u);
        u.Hp = Math.Min(u.HpMax, Math.Max(u.Hp > 0 ? 0.01 : 0, share * u.HpMax));
    }
}
