using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>
/// The start of a battle, frozen (ТЗ ч. 5, 18.7, T26; docs/DECISIONS_5.md §86): a copy of the content, the scenario, the
/// resolved seed (never «random» again) and the fingerprint of the first state. «Сброс» builds the battle from it anew —
/// edits of the content after the start do not leak in — and <see cref="Check"/> says which parts differ from the start.
/// </summary>
public sealed class BattleSnapshot
{
    public ContentDatabase Content { get; private init; } = null!;
    public string Scenario { get; private init; } = "";
    public ulong Seed { get; private init; }
    public bool Admin { get; private init; }
    public bool AutoPlayer { get; private init; }
    /// <summary>The squad chosen before the battle (part 6, §96); null — the scenario's default.</summary>
    public List<string>? Roster { get; private init; }
    public SimDigest Start { get; private set; } = new();

    /// <summary>Freezes the content and creates the battle from the copy.</summary>
    public static (BattleSnapshot Snapshot, Simulation Sim) Begin(ContentDatabase db, ScenarioDef sc, ulong? seed = null, bool admin = false, bool autoPlayer = false,
        IReadOnlyList<string>? roster = null)
    {
        var frozen = db.Clone();
        if (frozen.Get<ScenarioDef>(sc.Id) is not { } fsc || !ReferenceEquals(db.Get<ScenarioDef>(sc.Id), sc))
        {
            // A scenario not (or not like this) in the database — a test of the editor's copy.
            fsc = ContentJson.Clone(sc);
            frozen.Table(DefCategory.Scenario).Replace(fsc); // Add keeps an existing ID — the copy must win
        }
        var snap = new BattleSnapshot
        {
            Content = frozen, Scenario = fsc.Id, Seed = seed ?? (sc.Seed != 0 ? sc.Seed : (ulong)DateTime.UtcNow.Ticks), Admin = admin, AutoPlayer = autoPlayer,
            Roster = roster?.ToList(),
        };
        var sim = snap.Create();
        snap.Start = sim.Digest();
        return (snap, sim);
    }

    private Simulation Create() => ScenarioSystem.Create(Content, Content.Get<ScenarioDef>(Scenario)!, Seed, Admin, AutoPlayer, Roster);

    /// <summary>The start of a battle saved in a replay (a loaded battle — «начать заново» goes back to its start).</summary>
    public static BattleSnapshot FromReplay(BattleReplay r, ContentDatabase content)
    {
        var snap = new BattleSnapshot { Content = content, Scenario = r.Scenario, Seed = r.Seed, Admin = r.Admin, AutoPlayer = r.AutoPlayer, Roster = r.Roster?.ToList() };
        snap.Start = snap.Create().Digest();
        return snap;
    }

    /// <summary>The battle as it was at the start (the same content, scenario and seed).</summary>
    public Simulation Restore() => Create();

    /// <summary>The parts of the state that differ from the start; empty — the battle is at its start.</summary>
    public List<string> Check(Simulation sim) => Start.Diff(sim.Digest());
}

/// <summary>
/// The replay of a battle, MVP (ТЗ ч. 5, 19.7; docs/DECISIONS_5.md §86): the seed, the versions of the content (and,
/// optionally, the content itself — then it reproduces after any later edits), the player's calls with their ticks and
/// the fingerprints of the start and the end. Playing it back repeats the calls tick by tick and compares the end.
/// </summary>
public sealed class BattleReplay
{
    public const int CurrentFormat = 1;

