using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.8 — campaigns over the scenarios, rewards given once (ТЗ ч. 6, 9–10; T26–T27; docs/DECISIONS_6.md §103).</summary>
public class Part6CampaignTests
{
    private static Dictionary<OutcomeMetric, double> Won(double baseHp = 1, double deaths = 0) => new()
    {
        [OutcomeMetric.Victory] = 1, [OutcomeMetric.BaseHpRemaining] = baseHp, [OutcomeMetric.Leaks] = 0,
        [OutcomeMetric.TimeToWin] = 300, [OutcomeMetric.Deaths] = deaths, [OutcomeMetric.UnitsDeployed] = 0,
    };

    private static Dictionary<OutcomeMetric, double> Lost() => new() { [OutcomeMetric.Victory] = 0, [OutcomeMetric.BaseHpRemaining] = 0 };

    /// <summary>A → B and A → C; C only if A kept half of its base.</summary>
    private static (ContentDatabase Db, CampaignDef C) AbC()
    {
        var db = TestUtil.Shipped();
        var c = new CampaignDef
        {
            Id = "abc", Name = "A-B-C", StartNodes = { "a" },
            Nodes =
            {
                new CampaignNodeDef { Id = "a", Scenario = "td_pass", Next = { "b", "c" }, RewardSet = "rw_a" },
                new CampaignNodeDef { Id = "b", Scenario = "td_pass_fork" },
                new CampaignNodeDef
                {
                    Id = "c", Scenario = "td_pass_defense", Variant = "td_pass_defense_siege",
                    Requires = new CampaignConditionDef { Type = CampaignConditionType.Result, Node = "a", Criterion = new OutcomeCriterionDef { Metric = OutcomeMetric.BaseHpRemaining, Value = 0.5 } },
                },
            },
        };
        db.Add(c);
        db.Add(new RewardSetDef
        {
            Id = "rw_a", Name = "Награды A",
            Entries =
            {
                new RewardEntryDef { Id = "medal", Type = RewardType.Medal, Medal = "Первая", Policy = RewardPolicy.FirstClearOnly },
                new RewardEntryDef { Id = "gold", Type = RewardType.Currency, Currency = "mana", Amount = 5, Policy = RewardPolicy.Repeatable },
                new RewardEntryDef { Id = "flawless", Type = RewardType.Medal, Medal = "Без потерь", Criterion = new OutcomeCriterionDef { Metric = OutcomeMetric.Deaths, Compare = CompareMode.AtMost, Value = 0 } },
            },
        });
        return (db, c);
    }

    [Fact]
    public void T26_AThenBAndC_ByTheirConditions_OverTheOriginalScenarios()
    {
        var (db, c) = AbC();
        Assert.Empty(Campaigns.Check(db, c).Where(p => p.Error));
        var p = Campaigns.Start(c);
        CampaignNodeStatus S(string id) => Campaigns.Status(c, p, c.Node(id)!);
        Assert.Equal(CampaignNodeStatus.Available, S("a"));
        Assert.Equal(CampaignNodeStatus.Locked, S("b"));
        Assert.Equal(CampaignNodeStatus.Locked, S("c"));
        Assert.Contains("пройдите «a»", Campaigns.WhyLocked(c, p, c.Node("b")!));

        // A lost: nothing opens; A won with 40 % of the base: B opens, C does not (its threshold).
        Campaigns.Complete(db, c, p, "a", Lost(), "battle-1");
        Assert.Equal(CampaignNodeStatus.Available, S("a"));
        Assert.Equal(CampaignNodeStatus.Locked, S("b"));
        var done = Campaigns.Complete(db, c, p, "a", Won(baseHp: 0.4), "battle-2");
        Assert.True(done.Victory);
        Assert.Equal(new[] { "b" }, done.NewlyAvailable);
        Assert.Equal(CampaignNodeStatus.Completed, S("a"));
        Assert.Equal(CampaignNodeStatus.Available, S("b"));
        Assert.Equal(CampaignNodeStatus.Locked, S("c"));
        Assert.Contains("BaseHpRemaining", c.Node("c")!.Requires!.Criterion!.Metric.ToString());

        // A replayed better: C opens too (the best result counts); the missions are the original scenarios, not copies.
        done = Campaigns.Complete(db, c, p, "a", Won(baseHp: 0.8), "battle-3");
        Assert.Equal(new[] { "c" }, done.NewlyAvailable);
        Assert.Equal(CampaignNodeStatus.Available, S("c"));
        Assert.Same(db.Get<ScenarioDef>("td_pass_defense"), db.Get<ScenarioDef>(c.Node("c")!.Scenario));
        Assert.Equal("td_pass_defense", db.Get<DifficultyVariantDef>(c.Node("c")!.Variant)!.Scenario);
    }

