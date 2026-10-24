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
public sealed class BattleReport
{
    [JsonIgnore] private Simulation? _sim;

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

    public const string PlayerSide = "игрок";
    public const string EnemySide = "враги";
    public const string OtherSide = "прочие";

    /// <summary>Starts watching a simulation (before its first tick).</summary>
    public static BattleReport Attach(Simulation sim, string scenario = "", ulong seed = 0)
    {
        var r = new BattleReport { _sim = sim, Scenario = scenario, Seed = seed };
        sim.Observed += r.OnEvent;
        sim.Damage.Resolved += r.OnDamage;
        sim.Stepped += r.OnTick;
        return r;
    }

    public void Detach()
    {
        if (_sim == null) return;
        _sim.Observed -= OnEvent;
        _sim.Damage.Resolved -= OnDamage;
        _sim.Stepped -= OnTick;
    }

    private string SideOf(Unit? u)
    {
        if (u == null || _sim == null) return OtherSide;
        if (_sim.IsPlayerFaction(u.FactionId) || _sim.IsPlayerFaction(u.BaseFactionId)) return PlayerSide;
        return _sim.Options.AiFactions.Contains(u.FactionId) ? EnemySide : OtherSide;
    }

    private static void Add<TK>(Dictionary<TK, double> d, TK k, double v) where TK : notnull => d[k] = d.GetValueOrDefault(k) + v;
    private static void Inc<TK>(Dictionary<TK, int> d, TK k) where TK : notnull => d[k] = d.GetValueOrDefault(k) + 1;

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
                break;
            }
            case SimEventType.Heal when e.Value > 0:
                Healing += e.Value;
                Heals++;
                break;
            case SimEventType.CastReleased when e.DamageType != "":
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
                break;
            case SimEventType.GoalReached:
            {
                var u = sim.GetUnit(e.SourceUnit);
                if (u != null && SideOf(u) == EnemySide) EnemiesReachedGoal++;
                break;
            }
            case SimEventType.Death:
            {
                var dead = sim.GetUnit(e.TargetUnit);
                if (dead == null || dead.DeathCause == DeathCause.Removed && dead.Hp > 0) break;
                var killer = sim.GetUnit(e.SourceUnit);
                string side = SideOf(dead);
                if (side == PlayerSide && !dead.IsStructure) DefendersLost++;
                if (side == EnemySide && !dead.IsStructure && dead.DeathCause != DeathCause.Removed)
                {
                    EnemiesKilled++;
                    if (killer != null && SideOf(killer) == PlayerSide) EnemiesKilledByDefenders++;
                }
                break;
            }
            case SimEventType.StructureDestroyed:
                FailureReasons.Add(e.Text);
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
        if (r.Missed) return;
        Shielded += r.Shielded;
        Absorbed += r.Absorbed;
        if (q.Skill != null) Stat(q.Skill.Id).Hits++;
    }

    private void OnTick()
    {
        var sim = _sim!;
        int blocked = 0;
        foreach (var u in sim.Units)
        {
            if (!u.Alive || u.IsStructure || SideOf(u) != EnemySide) continue;
            if (u.Ai.BlockedSince >= 0 || sim.Blocking.BlockerOf(u) != null) blocked++;
        }
        RouteBlockedSeconds += blocked * sim.Dt;
    }

    /// <summary>Fixes the final values (outcome, waves, bases) — call when the run stops.</summary>
    public BattleReport Finish(string stopReason = "")
    {
        var sim = _sim!;
        Detach();
        Outcome = sim.Outcome switch { BattleOutcome.Victory => "победа", BattleOutcome.Defeat => "поражение", _ => "не завершён" };
        Reason = sim.OutcomeText;
        StopReason = stopReason != "" ? stopReason : sim.Outcome != BattleOutcome.None ? "бой завершён" : "";
        WavesTotal = sim.Scenario.Waves.Count;
        // A wave whose last enemy fell on the final tick is complete even if the end of the battle came first.
        WavesCompleted = sim.Scenario.Waves.Count(w => w.State == WaveState.Cleared
            || w.State == WaveState.Active && w.Planned.All(p => p.Done) && w.UnitIds.All(id => sim.GetUnit(id)?.Alive != true));
        Seconds = Math.Round(sim.Scenario.BattleTime, 2);
        Ticks = sim.Tick;
        EventChainCuts = sim.Scenario.EventChainCuts + sim.Scenario.TriggerCycleCuts;
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
        sb.AppendLine($"Блокировка маршрутов: {F(RouteBlockedSeconds)} враго-с; смен цели: {TargetChanges}");
        sb.AppendLine("Ошибки ИИ: " + (AiErrors.Count == 0 ? "нет" : string.Join(", ", AiErrors.Select(k => $"{AiErrorName(k.Key)} ×{k.Value}"))));
        if (FailureReasons.Count > 0) sb.AppendLine("Причины провала/потерь: " + string.Join("; ", FailureReasons));
        if (EventChainCuts > 0) sb.AppendLine($"Оборвано цепочек событий: {EventChainCuts}");
        return sb.ToString();
    }

    public static string AiErrorName(string kind) => kind switch
    {
        "stuck" => "застрял",
        "no_path" => "нет пути",
        "unreachable" => "цель недостижима",
        "route_blocked" => "маршрут заблокирован",
        _ => kind,
    };
}
