using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>The threat of a template: the base cost and its context in a scene (docs/DECISIONS_4.md §67, TZ §15).</summary>
public sealed class ThreatReport
{
    public string Unit { get; init; } = "";
    /// <summary>The base cost (no scene).</summary>
    public double Base { get; set; }
    /// <summary>On the scene's map (its routes, narrow places); null — no scene.</summary>
    public double? InMap { get; set; }
    /// <summary>Against the scene's defenders (their damage, their reach into the air); null — no defenders.</summary>
    public double? VsDefenders { get; set; }
    /// <summary>On the given route; null — none given.</summary>
    public double? OnRoute { get; set; }
    /// <summary>What the budget of a wave uses: the full context when the scene has enough data, else the base.</summary>
    public double Effective { get; set; }
    public List<PowerLine> Factors { get; } = new();
    public bool AuthorValue { get; set; }

    public string ToText() =>
        (AuthorValue ? $"Стоимость угрозы задана автором: {Base:0.#}" : $"Стоимость угрозы: базовая {Base:0.#}")
        + (InMap is { } m ? $", на карте {m:0.#}" : "") + (VsDefenders is { } d ? $", против защитников {d:0.#}" : "") + (OnRoute is { } r ? $", на маршруте {r:0.#}" : "")
        + "\n" + string.Join("\n", Factors.Select(f => $"  {f.Label}: ×{f.Value:0.##}" + (f.Detail != "" ? $" — {f.Detail}" : "")));
}

/// <summary>
/// The threat model (docs/DECISIONS_4.md §67): how dangerous a template is for a TD scene, apart from its level —
/// how long it survives the defenders, how fast it walks the route, its damage to the base, whether it bypasses
/// blockers (flying, burrowing), resists control, helps its group, attacks the defenders. The reference costs the
/// project's «стоимость угрозы эталона». In a scene: the length of its routes and their narrow places, the damage
/// types of the defenders and whether they can reach a flyer.
/// </summary>
public static class ThreatModel
{
    private static double Half(double v) => Math.Max(0.5, Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2);

    private static bool Flies(PowerReport r) => r.MoveCategories.Contains("fly");

    public static double Base(ContentDatabase db, UnitDef u) => Evaluate(db, u).Base;

