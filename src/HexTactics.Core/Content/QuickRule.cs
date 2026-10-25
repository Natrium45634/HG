using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HexTactics.Core.Content;

// «Быстрый ввод» (docs/DECISIONS_5.md §77): a rule written in words — «+25% входящего урона от святой магии»,
// «броня +10», «иммунитет к яду», «+15% урона по нежити» — becomes the same modifiers the form edits, linked to the
// objects already in the draft (elements, damage types, stats, statuses, categories…). Words that name nothing yet
// are reported, so the editor can create the missing object and link it at once.

/// <summary>A word (or a few) of the text that names an existing object.</summary>
public sealed class QuickRuleLink
{
    public string Text { get; init; } = "";
    public SearchEntry Entry { get; init; } = null!;
    public double Score { get; init; }
    /// <summary>What the object is in the rule: «тип урона», «против существ», «характеристика»…</summary>
    public string Role { get; init; } = "";
}

/// <summary>Words that should name an object but none matches: the editor offers to create one.</summary>
public sealed class QuickRuleMissing
{
    public string Term { get; init; } = "";
    /// <summary>A name for the new object («святой магии» → «Святая магия»).</summary>
    public string SuggestedName { get; init; } = "";
    /// <summary>What it can be created as, the most likely first.</summary>
    public List<DefCategory> CreateAs { get; init; } = new();
}

/// <summary>One rule of the text (clauses are separated by «;», a new line, or «и»/«,» between two values).</summary>
public sealed class QuickRuleItem
{
    public string Source { get; init; } = "";
    public ModifierDef? Modifier { get; set; }
    public List<QuickRuleLink> Links { get; } = new();
    public List<QuickRuleMissing> Missing { get; } = new();
    public List<string> Notes { get; } = new();
    public string? Error { get; set; }

    /// <summary>The modifier can be added as it is.</summary>
    public bool Ok => Modifier != null && Error == null && Missing.Count == 0;
}

