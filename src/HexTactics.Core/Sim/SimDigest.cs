using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HexTactics.Core.Sim;

/// <summary>
/// The fingerprint of a battle by parts (ТЗ ч. 5, T26; docs/DECISIONS_5.md §86): time, the three random streams, units,
/// their queues, statuses and cooldowns, cells, the scenario. Two states with equal parts are the same state for the
/// simulation; <see cref="Diff"/> names the parts that differ («что не вернулось к снимку»).
/// </summary>
public sealed class SimDigest
{
    public long Tick { get; init; }
    public Dictionary<string, string> Parts { get; init; } = new();

    public static readonly string[] Names = { "время", "ГСЧ", "юниты", "очереди", "состояния", "перезарядки", "клетки", "сценарий" };

    /// <summary>One line: the tick and every part.</summary>
    public string Hash => Tick + ":" + string.Join(":", Names.Select(n => Parts.GetValueOrDefault(n, "")));

    public List<string> Diff(SimDigest other) => Names.Where(n => Parts.GetValueOrDefault(n) != other.Parts.GetValueOrDefault(n)).ToList();

    public override string ToString() => Hash;

    public static SimDigest Of(Simulation sim)
    {
        var parts = new Dictionary<string, string>();
        string H(IEnumerable<string> items) => Simulation.StableHash(string.Join("|", items)).ToString("X", CultureInfo.InvariantCulture);
        string N(double v) => Math.Round(v, 4).ToString("R", CultureInfo.InvariantCulture);
        var units = sim.Units.OrderBy(u => u.Id).ToList();

        parts["время"] = H(new[] { sim.Tick.ToString(CultureInfo.InvariantCulture) });
        parts["ГСЧ"] = H(new[] { sim.Rng, sim.AiRng, sim.SpawnRng }.Distinct().Select(r => $"{r.Draws}/{r.StateMix:X}"));
        parts["юниты"] = H(units.Select(u =>
            $"{u.Id},{u.Def.Id},{u.FactionId},{u.Pos},{u.Layer},{(int)u.Facing},{N(u.Hp)},{N(u.Ap)},{u.Alive},{u.StepTo},{u.StepTicksDone},{u.BusyUntil}," +
            $"{u.Casting?.Skill.Id},{u.Stance != null},{N(u.ShieldTotal)},{u.InstanceMods.Count},{u.RouteId},{u.RouteIndex},{u.TargetObject}," +
            string.Join(";", u.Resources.OrderBy(r => r.Key).Select(r => $"{r.Key}={N(r.Value.Current)}"))));
        parts["очереди"] = H(units.Select(u => u.Id + ":" + string.Join(";", u.Queue.Items.Select(c =>
            $"{c.Type},{c.Status},{c.TargetCell},{c.TargetUnitId},{c.SkillId},{c.Direction},{N(c.Duration)}"))));
        parts["состояния"] = H(units.Select(u => u.Id + ":" + string.Join(";", u.Statuses.Select(s =>
            $"{s.Def.Id},{N(s.Points)},{s.Stacks},{s.RemainingTicks},{s.LevelIndex},{s.TickTimer}"))));
        parts["перезарядки"] = H(units.Select(u => u.Id + ":" + string.Join(";", u.CooldownUntil.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")
            .Concat(u.PassiveCooldownUntil.OrderBy(k => k.Key).Select(k => $"p{k.Key}={k.Value}")))));
        parts["клетки"] = H(sim.Map.Cells.Select(c => $"{c.Index},{c.Terrain.Id}," + string.Join(";", c.Effects.Select(e => $"{e.Def.Id},{e.Level},{e.RemainingTicks}"))));
        var sc = sim.Scenario;
        parts["сценарий"] = H(sc.Waves.Select(w => $"w{w.Index},{w.State},{w.StartedTick},{w.Planned.Count(p => p.Done)},{w.UnitIds.Count}")
            .Concat(sc.Objectives.Select(o => $"o{o.Def.Id},{o.State}"))
            .Concat(sc.Flags.OrderBy(f => f, StringComparer.Ordinal).Select(f => "f" + f))
            .Concat(sc.Variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"v{v.Key}={v.Value}"))
            // Part 6 (§96): the squad — only when the scenario has one, so the fingerprints of older battles stay the same.
            .Concat(sim.Squad.Enabled ? sim.Squad.Entries.Select(e => $"s{e.Index},{e.Template},{e.State},{e.Unit},{e.ReadyAt},{e.Deployments}") : Enumerable.Empty<string>())
            .Concat(sim.Deployment.Enabled ? new[] { "dp=" + N(sim.Deployment.Points) } : Enumerable.Empty<string>())
            // Part 6 (T28): which triggers fired and which boss phases were entered — a loaded battle repeats neither.
            .Concat(sc.TriggerFires.Select(t => $"t{t.Trigger}={t.Fires}"))
            .Concat(sc.BossPhasesEntered.Select(b => "b" + b))
            .Concat(sc.BossCurrentPhases.Select(b => "B" + b))
            .Concat(sc.SwitchedObjects.Select(x => "a" + x))
            .Concat(sim.Interactive.Digest().Select(x => "x" + x))
            // Part 6 (§104): scales and panic — only when there are some, so older fingerprints stay the same.
            .Concat(sim.Scales.Digest().Select(x => "z" + x))
            .Concat(sim.Commands.PanicDigest().Select(x => "p" + x))
            // Part 6 (§106): summons — only when there are some.
            .Concat(sim.Summons.Digest().Select(x => "m" + x))
            .Concat(sc.DelayedTriggers.Select(x => "d" + x))
            .Append($"outcome={sim.Outcome}"));
        return new SimDigest { Tick = sim.Tick, Parts = parts };
    }
}
