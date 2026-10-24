using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>One row of a comparison: the section, what, the value of the first and of the second version.</summary>
public sealed record CompareLine(string Section, string Label, string A, string B)
{
    public bool Changed => A != B;
}

/// <summary>A trial battle of a template against reference units: wins, duration, health left, damage dealt.</summary>
public sealed class DuelResult
{
    public int Runs { get; init; }
    public int Opponents { get; init; }
    public string Opponent { get; init; } = "";
    public int Wins { get; set; }
    public double Seconds { get; set; }
    public double HpLeft { get; set; }
    public double Damage { get; set; }

    public string ToText() =>
        $"против {Opponents}× «{Opponent}», {Runs} прогонов: побед {Wins} ({(double)Wins / Math.Max(1, Runs):P0}), бой {Seconds:0.#} с, осталось здоровья {HpLeft:P0}, урон {Damage:0}";
}

public sealed class VersionComparison
{
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    public List<CompareLine> Lines { get; } = new();
    public DuelResult? DuelA { get; set; }
    public DuelResult? DuelB { get; set; }
    /// <summary>What the trial battles say about the change (stronger, longer, the same).</summary>
    public string Verdict { get; set; } = "";

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Сравнение: {A} → {B}");
        foreach (var g in Lines.GroupBy(l => l.Section))
        {
            sb.AppendLine(g.Key + ":");
            foreach (var l in g) sb.AppendLine($"  {(l.Changed ? "≠" : "=")} {l.Label}: {l.A}" + (l.Changed ? $" → {l.B}" : ""));
        }
        if (DuelA != null && DuelB != null)
        {
            sb.AppendLine($"Пробный бой {A}: {DuelA.ToText()}");
            sb.AppendLine($"Пробный бой {B}: {DuelB.ToText()}");
        }
        if (Verdict != "") sb.AppendLine("Итог: " + Verdict);
        return sb.ToString();
    }
}