public static class QuickRuleParser
{
    private static readonly Regex Number = new(
        @"(?<mul>(?<![a-zа-яё])[x×х*]\s*(?<mv>\d+(?:[.,]\d+)?))(?![\d])|(?<mv2>\d+(?:[.,]\d+)?)\s*[x×](?![a-zа-я])|(?<sign>[+\-−–])?\s*(?<num>\d+(?:[.,]\d+)?)\s*(?<pct>%|процент\w*|п\.\s*п\.?|пп\b)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Clause = new(@"[;\n]+", RegexOptions.Compiled);

    // Words that shape the rule but never name an object on their own.
    private static readonly string[] Function =
    {
        "от", "к", "ко", "на", "по", "в", "во", "с", "со", "и", "или", "для", "при", "против", "из", "за", "до", "раз", "ед", "единиц", "всех", "все",
        "любой", "любого", "любым", "весь", "всего", "очков", "очка", "очко",
    };

    private static readonly string[] Neg = { "меньш", "ниж", "слабе", "дешевл", "уменьш", "сниж", "минус", "штраф", "теря", "хуже", "медленн" };
    private static readonly string[] Pos = { "больш", "выш", "сильне", "дорож", "увелич", "повыш", "плюс", "бонус", "лучше", "быстре" };
    private static readonly string[] Incoming = { "получ", "входящ", "уязвим", "сопротивл", "устойчив", "защит", "стойк", "впитыв" };
    private static readonly string[] Outgoing = { "нанос", "исходящ", "бьет", "бьёт", "атакам", "атаки" };
    private static readonly string[] Resist = { "сопротивл", "устойчив", "защит", "стойк", "сниж", "меньш", "уменьш" };
    private static readonly string[] Shape =
    {
        "урон", "получ", "входящ", "исходящ", "нанос", "уязвим", "сопротивл", "устойчив", "защит", "стойк", "иммун", "невосприимч", "неуязвим",
        "отраж", "поглощ", "удар", "стоимост", "дешевл", "дорож", "движени", "передвижени", "перемещени", "ходьб", "максимум", "макс", "запас",
        "восстанов", "реген", "больш", "меньш", "выш", "ниж", "сильне", "слабе", "увелич", "уменьш", "повыш", "сниж", "плюс", "минус", "бонус", "штраф",
        "процент", "навык", "умени", "способност", "заклинан", "проход", "лучше", "хуже", "быстре", "медленн", "теря", "впитыв",
    };
    // Generic words of damage: dropped when a more specific damage object is named («святой магии» → only «святой»).
    private static readonly string[] GenericHead = { "маги", "магич", "стихи", "элемент", "тип" };

    /// <summary>Parses the text into rules (one per clause).</summary>
    public static List<QuickRuleItem> Parse(ContentDatabase db, string text, ContentSearch? index = null)
    {
        index ??= ContentSearch.Build(db);
        var items = new List<QuickRuleItem>();
        foreach (var clause in SplitClauses(text))
            items.Add(ParseClause(db, clause, index));
        return items;
    }

    /// <summary>«;», a new line; inside a clause with two values also «, » and « и ».</summary>
    public static List<string> SplitClauses(string text)
    {
        var result = new List<string>();
        foreach (var part in Clause.Split(text))
        {
            var p = part.Trim();
            if (p.Length == 0) continue;
            if (Values(p) < 2)
            {
                result.Add(p);
                continue;
            }
            var pieces = Regex.Split(p, @",\s+|\s+и\s+|\s+а также\s+");
            // Keep a piece without a value with the previous one («иммунитет к яду и огню» stays whole).
            var merged = new List<string>();
            foreach (var x in pieces.Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                if (merged.Count > 0 && Values(x) == 0) merged[^1] += " и " + x;
                else merged.Add(x);
            }
            result.AddRange(merged);
        }
        return result;
    }

    private static int Values(string s) => Number.Matches(s).Count(m => m.Groups["num"].Success || m.Groups["mv"].Success || m.Groups["mv2"].Success);

    private static bool Has(IEnumerable<string> words, string[] stems) => words.Any(w => stems.Any(s => w.StartsWith(s, StringComparison.Ordinal)));

    private static bool IsShape(string w) => Shape.Any(s => w.StartsWith(s, StringComparison.Ordinal));

    private static bool IsFunction(string w) => Function.Contains(w) || IsShape(w);

    private static double ParseNum(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    private enum Kind { Damage, Creature, Stat, Status, Resource, Terrain, Skill }

    private static Kind? KindOf(SearchEntry e) => e.Category switch
    {
        DefCategory.DamageType or DefCategory.Element => Kind.Damage,
        null when e.Value.StartsWith(DamageTaxonomy.CategoryPrefix, StringComparison.Ordinal) => Kind.Damage,
        null when e.Value.StartsWith(DamageTaxonomy.TagPrefix, StringComparison.Ordinal) => Kind.Damage,
        DefCategory.Origin or DefCategory.Unit => Kind.Creature,
        DefCategory.Stat => Kind.Stat,
        DefCategory.Status => Kind.Status,
        DefCategory.Resource => Kind.Resource,
        DefCategory.Terrain => Kind.Terrain,
        DefCategory.Skill => Kind.Skill,
        _ => null,
    };

    // Ties between categories: a stat before an element, an element before a damage type, a category before a unit.
    private static int Rank(SearchEntry e) => e.Category switch
    {
        DefCategory.Stat => 0,
        DefCategory.Element => 1,
        DefCategory.DamageType => 2,
        null when e.Value.StartsWith(DamageTaxonomy.CategoryPrefix, StringComparison.Ordinal) => 3,
        DefCategory.Status => 4,
        DefCategory.Resource => 5,
        DefCategory.Terrain => 6,
        DefCategory.Origin => 7,
        DefCategory.Unit => 8,
        DefCategory.Skill => 9,
        _ => 10,
    };

    private sealed record Found(int From, int To, SearchEntry Entry, double Score, Kind Kind)
    {
        public int Len => To - From;
    }

    private static QuickRuleItem ParseClause(ContentDatabase db, string clause, ContentSearch index)
    {
        var item = new QuickRuleItem { Source = clause };
        string low = clause.ToLowerInvariant();

        // The value: «×1,25», «1.25x», «+25 %», «−10», «10 п. п.».
        double? value = null;
        bool percent = false, multiplier = false, points = false, signGiven = false;
        string rest = low;
        foreach (Match m in Number.Matches(low))
        {
            if (m.Groups["mv"].Success || m.Groups["mv2"].Success)
            {
                value = ParseNum(m.Groups["mv"].Success ? m.Groups["mv"].Value : m.Groups["mv2"].Value);
                multiplier = true;
            }
            else if (m.Groups["num"].Success)
            {
                value = ParseNum(m.Groups["num"].Value);
                var sign = m.Groups["sign"].Value;
                if (sign.Length > 0)
                {
                    signGiven = true;
                    if (sign != "+") value = -value;
                }
                var pct = m.Groups["pct"].Value;
                if (pct.StartsWith("п", StringComparison.Ordinal) && !pct.StartsWith("процент", StringComparison.Ordinal)) points = true;
                else if (pct.Length > 0) percent = true;
            }
            else continue;
            rest = low.Remove(m.Index, m.Length).Insert(m.Index, " ");
            break;
        }

        var words = RuText.Words(rest);
        bool immunity = Has(words, new[] { "иммун", "невосприимч", "неуязвим" });
        bool reflect = Has(words, new[] { "отраж" });
        bool absorb = Has(words, new[] { "поглощ" });
        bool damage = Has(words, new[] { "урон" });
        bool vulner = Has(words, new[] { "уязвим" });
        bool resist = Has(words, new[] { "сопротивл", "устойчив", "защит", "стойк" });
        bool hits = Has(words, new[] { "удар" });
        bool skills = Has(words, new[] { "навык", "умени", "способност", "заклинан" });
        bool cost = Has(words, new[] { "стоимост", "дешевл", "дорож" }) || (skills && words.Contains("од"));
        bool moving = Has(words, new[] { "движени", "передвижени", "перемещени", "ходьб", "проход" });
        bool resMax = Has(words, new[] { "максимум", "макс", "запас" });
        bool resRegen = Has(words, new[] { "восстанов", "реген" });
        bool incoming = Has(words, Incoming) || words.Contains("от");
        bool outgoing = Has(words, Outgoing) || words.Contains("по") || words.Contains("против");

        // Objects named by the words: the longest and best windows first, without overlaps.
        var found = Resolve(words, index);

        Found? Best(Kind k) => found.Where(f => f.Kind == k).OrderByDescending(f => f.Score).ThenBy(f => Rank(f.Entry)).FirstOrDefault();
        var stat = Best(Kind.Stat);
        bool statStrong = stat != null && (stat.Len >= 2 || stat.Score >= 95);

        // Specific damage objects push out the generic ones («магии» next to «святой»).
        var damages = found.Where(f => f.Kind == Kind.Damage).ToList();
        if (damages.Count > 1)
        {
            var specific = damages.Where(f => !IsGeneric(words, f)).ToList();
            if (specific.Count > 0)
                foreach (var g in damages.Except(specific)) found.Remove(g);
        }
        // A generic word alone after an unknown one is not the damage meant: «святой магии» without such an element.
        foreach (var g in found.Where(f => f.Kind == Kind.Damage && IsGeneric(words, f)).ToList())
            if (g.From > 0 && !found.Any(f => f.From <= g.From - 1 && f.To > g.From - 1) && IsMeaningful(words[g.From - 1]))
                found.Remove(g);
        var dmg = Best(Kind.Damage);

        ModifierTarget target;
        if (immunity) target = ModifierTarget.Immunity;
        else if (reflect) target = ModifierTarget.Reflection;
        else if (absorb) target = ModifierTarget.Absorption;
        else if (statStrong && !damage) target = ModifierTarget.Stat;
        else if (hits && !damage) target = Has(words, new[] { "сильне", "слабе", "мощн", "сил" }) || percent ? ModifierTarget.HitPower : ModifierTarget.HitCount;
        else if (cost && skills) target = ModifierTarget.SkillCost;
        else if (moving && Best(Kind.Terrain) != null) target = ModifierTarget.TerrainCost;
        else if (Best(Kind.Resource) is { } && (resMax || resRegen) && !statStrong) target = resRegen ? ModifierTarget.ResourceRegen : ModifierTarget.ResourceMax;
        else if (damage || vulner || (resist && dmg != null && !statStrong)) target = incoming && !(outgoing && !Has(words, Incoming)) ? ModifierTarget.IncomingDamage : ModifierTarget.OutgoingDamage;
        else if (stat != null) target = ModifierTarget.Stat;
        else if (dmg != null && (incoming || outgoing)) target = incoming ? ModifierTarget.IncomingDamage : ModifierTarget.OutgoingDamage;
        else
        {
            item.Error = "Не понял, что меняется. Начните с характеристики («броня +10»), урона («+25% урона от огня»), иммунитета («иммунитет к яду»).";
            AddMissing(item, words, ModifierTarget.Stat);
            return item;
        }

        var mod = new ModifierDef { Target = target, Mode = ModifierMode.Percent };
        void Use(Found? f, string role)
        {
            if (f == null) return;
            item.Links.Add(new QuickRuleLink { Text = string.Join(' ', words[f.From..f.To]), Entry = f.Entry, Score = f.Score, Role = role });
        }

        string DamageValue(SearchEntry e) => e.Category == DefCategory.Element ? DamageTaxonomy.ElementPrefix + e.Value : e.Value;

        switch (target)
        {
            case ModifierTarget.Stat:
                mod.Stat = stat!.Entry.Value;
                Use(stat, "характеристика");
                break;
            case ModifierTarget.ResourceMax:
            case ModifierTarget.ResourceRegen:
                var res = Best(Kind.Resource)!;
                mod.Resource = res.Entry.Value;
                Use(res, "ресурс");
                break;
            case ModifierTarget.TerrainCost:
                var ter = Best(Kind.Terrain)!;
                mod.Terrain = ter.Entry.Value;
                Use(ter, "местность");
                break;
            case ModifierTarget.Immunity:
                var stt = Best(Kind.Status);
                if (stt != null && (dmg == null || stt.Score >= dmg.Score))
                {
                    mod.Status = stt.Entry.Value;
                    Use(stt, "состояние");
                }
                else if (dmg != null)
                {
                    mod.Match = DamageValue(dmg.Entry);
                    Use(dmg, dmg.Entry.Kind);
                }
                break;
            case ModifierTarget.IncomingDamage:
            case ModifierTarget.OutgoingDamage:
            case ModifierTarget.Reflection:
            case ModifierTarget.Absorption:
                mod.Match = dmg != null ? DamageValue(dmg.Entry) : DamageTaxonomy.Any;
                if (dmg != null) Use(dmg, dmg.Entry.Kind);
                if (target is ModifierTarget.IncomingDamage or ModifierTarget.OutgoingDamage)
                    foreach (var c in found.Where(f => f.Kind == Kind.Creature))
                    {
                        mod.VsTypes.Add(c.Entry.Value);
                        Use(c, target == ModifierTarget.OutgoingDamage ? "по существам" : "от атак существ");
                    }
                break;
        }
        if (target is ModifierTarget.OutgoingDamage or ModifierTarget.HitCount or ModifierTarget.HitPower or ModifierTarget.SkillCost && Best(Kind.Skill) is { } skill)
        {
            mod.Skill = skill.Entry.Value;
            Use(skill, "навык");
        }

        // The value and its sign.
        if (target == ModifierTarget.Immunity)
        {
            mod.Value = 0;
            if (mod.Status == "" && mod.Match == "")
            {
                if (!AddMissing(item, words, target))
                    item.Error = "К чему иммунитет? Например: «иммунитет к яду», «иммунитет к оглушению».";
            }
        }
        else
        {
            if (value == null)
            {
                item.Error = "Не указана величина: например «25%», «+10», «×1,5».";
            }
            else
            {
                double v = value.Value;
                if (!signGiven && !multiplier)
                {
                    bool neg = Has(words, Neg) || (target == ModifierTarget.IncomingDamage && resist && !vulner);
                    if (neg) v = -Math.Abs(v);
                }
                if (multiplier)
                {
                    mod.Mode = ModifierMode.Multiplier;
                    mod.Value = v;
                }
                else if (target == ModifierTarget.HitCount)
                {
                    mod.Mode = ModifierMode.Percent;
                    mod.Value = Math.Round(v);
                }
                else if (target == ModifierTarget.Stat && db.Stat(mod.Stat)?.Format == StatFormat.Percent && (percent || points))
                {
                    // A stat kept as a share (evasion 0.1 = 10 %): «+10%» adds ten percentage points.
                    mod.Mode = ModifierMode.FlatBefore;
                    mod.Value = v / 100;
                    item.Notes.Add("характеристика в долях: «%» понимается как процентные пункты");
                }
                else if (percent || target is ModifierTarget.Reflection or ModifierTarget.Absorption or ModifierTarget.IncomingDamage or ModifierTarget.OutgoingDamage
                             or ModifierTarget.HitPower or ModifierTarget.SkillCost or ModifierTarget.TerrainCost && !points)
                {
                    mod.Mode = ModifierMode.Percent;
                    mod.Value = v;
                    if (!percent && target is ModifierTarget.IncomingDamage or ModifierTarget.OutgoingDamage) item.Notes.Add("без «%» величина понята как проценты");
                }
                else
                {
                    mod.Mode = ModifierMode.FlatBefore;
                    mod.Value = v;
                }
            }
            // Damage named but not found: «святой магии» before such an element exists.
            if (target is ModifierTarget.IncomingDamage or ModifierTarget.OutgoingDamage or ModifierTarget.Reflection or ModifierTarget.Absorption && dmg == null)
                AddMissing(item, words, target);
        }

        // Words nothing explains are shown, so the author sees what was not understood.
        var left = Uncovered(item, words).Where(IsMeaningful).ToList();
        var missingWords = item.Missing.SelectMany(m => m.Term.Split(' ')).ToHashSet();
        left.RemoveAll(missingWords.Contains);
        if (left.Count > 0) item.Notes.Add("не распознано: " + string.Join(", ", left.Select(w => $"«{w}»")));

        item.Modifier = mod;
        return item;
    }

    private static bool IsGeneric(string[] words, Found f) =>
        f.Len == 1 && GenericHead.Any(g => words[f.From].StartsWith(g, StringComparison.Ordinal));

    /// <summary>Windows of up to four words that name an object, the best first, without overlaps.</summary>
    private static List<Found> Resolve(string[] words, ContentSearch index)
    {
        var all = new List<Found>();
        for (int len = Math.Min(4, words.Length); len >= 1; len--)
            for (int i = 0; i + len <= words.Length; i++)
            {
                var span = words[i..(i + len)];
                if (span.All(IsFunction)) continue;
                if (len == 1 && (span[0].Length < 2 || IsFunction(span[0]))) continue;
                string phrase = string.Join(' ', span);
                double min = len == 1 ? 60 : 62;
                foreach (var h in index.Find(phrase, e => KindOf(e) != null, 8, min))
                {
                    var k = KindOf(h.Entry)!.Value;
                    // A tag counts only when written exactly.
                    if (h.Entry.Category == null && h.Entry.Value.StartsWith(DamageTaxonomy.TagPrefix, StringComparison.Ordinal) && h.Score < 95) continue;
                    all.Add(new Found(i, i + len, h.Entry, h.Score + len * 4, k));
                }
            }
        var taken = new bool[words.Length];
        var result = new List<Found>();
        // The best first (longer windows score a little more): a weak match of two words does not push out a strong one of
        // one word («святой» — the author's «Святость», not a built-in synonym of «Свет» in «святой магии»).
        foreach (var f in all.OrderByDescending(f => f.Score).ThenByDescending(f => f.Len).ThenBy(f => f.Entry.State == SearchState.New ? 0 : 1).ThenBy(f => Rank(f.Entry)))
        {
            bool free = true;
            for (int i = f.From; i < f.To; i++) free &= !taken[i];
            // One object per kind and window: the same words do not name a stat and an element at once.
            if (!free)
            {
                // …except the other kinds named by the same words: «яду» is a stat word and an element, the rule takes the
                // kind it needs (an immunity — the element, «сопротивление яду» — the stat).
                if (result.Any(r => r.From == f.From && r.To == f.To) && !result.Any(r => r.From == f.From && r.To == f.To && r.Kind == f.Kind))
                    result.Add(f);
                continue;
            }
            for (int i = f.From; i < f.To; i++) taken[i] = true;
            result.Add(f);
        }
        return result;
    }

    private static bool IsMeaningful(string w) => w.Length >= 3 && !IsFunction(w) && !w.All(char.IsDigit);

    private static bool[] Covered(QuickRuleItem item, string[] words)
    {
        var covered = new bool[words.Length];
        foreach (var l in item.Links)
        {
            var w = l.Text.Split(' ');
            for (int i = 0; i + w.Length <= words.Length; i++)
                if (words.Skip(i).Take(w.Length).SequenceEqual(w))
                {
                    for (int j = i; j < i + w.Length; j++) covered[j] = true;
                    break;
                }
        }
        return covered;
    }

    private static IEnumerable<string> Uncovered(QuickRuleItem item, string[] words)
    {
        var covered = Covered(item, words);
        for (int i = 0; i < words.Length; i++)
            if (!covered[i] && !GenericHead.Any(g => words[i].StartsWith(g, StringComparison.Ordinal)))
                yield return words[i];
    }

    /// <summary>Words left over are the name of an object that does not exist yet.</summary>
    private static bool AddMissing(QuickRuleItem item, string[] words, ModifierTarget target)
    {
        var covered = Covered(item, words);
        // The longest run of meaningful words that nothing names (a generic head word joins it: «святой магии»).
        var runs = new List<(int From, int To)>();
        int start = -1;
        for (int i = 0; i <= words.Length; i++)
        {
            bool meaningful = i < words.Length && !covered[i] && IsMeaningful(words[i]);
            bool generic = i < words.Length && !covered[i] && GenericHead.Any(g => words[i].StartsWith(g, StringComparison.Ordinal));
            if ((meaningful || (generic && start >= 0)) && start < 0) start = i;
            else if (!(meaningful || generic) && start >= 0)
            {
                runs.Add((start, i));
                start = -1;
            }
        }
        if (runs.Count == 0) return false;
        var run = runs.OrderByDescending(r => r.To - r.From).First();
        var termWords = words[run.From..run.To];
        if (termWords.All(w => GenericHead.Any(g => w.StartsWith(g, StringComparison.Ordinal)))) return false;
        var create = target switch
        {
            ModifierTarget.Stat => new List<DefCategory> { DefCategory.Stat },
            ModifierTarget.Immunity => new List<DefCategory> { DefCategory.Element, DefCategory.DamageType, DefCategory.Status },
            ModifierTarget.ResourceMax or ModifierTarget.ResourceRegen => new List<DefCategory> { DefCategory.Resource },
            _ => new List<DefCategory> { DefCategory.Element, DefCategory.DamageType },
        };
        item.Missing.Add(new QuickRuleMissing { Term = string.Join(' ', termWords), SuggestedName = GuessName(termWords), CreateAs = create });
        return true;
    }

    private static readonly Dictionary<string, string> Heads = new()
    {
        ["магии"] = "магия", ["магией"] = "магия", ["магию"] = "магия", ["магия"] = "магия",
        ["стихии"] = "стихия", ["стихией"] = "стихия", ["стихию"] = "стихия", ["стихия"] = "стихия",
        ["энергии"] = "энергия", ["энергией"] = "энергия", ["энергию"] = "энергия", ["энергия"] = "энергия",
        ["силы"] = "сила", ["силой"] = "сила", ["силу"] = "сила",
    };

    /// <summary>A name in the nominative for the new object, as far as simple rules go (the author may change it).</summary>
    public static string GuessName(string[] words)
    {
        var w = words.ToArray();
        if (w.Length > 0 && Heads.TryGetValue(w[^1], out var head))
        {
            w[^1] = head;
            // Adjectives before a feminine noun: «святой», «тёмную», «ледяною» → «святая», «тёмная», «ледяная».
            for (int i = 0; i < w.Length - 1; i++)
                foreach (var e in new[] { "ой", "ей", "ую", "ою", "юю", "ая", "яя" })
                    if (w[i].EndsWith(e, StringComparison.Ordinal) && w[i].Length > 3)
                    {
                        w[i] = w[i][..^2] + (e is "ей" or "юю" or "яя" ? "яя" : "ая");
                        break;
                    }
        }
        else if (w.Length == 1)
        {
            // «святого», «святому», «святым» → «святой»; «огненного» → «огненный».
            foreach (var (e, to) in new[] { ("ого", "ой"), ("ому", "ой"), ("ым", "ый"), ("ом", "ой"), ("ой", "ой") })
                if (w[0].EndsWith(e, StringComparison.Ordinal) && w[0].Length > e.Length + 2)
                {
                    var stem = w[0][..^e.Length];
                    w[0] = stem + (stem.EndsWith("нн", StringComparison.Ordinal) || stem.EndsWith("н", StringComparison.Ordinal) && e != "ой" ? "ый" : to);
                    break;
                }
        }
        var s = string.Join(' ', w);
        return s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.GetCultureInfo("ru-RU")) + s[1..];
    }

    /// <summary>
    /// Creates the missing object in the draft and returns it: an element, a damage type (linked to an element of
    /// the same name when there is one, or created together with it), a stat, a status or a resource.
    /// </summary>
    public static Def Create(ContentDatabase db, DefCategory category, string name, bool withElement = false)
    {
        Def d = category switch
        {
            DefCategory.Element => new ElementDef(),
            DefCategory.DamageType => new DamageTypeDef(),
            DefCategory.Stat => new StatDef(),
            DefCategory.Status => new StatusDef(),
            DefCategory.Resource => new ResourceDef(),
            _ => (Def)Activator.CreateInstance(ContentDatabase.TypeOf(category))!,
        };
        d.Name = name;
        d.Id = ContentIO.UniqueId(db, category, RuText.ToId(name));
        if (d is DamageTypeDef t)
        {
            t.Kind = DamageCategory.Magical;
            var index = ContentSearch.Build(db);
            var el = index.Find(name, e => e.Category == DefCategory.Element, 1, 75).FirstOrDefault()?.Entry.Def as ElementDef;
            if (el == null && withElement)
            {
                el = (ElementDef)Create(db, DefCategory.Element, name);
            }
            if (el != null) t.Element = el.Id;
        }
        db.Add(d);
        return d;
    }
}
