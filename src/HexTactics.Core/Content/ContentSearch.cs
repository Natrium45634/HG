using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HexTactics.Core.Content;

// Search over the content being edited — the draft with its new and unpublished objects (docs/DECISIONS_5.md §77):
// names, IDs, synonyms, tags and groups; Russian word endings and the wrong keyboard layout do not stop a match.

/// <summary>Normalisation, word splitting and a light Russian stemmer for search.</summary>
public static class RuText
{
    /// <summary>Lower case, «ё» → «е», everything but letters and digits becomes a space.</summary>
    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            char c = char.ToLowerInvariant(ch);
            if (c == 'ё') c = 'е';
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Words of a text (IDs split at «_»).</summary>
    public static string[] Words(string s) => Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // Endings, longest first: adjectives, nouns, verbs, «-ость».
    private static readonly string[] Endings =
    {
        "остями", "остью", "ости", "ость", "иями", "ями", "ами", "ыми", "ими", "его", "ого", "ему", "ому", "ее", "ие", "ые", "ое", "ей", "ий", "ый", "ой",
        "ем", "им", "ым", "ом", "их", "ых", "ую", "юю", "ая", "яя", "ою", "ею", "ия", "ья", "ие", "ье", "ию", "ью", "ах", "ях", "ам", "ям", "ов", "ев",
        "ешь", "ете", "ут", "ют", "ет", "ат", "ят",
        "а", "я", "о", "е", "ы", "и", "у", "ю", "ь", "й",
    };

    /// <summary>
    /// The word without its ending («святой», «святая», «святости» → «свят»). Latin words and numbers are kept;
    /// the stem keeps at least three letters (two for words of up to four letters: «яду» → «яд»).
    /// </summary>
    public static string Stem(string word)
    {
        if (word.Length < 3 || !IsCyrillic(word)) return word;
        int min = word.Length <= 4 ? 2 : 3;
        foreach (var e in Endings)
            if (word.Length - e.Length >= min && word.EndsWith(e, StringComparison.Ordinal))
                return word[..^e.Length];
        return word;
    }

    public static bool IsCyrillic(string w) => w.Any(c => c is >= 'а' and <= 'я');

    /// <summary>
    /// Stems with a fleeting vowel dropped («огон» → «огн» for «огня», «ветер» → «ветр», «лед» → «льд»), so all forms of
    /// such nouns meet.
    /// </summary>
    public static IEnumerable<string> Variants(string stem)
    {
        yield return stem;
        if (stem.Length >= 3 && !IsVowel(stem[^1]) && stem[^2] is 'о' or 'е' && !IsVowel(stem[^3]))
        {
            yield return stem[..^2] + stem[^1];
            if (stem[^2] == 'е') yield return stem[..^2] + "ь" + stem[^1];
        }
    }

    private static bool IsVowel(char c) => "аеиоуыэюяё".IndexOf(c) >= 0;

    private const string Lat = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string Cyr = "йцукенгшщзхъфывапролджэячсмитьбюё";

    /// <summary>The same keys in the other layout («cdtn» ↔ «свет»); null when nothing changes.</summary>
    public static string? SwapLayout(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool changed = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            int i = Lat.IndexOf(ch);
            int j = Cyr.IndexOf(ch);
            if (i >= 0) { sb.Append(Cyr[i]); changed = true; }
            else if (j >= 0) { sb.Append(Lat[j]); changed = true; }
            else sb.Append(ch);
        }
        return changed ? sb.ToString() : null;
    }

    /// <summary>Edit distance (with transpositions) up to <paramref name="max"/>; larger distances return max + 1.</summary>
    public static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int rowMin = int.MaxValue;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > max) return max + 1;
        }
        return Math.Min(d[a.Length, b.Length], max + 1);
    }

    private static readonly Dictionary<char, string> Tr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y",
        ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f",
        ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>An ID from a name: «Святая магия» → «svyataya_magiya».</summary>
    public static string ToId(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (Tr.TryGetValue(ch, out var t)) sb.Append(t);
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var id = sb.ToString().Trim('_');
        if (id.Length == 0) id = "new";
        if (char.IsDigit(id[0])) id = "n" + id;
        return id.Length > 40 ? id[..40].TrimEnd('_') : id;
    }
}

/// <summary>State of a found object relative to the published content.</summary>
public enum SearchState
{
    /// <summary>Published, not changed in the draft.</summary>
    Published,
    /// <summary>Created in the draft, not published yet.</summary>
    New,
    /// <summary>Published, changed in the draft.</summary>
    Changed,
    /// <summary>Marked «неполный (черновик)».</summary>
    Incomplete,
}

