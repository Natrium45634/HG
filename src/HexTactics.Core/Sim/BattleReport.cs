using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>Health of a base at the end of a run.</summary>
public sealed record BaseState(string Id, string Name, double Hp, double MaxHp, bool Destroyed)
{
    public double Fraction => MaxHp > 0 ? Hp / MaxHp : 0;
}

/// <summary>A skill in the report: launches and hits (a launch of a series makes several hits).</summary>
public sealed class SkillStat
{
    public int Casts { get; set; }
    public int Hits { get; set; }
}

/// <summary>
/// The report of a run (docs/DECISIONS_4.md §64, TZ §10.5): collected by an observer from the events of the
/// simulation and its state after each tick — the simulation itself does not change. The same report is built for a
/// battle played by hand and for an autobattle.
/// </summary>
/// <summary>The result of one wave in a run.</summary>
public sealed class WaveStat
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Spawned { get; set; }
    public int Killed { get; set; }
    public int ReachedGoal { get; set; }
    public double BaseDamage { get; set; }
    public double Started { get; set; } = -1;
    public double Ended { get; set; } = -1;
    // Part 5 (ТЗ ч. 5, 19.3; docs/DECISIONS_5.md §86).
    /// <summary>Appearances planned, and those cancelled (taken exit, a destroyed spawner, the limits).</summary>
    public int Planned { get; set; }
    public int Failed { get; set; }
    /// <summary>The most enemies of the wave alive at once.</summary>
    public int MaxAlive { get; set; }
    /// <summary>From the first appearance to the last enemy gone, ticks; −1 — not finished.</summary>
    public long CompletionTicks { get; set; } = -1;
    /// <summary>Enemies of the wave that were stuck at least once («нет продвижения»).</summary>
    public int StuckUnits { get; set; }
}

/// <summary>One unit in the report (ТЗ ч. 5, 19.2).</summary>
public sealed class UnitStat
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";
    public string Side { get; set; } = "";
    public int Wave { get; set; } = -1;
    public string Group { get; set; } = "";
    public double DamageDealt { get; set; }
    public double DamageTaken { get; set; }
    /// <summary>Damage per second of its life in the battle.</summary>
    public double Dps { get; set; }
    public double Healing { get; set; }
    public double Overheal { get; set; }
    /// <summary>Seconds of control (movement or actions blocked) on enemies, by its statuses.</summary>
    public double ControlSeconds { get; set; }
    /// <summary>Positive statuses it put on allies.</summary>
    public int Buffs { get; set; }
    /// <summary>Hexes it moved.</summary>
    public int PathHexes { get; set; }
    /// <summary>Ticks it waited with a move order (a taken hex, the queue).</summary>
    public long WaitingTicks { get; set; }
    public int Casts { get; set; }
    public int Misses { get; set; }
    public int Kills { get; set; }
    /// <summary>The tick of its death; −1 — alive at the end (or left the battle).</summary>
    public long DeathTick { get; set; } = -1;
    [JsonIgnore] internal long BornTick { get; set; }
    [JsonIgnore] internal Hex.HexCoord LastPos { get; set; }
}

public sealed class BattleReport
{
    [JsonIgnore] private Simulation? _sim;
    private readonly List<string> _destroyed = new();