/// <summary>
/// «Сравнение версий» (docs/DECISIONS_4.md §70, TZ §18.4): characteristics, level, threat, skills, passives, effective
/// health by damage type (resistances), role — and the trial battles of both versions against the same reference
/// units, so the author sees whether more health made the unit stronger or only made the fight longer.
/// </summary>
public static class VersionCompare
{
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static VersionComparison Compare(ContentDatabase db, UnitDef a, UnitDef b, string labelA, string labelB, int duelRuns = 0, ulong seed = 1)
    {
        var c = new VersionComparison { A = labelA, B = labelB };
        foreach (var key in a.Stats.Keys.Union(b.Stats.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            string name = db.Stat(key)?.DisplayName ?? key;
            c.Lines.Add(new CompareLine("Характеристики", name, a.Stats.TryGetValue(key, out var va) ? F(va) : "—", b.Stats.TryGetValue(key, out var vb) ? F(vb) : "—"));
        }
        var pa = PowerModel.Evaluate(db, a);
        var pb = PowerModel.Evaluate(db, b);
        c.Lines.Add(new CompareLine("Оценка", "Уровень", pa.LevelText, pb.LevelText));
        c.Lines.Add(new CompareLine("Оценка", "Сила к эталону", pa.Power.ToString("0.000", CultureInfo.InvariantCulture), pb.Power.ToString("0.000", CultureInfo.InvariantCulture)));
        c.Lines.Add(new CompareLine("Оценка", "Роль", EdUtil.EnumLabel(pa.Role), EdUtil.EnumLabel(pb.Role)));
        c.Lines.Add(new CompareLine("Оценка", "Ранг", EdUtil.EnumLabel(a.Rank), EdUtil.EnumLabel(b.Rank)));
        c.Lines.Add(new CompareLine("Оценка", "Стоимость угрозы", F(ThreatCost.Of(db, a)), F(ThreatCost.Of(db, b))));
        foreach (var key in new[] { "health", "damage", "range", "passives", "control", "synergy" })
        {
            var la = pa.Lines.FirstOrDefault(l => l.Key == key);
            var lb = pb.Lines.FirstOrDefault(l => l.Key == key);
            if (la != null || lb != null) c.Lines.Add(new CompareLine("Оценка", "Вклад: " + (la ?? lb)!.Label, la == null ? "—" : F(la.Value), lb == null ? "—" : F(lb.Value)));
        }
        string Names(IEnumerable<string> ids, Func<string, string?> name) => string.Join(", ", ids.Where(i => i != "").Select(i => name(i) ?? i)) is { Length: > 0 } s ? s : "—";
        c.Lines.Add(new CompareLine("Навыки", "Обычная атака", Names(new[] { a.BasicAttack }, i => db.Skill(i)?.DisplayName), Names(new[] { b.BasicAttack }, i => db.Skill(i)?.DisplayName)));
        c.Lines.Add(new CompareLine("Навыки", "Навыки", Names(a.Skills, i => db.Skill(i)?.DisplayName), Names(b.Skills, i => db.Skill(i)?.DisplayName)));
        c.Lines.Add(new CompareLine("Пассивы", "Пассивы", Names(a.Passives, i => db.Get<PassiveDef>(i)?.DisplayName), Names(b.Passives, i => db.Get<PassiveDef>(i)?.DisplayName)));
        c.Lines.Add(new CompareLine("Пассивы", "Категории", Names(new[] { a.MainCategory }.Concat(a.Origins).Distinct(), i => db.Get<OriginDef>(i)?.DisplayName),
            Names(new[] { b.MainCategory }.Concat(b.Origins).Distinct(), i => db.Get<OriginDef>(i)?.DisplayName)));
        foreach (var t in pa.EhpProfile.Select(e => e.Type).Union(pb.EhpProfile.Select(e => e.Type)))
        {
            var ea = pa.EhpProfile.FirstOrDefault(e => e.Type == t);
            var eb = pb.EhpProfile.FirstOrDefault(e => e.Type == t);
            c.Lines.Add(new CompareLine("Сопротивления", "Эфф. здоровье: " + (ea ?? eb)!.Name, ea == null ? "—" : $"{ea.Ehp:0} (×{F(ea.Ratio)})", eb == null ? "—" : $"{eb.Ehp:0} (×{F(eb.Ratio)})"));
        }
        if (duelRuns > 0)
        {
            int n = Math.Clamp((int)Math.Round(pa.Power), 1, 6);
            c.DuelA = Duel(db, a, n, duelRuns, seed);
            c.DuelB = Duel(db, b, n, duelRuns, seed);
            c.Verdict = Verdict(c.DuelA, c.DuelB, pa, pb);
        }
        return c;
    }

    private static string Verdict(DuelResult a, DuelResult b, PowerReport pa, PowerReport pb)
    {
        double wa = (double)a.Wins / Math.Max(1, a.Runs), wb = (double)b.Wins / Math.Max(1, b.Runs);
        var parts = new List<string>();
        if (wb > wa + 0.1) parts.Add($"вторая версия сильнее: побед {wa:P0} → {wb:P0}");
        else if (wb < wa - 0.1) parts.Add($"вторая версия слабее: побед {wa:P0} → {wb:P0}");
        else if (b.HpLeft > a.HpLeft + 0.1) parts.Add($"победы те же, но остаётся больше здоровья ({a.HpLeft:P0} → {b.HpLeft:P0})");
        else if (b.HpLeft < a.HpLeft - 0.1) parts.Add($"победы те же, но остаётся меньше здоровья ({a.HpLeft:P0} → {b.HpLeft:P0})");
        else parts.Add("исход боя почти не изменился");
        if (Math.Abs(b.Seconds - a.Seconds) > 0.2 * Math.Max(1, a.Seconds))
            parts.Add($"бой {(b.Seconds > a.Seconds ? "дольше" : "короче")}: {a.Seconds:0.#} → {b.Seconds:0.#} с");
        parts.Add($"оценка уровня {pa.Level:0.00} → {pb.Level:0.00}");
        return string.Join("; ", parts);
    }

    /// <summary>The opponent of the trial battles: the project's reference unit, else the weakest template of another faction.</summary>
    public static UnitDef? Opponent(ContentDatabase db, UnitDef u)
    {
        if (db.Unit(db.Rules.Power.ReferenceUnit) is { } r && r.Id != u.Id) return r;
        return db.All<UnitDef>().Where(x => x.Id != u.Id && !x.Incomplete && x.Stats.GetValueOrDefault(Stats.MoveSpeed, 1) > 0 && db.Skill(x.BasicAttack) != null)
            .OrderBy(x => PowerModel.Evaluate(db, x).Power).FirstOrDefault();
    }

    /// <summary>
    /// <paramref name="runs"/> trial battles (seeds seed … seed + runs − 1) of the template against
    /// <paramref name="opponents"/> reference units on an open hexagon, both sides driven by their AI, 90 s each.
    /// </summary>
    public static DuelResult Duel(ContentDatabase db, UnitDef u, int opponents, int runs, ulong seed = 1)
    {
        var opp = Opponent(db, u);
        var res = new DuelResult { Runs = runs, Opponents = opponents, Opponent = opp?.DisplayName ?? "—" };
        if (opp == null) return res;
        string terrain = db.Ids(DefCategory.Terrain).FirstOrDefault(t => db.Terrain(t)?.IsPassableFor("walk") == true) ?? db.Ids(DefCategory.Terrain).First();
        var map = MapDef.CreateHexagon("__duel", 5, terrain);
        string profile = db.Get<AiProfileDef>("ai_guard") != null ? "ai_guard" : "";
        double seconds = 0, hp = 0, dmg = 0;
        for (int i = 0; i < runs; i++)
        {
            var sim = new Simulation(db, map, new SimOptions
            {
                Seed = seed + (ulong)i, FogOfWar = false, AutoPlayer = true, DefaultPlayerProfile = profile,
                PlayerFactions = new() { "player" }, AiFactions = new() { "enemy" },
            });
            var me = sim.Spawn(u, "player", new HexCoord(-3, 0), HexDirection.E);
            var them = new List<Unit>();
            var cells = new[] { new HexCoord(3, 0), new HexCoord(3, -1), new HexCoord(2, 1), new HexCoord(4, -1), new HexCoord(2, 0), new HexCoord(4, 0) };
            for (int k = 0; k < opponents; k++) them.Add(sim.Spawn(opp, "enemy", cells[k % cells.Length], HexDirection.W));
            double hp0 = Math.Max(1, me.Hp);
            long limit = sim.TicksFor(90);
            double dealt = 0;
            sim.Observed += e =>
            {
                if (e.Type == SimEventType.Damage && e.SourceUnit == me.Id && e.Value > 0) dealt += e.Value;
            };
            while (sim.Tick < limit && me.Alive && them.Any(t => t.Alive)) sim.Step();
            if (me.Alive && them.All(t => !t.Alive)) res.Wins++;
            seconds += sim.Tick * sim.Dt;
            hp += me.Alive ? Math.Max(0, me.Hp) / hp0 : 0;
            dmg += dealt;
        }
        res.Seconds = Math.Round(seconds / Math.Max(1, runs), 1);
        res.HpLeft = hp / Math.Max(1, runs);
        res.Damage = Math.Round(dmg / Math.Max(1, runs));
        return res;
    }
}