/// <summary>One searchable thing: a definition, or a value that is not one (a damage category «cat:», a tag «tag:»).</summary>
public sealed class SearchEntry
{
    /// <summary>The category of the definition; null for values that are not definitions.</summary>
    public DefCategory? Category { get; init; }
    /// <summary>What a reference stores: the ID, «el:id», «cat:Magic», «tag:x».</summary>
    public string Value { get; init; } = "";
    /// <summary>What it is, for the author: «элемент», «тип урона», «тег»…</summary>
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public Def? Def { get; init; }
    public SearchState State { get; init; }

    internal string NormName = "";
    internal string NormId = "";
    internal string[] NameWords = Array.Empty<string>();
    internal string[] NameStems = Array.Empty<string>();
    internal HashSet<string> AltStems = new();
    internal string[] IdWords = Array.Empty<string>();
    internal string[] AliasPhrases = Array.Empty<string>();
    internal string[] AliasWords = Array.Empty<string>();
    internal string[] AliasStems = Array.Empty<string>();
    internal string[] TagWords = Array.Empty<string>();
    internal string[] DetailStems = Array.Empty<string>();

    /// <summary>«Свет [light] · элемент · новое».</summary>
    public string Label
    {
        get
        {
            string id = Def != null ? Def.Id : Value;
            string head = string.IsNullOrEmpty(Name) || Name == id ? id : $"{Name} [{id}]";
            return $"{head} · {Kind}{StateText(State)}";
        }
    }

    public static string StateText(SearchState s) => s switch
    {
        SearchState.New => " · новое, не опубликовано",
        SearchState.Changed => " · изменено в черновике",
        SearchState.Incomplete => " · неполный черновик",
        _ => "",
    };

    public override string ToString() => Label;
}

public sealed record SearchHit(SearchEntry Entry, double Score);

/// <summary>
/// The index of the content being edited. Built from the draft in a few milliseconds, so the editor rebuilds it
/// whenever the content changes; objects created a moment ago are found at once.
/// </summary>
public sealed class ContentSearch
{
    private readonly List<SearchEntry> _entries = new();

    public IReadOnlyList<SearchEntry> Entries => _entries;