    public string Scenario { get; set; } = "";
    public ulong Seed { get; set; }
    public string Outcome { get; set; } = "не завершён";
    public string Reason { get; set; } = "";
    /// <summary>Why the run stopped (the end of the battle, the time limit of the run, a breakpoint).</summary>
    public string StopReason { get; set; } = "";
    public int WavesTotal { get; set; }
    public int WavesCompleted { get; set; }
    public double Seconds { get; set; }
    public long Ticks { get; set; }
    public List<BaseState> Bases { get; set; } = new();
    public int DefendersLost { get; set; }
    public int EnemiesKilledByDefenders { get; set; }
    public int EnemiesKilled { get; set; }
    public int EnemiesReachedGoal { get; set; }
    /// <summary>Damage dealt by each side (to health), and the damage each side took.</summary>
    public Dictionary<string, double> DamageDealt { get; set; } = new();
    public Dictionary<string, double> DamageTaken { get; set; } = new();
    public double Healing { get; set; }
    public int Heals { get; set; }
    /// <summary>Damage the shields took instead of health, and the damage absorbed.</summary>
    public double Shielded { get; set; }
    public double Absorbed { get; set; }
    public Dictionary<string, SkillStat> Skills { get; set; } = new();
    public Dictionary<string, int> Passives { get; set; } = new();
    public Dictionary<string, double> DamageByType { get; set; } = new();
    /// <summary>Enemy-seconds spent blocked (a closed route, a blocker on the way).</summary>
    public double RouteBlockedSeconds { get; set; }
    public int TargetChanges { get; set; }
    public Dictionary<string, int> AiErrors { get; set; } = new();
    public List<string> FailureReasons { get; set; } = new();
    public long EventChainCuts { get; set; }
    /// <summary>Per wave: enemies created and killed, reached the goal, damage to the bases, start and end (docs/DECISIONS_4.md §70).</summary>
    public List<WaveStat> WaveStats { get; set; } = new();
    /// <summary>Enemy-seconds blocked on each route of the scenario (TZ §21.4: which routes were blocked most).</summary>
    public Dictionary<string, double> RouteBlocked { get; set; } = new();
    /// <summary>Per defender template: the damage it dealt and the enemies it killed (TZ §21.4: the most effective defenders).</summary>
    public Dictionary<string, double> DefenderDamage { get; set; } = new();
    public Dictionary<string, int> DefenderKills { get; set; } = new();
    // Part 5 (ТЗ ч. 5, 19.2–19.4; docs/DECISIONS_5.md §86).
    /// <summary>Every unit of the battle (structures aside).</summary>
    public List<UnitStat> UnitStats { get; set; } = new();
    /// <summary>The format of the content files and the rules version of the tick.</summary>
    public int SchemaVersion { get; set; } = ContentJson.CurrentFormatVersion;
    public int SimVersion { get; set; }
    /// <summary>Errors of the check before the start (an unsafe test has them).</summary>
    public int ValidationErrors { get; set; }
    /// <summary>
    /// The time of a tick, ms: mean and the worst. Measured, so it differs between runs of the same seed — not in the
    /// JSON (whose equality proves determinism), only in the text and the CSV.
    /// </summary>
    [JsonIgnore] public double TickMsMean { get; set; }
    [JsonIgnore] public double TickMsMax { get; set; }
    [JsonIgnore] private readonly Dictionary<int, UnitStat> _units = new();
    [JsonIgnore] private readonly Dictionary<int, HashSet<int>> _stuck = new();
    [JsonIgnore] private double _tickMsSum;
    [JsonIgnore] private long _tickCount;
    [JsonIgnore] private long _tickStarted;

    public const string PlayerSide = "игрок";
    public const string EnemySide = "враги";
    public const string OtherSide = "прочие";

    /// <summary>Starts watching a simulation (before its first tick).</summary>
    public static BattleReport Attach(Simulation sim, string scenario = "", ulong seed = 0)
    {
        var r = new BattleReport { _sim = sim, Scenario = scenario, Seed = seed, SimVersion = sim.Rules.Time.SimVersion };
        sim.Observed += r.OnEvent;
        sim.Damage.Resolved += r.OnDamage;
        sim.Stepped += r.OnTick;
        sim.Combat.Healed += r.OnHealed;
        r._tickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        return r;
    }

    public void Detach()
    {
        if (_sim == null) return;
        _sim.Observed -= OnEvent;
        _sim.Damage.Resolved -= OnDamage;
        _sim.Stepped -= OnTick;
        _sim.Combat.Healed -= OnHealed;
    }

    /// <summary>The line of a unit, created when it is first seen.</summary>
    private UnitStat? U(Unit? u)
    {
        if (u == null || u.IsStructure) return null;
        if (!_units.TryGetValue(u.Id, out var s))
            _units[u.Id] = s = new UnitStat
            {
                Id = u.Id, Name = u.Name, Template = u.Def.Id, Side = SideOf(u), Wave = u.Wave, Group = u.Group, BornTick = _sim!.Tick, LastPos = u.Pos,
            };
        return s;
    }

    private void OnHealed(Unit? source, Unit target, double healed, double over)
    {
        if (U(source) is { } s)
        {
            s.Healing += healed;
            s.Overheal += over;
        }
    }

    private string SideOf(Unit? u)
    {
        if (u == null || _sim == null) return OtherSide;
        if (_sim.IsPlayerFaction(u.FactionId) || _sim.IsPlayerFaction(u.BaseFactionId)) return PlayerSide;
        return _sim.Options.AiFactions.Contains(u.FactionId) ? EnemySide : OtherSide;
    }

