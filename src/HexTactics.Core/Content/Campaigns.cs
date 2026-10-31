using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HexTactics.Core.Content;

public enum CampaignNodeStatus
{
    [Ed("Закрыто")] Locked,
    [Ed("Доступно")] Available,
    [Ed("Пройдено")] Completed,
}

/// <summary>What one mission of a run of a campaign came to.</summary>
public sealed class CampaignNodeRecord
{
    public int Attempts { get; set; }
    public int Victories { get; set; }
    /// <summary>The best value of every metric over the victories (smaller is better for leaks, time, deaths).</summary>
    public Dictionary<OutcomeMetric, double> Best { get; set; } = new();
    public Dictionary<OutcomeMetric, double> Last { get; set; } = new();
    /// <summary>The variants won at least once ("" — the ordinary one).</summary>
    public List<string> Variants { get; set; } = new();
}

/// <summary>A reward given: its key of once-only (run, mission, entry, occurrence) and what it gave.</summary>
public sealed class RewardGrant
{
    public string Key { get; set; } = "";
    public string Node { get; set; } = "";
    public string Entry { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>A reward «одна на выбор» waiting for the player's choice.</summary>
public sealed class PendingChoice
{
    public string Node { get; set; } = "";
    public string RewardSet { get; set; } = "";
    public string Group { get; set; } = "";
    public List<string> Entries { get; set; } = new();
}

/// <summary>
/// The progress of a campaign (ТЗ 9.6): only the state of the run and the flags of the author — the user's data, apart from the
/// content. The ledger of rewards with keys of once-only and the processed events of completion (ТЗ 10.5).
/// </summary>
public sealed class CampaignProgress
{
    public int FormatVersion { get; set; } = 1;
    public string Campaign { get; set; } = "";
    /// <summary>The run of the campaign: a key of once-only of its rewards.</summary>
    public string RunId { get; set; } = "";
    /// <summary>The version of the campaign when the run started (a later edit is told, never reset — ТЗ 9.6).</summary>
    public int CampaignVersion { get; set; }
    public Dictionary<string, CampaignNodeRecord> Nodes { get; set; } = new();
    public List<string> Flags { get; set; } = new();
    public Dictionary<string, double> Currency { get; set; } = new();
    public List<string> Medals { get; set; } = new();
    public List<string> UnlockedScenarios { get; set; } = new();
    public List<string> UnlockedUnits { get; set; } = new();
    public List<RewardGrant> Ledger { get; set; } = new();
    public List<string> Processed { get; set; } = new();
    public List<PendingChoice> Pending { get; set; } = new();

    public CampaignNodeRecord? Record(string node) => Nodes.GetValueOrDefault(node);
}

/// <summary>What a completion of a mission gave.</summary>
public sealed class CampaignCompletion
{
    /// <summary>The event was processed before: nothing changed (ТЗ 10.5, T27).</summary>
    public bool Duplicate { get; set; }
    public bool Victory { get; set; }
    public List<string> Granted { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<PendingChoice> Choices { get; } = new();
    public List<string> NewlyAvailable { get; } = new();
}

/// <summary>A problem of a campaign or of a set of rewards.</summary>
public sealed record CampaignProblem(bool Error, string Where, string Message);

/// <summary>
/// Campaigns (ТЗ ч. 6, 9–10; docs/DECISIONS_6.md §103): statuses of the missions, the access, the completion with its rewards
/// given once by their keys, the check of the graph (rings, ways to the end), the progress saved atomically.
/// </summary>
public static class Campaigns
{
    public const int CurrentFormat = 1;

    public static CampaignProgress Start(CampaignDef c) => new()
    {
        Campaign = c.Id, CampaignVersion = c.Version, RunId = Guid.NewGuid().ToString("N")[..12],
    };

    public static bool Completed(CampaignProgress p, string node) => p.Record(node) is { Victories: > 0 };

    /// <summary>The missions whose «далее» lead to a mission.</summary>
    public static IEnumerable<CampaignNodeDef> Predecessors(CampaignDef c, string node) => c.Nodes.Where(n => n.Next.Contains(node));

    /// <summary>Is the access of a mission open: its own condition, else a start mission or a mission leading here passed.</summary>
    public static bool Open(CampaignDef c, CampaignProgress p, CampaignNodeDef n)
    {
        if (n.NeedsUnlock && !p.UnlockedScenarios.Contains(n.Scenario)) return false;
        if (n.Requires != null) return Evaluate(n.Requires, c, p);
        return c.StartNodes.Contains(n.Id) || Predecessors(c, n.Id).Any(x => Completed(p, x.Id));
    }

    public static CampaignNodeStatus Status(CampaignDef c, CampaignProgress p, CampaignNodeDef n) =>
        Completed(p, n.Id) ? CampaignNodeStatus.Completed : Open(c, p, n) ? CampaignNodeStatus.Available : CampaignNodeStatus.Locked;

    /// <summary>Why a mission is closed, in words (the screen of missions shows it).</summary>
    public static string WhyLocked(CampaignDef c, CampaignProgress p, CampaignNodeDef n)
    {
        if (n.NeedsUnlock && !p.UnlockedScenarios.Contains(n.Scenario)) return $"сценарий «{n.Scenario}» ещё не открыт наградой";
        if (n.Requires != null) return "нужно: " + n.Requires.Describe();
        var before = Predecessors(c, n.Id).Select(x => $"«{x.Id}»").ToList();
        return before.Count == 0 ? "нет пути к заданию" : "пройдите " + string.Join(" или ", before);
    }

    public static bool Evaluate(CampaignConditionDef cond, CampaignDef c, CampaignProgress p) => cond.Type switch
    {
        CampaignConditionType.Always => true,
        CampaignConditionType.All => cond.Children.All(x => Evaluate(x, c, p)),
        CampaignConditionType.Any => cond.Children.Any(x => Evaluate(x, c, p)),
        CampaignConditionType.Not => !cond.Children.Any(x => Evaluate(x, c, p)),
        CampaignConditionType.NodeCompleted => Completed(p, cond.Node),
        CampaignConditionType.CompletedCount => (cond.Nodes.Count == 0 ? c.Nodes.Select(n => n.Id) : cond.Nodes).Count(id => Completed(p, id)) >= cond.Count,
        CampaignConditionType.Flag => p.Flags.Contains(cond.Flag),
        CampaignConditionType.Result => p.Record(cond.Node) is { Victories: > 0 } r && cond.Criterion != null && cond.Criterion.Holds(r.Best),
        _ => false,
    };

    private static bool LowerIsBetter(OutcomeMetric m) => m is OutcomeMetric.Leaks or OutcomeMetric.TimeToWin or OutcomeMetric.Deaths;

    /// <summary>
    /// A mission came to an end (ТЗ 9.3, 10.4–10.5). The same event twice changes nothing; a reward is given once per its key:
    /// «первое прохождение» — once a run, «каждое» — once a victory, «на вариант» — once a variant, «на выбор» — the player picks.
    /// </summary>
    public static CampaignCompletion Complete(ContentDatabase db, CampaignDef c, CampaignProgress p, string nodeId,
        IReadOnlyDictionary<OutcomeMetric, double> result, string eventId)
    {
        var done = new CampaignCompletion();
        if (eventId != "" && p.Processed.Contains(eventId))
        {
            done.Duplicate = true;
            return done;
        }
        var node = c.Node(nodeId) ?? throw new ArgumentException($"задания «{nodeId}» в кампании нет");
        var before = c.Nodes.Where(n => Status(c, p, n) == CampaignNodeStatus.Available).Select(n => n.Id).ToHashSet();
        if (eventId != "") p.Processed.Add(eventId);
        bool victory = result.GetValueOrDefault(OutcomeMetric.Victory) >= 1;
        done.Victory = victory;
        if (!p.Nodes.TryGetValue(nodeId, out var rec)) p.Nodes[nodeId] = rec = new CampaignNodeRecord();
        rec.Attempts++;
        rec.Last = result.ToDictionary(k => k.Key, k => k.Value);
        if (victory)
        {
            rec.Victories++;
            if (!rec.Variants.Contains(node.Variant)) rec.Variants.Add(node.Variant);
            foreach (var (m, v) in result)
                if (!rec.Best.TryGetValue(m, out var old) || (LowerIsBetter(m) ? v < old : v > old))
                    rec.Best[m] = v;
        }
        if (db.Get<RewardSetDef>(node.RewardSet) is { } set)
        {
            foreach (var e in set.Entries)
            {
                if (!victory && !e.OnDefeat)
                {
                    done.Skipped.Add($"{e.Describe()} — только при победе");
                    continue;
                }
                if (e.Criterion != null && !e.Criterion.Holds(result))
                {
                    done.Skipped.Add($"{e.Describe()} — не выполнено: {e.Criterion.Describe()}");
                    continue;
                }
                if (e.Policy == RewardPolicy.ExclusiveChoice)
                {
                    if (p.Ledger.Any(g => g.Key == ChoiceKey(p, nodeId, e.ChoiceGroup)) || p.Pending.Any(x => x.Node == nodeId && x.Group == e.ChoiceGroup)) continue;
                    var options = set.Entries.Where(x => x.Policy == RewardPolicy.ExclusiveChoice && x.ChoiceGroup == e.ChoiceGroup).Select(x => x.Id).ToList();
                    var pc = new PendingChoice { Node = nodeId, RewardSet = set.Id, Group = e.ChoiceGroup, Entries = options };
                    p.Pending.Add(pc);
                    done.Choices.Add(pc);
                    continue;
                }
                string key = KeyOf(p, nodeId, e, node.Variant, rec);
                if (p.Ledger.Any(g => g.Key == key))
                {
                    done.Skipped.Add($"{e.Describe()} — уже получено");
                    continue;
                }
                Grant(p, nodeId, e, key);
                done.Granted.Add(e.Describe());
            }
        }
        foreach (var n in c.Nodes)
            if (!before.Contains(n.Id) && Status(c, p, n) == CampaignNodeStatus.Available)
                done.NewlyAvailable.Add(n.Id);
        return done;
    }

    /// <summary>The player picked a reward «одна на выбор»: given once by the key of the group.</summary>
    public static string? Choose(ContentDatabase db, CampaignProgress p, PendingChoice choice, string entryId)
    {
        if (!p.Pending.Contains(choice)) return "выбор уже сделан";
        var e = db.Get<RewardSetDef>(choice.RewardSet)?.Entries.Find(x => x.Id == entryId);
        if (e == null || !choice.Entries.Contains(entryId)) return "такой награды в выборе нет";
        string key = ChoiceKey(p, choice.Node, choice.Group);
        p.Pending.Remove(choice);
        if (p.Ledger.Any(g => g.Key == key)) return "выбор уже сделан";
        Grant(p, choice.Node, e, key);
        return null;
    }

    private static string ChoiceKey(CampaignProgress p, string node, string group) => $"{p.RunId}/{node}/choice:{group}";

    /// <summary>The key of once-only (ТЗ 10.5): the run, the mission, the entry and the occurrence by the policy.</summary>
    private static string KeyOf(CampaignProgress p, string node, RewardEntryDef e, string variant, CampaignNodeRecord rec) => e.Policy switch
    {
        RewardPolicy.Repeatable => $"{p.RunId}/{node}/{e.Id}/#{rec.Attempts}",
        RewardPolicy.PerDifficulty => $"{p.RunId}/{node}/{e.Id}/variant:{(variant == "" ? "обычный" : variant)}",
        _ => $"{p.RunId}/{node}/{e.Id}/first",
    };

    private static void Grant(CampaignProgress p, string node, RewardEntryDef e, string key)
    {
        switch (e.Type)
        {
            case RewardType.UnlockScenario when !p.UnlockedScenarios.Contains(e.Scenario):
                p.UnlockedScenarios.Add(e.Scenario);
                break;
            case RewardType.UnlockUnit when !p.UnlockedUnits.Contains(e.Unit):
                p.UnlockedUnits.Add(e.Unit);
                break;
            case RewardType.SetFlag when !p.Flags.Contains(e.Flag):
                p.Flags.Add(e.Flag);
                break;
            case RewardType.Currency:
                p.Currency[e.Currency] = p.Currency.GetValueOrDefault(e.Currency) + e.Amount;
                break;
            case RewardType.Medal when !p.Medals.Contains(e.Medal):
                p.Medals.Add(e.Medal);
                break;
        }
        p.Ledger.Add(new RewardGrant { Key = key, Node = node, Entry = e.Id, Text = e.Describe() });
    }

    /// <summary>The rewards of a set in words with their policy and conditions (ТЗ 10.4: the editor previews them).</summary>
    public static List<string> Preview(RewardSetDef set) => set.Entries.Select(e =>
        $"{e.Describe()} — {EdUtil.EnumLabel(e.Policy).ToLowerInvariant()}" + (e.Policy == RewardPolicy.ExclusiveChoice ? $" «{e.ChoiceGroup}»" : "") +
        (e.OnDefeat ? ", и при поражении" : ", при победе") + (e.Criterion != null ? ", если " + e.Criterion.Describe() : "")).ToList();

    /// <summary>
    /// The progress against an edited campaign (ТЗ 9.6): nothing is reset; what changed is told — a newer version, missions
    /// no longer there, flags no longer declared.
    /// </summary>
    public static List<string> Reconcile(CampaignDef c, CampaignProgress p)
    {
        var notes = new List<string>();
        if (p.CampaignVersion != c.Version)
            notes.Add($"Кампания изменена после начала прохождения (версия {p.CampaignVersion} → {c.Version}); прогресс сохранён.");
        foreach (var id in p.Nodes.Keys.Where(id => c.Node(id) == null))
            notes.Add($"Задания «{id}» в кампании больше нет: его прохождение сохранено, но не показывается.");
        foreach (var f in p.Flags.Where(f => !c.Flags.Contains(f)))
            notes.Add($"Флаг «{f}» больше не объявлен в кампании.");
        return notes;
    }

    // ------------------------------------------------------------------ check

    public static List<CampaignProblem> Check(ContentDatabase db, CampaignDef c)
    {
        var list = new List<CampaignProblem>();
        var ids = c.Nodes.Select(n => n.Id).ToList();
        foreach (var d in ids.GroupBy(x => x).Where(g => g.Key == "" || g.Count() > 1))
            list.Add(new(true, d.Key, d.Key == "" ? "задание без ID" : $"ID задания «{d.Key}» повторяется"));
        if (c.StartNodes.Count == 0) list.Add(new(true, "", "нет стартовых заданий"));
        foreach (var s in c.StartNodes.Where(s => !ids.Contains(s))) list.Add(new(true, s, $"стартового задания «{s}» нет"));
        foreach (var n in c.Nodes)
        {
            foreach (var x in n.Next.Where(x => !ids.Contains(x))) list.Add(new(true, n.Id, $"«{n.Id}»: следующего задания «{x}» нет"));
            if (n.Variant != "" && db.Get<DifficultyVariantDef>(n.Variant) is { } v && v.Scenario != n.Scenario)
                list.Add(new(true, n.Id, $"«{n.Id}»: вариант «{n.Variant}» — другого сценария («{v.Scenario}»)"));
            if (n.Requires != null)
                foreach (var r in Refs(n.Requires).Where(r => !ids.Contains(r)))
                    list.Add(new(true, n.Id, $"«{n.Id}»: условие доступа ссылается на задание «{r}», которого нет"));
            if (n.Requires != null)
                foreach (var f in Flags(n.Requires).Where(f => !c.Flags.Contains(f)))
                    list.Add(new(false, n.Id, $"«{n.Id}»: флаг «{f}» не объявлен в кампании"));
            if (n.NeedsUnlock && !c.Nodes.Select(x => db.Get<RewardSetDef>(x.RewardSet)).Any(s => s?.Entries.Any(e => e.Type == RewardType.UnlockScenario && e.Scenario == n.Scenario) == true))
                list.Add(new(true, n.Id, $"«{n.Id}»: ждёт открытия сценария «{n.Scenario}», но ни одна награда кампании его не открывает"));
            if (db.Get<RewardSetDef>(n.RewardSet) is { } set)
                foreach (var e in set.Entries.Where(e => e.Type == RewardType.SetFlag && !c.Flags.Contains(e.Flag)))
                    list.Add(new(false, n.Id, $"«{n.Id}»: награда ставит флаг «{e.Flag}», не объявленный в кампании"));
        }
        // Reachable: from the starts along «далее», or by an access of its own.
        var reach = new HashSet<string>(c.StartNodes);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var n in c.Nodes.Where(n => reach.Contains(n.Id)))
                foreach (var x in n.Next)
                    if (reach.Add(x)) grew = true;
            foreach (var n in c.Nodes.Where(n => !reach.Contains(n.Id) && n.Requires != null && Refs(n.Requires).All(reach.Contains)))
                if (reach.Add(n.Id)) grew = true;
        }
        foreach (var n in c.Nodes.Where(n => !reach.Contains(n.Id)))
            list.Add(new(true, n.Id, $"задание «{n.Id}» недостижимо от стартовых"));
        // Rings (ТЗ 9.5).
        var rings = Rings(c);
        foreach (var ring in rings)
        {
            string text = string.Join(" → ", ring.Append(ring[0]).Select(x => $"«{x}»"));
            switch (c.Cycles)
            {
                case CampaignCycles.Forbidden:
                    list.Add(new(true, ring[0], "кольцо заданий " + text + " — кольца запрещены; повтор задания — переигрыванием"));
                    break;
                case CampaignCycles.WithExit when !ring.Any(id => ToEnd(c, id)):
                    list.Add(new(true, ring[0], "кольцо " + text + " без пути к окончанию — выберите «бесконечная кампания» или добавьте выход"));
                    break;
            }
        }
        if (c.Cycles == CampaignCycles.Endless && rings.Count == 0)
            list.Add(new(false, "", "режим «бесконечная кампания», но колец нет"));
        return list;
    }

    /// <summary>The problems of a set of rewards.</summary>
    public static List<CampaignProblem> Check(ContentDatabase db, RewardSetDef set)
    {
        var list = new List<CampaignProblem>();
        foreach (var d in set.Entries.GroupBy(e => e.Id).Where(g => g.Key == "" || g.Count() > 1))
            list.Add(new(true, d.Key, d.Key == "" ? "награда без ID (ключ выдачи)" : $"ID награды «{d.Key}» повторяется — ключ выдачи должен быть единственным"));
        foreach (var e in set.Entries)
        {
            if (e.Policy == RewardPolicy.ExclusiveChoice && e.ChoiceGroup == "") list.Add(new(true, e.Id, $"«{e.Id}»: «одна на выбор» без группы выбора"));
            if (e.Policy == RewardPolicy.ExclusiveChoice && set.Entries.Count(x => x.Policy == RewardPolicy.ExclusiveChoice && x.ChoiceGroup == e.ChoiceGroup) < 2)
                list.Add(new(false, e.Id, $"«{e.Id}»: в группе выбора «{e.ChoiceGroup}» одна награда — выбирать не из чего"));
            if (e.Type == RewardType.Currency && e.Currency != "" && !db.All<UnitDef>().Any(u => u.Resources.Any(r => r.Resource == e.Currency)))
                list.Add(new(false, e.Id, $"«{e.Id}»: ресурс «{e.Currency}» не используется ни одним юнитом проекта"));
            if (e.Type == RewardType.Medal && e.Medal == "") list.Add(new(true, e.Id, $"«{e.Id}»: медаль без названия"));
        }
        return list;
    }

    private static IEnumerable<string> Refs(CampaignConditionDef c) =>
        (c.Node != "" && c.Type is CampaignConditionType.NodeCompleted or CampaignConditionType.Result ? new[] { c.Node } : Array.Empty<string>())
        .Concat(c.Type == CampaignConditionType.CompletedCount ? c.Nodes : Enumerable.Empty<string>())
        .Concat(c.Children.SelectMany(Refs));

    private static IEnumerable<string> Flags(CampaignConditionDef c) =>
        (c.Type == CampaignConditionType.Flag && c.Flag != "" ? new[] { c.Flag } : Array.Empty<string>()).Concat(c.Children.SelectMany(Flags));

    /// <summary>Does a mission lead to an end (a mission with no «далее»).</summary>
    private static bool ToEnd(CampaignDef c, string from)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>(new[] { from });
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id) || c.Node(id) is not { } n) continue;
            if (n.Next.Count == 0) return true;
            foreach (var x in n.Next) stack.Push(x);
        }
        return false;
    }

    public static List<List<string>> Rings(CampaignDef c)
    {
        var rings = new List<List<string>>();
        var seen = new HashSet<string>();
        foreach (var start in c.Nodes.Select(n => n.Id).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            var path = new List<string>();
            void Walk(string id)
            {
                if (id == start && path.Count > 0)
                {
                    if (seen.Add(string.Join(">", path.OrderBy(x => x, StringComparer.Ordinal)))) rings.Add(path.ToList());
                    return;
                }
                if (path.Contains(id) || string.CompareOrdinal(id, start) < 0 || path.Count > 64 || c.Node(id) is not { } n) return;
                path.Add(id);
                foreach (var x in n.Next) Walk(x);
                path.RemoveAt(path.Count - 1);
            }
            Walk(start);
        }
        return rings;
    }

    /// <summary>The scheme of a campaign for the editor and the screen of missions (the canvas of the graphs, read-only).</summary>
    public static RuleGraphDef Scheme(CampaignDef c, CampaignProgress? p = null, ContentDatabase? db = null)
    {
        var g = new RuleGraphDef { Id = "campaign", Name = c.DisplayName };
        // Columns by the depth from the starts, rows in the order of the list.
        var depth = new Dictionary<string, int>();
        var queue = new Queue<string>(c.StartNodes.Where(s => c.Node(s) != null));
        foreach (var s in queue) depth[s] = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var x in c.Node(id)!.Next.Where(x => c.Node(x) != null && !depth.ContainsKey(x)))
            {
                depth[x] = depth[id] + 1;
                queue.Enqueue(x);
            }
        }
        var rows = new Dictionary<int, int>();
        foreach (var n in c.Nodes)
        {
            int col = depth.GetValueOrDefault(n.Id, depth.Count == 0 ? 0 : depth.Values.Max() + 1);
            int row = rows.GetValueOrDefault(col);
            rows[col] = row + 1;
            string status = p == null ? "" : "\n" + EdUtil.EnumLabel(Status(c, p, n));
            g.Nodes.Add(new RuleNodeDef
            {
                Id = n.Id, Kind = c.StartNodes.Contains(n.Id) ? RuleNodeKind.Event : RuleNodeKind.Sequence,
                Label = (n.StageLabel != "" ? n.StageLabel + " · " : "") + n.Id + "\n" + (db?.Get<ScenarioDef>(n.Scenario)?.DisplayName ?? n.Scenario) +
                        (n.Variant != "" ? " · " + (db?.Get<DifficultyVariantDef>(n.Variant)?.DisplayName ?? n.Variant) : "") + status,
                X = n.X != 0 || n.Y != 0 ? n.X : col * 280, Y = n.X != 0 || n.Y != 0 ? n.Y : row * 150,
            });
        }
        foreach (var n in c.Nodes)
            foreach (var x in n.Next.Where(x => c.Node(x) != null && x != n.Id))
                g.Links.Add(new RuleLinkDef { From = n.Id, To = x });
        return g;
    }
}

/// <summary>The progress of the campaigns on the disk: one file per campaign, written atomically (a temp file, then a replace).</summary>
public static class CampaignStore
{
    public static string PathOf(string dir, string campaign) => Path.Combine(dir, campaign + ".json");

    public static CampaignProgress? Load(string dir, string campaign)
    {
        var f = PathOf(dir, campaign);
        if (!File.Exists(f)) return null;
        try
        {
            return ContentJson.Deserialize<CampaignProgress>(File.ReadAllText(f));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string dir, CampaignProgress p)
    {
        Directory.CreateDirectory(dir);
        var f = PathOf(dir, p.Campaign);
        var tmp = f + ".tmp";
        File.WriteAllText(tmp, ContentJson.Serialize(p));
        if (File.Exists(f)) File.Replace(tmp, f, null);
        else File.Move(tmp, f);
    }

    public static void Delete(string dir, string campaign)
    {
        var f = PathOf(dir, campaign);
        if (File.Exists(f)) File.Delete(f);
    }
}