    /// <summary>
    /// Index of every definition of <paramref name="db"/> (state against <paramref name="published"/>), plus the
    /// damage categories and the tags used in the content.
    /// </summary>
    public static ContentSearch Build(ContentDatabase db, ContentDatabase? published = null)
    {
        var s = new ContentSearch();
        foreach (var c in Enum.GetValues<DefCategory>())
        {
            if (c == DefCategory.MoveCategory) continue;
            foreach (var d in db.Table(c).Items)
                s._entries.Add(Make(d, c, KindOf(c, d), StateOf(d, published)));
        }
        foreach (var name in Enum.GetNames(typeof(DamageCategory)))
            s._entries.Add(Make(null, null, "категория урона", SearchState.Published, DamageTaxonomy.CategoryPrefix + name, DamageTaxonomy.CategoryName(name), name));
        foreach (var name in db.All<DamageTypeDef>().Where(d => d.Kind == DamageCategory.Custom && !string.IsNullOrEmpty(d.CustomCategory)).Select(d => d.CustomCategory).Distinct())
            s._entries.Add(Make(null, null, "категория урона", SearchState.Published, DamageTaxonomy.CategoryPrefix + name, name, name));
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in Enum.GetValues<DefCategory>())
            if (c != DefCategory.MoveCategory)
                foreach (var d in db.Table(c).Items)
                    foreach (var t in d.Tags)
                        if (!string.IsNullOrWhiteSpace(t)) tags.Add(t.Trim());
        foreach (var t in tags) s._entries.Add(Make(null, null, "тег", SearchState.Published, DamageTaxonomy.TagPrefix + t, t, t));
        return s;
    }

    public static SearchState StateOf(Def d, ContentDatabase? published)
    {
        if (d.Incomplete) return SearchState.Incomplete;
        if (published == null) return SearchState.Published;
        var p = published.Table(d.Category).Get(d.Id);
        if (p == null || p.GetType() != d.GetType()) return SearchState.New;
        return ContentJson.Serialize<object>(p) == ContentJson.Serialize<object>(d) ? SearchState.Published : SearchState.Changed;
    }

    /// <summary>What the author calls a category: «элемент», «тип урона», «характеристика»…</summary>
    public static string KindOf(DefCategory c, Def? d = null) => c switch
    {
        DefCategory.Element => "элемент",
        DefCategory.DamageType => "тип урона",
        DefCategory.Stat => "характеристика",
        DefCategory.Resource => "ресурс",
        DefCategory.Status => "состояние",
        DefCategory.Passive => d is PassiveDef { Kind: PassiveKind.Talent } ? "талант" : "пассив",
        DefCategory.Origin => d is OriginDef o ? o.Kind switch
        {
            OriginKind.Race => "раса",
            OriginKind.Species => "вид существа",
            OriginKind.Class => "класс",
            OriginKind.Role => "роль",
            _ => "категория существ",
        } : "категория существ",
        DefCategory.Unit => "юнит",
        DefCategory.Skill => "навык",
        DefCategory.Terrain => "местность",
        DefCategory.CellEffect => "эффект клетки",
        DefCategory.Synergy => "синергия",
        DefCategory.Faction => "фракция",
        DefCategory.AiProfile => "профиль ИИ",
        DefCategory.Formation => "формация",
        DefCategory.Layer => "слой",
        DefCategory.Map => "карта",
        DefCategory.MapTemplate => "шаблон карты",
        DefCategory.Scenario => "сценарий",
        DefCategory.Asset => "ресурс файла",
        DefCategory.Vfx => "визуальный эффект",
        DefCategory.GroupPreset => "пресет группы",
        DefCategory.ScalingProfile => "профиль масштабирования",
        DefCategory.RulesPreset => "пресет правил",
        DefCategory.GameRules => "правила режима",
        DefCategory.SquadPreset => "пресет отряда",
        DefCategory.ZonePreset => "пресет зоны",
        DefCategory.ModifierSet => "набор модификаторов",
        DefCategory.DifficultyVariant => "вариант сценария",
        DefCategory.ObjectPreset => "пресет объекта",
        DefCategory.Campaign => "кампания",
        DefCategory.RewardSet => "набор наград",
        DefCategory.Scale => "шкала",
        DefCategory.Summon => "призыв",
        _ => c.ToString(),
    };

    /// <summary>An entry of one definition (the list search of a section scores its items with it).</summary>
    public static SearchEntry EntryOf(Def d, ContentDatabase? published = null) => Make(d, d.Category, KindOf(d.Category, d), StateOf(d, published));

    /// <summary>An entry of a value that is not a definition (an option of a list).</summary>
    public static SearchEntry TextEntry(string name, string value, string kind = "") => Make(null, null, kind, SearchState.Published, value, name, value);

    private static SearchEntry Make(Def? d, DefCategory? c, string kind, SearchState state, string? value = null, string? name = null, string? id = null)
    {
        string nm = d?.DisplayName ?? name ?? "";
        string ident = d?.Id ?? id ?? "";
        var e = new SearchEntry
        {
            Category = c,
            Value = value ?? ident,
            Kind = kind,
            Name = nm,
            Detail = d?.Description ?? "",
            Def = d,
            State = state,
        };
        e.NormName = RuText.Norm(nm);
        e.NormId = RuText.Norm(ident);
        e.NameWords = RuText.Words(nm);
        e.NameStems = e.NameWords.Select(RuText.Stem).ToArray();
        e.IdWords = RuText.Words(ident.Replace('_', ' '));
        var aliases = d?.Aliases ?? new List<string>();
        e.AliasPhrases = aliases.Select(RuText.Norm).Where(a => a.Length > 0).ToArray();
        e.AliasWords = e.AliasPhrases.SelectMany(a => a.Split(' ')).Distinct().ToArray();
        e.AliasStems = e.AliasWords.Select(RuText.Stem).ToArray();
        e.AltStems = e.NameStems.Concat(e.AliasStems).SelectMany(RuText.Variants).Where(x => x.Length >= 2).ToHashSet();
        e.TagWords = (d?.Tags ?? new List<string>()).Concat(d == null || string.IsNullOrEmpty(d.Group) ? Array.Empty<string>() : new[] { d.Group })
            .SelectMany(RuText.Words).Distinct().ToArray();
        e.DetailStems = RuText.Words(e.Detail).Where(w => w.Length >= 4).Select(RuText.Stem).Distinct().ToArray();
        return e;
    }

    /// <summary>
    /// How well one normalised query word matches the entry (0 — not at all): exact name or ID 100, a synonym 95, a whole
    /// word 80, a word of a synonym 78, the start of a word 70, the same stem 62, a tag 50, inside a word 40, a typo 30,
    /// the description 15.
    /// </summary>
    public static double WordScore(SearchEntry e, string w)
    {
        if (w.Length == 0) return 0;
        if (e.NormName == w || e.NormId == w) return 100;
        string st = RuText.Stem(w);
        double best = 0;
        void Up(double v) { if (v > best) best = v; }
        foreach (var x in e.NameWords) { if (x == w) Up(80); else if (x.StartsWith(w, StringComparison.Ordinal)) Up(70); }
        foreach (var x in e.IdWords) { if (x == w) Up(80); else if (w.Length >= 2 && x.StartsWith(w, StringComparison.Ordinal)) Up(66); }
        foreach (var x in e.AliasPhrases) if (x == w) Up(95);
        foreach (var x in e.AliasWords) { if (x == w) Up(78); else if (x.StartsWith(w, StringComparison.Ordinal)) Up(64); }
        if (best >= 70) return best;
        if (st.Length >= 2)
        {
            for (int i = 0; i < e.NameStems.Length; i++)
                if (e.NameStems[i] == st || (st.Length >= 3 && e.NameWords[i].StartsWith(st, StringComparison.Ordinal))) Up(62);
            for (int i = 0; i < e.AliasStems.Length; i++)
                if (e.AliasStems[i] == st || (st.Length >= 3 && e.AliasWords[i].StartsWith(st, StringComparison.Ordinal))) Up(60);
            if (best == 0 && RuText.Variants(st).Any(e.AltStems.Contains)) Up(62);
        }
        foreach (var x in e.TagWords) { if (x == w) Up(52); else if (w.Length >= 3 && x.StartsWith(w, StringComparison.Ordinal)) Up(45); }
        if (best > 0) return best;
        if (w.Length >= 3)
            foreach (var x in e.NameWords.Concat(e.IdWords).Concat(e.AliasWords))
                if (x.Contains(w, StringComparison.Ordinal)) Up(40);
        if (best > 0) return best;
        if (w.Length >= 4)
        {
            int max = w.Length >= 7 ? 2 : 1;
            foreach (var x in e.NameWords.Concat(e.AliasWords))
                if (x.Length >= 3 && (RuText.Distance(x, w, max) <= max || RuText.Distance(RuText.Stem(x), st, max) <= max)) Up(30);
        }
        if (best > 0) return best;
        if (st.Length >= 4)
            foreach (var x in e.DetailStems)
                if (x == st || x.StartsWith(st, StringComparison.Ordinal)) Up(15);
        return best;
    }

    /// <summary>
    /// Score of a query of several words: every word must match; the whole phrase equal to the name or a synonym
    /// scores more. 0 — no match.
    /// </summary>
    public static double Score(SearchEntry e, string query)
    {
        var q = RuText.Norm(query);
        if (q.Length == 0) return 0;
        if (e.NormName == q || e.NormId == q) return 100;
        if (e.AliasPhrases.Contains(q)) return 97;
        var words = q.Split(' ');
        double sum = 0;
        foreach (var w in words)
        {
            double s = WordScore(e, w);
            if (s <= 0) return 0;
            sum += s;
        }
        double avg = sum / words.Length;
        if (words.Length > 1 && e.NormName.StartsWith(q, StringComparison.Ordinal)) avg = Math.Max(avg, 90);
        return avg;
    }

    /// <summary>
    /// Matches of a query, best first (ties: shorter names, then names). Nothing found in the typed layout —
    /// the other keyboard layout is tried.
    /// </summary>
    public List<SearchHit> Find(string query, Func<SearchEntry, bool>? filter = null, int limit = 30, double minScore = 1)
    {
        var hits = FindCore(query, filter, minScore);
        if (hits.Count == 0 && RuText.SwapLayout(query) is { } swapped) hits = FindCore(swapped, filter, minScore);
        return hits.Take(limit).ToList();
    }

    private List<SearchHit> FindCore(string query, Func<SearchEntry, bool>? filter, double minScore)
    {
        var list = new List<SearchHit>();
        foreach (var e in _entries)
        {
            if (filter != null && !filter(e)) continue;
            double s = Score(e, query);
            if (s >= minScore) list.Add(new SearchHit(e, s));
        }
        // Equal scores: what the author has just added comes first (it is what they are working with).
        return list.OrderByDescending(h => h.Score).ThenBy(h => h.Entry.State == SearchState.New ? 0 : 1).ThenBy(h => h.Entry.Name.Length)
            .ThenBy(h => h.Entry.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>The entry of a stored reference value (an ID, «el:…», «cat:…», «tag:…»).</summary>
    public SearchEntry? Get(DefCategory? c, string value) => _entries.FirstOrDefault(e => e.Category == c && e.Value == value);
}