    private static void Add<TK>(Dictionary<TK, double> d, TK k, double v) where TK : notnull => d[k] = d.GetValueOrDefault(k) + v;
    private static void Inc<TK>(Dictionary<TK, int> d, TK k) where TK : notnull => d[k] = d.GetValueOrDefault(k) + 1;

    private WaveStat Wave(int index)
    {
        while (WaveStats.Count <= index) WaveStats.Add(new WaveStat { Index = WaveStats.Count });
        return WaveStats[index];
    }

    private void OnEvent(SimEvent e)
    {
        var sim = _sim!;
        switch (e.Type)
        {
            case SimEventType.Damage when e.Value > 0:
            {
                var src = sim.GetUnit(e.SourceUnit);
                var dst = sim.GetUnit(e.TargetUnit);
                Add(DamageDealt, src == null ? OtherSide : SideOf(src), e.Value);
                Add(DamageTaken, SideOf(dst), e.Value);
                if (e.DamageType != "") Add(DamageByType, e.DamageType, e.Value);
                if (dst is { IsStructure: true } && src is { Wave: >= 0 } && SideOf(dst) == PlayerSide) Wave(src.Wave).BaseDamage += e.Value;
                if (src is { IsStructure: false } && SideOf(src) == PlayerSide && SideOf(dst) == EnemySide) Add(DefenderDamage, src.Def.DisplayName, e.Value);
                if (U(src) is { } us) us.DamageDealt += e.Value;
                if (U(dst) is { } ud) ud.DamageTaken += e.Value;
                break;
            }
            case SimEventType.StatusApplied when e.DamageType != "" && e.SourceUnit >= 0 && e.SourceUnit != e.TargetUnit:
            {
                var src = sim.GetUnit(e.SourceUnit);
                var dst = sim.GetUnit(e.TargetUnit);
                if (src != null && dst != null && !sim.IsHostile(src, dst) && sim.Content.Get<StatusDef>(e.DamageType) is { Negative: false } && U(src) is { } ub) ub.Buffs++;
                break;
            }
            case SimEventType.CastReleased when e.SourceUnit >= 0:
                if (U(sim.GetUnit(e.SourceUnit)) is { } uc) uc.Casts++;
                if (e.DamageType != "") Stat(e.DamageType).Casts++;
                break;
            case SimEventType.WaveStarted:
            {
                var w = Wave((int)e.Value);
                if (w.Started < 0) w.Started = Math.Round(sim.Scenario.BattleTime, 2);
                break;
            }
            case SimEventType.WaveEnded:
                Wave((int)e.Value).Ended = Math.Round(sim.Scenario.BattleTime, 2);
                break;
            case SimEventType.Heal when e.Value > 0:
                Healing += e.Value;
                Heals++;
                break;
            case SimEventType.CastReleased when e.DamageType != "" && e.SourceUnit < 0:
                Stat(e.DamageType).Casts++;
                break;
            case SimEventType.PassiveTriggered when e.DamageType != "":
                Inc(Passives, e.DamageType);
                break;
            case SimEventType.TargetChanged:
                TargetChanges++;
                break;
            case SimEventType.AiProblem:
                Inc(AiErrors, e.DamageType == "" ? "other" : e.DamageType);
                if (e.DamageType == "stuck" && sim.GetUnit(e.SourceUnit) is { Wave: >= 0 } stuck)
                {
                    if (!_stuck.TryGetValue(stuck.Wave, out var set)) _stuck[stuck.Wave] = set = new HashSet<int>();
                    set.Add(stuck.Id);
                }
                break;
            case SimEventType.GoalReached:
            {
                var u = sim.GetUnit(e.SourceUnit);
                if (u != null && SideOf(u) == EnemySide) EnemiesReachedGoal++;
                if (u is { Wave: >= 0 }) Wave(u.Wave).ReachedGoal++;
                break;
            }
            case SimEventType.Death:
            {
                var dead = sim.GetUnit(e.TargetUnit);
                if (dead == null || dead.DeathCause == DeathCause.Removed && dead.Hp > 0) break;
                var killer = sim.GetUnit(e.SourceUnit);
                if (U(dead) is { } ud) ud.DeathTick = sim.Tick;
                if (killer != null && killer != dead && U(killer) is { } uk) uk.Kills++;
                string side = SideOf(dead);
                if (side == PlayerSide && !dead.IsStructure) DefendersLost++;
                if (side == EnemySide && !dead.IsStructure && dead.DeathCause != DeathCause.Removed)
                {
                    EnemiesKilled++;
                    if (dead.Wave >= 0) Wave(dead.Wave).Killed++;
                    if (killer != null && SideOf(killer) == PlayerSide)
                    {
                        EnemiesKilledByDefenders++;
                        if (!killer.IsStructure) Inc(DefenderKills, killer.Def.DisplayName);
                    }
                }
                break;
            }
            case SimEventType.StructureDestroyed:
                _destroyed.Add(e.Text);
                break;
        }
    }

