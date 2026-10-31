using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>
/// The checks of a description of a summon (ТЗ ч. 6, 20.5; docs/DECISIONS_6.md §106): references, the pattern against the count,
/// the end of life, the limits — an unlimited life of an unkillable or unlimited summoned risks an endless scenario — and chains.
/// </summary>
public static class SummonChecks
{
    /// <summary>The hexes the pattern offers (the caster at the centre, looking east).</summary>
    public static int PatternSize(SummonDef d) => SummonSystem.PatternOf(d, default, HexDirection.E, new HexCoord(3, 0), new HexCoord(3, 0)).Count;

    /// <summary>A unit nothing can kill: huge health or the tag «invulnerable».</summary>
    public static bool Unkillable(UnitDef u) => u.Stats.TryGetValue(Stats.HpMax, out var hp) && hp >= 100000 || u.Tags.Contains("invulnerable");

    public static List<ScaleProblem> Check(ContentDatabase db, SummonDef d)
    {
        var list = new List<ScaleProblem>();
        void Err(string path, string text) => list.Add(new ScaleProblem(true, path, text));
        void Warn(string path, string text) => list.Add(new ScaleProblem(false, path, text));
        var unit = db.Unit(d.Unit);
        if (unit == null) Err(nameof(d.Unit), d.Unit == "" ? "Не выбран призываемый юнит" : $"Нет юнита «{d.Unit}»");
        if (d.Count < 1) Err(nameof(d.Count), "Количество должно быть не меньше 1");
        if (d.Placement == SummonPlacement.Offsets && d.Offsets.Count == 0) Err(nameof(d.Offsets), "Расстановка «заданные смещения» без смещений");
        else if (PatternSize(d) < d.Count)
            Warn(nameof(d.Placement), $"Гексов расстановки {PatternSize(d)} меньше количества {d.Count} — остальные по правилу «место занято» ({EdUtil.EnumLabel(d.OnBlocked).ToLowerInvariant()})");
        if (d.Faction == SummonFaction.Fixed && (d.FixedFaction == "" || db.Get<FactionDef>(d.FixedFaction) == null))
            Err(nameof(d.FixedFaction), d.FixedFaction == "" ? "Сторона «заданная фракция», но фракция не выбрана" : $"Нет фракции «{d.FixedFaction}»");
        if (d.Despawn is SummonEnd.ByRules or SummonEnd.Keep)
            Err(nameof(d.Despawn), "«По окончании» — нужно действие: исчезает, гибнет, нейтрализуется, становится независимым или передаётся");
        foreach (var s in d.InheritStats)
            if (s.Stat == "" || db.Stat(s.Stat) == null) Err(nameof(d.InheritStats), $"Нет характеристики «{s.Stat}» для наследования");
        if (d.AiProfile != "" && db.Get<AiProfileDef>(d.AiProfile) == null) Err(nameof(d.AiProfile), $"Нет профиля ИИ «{d.AiProfile}»");
        foreach (var t in d.Terrains)
            if (db.Get<TerrainDef>(t) == null) Err(nameof(d.Terrains), $"Нет местности «{t}»");
        if (d.Behavior == SummonBehavior.PatrolSimple && d.Patrol.Count == 0) Warn(nameof(d.Patrol), "Патруль без точек — призванный будет держать позицию");
        if (d.AllowSubSummon && d.MaxDepth < 2) Warn(nameof(d.MaxDepth), "«Призванные могут призывать» при глубине 1 ничего не даёт");
        if (d.MaxDepth > 3) Warn(nameof(d.MaxDepth), $"Глубина цепочки {d.MaxDepth}: число призванных растёт как степень — задайте лимиты");
        // ТЗ 20.5: an unlimited life needs limits and a way to die.
        bool noLimits = d.MaxPerSummoner == 0 && d.MaxPerTeam == 0 && d.MaxPerScenario == 0 && d.MaxTotal == 0;
        bool outlives = d.Lifetime == SummonLifetime.Unlimited
                        && d.OnSummonerDeath is SummonEnd.Keep or SummonEnd.BecomeIndependent or SummonEnd.TransferControl or SummonEnd.Neutralize
                        || d.Lifetime == SummonLifetime.Unlimited && d.OnSummonerDeath == SummonEnd.ByRules && !db.Rules.Death.SummonsDieWithOwner;
        if (d.Lifetime == SummonLifetime.Unlimited && noLimits) Warn(nameof(d.MaxPerSummoner), "Время жизни без ограничения и без лимитов живых — число призванных не ограничено");
        if (unit != null && Unkillable(unit) && d.Lifetime == SummonLifetime.Unlimited)
            Warn(nameof(d.Lifetime), $"«{unit.DisplayName}» неуничтожаем, а время жизни без ограничения — волна или сценарий могут не закончиться");
        else if (outlives) Warn(nameof(d.OnSummonerDeath), "Призванные без ограничения жизни переживают призывателя — проверьте, что сценарий может закончиться");
        // A chain: the summoned unit has a skill summoning by a description that lets it summon again.
        if (unit != null && d.AllowSubSummon && d.MaxTotal == 0)
            foreach (var sk in unit.Skills.Select(db.Skill).Where(s => s != null))
                foreach (var e in sk!.Stages.SelectMany(st => st.Effects).Where(e => e.Type == EffectType.Summon && e.Summon != ""))
                    if (db.Get<SummonDef>(e.Summon) is { AllowSubSummon: true })
                        Warn(nameof(d.MaxTotal), $"Цепочка: «{unit.DisplayName}» сам призывает («{e.Summon}») — задайте «всего за бой»");
        if (d.Lifetime == SummonLifetime.UntilSkillEnd && !db.All<SkillDef>().Any(s => s.Stages.Count > 1 && s.Stages.SelectMany(st => st.Effects).Any(e => e.Summon == d.Id)))
            Warn(nameof(d.Lifetime), "«До конца навыка»: у навыка в один этап призванные исчезнут сразу после применения");
        if (d.DeployCost > 0) Warn(nameof(d.DeployCost), "Цена в очках развёртывания действует только в сценариях с вызовом за ОР");
        return list;
    }