    [Fact]
    public void T27_TheSameEventTwice_GivesAOnceOnlyRewardOnce()
    {
        var (db, c) = AbC();
        var p = Campaigns.Start(c);
        var first = Campaigns.Complete(db, c, p, "a", Won(), "battle-7");
        Assert.Contains("медаль «Первая»", first.Granted);
        Assert.Contains("+5 mana", first.Granted);
        Assert.Contains("медаль «Без потерь»", first.Granted);
        int ledger = p.Ledger.Count;

        // The same completion event again (a second «victory» signal of the same battle): nothing at all.
        var again = Campaigns.Complete(db, c, p, "a", Won(), "battle-7");
        Assert.True(again.Duplicate);
        Assert.Empty(again.Granted);
        Assert.Equal(ledger, p.Ledger.Count);
        Assert.Equal(1, p.Record("a")!.Victories);

        // A replay (another battle): the once-only medals are not given again, the repeatable currency is.
        var replay = Campaigns.Complete(db, c, p, "a", Won(deaths: 2), "battle-8");
        Assert.DoesNotContain("медаль «Первая»", replay.Granted);
        Assert.Contains(replay.Skipped, s => s.StartsWith("медаль «Первая»") && s.EndsWith("уже получено"));
        Assert.Contains(replay.Skipped, s => s.Contains("не выполнено: Потери игрока ≤ 0"));
        Assert.Equal(10, p.Currency["mana"]);
        Assert.Single(p.Medals, m => m == "Первая");
        Assert.Equal(p.Ledger.Count, p.Ledger.Select(g => g.Key).Distinct().Count()); // every key once
        Assert.All(p.Ledger, g => Assert.StartsWith(p.RunId + "/a/", g.Key));
    }

    [Fact]
    public void PerDifficulty_ExclusiveChoice_AndTheShippedCampaign()
    {
        var db = TestUtil.Shipped();
        var c = db.Get<CampaignDef>("pass_campaign")!;
        Assert.Empty(Campaigns.Check(db, c));
        foreach (var r in db.All<RewardSetDef>()) Assert.Empty(Campaigns.Check(db, r).Where(x => x.Error));
        var p = Campaigns.Start(c);
        Campaigns.Complete(db, c, p, "scout", Won(baseHp: 0.9), "e1");
        Assert.Contains("pass_held", p.Flags);
        Assert.Equal(CampaignNodeStatus.Available, Campaigns.Status(c, p, c.Node("fork")!));
        Assert.Equal(CampaignNodeStatus.Available, Campaigns.Status(c, p, c.Node("siege")!));
        Assert.Equal(CampaignNodeStatus.Locked, Campaigns.Status(c, p, c.Node("titan")!));

        // Siege: a medal per variant of difficulty and a trophy to choose — once.
        var s1 = Campaigns.Complete(db, c, p, "siege", Won(), "e2");
        Assert.Contains("медаль «Осада выдержана»", s1.Granted);
        var choice = Assert.Single(s1.Choices);
        Assert.Equal(new[] { "ranger", "priestess" }, choice.Entries);
        Assert.Null(Campaigns.Choose(db, p, choice, "priestess"));
        Assert.Contains("priestess", p.UnlockedUnits);
        Assert.NotNull(Campaigns.Choose(db, p, choice, "ranger")); // already chosen
        Assert.DoesNotContain("elf_ranger", p.UnlockedUnits);
        var s2 = Campaigns.Complete(db, c, p, "siege", Won(), "e3");
        Assert.Empty(s2.Choices);
        Assert.Empty(s2.Granted); // the same variant again
        Assert.Equal(CampaignNodeStatus.Available, Campaigns.Status(c, p, c.Node("titan")!)); // flag and one of the two

        // The preview of the rewards says the policy of each.
        Assert.Contains(Campaigns.Preview(db.Get<RewardSetDef>("rw_pass_siege")!), l => l.Contains("раз на вариант сложности"));
    }