    private SkillStat Stat(string id)
    {
        if (!Skills.TryGetValue(id, out var s)) Skills[id] = s = new SkillStat();
        return s;
    }

    private void OnDamage(DamageRequest q, DamageResult r)
    {
        if (r.Missed)
        {
            if (U(q.Attacker) is { } um) um.Misses++;
            return;
        }
        Shielded += r.Shielded;
        Absorbed += r.Absorbed;
        if (q.Skill != null) Stat(q.Skill.Id).Hits++;
    }

    private void OnTick()
    {
        var sim = _sim!;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (now - _tickStarted) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _tickStarted = now;
        _tickMsSum += ms;
        _tickCount++;
        if (ms > TickMsMax) TickMsMax = ms;
        int blocked = 0;
        var aliveByWave = new Dictionary<int, int>();
        foreach (var u in sim.Units)
        {
            if (u.Alive && U(u) is { } us)
            {
                if (u.Pos != us.LastPos)
                {
                    us.PathHexes += Hex.HexCoord.Distance(us.LastPos, u.Pos);
                    us.LastPos = u.Pos;
                }
                if (u.WaitingFor >= 0 || u.Queue.Current is { Type: CommandType.Move, Status: CommandStatus.Waiting }) us.WaitingTicks++;
                // Control on it: the seconds go to the sources of its negative statuses.
                if (u.Flags.BlocksMovement || u.Flags.BlocksActions)
                    foreach (var src in u.Statuses.Where(x => x.Def.Negative && x.SourceUnit >= 0 && x.SourceUnit != u.Id).Select(x => x.SourceUnit).Distinct())
                        if (U(sim.GetUnit(src)) is { } uc) uc.ControlSeconds += sim.Dt;
                if (u.Wave >= 0) aliveByWave[u.Wave] = aliveByWave.GetValueOrDefault(u.Wave) + 1;
            }
            if (!u.Alive || u.IsStructure || SideOf(u) != EnemySide) continue;
            if (u.Ai.BlockedSince >= 0 || sim.Blocking.BlockerOf(u) != null)
            {
                blocked++;
                if (u.RouteId != "") Add(RouteBlocked, u.RouteId, sim.Dt);
            }
        }
        RouteBlockedSeconds += blocked * sim.Dt;
        foreach (var (w, n) in aliveByWave)
            if (n > Wave(w).MaxAlive) Wave(w).MaxAlive = n;
    }

    /// <summary>Fixes the final values (outcome, waves, bases) and stops watching — call when the run stops.</summary>
    public BattleReport Finish(string stopReason = "")
    {
        Fill(stopReason);
        Detach();
        return this;
    }