    public int FormatVersion { get; set; } = CurrentFormat;
    public string Scenario { get; set; } = "";
    public ulong Seed { get; set; }
    public bool Admin { get; set; }
    public bool AutoPlayer { get; set; }
    /// <summary>The squad of the battle (part 6); null — the scenario's default.</summary>
    public List<string>? Roster { get; set; }
    public int ContentFormat { get; set; } = ContentJson.CurrentFormatVersion;
    public int SimVersion { get; set; }
    /// <summary>«category:id» → version of every definition at the start.</summary>
    public Dictionary<string, int> Versions { get; set; } = new();
    /// <summary>A hash of the whole content: equal versions with a different hash — edited without publishing.</summary>
    public string ContentHash { get; set; } = "";
    /// <summary>The content itself (files of the project), when the replay must survive later edits.</summary>
    public Dictionary<string, string>? Content { get; set; }
    public string StartHash { get; set; } = "";
    public long EndTick { get; set; }
    public string EndHash { get; set; } = "";
    public List<ReplayEntry> Entries { get; set; } = new();

    /// <summary>Starts writing the player's calls of a battle created from the snapshot.</summary>
    public static BattleReplay Record(BattleSnapshot snap, Simulation sim, bool withContent = false)
    {
        var r = new BattleReplay
        {
            Scenario = snap.Scenario, Seed = snap.Seed, Admin = snap.Admin, AutoPlayer = snap.AutoPlayer, Roster = snap.Roster?.ToList(), SimVersion = sim.Rules.Time.SimVersion,
            Versions = VersionsOf(snap.Content), ContentHash = HashOf(snap.Content), StartHash = sim.Digest().Hash,
        };
        if (withContent)
        {
            var mem = new MemoryStorage();
            ContentIO.Save(snap.Content, mem);
            r.Content = new Dictionary<string, string>(mem.Files);
        }
        sim.Input.Journal = r.Entries;
        return r;
    }

    /// <summary>Stops writing; the end is the current state.</summary>
    public BattleReplay Finish(Simulation sim)
    {
        if (ReferenceEquals(sim.Input.Journal, Entries)) sim.Input.Journal = null;
        EndTick = sim.Tick;
        EndHash = sim.Digest().Hash;
        return this;
    }

    public static Dictionary<string, int> VersionsOf(ContentDatabase db) =>
        db.AllDefs().OrderBy(d => d.Category).ThenBy(d => d.Id, StringComparer.Ordinal).ToDictionary(d => $"{d.Category}:{d.Id}", d => d.Version);

    public static string HashOf(ContentDatabase db) =>
        Simulation.StableHash(ContentJson.Serialize(db.Rules) + string.Concat(db.AllDefs().OrderBy(d => d.Category).ThenBy(d => d.Id, StringComparer.Ordinal)
            .Select(d => JsonSerializer.Serialize(d, d.GetType(), ContentJson.Options)))).ToString("X");

    /// <summary>What differs in the content from the start of the record: changed versions, added, removed, unpublished edits.</summary>
    public List<string> Changes(ContentDatabase db)
    {
        var now = VersionsOf(db);
        var list = new List<string>();
        foreach (var (k, v) in Versions)
        {
            if (!now.TryGetValue(k, out var n)) list.Add($"удалён {k}");
            else if (n != v) list.Add($"{k}: версия {v} → {n}");
        }
        foreach (var k in now.Keys.Where(k => !Versions.ContainsKey(k))) list.Add($"добавлен {k}");
        if (list.Count == 0 && HashOf(db) != ContentHash) list.Add("содержимое изменено без смены версий (неопубликованные правки)");
        return list;
    }

    /// <summary>
    /// The battle saved in the middle (ТЗ ч. 6, T28; docs/DECISIONS_6.md §93.7, §97): a copy of the record so far, with the
    /// content of <paramref name="snap"/>, ending at the current tick. The record itself goes on.
    /// </summary>
    public BattleReplay SaveAt(BattleSnapshot snap, Simulation sim)
    {
        var r = new BattleReplay
        {
            Scenario = Scenario, Seed = Seed, Admin = Admin, AutoPlayer = AutoPlayer, Roster = Roster?.ToList(), SimVersion = SimVersion,
            Versions = new Dictionary<string, int>(Versions), ContentHash = ContentHash, StartHash = StartHash, Entries = Entries.ToList(),
            Content = Content != null ? new Dictionary<string, string>(Content) : null,
        };
        if (r.Content == null)
        {
            var mem = new MemoryStorage();
            ContentIO.Save(snap.Content, mem);
            r.Content = new Dictionary<string, string>(mem.Files);
        }
        r.EndTick = sim.Tick;
        r.EndHash = sim.Digest().Hash;
        return r;
    }