    /// <summary>A short summary of the description (the editor's preview).</summary>
    public static string Summary(ContentDatabase db, SummonDef d)
    {
        var unit = db.Unit(d.Unit);
        string life = d.Lifetime == SummonLifetime.Duration ? $"{d.Duration:0.#} с" : EdUtil.EnumLabel(d.Lifetime).ToLowerInvariant();
        var limits = new List<string>();
        if (d.MaxPerSummoner > 0) limits.Add($"у призывателя ≤ {d.MaxPerSummoner}");
        if (d.MaxPerTeam > 0) limits.Add($"у стороны ≤ {d.MaxPerTeam}");
        if (d.MaxPerScenario > 0) limits.Add($"в сценарии ≤ {d.MaxPerScenario}");
        if (d.MaxTotal > 0) limits.Add($"за бой ≤ {d.MaxTotal}");
        return $"{unit?.DisplayName ?? d.Unit} ×{d.Count} · {EdUtil.EnumLabel(d.Placement).ToLowerInvariant()} (гексов {PatternSize(d)}) · живёт: {life}, затем {EdUtil.EnumLabel(d.Despawn).ToLowerInvariant()}\n" +
               $"Гибель призывателя: {EdUtil.EnumLabel(d.OnSummonerDeath).ToLowerInvariant()} · ИИ: {EdUtil.EnumLabel(d.Behavior).ToLowerInvariant()} (запасное — {EdUtil.EnumLabel(d.Fallback).ToLowerInvariant()}, " +
               $"после призывателя — {EdUtil.EnumLabel(d.AfterSummoner).ToLowerInvariant()})\n" +
               $"Лимиты: {(limits.Count == 0 ? "нет" : string.Join(", ", limits))}; достигнут — {EdUtil.EnumLabel(d.OnLimit).ToLowerInvariant()}; глубина {d.MaxDepth}" +
               (d.AllowSubSummon ? ", призванные призывают" : "") + $"; место занято — {EdUtil.EnumLabel(d.OnBlocked).ToLowerInvariant()}" + (d.Refund ? " с возвратом" : "");
    }
}