    /// <summary>The values so far (the battle may go on; a later call refreshes them).</summary>
    public BattleReport Fill(string stopReason = "")
    {
        var sim = _sim!;
        Outcome = sim.Outcome switch { BattleOutcome.Victory => "победа", BattleOutcome.Defeat => "поражение", _ => "не завершён" };
        Reason = sim.OutcomeText;
        StopReason = stopReason != "" ? stopReason : sim.Outcome != BattleOutcome.None ? "бой завершён" : "";
        WavesTotal = sim.Scenario.Waves.Count;
        for (int i = 0; i < sim.Scenario.Waves.Count; i++)
        {
            var wr = sim.Scenario.Waves[i];
            var ws = Wave(i);
            ws.Name = wr.Def.Name;
            ws.Spawned = wr.UnitIds.Count;
            ws.Planned = wr.Planned.Count;
            ws.Failed = wr.Planned.Count(p => p.Done && p.SpawnedTick < 0);
            ws.CompletionTicks = wr.StartedTick >= 0 && wr.ClearedTick >= 0 ? wr.ClearedTick - wr.StartedTick : -1;
            ws.StuckUnits = _stuck.TryGetValue(i, out var st) ? st.Count : 0;
        }
        foreach (var u in sim.Units) U(u);
        foreach (var us in _units.Values)
        {
            double alive = ((us.DeathTick >= 0 ? us.DeathTick : sim.Tick) - us.BornTick) * sim.Dt;
            us.Dps = alive > 0 ? Math.Round(us.DamageDealt / alive, 3) : 0;
            us.DamageDealt = Math.Round(us.DamageDealt, 2);
            us.DamageTaken = Math.Round(us.DamageTaken, 2);
            us.Healing = Math.Round(us.Healing, 2);
            us.Overheal = Math.Round(us.Overheal, 2);
            us.ControlSeconds = Math.Round(us.ControlSeconds, 2);
        }
        UnitStats = _units.Values.OrderBy(u => u.Id).ToList();
        TickMsMean = _tickCount > 0 ? Math.Round(_tickMsSum / _tickCount, 4) : 0;
        TickMsMax = Math.Round(TickMsMax, 3);
        // A wave whose last enemy fell on the final tick is complete even if the end of the battle came first.
        WavesCompleted = sim.Scenario.Waves.Count(w => w.State == WaveState.Cleared
            || w.State == WaveState.Active && w.Planned.All(p => p.Done) && w.UnitIds.All(id => sim.GetUnit(id)?.Alive != true));
        Seconds = Math.Round(sim.Scenario.BattleTime, 2);
        Ticks = sim.Tick;
        EventChainCuts = sim.Scenario.EventChainCuts + sim.Scenario.TriggerCycleCuts;
        Bases.Clear();
        FailureReasons = new List<string>(_destroyed);
        if (sim.Scenario.Def is { } sc)
            foreach (var o in sc.Objects.Where(o => o.Type == ScenarioObjectType.PlayerBase && o.Health != null))
            {
                var s = sim.Structures.Of(o.Id);
                Bases.Add(new BaseState(o.Id, o.DisplayName, Math.Round(s?.Alive == true ? s.Hp : 0, 1), o.Health!.MaxHp, s == null || !s.Alive));
            }
        foreach (var o in sim.Scenario.Objectives.Where(o => o.State == ObjectiveState.Failed))
            FailureReasons.Add($"цель «{o.Def.Name}» провалена: {o.Reason}");
        if (sim.Outcome == BattleOutcome.Defeat && Reason != "") FailureReasons.Insert(0, Reason);
        FailureReasons = FailureReasons.Distinct().ToList();
        return this;
    }

    /// <summary>Health of all bases together (0..1), 1 without bases.</summary>
    [JsonIgnore]
    public double BaseHealth => Bases.Count == 0 ? 1 : Bases.Sum(b => b.Hp) / Math.Max(1, Bases.Sum(b => b.MaxHp));