    /// <summary>
    /// Loads a battle saved with <see cref="SaveAt"/>: plays it back to its tick on its own content and checks the
    /// fingerprint. When it matches, the battle goes on from there and its calls are written on into this record.
    /// <paramref name="created"/> sees the battle before the first tick (to attach a report or breakpoints).
    /// </summary>
    public ReplayResult Resume(Action<Simulation>? created = null)
    {
        var res = Play(null, created);
        if (res.Matches && res.Sim != null) res.Sim.Input.Journal = Entries;
        return res;
    }

    /// <summary>The content saved in the replay; null — not saved.</summary>
    public ContentDatabase? SavedContent()
    {
        if (Content == null) return null;
        var mem = new MemoryStorage();
        foreach (var (k, v) in Content) mem.Files[k] = v;
        return ContentIO.Load(mem, validate: false).Db;
    }

    /// <summary>
    /// Plays the battle back on <paramref name="db"/> (null — the saved content): the same seed, the same calls at the
    /// same ticks, up to the end tick. The result says whether the end matches and, if not, which parts differ.
    /// </summary>
    public ReplayResult Play(ContentDatabase? db = null, Action<Simulation>? created = null)
    {
        var content = db ?? SavedContent() ?? throw new InvalidOperationException("В реплее нет сохранённого контента — укажите проект");
        var res = new ReplayResult { ContentChanges = Changes(content), Content = content };
        var sc = content.Get<ScenarioDef>(Scenario);
        if (sc == null)
        {
            res.Problems.Add($"сценария «{Scenario}» нет");
            return res;
        }
        var sim = ScenarioSystem.Create(content, sc, Seed, Admin, AutoPlayer, Roster);
        res.Sim = sim;
        created?.Invoke(sim);
        int i = 0;
        while (true)
        {
            while (i < Entries.Count && Entries[i].Tick <= sim.Tick)
            {
                if (!sim.Input.Apply(Entries[i])) res.Skipped++;
                i++;
            }
            if (sim.Tick >= EndTick) break;
            sim.Step();
        }
        var end = sim.Digest();
        res.Matches = end.Hash == EndHash;
        if (!res.Matches)
        {
            var want = EndHash.Split(':');
            var got = end.Hash.Split(':');
            for (int k = 1; k < Math.Min(want.Length, got.Length) && k - 1 < SimDigest.Names.Length; k++)
                if (want[k] != got[k]) res.Problems.Add($"расходится: {SimDigest.Names[k - 1]}");
        }
        if (res.Skipped > 0) res.Problems.Add($"пропущено действий игрока: {res.Skipped} (их юнитов нет)");
        return res;
    }

    private static readonly JsonSerializerOptions Json = new(ContentJson.Options) { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BattleReplay? FromJson(string json) => JsonSerializer.Deserialize<BattleReplay>(json, Json);
}

public sealed class ReplayResult
{
    public Simulation? Sim { get; set; }
    /// <summary>The content the battle was played on.</summary>
    public ContentDatabase? Content { get; set; }
    public bool Matches { get; set; }
    public int Skipped { get; set; }
    public List<string> ContentChanges { get; set; } = new();
    public List<string> Problems { get; } = new();

    public string Text => (Matches ? "Реплей совпал с записью" : "Реплей разошёлся с записью")
                          + (Problems.Count > 0 ? ": " + string.Join("; ", Problems) : "")
                          + (ContentChanges.Count > 0 ? $". Контент изменён после записи ({ContentChanges.Count}): " + string.Join(", ", ContentChanges.Take(6)) + (ContentChanges.Count > 6 ? "…" : "") : "");
}