    public static ThreatReport Evaluate(ContentDatabase db, UnitDef u, ScenarioDef? sc = null, string route = "")
    {
        var rep = new ThreatReport { Unit = u.Id };
        var p = PowerModel.Evaluate(db, u);
        var refDef = PowerModel.ReferenceDef(db);
        double baseDmg = u.Stats.TryGetValue(Stats.BaseDamage, out var bd) ? bd : db.Stat(Stats.BaseDamage)?.Default ?? 25;
        double refBase = refDef.Stats.TryGetValue(Stats.BaseDamage, out var rb) ? rb : db.Stat(Stats.BaseDamage)?.Default ?? 25;
        double survive = Math.Pow(Math.Max(0.05, p.H), 0.6);
        double speed = Math.Pow(Math.Max(0.1, p.Speed / Math.Max(0.1, p.ReferenceSpeed)), 0.5);
        double attack = Math.Pow(0.6 + 0.4 * Math.Max(0, p.D), 0.5);
        double toBase = Math.Clamp(1 + 0.4 * (baseDmg / Math.Max(1, refBase) - 1), 0.4, 3);
        double bypass = Flies(p) ? 1.3 : p.MoveCategories.Contains("burrow") ? 1.2 : p.MoveCategories.Contains("jump") ? 1.1 : 1;
        double cr = u.Stats.TryGetValue(Stats.ControlResist, out var c) ? Math.Clamp(c, 0, 1) : 0;
        double group = 1 + 0.5 * PowerModel.Evaluate(db, u, PowerContext.Typical(db)).S;
        double size = u.Stats.TryGetValue(Stats.Size, out var sz) && sz > 1 ? 1.1 : 1;
        double rank = u.Rank == UnitRank.Boss || u.Boss.IsBoss ? 1.25 : 1;
        rep.Factors.Add(new PowerLine("survive", "Живучесть против защитников", survive, $"эфф. здоровье ×{p.H:0.##} к эталону"));
        rep.Factors.Add(new PowerLine("speed", "Скорость прохождения маршрута", speed, $"скорость {p.Speed:0.#}"));
        rep.Factors.Add(new PowerLine("attack", "Атака защитников", attack, $"урон ×{p.D:0.##}"));
        rep.Factors.Add(new PowerLine("base", "Урон по базе", toBase, $"{baseDmg:0} (эталон {refBase:0})"));
        if (bypass != 1) rep.Factors.Add(new PowerLine("bypass", "Обход препятствий и блокирующих", bypass, string.Join("/", p.MoveCategories)));
        if (cr > 0) rep.Factors.Add(new PowerLine("control", "Устойчивость к контролю", 1 + 0.3 * cr, $"{cr:P0}"));
        if (group > 1.001) rep.Factors.Add(new PowerLine("group", "Помощь группе (лечение, ауры, призыв)", group, ""));
        if (size > 1) rep.Factors.Add(new PowerLine("size", "Размер (занимает узкие места)", size, ""));
        if (rank > 1) rep.Factors.Add(new PowerLine("rank", "Босс", rank, ""));
        double t = survive * speed * attack * toBase * bypass * (1 + 0.3 * cr) * group * size * rank;
        double cost = db.Rules.Power.ReferenceThreat * t;
        if (u.ThreatCost > 0)
        {
            rep.AuthorValue = true;
            rep.Base = rep.Effective = u.ThreatCost;
            return rep;
        }
        rep.Base = Half(cost);
        rep.Effective = rep.Base;
        if (sc == null) return rep;

        var map = db.Get<MapDef>(sc.Map);
        double ctxCost = cost;
        var routes = sc.Routes.Where(r => r.Nodes.Count > 0 && (route == "" || r.Id == route)).ToList();
        if (map != null && routes.Count > 0)
        {
            double len = routes.Average(r => Math.Max(1, r.MainPath().Count - 1));
            double lenF = Math.Clamp(Math.Pow(len / 12.0, -0.25), 0.6, 1.4);
            double narrow = Flies(p) ? 0 : routes.Average(r => NarrowShare(db, map, r.MainPath()));
            double chokeF = 1 - 0.3 * narrow;
            ctxCost *= lenF * chokeF;
            rep.Factors.Add(new PowerLine("route", route == "" ? "Длина маршрутов сцены" : "Длина маршрута", lenF, $"{len:0} гекс. (эталон 12)"));
            if (narrow > 0) rep.Factors.Add(new PowerLine("narrow", "Узкие места на пути", chokeF, $"{narrow:P0} гексов узкие — блокировать проще"));
            if (route == "") rep.InMap = Half(ctxCost);
            else rep.OnRoute = Half(ctxCost);
        }
        var defenders = Defenders(db, sc);
        if (defenders.Count > 0)
        {
            // Effective health against the damage of the defenders instead of the project's mix.
            var mix = new Dictionary<string, double>(StringComparer.Ordinal);
            bool reachAir = false;
            foreach (var d in defenders)
                foreach (var sid in d.Skills.Append(d.BasicAttack))
                    if (db.Skill(sid) is { } s)
                    {
                        if (s.RangeMax >= 2) reachAir = true;
                        foreach (var e in s.Stages.SelectMany(st => st.Effects.Where(e => e.Type == EffectType.Damage).Select(e => st.DamageType != "" ? st.DamageType : e.DamageType)))
                            mix[e] = mix.GetValueOrDefault(e) + 1;
                    }
            double total = mix.Values.Sum();
            if (total > 0 && p.EhpProfile.Count > 0)
            {
                double phys = p.EhpProfile.FirstOrDefault(e => e.Type == "physical")?.Ehp ?? p.Ehp;
                double inv = 0;
                foreach (var (type, n) in mix)
                {
                    var line = p.EhpProfile.FirstOrDefault(e => e.Type == type) ?? p.EhpProfile.FirstOrDefault(e => db.DamageType(type)?.Parent is { } par && e.Type == par);
                    inv += n / total / Math.Max(1, line?.Ehp ?? phys);
                }
                double ehpVs = 1 / Math.Max(1e-9, inv);
                double f = Math.Clamp(Math.Pow(ehpVs / Math.Max(1, p.Ehp), 0.6), 0.3, 3);
                ctxCost *= f;
                rep.Factors.Add(new PowerLine("defenders", "Устойчивость к урону защитников", f, $"эфф. здоровье против их урона {ehpVs:0} (обычно {p.Ehp:0})"));
            }
            if (Flies(p))
            {
                double air = reachAir ? 0.9 : 1.5;
                ctxCost *= air;
                rep.Factors.Add(new PowerLine("air", reachAir ? "Защитники бьют по летающим" : "Защитникам нечем достать летающего", air, ""));
            }
            rep.VsDefenders = Half(ctxCost);
        }
        if (rep.InMap != null || rep.OnRoute != null || rep.VsDefenders != null) rep.Effective = Half(ctxCost);
        return rep;
    }

    /// <summary>The scene has data for a contextual cost: a drawn route and at least one defender.</summary>
    public static bool HasContext(ScenarioDef sc) => sc.Routes.Any(r => r.Nodes.Count > 0) && (sc.Units.Count > 0 || sc.Placement.Preset.Count > 0 || sc.Placement.Units.Count > 0);

    /// <summary>The defenders of a scene: its units of the player's side, the preset placement, the placeable templates.</summary>
    public static List<UnitDef> Defenders(ContentDatabase db, ScenarioDef sc)
    {
        var player = sc.Sides.Where(s => s.Controller == SideController.Player).Select(s => s.Faction).ToHashSet();
        var list = new List<UnitDef>();
        foreach (var su in sc.Units)
            if (db.Unit(su.Unit) is { } d && player.Contains(su.Faction != "" ? su.Faction : d.Faction))
                list.Add(d);
        foreach (var su in sc.Placement.Preset)
            if (db.Unit(su.Unit) is { } d)
                list.Add(d);
        if (list.Count == 0)
            foreach (var e in sc.Placement.Units)
                if (db.Unit(e.Unit) is { } d)
                    list.Add(d);
        return list;
    }

    /// <summary>The share of the hexes of a path that are narrow (two impassable opposite neighbours).</summary>
    private static double NarrowShare(ContentDatabase db, MapDef map, List<HexCoord> path)
    {
        if (path.Count == 0) return 0;
        var cells = map.Cells.ToDictionary(c => c.C);
        bool Blocked(HexCoord c) => !cells.TryGetValue(c, out var cell) || cell.Blocked == true || db.Terrain(cell.T) is { } t && !t.IsPassableFor("walk");
        int narrow = 0;
        foreach (var h in path)
            for (int d = 0; d < 3; d++)
                if (Blocked(h.Neighbor((HexDirection)d)) && Blocked(h.Neighbor((HexDirection)(d + 3))))
                {
                    narrow++;
                    break;
                }
        return narrow / (double)path.Count;
    }
}