    [JsonIgnore] public bool Won => Outcome == "победа";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public string ToText(ContentDatabase? db = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Сценарий «{Scenario}», seed {Seed}");
        sb.AppendLine($"Результат: {Outcome}" + (Reason != "" ? $" — {Reason}" : "") + (StopReason != "" && StopReason != "бой завершён" ? $" (остановка: {StopReason})" : ""));
        sb.AppendLine($"Волны: {WavesCompleted} из {WavesTotal}; время {F(Seconds)} с");
        if (Bases.Count > 0) sb.AppendLine("Базы: " + string.Join(", ", Bases.Select(b => $"«{b.Name}» {(b.Destroyed ? "уничтожена" : $"{F(b.Hp)}/{F(b.MaxHp)}")}")));
        sb.AppendLine($"Погибло защитников: {DefendersLost}; врагов уничтожено защитниками: {EnemiesKilledByDefenders} (всего {EnemiesKilled}); дошли до цели: {EnemiesReachedGoal}");
        sb.AppendLine("Урон по сторонам: " + string.Join(", ", DamageDealt.OrderByDescending(k => k.Value).Select(k => $"{k.Key} нанесли {F(k.Value)}"))
                      + (DamageTaken.Count > 0 ? "; получили: " + string.Join(", ", DamageTaken.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {F(k.Value)}")) : ""));
        sb.AppendLine($"Лечение: {F(Healing)} ({Heals} раз); щиты приняли {F(Shielded)}, поглощено {F(Absorbed)}");
        if (DamageByType.Count > 0)
            sb.AppendLine("Урон по типам: " + string.Join(", ", DamageByType.OrderByDescending(k => k.Value).Select(k => $"{db?.DamageType(k.Key)?.DisplayName ?? k.Key} {F(k.Value)}")));
        if (Skills.Count > 0)
            sb.AppendLine("Навыки (запуски/попадания): " + string.Join(", ", Skills.OrderByDescending(k => k.Value.Casts + k.Value.Hits).Take(12)
                .Select(k => $"{db?.Skill(k.Key)?.DisplayName ?? k.Key} {k.Value.Casts}/{k.Value.Hits}")));
        if (Passives.Count > 0)
            sb.AppendLine("Срабатывания пассивов: " + string.Join(", ", Passives.OrderByDescending(k => k.Value).Select(k => $"{db?.Get<PassiveDef>(k.Key)?.DisplayName ?? k.Key} ×{k.Value}")));
        sb.AppendLine($"Блокировка маршрутов: {F(RouteBlockedSeconds)} враго-с" +
                      (RouteBlocked.Count > 0 ? " (" + string.Join(", ", RouteBlocked.OrderByDescending(k => k.Value).Select(k => $"«{k.Key}» {F(k.Value)}")) + ")" : "") + $"; смен цели: {TargetChanges}");
        if (DefenderDamage.Count > 0)
            sb.AppendLine("Защитники: " + string.Join(", ", DefenderDamage.OrderByDescending(k => k.Value)
                .Select(k => $"{k.Key} — урон {F(k.Value)}, уничтожил {DefenderKills.GetValueOrDefault(k.Key)}")));
        sb.AppendLine("Ошибки ИИ: " + (AiErrors.Count == 0 ? "нет" : string.Join(", ", AiErrors.Select(k => $"{AiErrorName(k.Key)} ×{k.Value}"))));
        foreach (var w in WaveStats.Where(w => w.Spawned > 0 || w.Started >= 0))
            sb.AppendLine($"  волна {w.Index + 1} «{w.Name}»: появилось {w.Spawned}, уничтожено {w.Killed}, дошли до цели {w.ReachedGoal}, урон по базе {F(w.BaseDamage)}" +
                          (w.Started >= 0 ? $", {F(w.Started)}–{(w.Ended >= 0 ? F(w.Ended) : "…")} с" : ""));
        if (FailureReasons.Count > 0) sb.AppendLine("Причины провала/потерь: " + string.Join("; ", FailureReasons));
        if (EventChainCuts > 0) sb.AppendLine($"Оборвано цепочек событий: {EventChainCuts}");
        // Part 5 (ТЗ ч. 5, 19.2–19.4).
        foreach (var w in WaveStats.Where(w => w.Planned > 0))
            sb.AppendLine($"  волна {w.Index + 1}: запланировано {w.Planned}, отменено {w.Failed}, живых одновременно до {w.MaxAlive}, застревали {w.StuckUnits}" +
                          (w.CompletionTicks >= 0 ? $", пройдена за {w.CompletionTicks} тиков" : ""));
        var top = UnitStats.Where(u => u.DamageDealt > 0 || u.Healing > 0).OrderByDescending(u => u.DamageDealt + u.Healing).Take(8).ToList();
        if (top.Count > 0)
            sb.AppendLine("Лучшие юниты: " + string.Join("; ", top.Select(u => $"{u.Name}#{u.Id} урон {F(u.DamageDealt)} (DPS {u.Dps:0.##}), лечение {F(u.Healing)}, убийств {u.Kills}")));
        sb.AppendLine($"Схема данных v{SchemaVersion}, правила тика v{SimVersion}" + (ValidationErrors > 0 ? $", ошибок проверки {ValidationErrors}" : "") +
                      (TickMsMean > 0 ? $"; тик: в среднем {TickMsMean:0.###} мс, максимум {TickMsMax:0.##} мс" : ""));
        return sb.ToString();
    }

    public static string AiErrorName(string kind) => kind switch
    {
        "stuck" => "застрял",
        "no_path" => "нет пути",
        "unreachable" => "цель недостижима",
        "route_blocked" => "маршрут заблокирован",
        // The reactions to a block (ТЗ ч. 5, 11.3; §86): not errors, but counted with them.
        "detour" => "обход затора",
        "repath" => "новый путь",
        "switch_route" => "смена маршрута",
        "attack_blocker" => "атака мешающего",
        "aborted" => "отказ от цели",
        "route_only" => "только маршрут",
        _ => kind,
    };
}
