using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>The scale of one unit in a battle.</summary>
public sealed class ScaleState
{
    public ScaleProfileDef Profile { get; init; } = null!;
    public ScaleUseMode Mode { get; init; }
    public double Value { get; set; }
    /// <summary>The bands whose status is on (indices of the profile).</summary>
    public SortedSet<int> Active { get; } = new();
    public Dictionary<int, long> BandCooldown { get; } = new();
    public Dictionary<int, long> BandRetry { get; } = new();
    public Dictionary<int, long> RuleCooldown { get; } = new();
    public Dictionary<int, long> RuleNext { get; } = new();
    public SortedSet<int> RuleDone { get; } = new();
    /// <summary>The last causes of change, newest last (the HUD shows them).</summary>
    public List<(long Tick, string Source, double Delta)> Recent { get; } = new();
    internal string CurveKey = "";
}

/// <summary>
/// Scales of units (ТЗ ч. 6, 18; docs/DECISIONS_6.md §104): sanity, morale… A number of a unit changed by the events of the battle
/// (queued as they happen, applied at a fixed point of the tick, each event once), recovered in game time, kept by its mode of
/// bounds; its bands put ordinary statuses on (chance, re-check, cooldown, hysteresis) and its curves give ordinary modifiers
/// of characteristics. Units without a scale cost nothing.
/// </summary>
public sealed class ScaleSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<int, ScaleState> _states = new();
    private readonly List<(int Unit, int Rule, double Delta, string Source, int Other)> _queue = new();
    /// <summary>(unit, rule, event key): an event applies a rule to a unit once (a death reported twice changes nothing).</summary>
    private readonly HashSet<(int Unit, int Rule, string Key)> _seen = new();
    public const string ModSource = "шкала: ";

    public ScaleSystem(Simulation sim)
    {
        _sim = sim;
        sim.Observed += OnEvent;
    }

    public bool Any => _states.Count > 0;

    /// <summary>The scale of a unit (null — it has none: no calculation at all).</summary>
    public ScaleState? Of(Unit u)
    {
        if (_states.TryGetValue(u.Id, out var s)) return s;
        if (u.Def.Scale is not { } use || _sim.Content.Get<ScaleProfileDef>(use.Profile) is not { } p) return null;
        bool immune = use.Mode == ScaleUseMode.Immune || p.ImmuneTags.Any(u.HasTag);
        double start = use.OwnStart ? use.Start : _sim.Tick > 1 ? p.SpawnValue : p.Start;
        s = new ScaleState { Profile = p, Mode = immune ? ScaleUseMode.Immune : ScaleUseMode.Active, Value = Bound(p, Finite(start, p.Start)) };
        _states[u.Id] = s;
        return s;
    }

    private static double Finite(double v, double fallback) => double.IsNaN(v) || double.IsInfinity(v) ? fallback : v;

    private static double Bound(ScaleProfileDef p, double v) => Math.Clamp(v, Math.Min(p.Lowest, p.Highest), Math.Max(p.Lowest, p.Highest));

    /// <summary>
    /// The value after a change by the mode of the bounds (ТЗ 18.2): Clamp — within min/max; Overflow — within the hard limits;
    /// SoftCap — beyond min/max the change shrinks by the curve of the profile, never past the hard limits. No NaN, no infinity.
    /// </summary>
    public static double Shift(ScaleProfileDef p, double v, double delta)
    {
        if (double.IsNaN(delta) || double.IsInfinity(delta) || double.IsNaN(v)) return Bound(p, Finite(v, p.Start));
        switch (p.Mode)
        {
            case ScaleLimitMode.Clamp:
                return Math.Clamp(v + delta, p.Min, p.Max);
            case ScaleLimitMode.Overflow:
                return Math.Clamp(v + delta, p.HardMin, p.HardMax);
        }
        // SoftCap: in 32 slices, each one shrunk by how far beyond the soft bound it starts.
        const int Slices = 32;
        double step = delta / Slices;
        var curve = new ScaleCurveDef { Points = p.SoftCurve };
        for (int i = 0; i < Slices; i++)
        {
            double mult = 1;
            if (step > 0 && v >= p.Max && p.HardMax > p.Max) mult = curve.Eval((v - p.Max) / (p.HardMax - p.Max));
            else if (step < 0 && v <= p.Min && p.Min > p.HardMin) mult = curve.Eval((p.Min - v) / (p.Min - p.HardMin));
            v = Math.Clamp(v + step * Math.Clamp(mult, 0, 1), p.HardMin, p.HardMax);
        }
        return v;
    }

    /// <summary>Changes the scale of a unit now (a script, an operation of a skill, a modifier of the mission).</summary>
    public void Change(Unit u, double delta, string source, Unit? by)
    {
        if (!u.Alive || Of(u) is not { } s) return;
        Apply(u, s, delta, source, by?.Id ?? -1);
    }

    private void Apply(Unit u, ScaleState s, double delta, string source, int other)
    {
        if (s.Mode == ScaleUseMode.Immune)
        {
            _sim.Emit(SimEventType.ScaleChanged, LogLevel.Diagnostic, $"{u.Name}: {s.Profile.Short} не меняется (иммунитет) — {source}", other, u.Id);
            return;
        }
        if (double.IsNaN(delta) || double.IsInfinity(delta))
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"{u.Name}: изменение шкалы не число — пропущено ({source})", other, u.Id);
            return;
        }
        double before = s.Value;
        s.Value = Shift(s.Profile, before, delta);
        double d = s.Value - before;
        if (Math.Abs(d) < 1e-9) return;
        s.Recent.Add((_sim.Tick, source, d));
        if (s.Recent.Count > 6) s.Recent.RemoveAt(0);
        _sim.Emit(SimEventType.ScaleChanged, LogLevel.Detail, $"{u.Name}: {s.Profile.Short} {before:0.#} → {s.Value:0.#} ({d:+0.#;-0.#}) — {source}", other, u.Id, value: d);
    }

    // ------------------------------------------------------------------ events

    private static bool Near(Unit a, Unit b, int radius) => radius <= 0 || HexCoord.Distance(a.Pos, b.Pos) <= radius;

    private bool Matches(Unit? other, string filter) =>
        filter == "" || other != null && (other.Tag == filter || other.Def.Id == filter || other.FactionId == filter || _sim.Conditions.TypeMatches(other, filter));

    private void Queue(Unit owner, ScaleState s, ScaleEvent ev, Unit? other, string key, string filterValue = "")
    {
        var rules = s.Profile.Rules;
        for (int i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            if (!r.Enabled || r.Event != ev) continue;
            if (ev is ScaleEvent.SkillUsed or ScaleEvent.StatusReceived ? r.Filter != "" && r.Filter != filterValue : !Matches(other, r.Filter)) continue;
            if (ev is ScaleEvent.AllyDied or ScaleEvent.EnemyDied or ScaleEvent.LeaderDied && other != null && !Near(owner, other, r.Radius)) continue;
            if (!_seen.Add((owner.Id, i, key))) continue;
            double amount = r.Percent ? r.Amount / 100 * (s.Profile.Max - s.Profile.Min) : r.Amount;
            _queue.Add((owner.Id, i, amount, r.Name != "" ? r.Name : EdUtil.EnumLabel(ev), other?.Id ?? -1));
        }
    }

    private IEnumerable<(Unit U, ScaleState S)> WithScales() =>
        _sim.Units.Where(u => u.Alive && u.Def.Scale != null).Select(u => (u, Of(u))).Where(x => x.Item2 != null).Select(x => (x.u, x.Item2!));

    private void OnEvent(SimEvent e)
    {
        if (_sim.Units.All(u => u.Def.Scale == null)) return;
        var src = e.SourceUnit >= 0 ? _sim.GetUnit(e.SourceUnit) : null;
        var dst = e.TargetUnit >= 0 ? _sim.GetUnit(e.TargetUnit) : null;
        string key = $"{e.Type}:{e.Tick}:{e.SourceUnit}:{e.TargetUnit}:{e.DamageType}:{e.Value}";
        void For(Unit? u, ScaleEvent ev, Unit? other, string filterValue = "")
        {
            if (u is { Alive: true } && Of(u) is { } s) Queue(u, s, ev, other, key, filterValue);
        }
        switch (e.Type)
        {
            case SimEventType.Damage when e.Value > 0:
                For(dst, ScaleEvent.DamageTaken, src);
                if (src != dst) For(src, ScaleEvent.DamageDealt, dst);
                break;
            case SimEventType.Hit:
                For(src, ScaleEvent.Hit, dst);
                break;
            case SimEventType.Miss:
                For(src, ScaleEvent.Missed, dst);
                break;
            case SimEventType.Crit:
                For(src, ScaleEvent.Crit, dst);
                break;
            case SimEventType.Heal when e.Value > 0:
                For(dst, ScaleEvent.Healed, src);
                break;
            case SimEventType.Death when dst != null:
            {
                // A death once per dead unit, however many times it is reported (T40).
                key = "death:" + dst.Id;
                if (src != null && src != dst) For(src, ScaleEvent.Killed, dst);
                foreach (var (u, s) in WithScales().ToList())
                {
                    if (u == dst) continue;
                    bool ally = _sim.RelationOf(u, dst) == Relation.Ally;
                    if (ally)
                    {
                        Queue(u, s, ScaleEvent.AllyDied, dst, key);
                        if (s.Profile.Rules.Any(r => r.Event == ScaleEvent.LeaderDied))
                            foreach (var (r, i) in s.Profile.Rules.Select((r, i) => (r, i)).Where(x => x.r.Event == ScaleEvent.LeaderDied))
                                if (r.Enabled && dst.HasTag(r.Filter == "" ? "leader" : r.Filter) && Near(u, dst, r.Radius) && _seen.Add((u.Id, i, key)))
                                    _queue.Add((u.Id, i, r.Percent ? r.Amount / 100 * (s.Profile.Max - s.Profile.Min) : r.Amount, r.Name != "" ? r.Name : "гибель лидера", dst.Id));
                        if (dst.IsStructure) Queue(u, s, ScaleEvent.BaseLost, dst, key);
                    }
                    else if (_sim.IsHostile(u, dst)) Queue(u, s, ScaleEvent.EnemyDied, dst, key);
                }
                break;
            }
            case SimEventType.WaveEnded:
                foreach (var (u, s) in WithScales().ToList()) Queue(u, s, ScaleEvent.WaveEnded, null, key);
                break;
            case SimEventType.CastStarted when src?.Casting != null:
                For(src, ScaleEvent.SkillUsed, dst, src.Casting.Skill.Id);
                break;
            case SimEventType.StatusApplied when e.DamageType != "":
                For(dst, ScaleEvent.StatusReceived, src, e.DamageType);
                break;
        }
    }

    /// <summary>A boss entered a phase: «босс сменил фазу» for every scale (the filter — the boss's tag or template).</summary>
    internal void OnBossPhase(Unit boss, int phase)
    {
        foreach (var (u, s) in WithScales().ToList()) Queue(u, s, ScaleEvent.BossPhase, boss, $"phase:{boss.Id}:{phase}:{_sim.Tick}");
    }

    // ------------------------------------------------------------------ the tick

    public void Update()
    {
        if (_queue.Count == 0 && !_sim.Units.Any(u => u.Def.Scale != null)) return;
        double dt = _sim.Dt;
        // 1. The events of the tick, in the order of the priority of their rules, then as they came.
        if (_queue.Count > 0)
        {
            var batch = _queue.Select((q, n) => (q, n)).ToList();
            _queue.Clear();
            var stackedOnce = new HashSet<(int, int)>();
            foreach (var (q, _) in batch.OrderBy(x => x.q.Unit).ThenByDescending(x => RuleOf(x.q.Unit, x.q.Rule)?.Priority ?? 0).ThenBy(x => x.n))
            {
                if (_sim.GetUnit(q.Unit) is not { Alive: true } u || Of(u) is not { } s || q.Rule >= s.Profile.Rules.Count) continue;
                var r = s.Profile.Rules[q.Rule];
                if (r.Once && s.RuleDone.Contains(q.Rule)) continue;
                if (s.RuleCooldown.TryGetValue(q.Rule, out var until) && _sim.Tick < until) continue;
                if (!r.Stack && !stackedOnce.Add((q.Unit, q.Rule))) continue;
                if (r.Condition != null && !_sim.Conditions.Evaluate(r.Condition, new ConditionContext(u, _sim.GetUnit(q.Other), u.Pos, null))) continue;
                double d = r.MaxPerEvent > 0 ? Math.Clamp(q.Delta, -r.MaxPerEvent, r.MaxPerEvent) : q.Delta;
                Apply(u, s, d, q.Source, q.Other);
                if (r.Once) s.RuleDone.Add(q.Rule);
                if (r.Cooldown > 0) s.RuleCooldown[q.Rule] = _sim.Tick + _sim.TicksFor(r.Cooldown);
            }
        }
        foreach (var u in _sim.Units)
        {
            if (!u.Alive || u.Def.Scale == null || Of(u) is not { } s) continue;
            var p = s.Profile;
            // 2. Periodic rules: «каждые N с», «рядом с…».
            for (int i = 0; i < p.Rules.Count; i++)
            {
                var r = p.Rules[i];
                if (!r.Enabled || r.Event is not (ScaleEvent.Timer or ScaleEvent.Near)) continue;
                long every = Math.Max(1, _sim.TicksFor(Math.Max(0.05, r.Interval)));
                if (!s.RuleNext.TryGetValue(i, out var next)) s.RuleNext[i] = next = _sim.Tick + every;
                if (_sim.Tick < next) continue;
                s.RuleNext[i] = _sim.Tick + every;
                if (r.Once && s.RuleDone.Contains(i)) continue;
                if (r.Event == ScaleEvent.Near && !_sim.Units.Any(o => o.Alive && o != u && Near(u, o, Math.Max(1, r.Radius)) && Matches(o, r.Filter))) continue;
                if (r.Condition != null && !_sim.Conditions.Evaluate(r.Condition, new ConditionContext(u, null, u.Pos, null))) continue;
                double amount = r.Percent ? r.Amount / 100 * (p.Max - p.Min) : r.Amount;
                Apply(u, s, r.MaxPerEvent > 0 ? Math.Clamp(amount, -r.MaxPerEvent, r.MaxPerEvent) : amount, r.Name != "" ? r.Name : EdUtil.EnumLabel(r.Event), -1);
                if (r.Once) s.RuleDone.Add(i);
            }
            // 3. Recovery toward the target, in game time (a paused battle has no ticks).
            if (p.Recovery > 0 && s.Mode == ScaleUseMode.Active && Math.Abs(s.Value - p.RecoverTarget) > 1e-9)
            {
                double step = Math.Min(Math.Abs(p.RecoverTarget - s.Value), p.Recovery * dt) * Math.Sign(p.RecoverTarget - s.Value);
                s.Value = Shift(p, s.Value, step);
            }
            // 4. Bands → statuses.
            UpdateBands(u, s);
            // 5. Curves → modifiers.
            UpdateCurves(u, s);
        }
    }

    private ScaleRuleDef? RuleOf(int unit, int rule) =>
        _states.TryGetValue(unit, out var s) && rule < s.Profile.Rules.Count ? s.Profile.Rules[rule] : null;

    private void UpdateBands(Unit u, ScaleState s)
    {
        var p = s.Profile;
        for (int i = 0; i < p.Bands.Count; i++)
        {
            var b = p.Bands[i];
            bool inside = b.Enabled && s.Value >= b.From && s.Value <= b.To;
            var sd = _sim.Content.Status(b.Status);
            if (s.Active.Contains(i))
            {
                bool still = sd != null && u.FindStatus(sd.Id) != null;
                bool left = !b.Enabled || s.Value < b.From - b.Hysteresis || s.Value > b.To + b.Hysteresis;
                if (!left && still) continue;
                s.Active.Remove(i);
                if (still && sd != null) _sim.Statuses.RemoveById(u, sd.Id, $"{p.Short} вне диапазона «{BandName(b)}»");
                if (left) _sim.Emit(SimEventType.ScaleThreshold, LogLevel.Normal, $"{u.Name}: {p.Short} {s.Value:0.#} — выход из «{BandName(b)}»", -1, u.Id, value: s.Value);
                _sim.Emit(SimEventType.ScaleState, LogLevel.Detail, $"{u.Name}: «{sd?.DisplayName ?? b.Status}» снято ({p.Short})", -1, u.Id, damageType: b.Status);
                if (b.Cooldown > 0) s.BandCooldown[i] = _sim.Tick + _sim.TicksFor(b.Cooldown);
                // Ended by itself inside the band: again only after the re-check (or after leaving the band).
                if (!left) s.BandRetry[i] = b.Recheck > 0 ? _sim.Tick + _sim.TicksFor(b.Recheck) : long.MaxValue;
                continue;
            }
            if (!inside)
            {
                s.BandRetry.Remove(i); // leaving the band resets «only after leaving»
                continue;
            }
            if (sd == null) continue;
            if (s.BandCooldown.TryGetValue(i, out var cd) && _sim.Tick < cd) continue;
            if (s.BandRetry.TryGetValue(i, out var retry) && _sim.Tick < retry) continue;
            if (b.Chance < 1 && !_sim.Rng.Chance(Math.Max(0, b.Chance)))
            {
                s.BandRetry[i] = b.Recheck > 0 ? _sim.Tick + _sim.TicksFor(b.Recheck) : long.MaxValue;
                _sim.Emit(SimEventType.ScaleThreshold, LogLevel.Detail, $"{u.Name}: {p.Short} {s.Value:0.#} в «{BandName(b)}» — устоял (шанс {b.Chance:P0})", -1, u.Id, value: s.Value);
                continue;
            }
            s.BandRetry.Remove(i);
            _sim.Emit(SimEventType.ScaleThreshold, LogLevel.Normal, $"{u.Name}: {p.Short} {s.Value:0.#} — вход в «{BandName(b)}»", -1, u.Id, value: s.Value);
            double dur = b.Duration > 0 ? b.Duration : 0;
            if (_sim.Statuses.Apply(u, sd, 1, dur, -1, force: true))
            {
                s.Active.Add(i);
                _sim.Emit(SimEventType.ScaleState, LogLevel.Normal, $"{u.Name}: «{sd.DisplayName}» от {p.Short} ({s.Value:0.#})", -1, u.Id, damageType: sd.Id);
            }
            else s.BandRetry[i] = b.Recheck > 0 ? _sim.Tick + _sim.TicksFor(b.Recheck) : long.MaxValue;
        }
    }

    private static string BandName(ScaleBandDef b) => b.Name != "" ? b.Name : b.Status;

    /// <summary>The modifiers of the curves at a value (the editor previews them; the battle puts them on the unit).</summary>
    public static List<StatModDef> CurveMods(ScaleProfileDef p, double value, Func<string, double>? statWithoutCurves = null)
    {
        var mods = new List<StatModDef>();
        foreach (var c in p.Curves.Where(c => c.Enabled && c.Stat != "" && c.Points.Count > 0))
        {
            double y = c.Eval(value);
            if (double.IsNaN(y) || double.IsInfinity(y)) continue;
            switch (c.Op)
            {
                case ScaleCurveOp.Add: mods.Add(new StatModDef { Stat = c.Stat, Add = y }); break;
                case ScaleCurveOp.Multiply: mods.Add(new StatModDef { Stat = c.Stat, Mul = y }); break;
                case ScaleCurveOp.Override when statWithoutCurves != null:
                    mods.Add(new StatModDef { Stat = c.Stat, Add = y - statWithoutCurves(c.Stat) });
                    break;
            }
        }
        return mods;
    }

    private void UpdateCurves(Unit u, ScaleState s)
    {
        var p = s.Profile;
        string key = Math.Round(s.Value, 3) + "|" + string.Join("", p.Curves.Select(c => c.Enabled ? '1' : '0'));
        if (key == s.CurveKey) return;
        s.CurveKey = key;
        string src = ModSource + p.Short;
        bool had = u.InstanceMods.RemoveAll(m => m.Source == src) > 0;
        if (had) _sim.Statuses.RecalculateModifiers(u);
        var mods = CurveMods(p, s.Value, stat => u.Stat(stat));
        foreach (var m in mods) u.InstanceMods.Add((m, src));
        if (had || mods.Count > 0)
        {
            double share = u.HpMax > 0 ? u.Hp / u.HpMax : 1;
            _sim.Statuses.RecalculateModifiers(u);
            u.Hp = Math.Min(u.Hp, u.HpMax);
            _ = share;
        }
    }

    /// <summary>The state of every scale for the fingerprint of the battle.</summary>
    public IEnumerable<string> Digest() => _states.OrderBy(k => k.Key).Select(k =>
        $"{k.Key}:{Math.Round(k.Value.Value, 4)}:{string.Join(",", k.Value.Active)}:{string.Join(",", k.Value.BandCooldown.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"))}:" +
        $"{string.Join(",", k.Value.BandRetry.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"))}:{string.Join(",", k.Value.RuleDone)}:" +
        string.Join(",", k.Value.RuleCooldown.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")));
}