    [Fact]
    public void TheCheck_Rings_Unreachable_UnknownLinks_AndTheEndlessMode()
    {
        var db = TestUtil.Shipped();
        var c = new CampaignDef
        {
            Id = "ring", StartNodes = { "a" },
            Nodes =
            {
                new CampaignNodeDef { Id = "a", Scenario = "td_pass", Next = { "b" } },
                new CampaignNodeDef { Id = "b", Scenario = "td_pass_fork", Next = { "a", "nope" } },
                new CampaignNodeDef { Id = "lost", Scenario = "titan_lair" },
                new CampaignNodeDef { Id = "v", Scenario = "td_pass", Variant = "td_pass_defense_siege", Requires = new CampaignConditionDef { Type = CampaignConditionType.NodeCompleted, Node = "a" } },
            },
        };
        var problems = Campaigns.Check(db, c);
        Assert.Contains(problems, p => p.Error && p.Message.StartsWith("кольцо заданий"));
        Assert.Contains(problems, p => p.Error && p.Message.Contains("следующего задания «nope» нет"));
        Assert.Contains(problems, p => p.Error && p.Message.Contains("«lost» недостижимо"));
        Assert.Contains(problems, p => p.Error && p.Message.Contains("другого сценария"));
        c.Cycles = CampaignCycles.WithExit;
        Assert.Contains(Campaigns.Check(db, c), p => p.Message.Contains("без пути к окончанию"));
        c.Nodes[1].Next.Remove("nope");
        c.Nodes[1].Next.Add("end");
        c.Nodes.Add(new CampaignNodeDef { Id = "end", Scenario = "titan_lair" });
        Assert.DoesNotContain(Campaigns.Check(db, c), p => p.Message.Contains("кольц"));
        c.Cycles = CampaignCycles.Endless;
        Assert.DoesNotContain(Campaigns.Check(db, c), p => p.Error && p.Message.Contains("кольц"));
    }

    [Fact]
    public void TheProgress_IsSavedAtomically_AndAnEditedCampaignResetsNothing()
    {
        var (db, c) = AbC();
        var p = Campaigns.Start(c);
        Campaigns.Complete(db, c, p, "a", Won(), "x1");
        var dir = Path.Combine(Path.GetTempPath(), "hex_campaign_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            CampaignStore.Save(dir, p);
            CampaignStore.Save(dir, p); // the second write replaces the first
            Assert.False(File.Exists(CampaignStore.PathOf(dir, c.Id) + ".tmp"));
            var back = CampaignStore.Load(dir, c.Id)!;
            Assert.Equal(p.RunId, back.RunId);
            Assert.Equal(p.Ledger.Select(g => g.Key), back.Ledger.Select(g => g.Key));
            Assert.Equal(1, back.Record("a")!.Victories);
            Assert.True(Campaigns.Complete(db, c, back, "a", Won(), "x1").Duplicate); // processed events survive the save

            // The campaign edited: a mission removed, the version raised — the progress stays, the change is told.
            c.Nodes.RemoveAll(n => n.Id == "a");
            c.StartNodes.Clear();
            c.StartNodes.Add("b");
            c.Version++;
            var notes = Campaigns.Reconcile(c, back);
            Assert.Contains(notes, n => n.Contains("изменена после начала"));
            Assert.Contains(notes, n => n.Contains("«a» в кампании больше нет"));
            Assert.Equal(1, back.Record("a")!.Victories);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TheResultOfABattle_ComesFromItsReport()
    {
        var db = TestUtil.Shipped();
        var sc = db.Get<ScenarioDef>("arena_duel")!;
        var sim = ScenarioSystem.Create(db, sc, 3);
        var report = BattleReport.Attach(sim, sc.DisplayName, 3);
        for (int i = 0; i < 40; i++) sim.Step();
        var r = CampaignResults.Of(sim, report.Fill());
        Assert.Equal(0, r[OutcomeMetric.Victory]);
        Assert.Equal(1, r[OutcomeMetric.BaseHpRemaining]); // no bases
        Assert.True(r[OutcomeMetric.TimeToWin] > 0);
        Assert.Equal(6, r.Count);
    }
}
