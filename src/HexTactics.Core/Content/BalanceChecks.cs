using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>
/// «Чрезмерно сильные» (docs/DECISIONS_4.md §70): warnings with the rule that fired — nothing is forbidden. High
/// resistance to several damage types, healing not less than the reference incoming damage, unbounded growth of
/// stacks, endless summoning, a multiplier from allies without a limit, a cheap strong skill, permanent
/// interruption, an unreachable speed, immunity to control together with high mobility.
/// </summary>
public static class BalanceChecks
{
    public static void Run(ContentDatabase db, List<ContentIssue> issues)
    {
        foreach (var u in db.All<UnitDef>())
        {
            if (u.Incomplete) continue;
            PowerReport r;
            try
            {
                r = PowerModel.Evaluate(db, u);
            }
            catch (Exception e)
            {
                issues.Add(new ContentIssue(IssueSeverity.Warning, $"{DefCategory.Unit}:{u.Id}", "Balance", $"Оценка силы не рассчитана: {e.Message}"));
                continue;
            }
            foreach (var (rule, msg) in Check(db, u, r))
                issues.Add(new ContentIssue(IssueSeverity.Warning, $"{DefCategory.Unit}:{u.Id}", "Balance", $"Чрезмерно сильный? [{rule}] {msg}"));
        }
    }

    /// <summary>The fired rules of one template: (rule, explanation).</summary>
    public static List<(string Rule, string Message)> Check(ContentDatabase db, UnitDef u, PowerReport? report = null)
    {
        var r = report ?? PowerModel.Evaluate(db, u);
        var list = new List<(string, string)>();
        double phys = r.EhpProfile.FirstOrDefault(e => e.Type == "physical")?.Ehp ?? r.Ehp;
        var resistant = r.EhpProfile.Where(e => e.Type != "physical" && e.Ehp >= 2.5 * Math.Max(1, Math.Min(phys, r.Ehp))).Select(e => e.Name).ToList();
        if (phys >= 2.5 * Math.Max(1, u.Stats.GetValueOrDefault(Stats.HpMax, 100))) resistant.Insert(0, "физический");
        if (resistant.Count >= 3)
            list.Add(("устойчивость", $"высокая устойчивость к нескольким типам урона: {string.Join(", ", resistant)} (эффективное здоровье ≥ 2,5× обычного)"));
        // «Эталонный входящий урон» — two reference attackers at once.
        double incoming = 2 * r.ReferenceDps;
        if (r.SelfHealPerSec > 0 && r.SelfHealPerSec >= incoming)
            list.Add(("бесконечное лечение", $"восстанавливает {r.SelfHealPerSec:0.#}/с — не меньше эталонного входящего урона {incoming:0.#}/с (двое эталонных)"));

        var skills = u.Skills.Append(u.BasicAttack).Select(id => db.Skill(id)).Where(s => s != null).Select(s => s!).Distinct().ToList();
        foreach (var s in skills)
        {
            foreach (var e in s.Stages.SelectMany(st => st.Effects))
            {
                if (e.Type == EffectType.ApplyStatus && db.Status(e.Status) is { Reapply: ReapplyPolicy.Stack, MaxStacks: >= 50 } st)
                    list.Add(("рост стаков", $"«{s.DisplayName}» накладывает «{st.DisplayName}» до {st.MaxStacks} стаков"));
                if (e.Type == EffectType.Summon && s.Cooldown <= 1)
                    list.Add(("бесконечный призыв", $"«{s.DisplayName}» призывает без перезарядки (≤ 1 с)"));
                if (e.Type == EffectType.ApplyStatus && db.Status(e.Status) is { Kind: StatusKind.Timed } cs && cs.Levels.Any(l => l.Flags.BlocksActions || l.Flags.InterruptsCast))
                {
                    double dur = e.Duration > 0 ? e.Duration : cs.Duration;
                    double cyc = Math.Max(s.Cooldown, s.PrepTime + s.RecoveryTime);
                    if (cyc > 0 && dur >= 0.8 * cyc && e.Chance >= 0.5)
                        list.Add(("постоянное прерывание", $"«{s.DisplayName}»: «{cs.DisplayName}» {dur:0.#} с при цикле {cyc:0.#} с"));
                }
            }
        }
        var basic = db.Skill(u.BasicAttack);
        double Dmg(SkillDef s) => s.Stages.Sum(st => Math.Max(1, st.Repeat) * st.Effects.Where(e => e.Type == EffectType.Damage).Sum(e => e.Amount * st.DamageMultiplier + st.FlatBonus));
        if (basic != null && Dmg(basic) > 0)
            foreach (var s in skills.Where(s => s != basic && s.ApCost <= basic.ApCost && s.Cooldown < 3 && Dmg(s) >= 4 * Dmg(basic)))
                list.Add(("дешёвый сильный навык", $"«{s.DisplayName}»: урон {Dmg(s):0} при цене {s.ApCost:0.#} ОД и перезарядке {s.Cooldown:0.#} с (обычная атака {Dmg(basic):0})"));

        foreach (var pid in u.Passives.Concat(CategoryTree.Resolve(db, u).Of(CategoryRuleKind.Passive).Select(x => x.Rule.Passive)).Distinct())
        {
            var p = db.Get<PassiveDef>(pid);
            if (p == null) continue;
            foreach (var m in p.Modifiers.Where(m => m.PerEach != null && m.PerEach.MaxValue <= 0 && m.PerEach.MaxCount <= 0))
                list.Add(("множитель от союзников", $"«{p.DisplayName}»: «за каждого» без предела величины и количества"));
            if (p.Stacking == PassiveStacking.Stack && p.MaxStacks >= 50)
                list.Add(("рост стаков", $"«{p.DisplayName}»: до {p.MaxStacks} стаков"));
        }

        if (r.ReferenceSpeed > 0 && r.Speed >= 3 * r.ReferenceSpeed)
            list.Add(("скорость", $"скорость {r.Speed:0.#} — в {r.Speed / r.ReferenceSpeed:0.#} раза выше эталона: защитники не успеют перехватить"));
        double cr = u.Stats.GetValueOrDefault(Stats.ControlResist, 0);
        if (cr >= 0.8 && r.M >= 1.5)
            list.Add(("неуязвимость к контролю", $"устойчивость к контролю {cr:P0} и высокая мобильность (×{r.M:0.#})"));
        return list.Distinct().ToList();
    }
}
