using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

public enum MassStatMode
{
    [Ed("Задать значение")] Set,
    [Ed("Умножить")] Multiply,
    [Ed("Прибавить")] Add,
}

/// <summary>
/// Mass changes of templates (docs/DECISIONS_4.md §69, TZ §22 этап 7): a characteristic, a category, a rank, an AI
/// profile, a fit to a level — for every selected template at once. The editor makes a restore point first and
/// commits the whole change as one operation of the history (one Ctrl+Z).
/// </summary>
public static class MassEdit
{
    /// <summary>Changes a characteristic; returns how many templates changed.</summary>
    public static int SetStat(ContentDatabase db, IEnumerable<UnitDef> units, string stat, MassStatMode mode, double value)
    {
        int n = 0;
        double def = db.Stat(stat)?.Default ?? 0;
        foreach (var u in units)
        {
            double cur = u.Stats.TryGetValue(stat, out var v) ? v : def;
            double to = mode switch
            {
                MassStatMode.Set => value,
                MassStatMode.Multiply => cur * value,
                _ => cur + value,
            };
            if (db.Stat(stat) is { } sd) to = Math.Clamp(to, sd.Min, sd.Max);
            if (Math.Abs(to - cur) < 1e-12 && u.Stats.ContainsKey(stat)) continue;
            u.Stats[stat] = Math.Round(to, 4);
            n++;
        }
        return n;
    }

    /// <summary>Assigns a category: as the main one, or adds it to the additional ones.</summary>
    public static int SetCategory(IEnumerable<UnitDef> units, string category, bool main)
    {
        int n = 0;
        foreach (var u in units)
            if (main && u.MainCategory != category)
            {
                u.MainCategory = category;
                n++;
            }
            else if (!main && category != "" && !u.Origins.Contains(category) && u.MainCategory != category)
            {
                u.Origins.Add(category);
                n++;
            }
        return n;
    }

    public static int SetRank(IEnumerable<UnitDef> units, UnitRank rank)
    {
        int n = 0;
        foreach (var u in units.Where(u => u.Rank != rank))
        {
            u.Rank = rank;
            n++;
        }
        return n;
    }

    public static int SetAiProfile(IEnumerable<UnitDef> units, string profile)
    {
        int n = 0;
        foreach (var u in units.Where(u => u.AiProfile != profile))
        {
            u.AiProfile = profile;
            n++;
        }
        return n;
    }

    /// <summary>
    /// «Подогнать под уровень» for every template (its own scaling profile): each is replaced in the database by its
    /// fitted copy (the previous build stays in the history and the restore point); the reference of level 1 is
    /// skipped. Returns the results.
    /// </summary>
    public static List<FitResult> FitAll(ContentDatabase db, IEnumerable<UnitDef> units, double level, FitVariant variant = FitVariant.Combined)
    {
        var list = new List<FitResult>();
        foreach (var u in units.ToList())
        {
            if (u.Id == db.Rules.Power.ReferenceUnit)
            {
                // The reference is level 1 by definition: fitting it would move every other level instead.
                list.Add(new FitResult
                {
                    Variant = variant, Unit = u, TargetLevel = level, LevelBefore = 1, LevelAfter = 1,
                    Message = "эталон уровня 1 не подгоняется: его уровень всегда 1, изменение эталона меняет все уровни",
                });
                continue;
            }
            var r = LevelFit.Fit(db, u, level, null, variant);
            db.Table(DefCategory.Unit).Replace(r.Unit);
            list.Add(r);
        }
        return list;
    }
}

/// <summary>
/// Local overrides of a wave group (docs/DECISIONS_4.md §71, TZ §19.2): modifiers of its instances, their level, an AI
/// profile, a faction — shown with a badge; «Превратить в шаблон» makes them a template of its own and switches the
/// group to it.
/// </summary>
public static class GroupOverrides
{
    public static bool Has(SpawnGroupDef g) => g.StatMods.Count > 0 || g.Level > 0 || g.AiProfile != "" || g.Faction != "";

    public static List<string> Describe(ContentDatabase db, SpawnGroupDef g)
    {
        var list = new List<string>();
        foreach (var m in g.StatMods)
        {
            string name = db.Stat(m.Stat)?.DisplayName ?? m.Stat;
            if (m.Add != 0) list.Add($"{name} {m.Add:+0.##;−0.##}");
            if (m.Mul != 0) list.Add($"{name} {m.Mul:+0%;−0%}");
        }
        if (g.Level > 0) list.Add(g.LevelMax > g.Level ? $"уровень {g.Level}–{g.LevelMax}" : $"уровень {g.Level}");
        if (g.AiProfile != "") list.Add($"ИИ «{db.Get<AiProfileDef>(g.AiProfile)?.DisplayName ?? g.AiProfile}»");
        if (g.Faction != "") list.Add($"фракция «{g.Faction}»");
        return list;
    }

    /// <summary>
    /// A new template from the group's template with its overrides applied (the level by the scaling profile, the
    /// modifiers as (base + add) × (1 + mul)); the group refers to it and keeps no overrides. The faction stays an
    /// override of the group (a template's faction would change where else it can be used).
    /// </summary>
    public static UnitDef ToTemplate(ContentDatabase db, SpawnGroupDef g, string? id = null, string? name = null)
    {
        var src = db.Unit(g.Unit) ?? throw new InvalidOperationException($"Шаблон «{g.Unit}» не найден");
        var u = ContentJson.Clone(src);
        if (g.Level > 0)
            foreach (var (stat, mul) in LevelFit.InstanceMultipliers(db, src, g.Level))
                u.Stats[stat] = Math.Round(u.Stats.GetValueOrDefault(stat, db.Stat(stat)?.Default ?? 0) * mul, 2);
        foreach (var grp in g.StatMods.GroupBy(m => m.Stat))
        {
            double b = u.Stats.TryGetValue(grp.Key, out var v) ? v : db.Stat(grp.Key)?.Default ?? 0;
            u.Stats[grp.Key] = Math.Round((b + grp.Sum(m => m.Add)) * (1 + grp.Sum(m => m.Mul)), 4);
        }
        if (g.AiProfile != "") u.AiProfile = g.AiProfile;
        string groupName = g.Name != "" ? g.Name : g.Id;
        u.Id = ContentIO.UniqueId(db, DefCategory.Unit, id ?? $"{src.Id}_{(g.Id != "" ? g.Id : "group")}");
        u.Name = name ?? $"{src.DisplayName} ({groupName})";
        u.Version = 1;
        u.Description = (src.Description + $"\nСоздан из переопределений группы «{groupName}»: {string.Join(", ", Describe(db, g))}.").Trim();
        db.Add(u);
        g.Unit = u.Id;
        g.StatMods.Clear();
        g.Level = 0;
        g.LevelMax = 0;
        g.AiProfile = "";
        return u;
    }
}
