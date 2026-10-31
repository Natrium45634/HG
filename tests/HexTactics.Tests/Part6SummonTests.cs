using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.12 — summons of sub-units and their simple AI (ТЗ ч. 6, 20; T47–T55; docs/DECISIONS_6.md §106).</summary>
public class Part6SummonTests
{
    /// <summary>
    /// A beastmaster of the player at (3,3) looking east, a knight of the player at (2,5), a goblin of the enemy far at (11,3);
    /// a summon «test» (a copy of «Стая волков» to edit) and its skill «test_call» (no cooldown). The AI and auto-attacks are off.
    /// </summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene(System.Action<SummonDef>? summon = null, System.Action<ScenarioDef>? edit = null,
        System.Action<ContentDatabase>? dbEdit = null)
    {
        var db = TestUtil.Shipped();
        var def = ContentJson.Clone(db.Get<SummonDef>("wolf_pack")!);
        def.Id = "test";
        summon?.Invoke(def);
        db.Add(def);
        var skill = ContentJson.Clone(db.Skill("call_wolves")!);
        skill.Id = "test_call";
        skill.Cooldown = 0;
        skill.Stages[0].Effects[0].Summon = "test";
        db.Add(skill);
        db.Unit("beastmaster")!.Skills.Add("test_call");
        dbEdit?.Invoke(db);
        var map = TestUtil.Field(14, 8);
        map.Id = "summon_field";
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "summon_test", Name = "Призывы", Map = map.Id,
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 9999 } },
            Defeat = new List<ConditionDef>(),
            Units =
            {
                new ScenarioUnitDef { Unit = "beastmaster", Faction = "player", C = new HexCoord(3, 3), Facing = HexDirection.E, Tag = "bm" },
                new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(2, 5), Facing = HexDirection.E, Tag = "ally" },
                new ScenarioUnitDef { Unit = "goblin", Faction = "enemy", C = new HexCoord(11, 3), Facing = HexDirection.W, Tag = "foe" },
            },
        };
        edit?.Invoke(sc);
        db.Add(sc);
        return (db, sc);
    }

    private static Simulation Start(ContentDatabase db, ScenarioDef sc, bool ai = false)
    {
        var sim = ScenarioSystem.Create(db, sc, 4);
        sim.Ai.Enabled = ai;
        foreach (var u in sim.Units) sim.AutoAttacks.DisabledUnits.Add(u.Id);
        sim.Step();
        return sim;
    }

    private static Unit U(Simulation sim, string tag) => sim.Units.First(u => u.Tag == tag);
    private static List<Unit> Summoned(Simulation sim) => sim.Units.Where(u => u.Alive && u.Summon != null).ToList();
    private static void Run(Simulation sim, double seconds)
    {
        for (int i = 0; i < sim.TicksFor(seconds); i++) sim.Step();
    }

    /// <summary>The beastmaster casts the test summon; the battle runs until it is released.</summary>
    private static void Call(Simulation sim, double run = 1.5)
    {
        Assert.Null(sim.Enqueue(U(sim, "bm"), Command.Skill("test_call", null)));
        Run(sim, run);
    }

    private static SummonDef Def(Simulation sim, string id = "test") => sim.Content.Get<SummonDef>(id)!;

    [Fact]
    public void T47_TwoSubUnits_InFreeAdjacentHexes_LinkedToTheSummoner()
    {
        var (db, sc) = Scene();
        var sim = Start(db, sc);
        var rep = BattleReport.Attach(sim);
        var bm = U(sim, "bm");
        Call(sim);
        var wolves = Summoned(sim);
        Assert.Equal(2, wolves.Count);
        Assert.All(wolves, w =>
        {
            Assert.Equal("wolf", w.Def.Id);
            Assert.Equal(bm.Id, w.SummonerId);
            Assert.Equal(bm.Id, w.Summon!.OwnerId);
            Assert.Equal(1, w.Summon.Depth);
            Assert.Equal(bm.FactionId, w.FactionId);
            Assert.Equal(1, HexCoord.Distance(w.Pos, bm.Pos));
            Assert.True(w.AiControlled, "по умолчанию призванные управляются ИИ");
        });
        Assert.NotEqual(wolves[0].Pos, wolves[1].Pos);
        Assert.Equal(wolves[0].Summon!.Group, wolves[1].Summon!.Group);
        // The front of the summoner first (it looks east).
        Assert.Contains(wolves, w => w.Pos == bm.Pos.Neighbor(HexDirection.E));
        var r = rep.Fill();
        Assert.Equal(2, r.SummonsCreated);
        Assert.Equal(2, r.ActiveSummonsPeak);
        Assert.Contains("Призванные: создано 2", r.ToText());
    }

    [Theory]
    [InlineData(SummonFailure.CancelAll, 0, 30.0)]
    [InlineData(SummonFailure.SpawnPartial, 1, 15.0)]
    [InlineData(SummonFailure.SearchNearest, 2, 0.0)]
    public void T48_BlockedHexes_CancelPartialOrNearest_WithTheRefund(SummonFailure policy, int expected, double refund)
    {
        // Two hexes in front of the summoner; the first is taken by the knight.
        var (db, sc) = Scene(d =>
        {
            d.Placement = SummonPlacement.Offsets;
            d.Offsets = new List<HexCoord> { new(1, 0), new(2, 0) };
            d.OnBlocked = policy;
            d.SearchRadius = 1;
        }, s => s.Units[1].C = new HexCoord(4, 3));
        var sim = Start(db, sc);
        U(sim, "bm").Resources["mana"].Current = 30; // the refund is not capped by the maximum
        var rep = BattleReport.Attach(sim);
        Call(sim);
        Assert.Equal(expected, Summoned(sim).Count);
        double refunded = sim.Log.Entries.Where(e => e.Type == SimEventType.ResourceChanged && e.Text.Contains("возврат")).Sum(e => e.Value);
        Assert.Equal(refund, refunded, 3);
        Assert.Equal(expected < 2 ? 1 : 0, rep.Fill().SummonFailedAttempts);
        // Never two creatures in one hex against the rules of size.
        Assert.Equal(Summoned(sim).Count, Summoned(sim).Select(w => w.Pos).Distinct().Count());
        Assert.DoesNotContain(Summoned(sim), w => w.Pos == U(sim, "ally").Pos);
    }

    [Fact]
    public void T48_QueueUntilFree_TheSecondComes_WhenTheHexIsFreed_OrIsRefundedAtTheTimeout()
    {
        foreach (bool free in new[] { true, false })
        {
            var (db, sc) = Scene(d =>
            {
                d.Placement = SummonPlacement.Offsets;
                d.Offsets = new List<HexCoord> { new(1, 0), new(2, 0) };
                d.OnBlocked = SummonFailure.QueueUntilFree;
                d.QueueTimeout = 3;
            }, s => s.Units[1].C = new HexCoord(4, 3));
            var sim = Start(db, sc);
            Call(sim, 1);
            Assert.Single(Summoned(sim));
            Assert.Equal(1, sim.Summons.PendingCount);
            if (free) sim.Combat.Kill(U(sim, "ally"), null, "тест: гекс освобождён");
            Run(sim, 4);
            Assert.Equal(free ? 2 : 1, Summoned(sim).Count);
            Assert.Equal(0, sim.Summons.PendingCount);
            Assert.Equal(free ? 0 : 1, sim.Log.Entries.Count(e => e.Type == SimEventType.SummonFailed));
            Assert.Equal(free ? 0 : 15, sim.Log.Entries.Where(e => e.Type == SimEventType.ResourceChanged && e.Text.Contains("возврат")).Sum(e => e.Value), 3);
        }
    }

    [Fact]
    public void T49_Follow_StaysCloseAndOffTheSummonersHex()
    {
        var (db, sc) = Scene(d => d.Behavior = SummonBehavior.FollowSummoner);
        var sim = Start(db, sc, ai: true);
        var bm = U(sim, "bm");
        Call(sim);
        Assert.Null(sim.Enqueue(bm, Command.Move(new HexCoord(3, 7))));
        for (int i = 0; i < sim.TicksFor(10); i++)
        {
            sim.Step();
            Assert.All(Summoned(sim), w => Assert.NotEqual(bm.Pos, w.Pos));
        }
        Assert.Equal(new HexCoord(3, 7), bm.Pos);
        Assert.All(Summoned(sim), w => Assert.True(HexCoord.Distance(w.Pos, bm.Pos) <= Def(sim).Distance, $"{w.Name} в {w.Pos}, призыватель в {bm.Pos}"));
    }

    [Fact]
    public void T49_Guard_InterceptsTheAttackerInsideTheLeash_AndAttackSummonerTarget_FollowsTheOrder()
    {
        // Guard: a goblin comes for the summoner — the wolves go for it, never further than the leash.
        var (db, sc) = Scene(edit: s => s.Units[2].C = new HexCoord(7, 3));
        var sim = Start(db, sc, ai: true);
        var bm = U(sim, "bm");
        var foe = U(sim, "foe");
        Call(sim, 0.6);
        int maxAway = 0;
        bool engaged = false;
        for (int i = 0; i < sim.TicksFor(8) && foe.Alive; i++)
        {
            sim.Step();
            foreach (var w in Summoned(sim))
            {
                maxAway = System.Math.Max(maxAway, HexCoord.Distance(w.Pos, bm.Pos));
                engaged |= w.Ai.TargetUnitId == foe.Id;
            }
        }
        Assert.True(engaged, "охрана не перехватила гоблина");
        Assert.True(maxAway <= Def(sim).Leash + 1, $"волк ушёл на {maxAway} от призывателя (поводок {Def(sim).Leash})");

        // «Атаковать цель призывателя»: the order of the summoner names the target; without it — the fallback (follow).
        var (db2, sc2) = Scene(d => d.Behavior = SummonBehavior.AttackSummonerTarget, s => s.Units[2].C = new HexCoord(9, 3));
        var s2 = Start(db2, sc2, ai: true);
        var bm2 = U(s2, "bm");
        var foe2 = U(s2, "foe");
        Call(s2, 0.6);
        Run(s2, 1);
        Assert.All(Summoned(s2), w => Assert.True(HexCoord.Distance(w.Pos, bm2.Pos) <= 2, "без цели призывателя — запасное «следовать»"));
        Assert.Null(s2.Enqueue(bm2, Command.Attack(foe2.Id)));
        Run(s2, 0.6);
        Assert.All(Summoned(s2), w => Assert.Equal(foe2.Id, w.Ai.TargetUnitId));
    }

    [Theory]
    [InlineData(SummonEnd.BecomeIndependent)]
    [InlineData(SummonEnd.Vanish)]
    [InlineData(SummonEnd.Die)]
    [InlineData(SummonEnd.Neutralize)]
    [InlineData(SummonEnd.TransferControl)]
    public void T50_TheSummonerDies_TheDespawnPolicyRuns(SummonEnd policy)
    {
        var (db, sc) = Scene(d => d.OnSummonerDeath = policy);
        var sim = Start(db, sc);
        var bm = U(sim, "bm");
        var knight = U(sim, "ally");
        if (policy == SummonEnd.TransferControl) bm.SummonerId = knight.Id; // the beastmaster was summoned by the knight
        Call(sim);
        var wolves = Summoned(sim);
        Assert.Equal(2, wolves.Count);
        sim.Combat.Kill(bm, null, "тест");
        sim.Step();
        int deaths = sim.Log.Entries.Count(e => e.Type == SimEventType.Death && wolves.Any(w => w.Id == e.TargetUnit));
        switch (policy)
        {
            case SummonEnd.BecomeIndependent:
                Assert.All(wolves, w => Assert.True(w.Alive && w.SummonerId == -1 && w.Summon!.Independent && w.Summon.Behavior == SummonBehavior.AttackNearestEnemy));
                break;
            case SummonEnd.Vanish:
                Assert.All(wolves, w => Assert.True(!w.Alive && w.DeathCause == DeathCause.Removed));
                Assert.Equal(0, deaths); // no events of death, no loot
                Assert.Equal(2, sim.Log.Entries.Count(e => e.Type == SimEventType.SummonEnded));
                Assert.All(wolves, w => Assert.DoesNotContain(w, sim.Map.Get(w.Pos)!.Occupants));
                break;
            case SummonEnd.Die:
                Assert.All(wolves, w => Assert.False(w.Alive));
                Assert.Equal(2, deaths);
                break;
            case SummonEnd.Neutralize:
                Assert.All(wolves, w => Assert.True(w.Alive && w.FactionId == "neutral" && w.SummonerId == -1));
                break;
            case SummonEnd.TransferControl:
                Assert.All(wolves, w => Assert.True(w.Alive && w.SummonerId == knight.Id && w.FactionId == knight.FactionId));
                break;
        }
    }

    [Fact]
    public void T50_TheLifetimeEnds_ByTime_ByTheTargetsDeath()
    {
        var (db, sc) = Scene(d => d.Duration = 2);
        var sim = Start(db, sc);
        Call(sim, 1);
        Assert.Equal(2, Summoned(sim).Count);
        Run(sim, 2);
        Assert.Empty(Summoned(sim));
        Assert.Equal(0, sim.Log.Entries.Count(e => e.Type == SimEventType.Death)); // «исчезает»

        var (db2, sc2) = Scene(d => d.Lifetime = SummonLifetime.UntilTargetDies);
        var s2 = Start(db2, sc2);
        var foe = U(s2, "foe");
        Assert.True(s2.Summons.Summon(Def(s2), U(s2, "bm"), foe.Pos, foe.Id, null, "тест"));
        Assert.Equal(2, Summoned(s2).Count);
        Run(s2, 1);
        Assert.Equal(2, Summoned(s2).Count);
        s2.Combat.Kill(foe, null, "тест");
        s2.Step();
        Assert.Empty(Summoned(s2));
    }

    [Fact]
    public void T51_LimitsOfTheAlive_HoldUnderAMassSummon_AndReplaceOldest_KeepsTheNewest()
    {
        var (db, sc) = Scene();
        var sim = Start(db, sc);
        var bm = U(sim, "bm");
        for (int i = 0; i < 5; i++) sim.Summons.Summon(Def(sim), bm, bm.Pos, -1, null, "тест");
        Assert.Equal(4, Summoned(sim).Count); // maxAlivePerSummoner = 4
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.SummonFailed && e.Text.Contains("у призывателя уже 4"));

        // The scenario's limit of all the alive, with two summoners.
        var (db2, sc2) = Scene(edit: s =>
        {
            s.Summons = new SummonRulesDef { MaxAlive = 5 };
            s.Units.Add(new ScenarioUnitDef { Unit = "beastmaster", Faction = "player", C = new HexCoord(6, 6), Facing = HexDirection.E, Tag = "bm2" });
        });
        var s2 = Start(db2, sc2);
        for (int i = 0; i < 4; i++)
            foreach (var who in new[] { "bm", "bm2" })
                s2.Summons.Summon(Def(s2), U(s2, who), U(s2, who).Pos, -1, null, "тест");
        Assert.Equal(5, Summoned(s2).Count);
        Assert.Contains(s2.Log.Entries, e => e.Type == SimEventType.SummonFailed && e.Text.Contains("лимит сценария"));

        // «Заменить старейшего»: the fourth skeleton takes the place of the first.
        var (db3, sc3) = Scene();
        var s3 = Start(db3, sc3);
        var b3 = U(s3, "bm");
        var skel = s3.Content.Get<SummonDef>("risen_skeleton")!;
        var first = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            s3.Summons.Summon(skel, b3, new HexCoord(6, 3 + i % 3), -1, null, "тест");
            if (i == 0) first.Add(Summoned(s3).Single().Id);
        }
        Assert.Equal(3, Summoned(s3).Count);
        Assert.DoesNotContain(Summoned(s3), u => u.Id == first[0]);
    }

    [Fact]
    public void T52_AChainOfSummons_StopsAtTheDepth_AndAtTheGlobalQuota()
    {
        // A beastmaster summons beastmasters who may summon: depth 3 at most.
        static void Chain(SummonDef d)
        {
            d.Unit = "beastmaster";
            d.Count = 1;
            d.AllowSubSummon = true;
            d.MaxDepth = 3;
            d.MaxPerSummoner = 0;
        }
        var (db, sc) = Scene(Chain);
        var sim = Start(db, sc);
        var who = U(sim, "bm");
        for (int depth = 1; depth <= 4; depth++)
        {
            bool ok = sim.Summons.Summon(Def(sim), who, who.Pos, -1, null, "тест");
            Assert.Equal(depth <= 3, ok);
            if (!ok) break;
            who = Summoned(sim).OrderByDescending(u => u.Summon!.Serial).First();
            Assert.Equal(depth, who.Summon!.Depth);
            Assert.Equal(U(sim, "bm").Id, who.Summon.OwnerId);
        }
        Assert.Contains(sim.Log.Entries, e => e.Type == SimEventType.SummonFailed && e.Text.Contains("глубина цепочки призывов 4 больше 3"));
        // The scenario caps the depth lower, and counts all the summons of the battle.
        var (db2, sc2) = Scene(Chain, s => s.Summons = new SummonRulesDef { MaxDepth = 2, MaxTotal = 3 });
        var s2 = Start(db2, sc2);
        var first = U(s2, "bm");
        Assert.True(s2.Summons.Summon(Def(s2), first, first.Pos, -1, null, "тест"));
        var second = Summoned(s2).Single();
        Assert.True(s2.Summons.Summon(Def(s2), second, second.Pos, -1, null, "тест"));
        var third = Summoned(s2).Single(u => u != second);
        Assert.False(s2.Summons.Summon(Def(s2), third, third.Pos, -1, null, "тест")); // depth 3 > 2
        Assert.True(s2.Summons.Summon(Def(s2), first, first.Pos, -1, null, "тест"));  // the third summon of the battle
        Assert.False(s2.Summons.Summon(Def(s2), first, first.Pos, -1, null, "тест") && Summoned(s2).Count > 3);
        Assert.Equal(3, s2.Summons.Total);
        // A summoned whose description does not allow summoning cannot summon at all.
        var (db3, sc3) = Scene(d => { Chain(d); d.AllowSubSummon = false; });
        var s3 = Start(db3, sc3);
        Assert.True(s3.Summons.Summon(Def(s3), U(s3, "bm"), U(s3, "bm").Pos, -1, null, "тест"));
        var sub = Summoned(s3).Single();
        Assert.False(s3.Summons.Summon(Def(s3), sub, sub.Pos, -1, null, "тест"));
    }

    [Fact]
    public void T53_TheFactionByPolicy_TheFriendlyFire_AndTheAuras()
    {
        // The summoner goes over to the enemy: its summoned follow — or keep their side.
        foreach (bool follow in new[] { true, false })
        {
            var (db, sc) = Scene(d => d.FollowFactionChange = follow);
            var sim = Start(db, sc);
            var bm = U(sim, "bm");
            Call(sim);
            bm.FactionId = "enemy";
            sim.Step();
            Assert.All(Summoned(sim), w => Assert.Equal(follow ? "enemy" : "player", w.FactionId));
        }
        // A fixed faction.
        var (dbF, scF) = Scene(d =>
        {
            d.Faction = SummonFaction.Fixed;
            d.FixedFaction = "neutral";
        });
        var sF = Start(dbF, scF);
        Call(sF);
        Assert.All(Summoned(sF), w => Assert.Equal("neutral", w.FactionId));

        // Friendly fire: an area of the summoner hits the knight but spares its own summoned when the rule says so.
        var (db2, sc2) = Scene(edit: s =>
        {
            s.Units[1].C = new HexCoord(4, 2);
            s.GameRulesOverride = new GameRulesOverrideDef
            {
                FriendlyFireRules = new FriendlyFireRulesDef
                {
                    Area = new FriendlyFireDef { Targets = FfTargets.Custom, AlliesDamage = true, OwnSummons = false, Neutrals = true },
                },
            };
        }, dbEdit: d =>
        {
            var ball = ContentJson.Clone(d.Skill("fireball")!);
            ball.Id = "summon_ball";
            ball.HitMode = HitMode.AlwaysHit;
            ball.Cooldown = 0;
            ball.ResourceCosts.Clear();
            ball.Stages[0].Effects.RemoveAll(e => e.Type != EffectType.Damage);
            ball.Stages[0].Effects[0].Amount = 20;
            ball.Stages[0].Effects[0].ScalingStat = "";
            ball.RangeMin = 0;
            d.Add(ball);
            d.Unit("beastmaster")!.Skills.Add("summon_ball");
        });
        var s2 = Start(db2, sc2);
        var bm2 = U(s2, "bm");
        Call(s2);
        var wolf = Summoned(s2).First(w => w.Pos == bm2.Pos.Neighbor(HexDirection.E));
        Assert.Equal(FfRelation.OwnSummon, s2.FriendlyFire.RelationOf(bm2, bm2.FactionId, wolf));
        double knightHp = U(s2, "ally").Hp, wolfHp = wolf.Hp;
        Assert.Null(s2.Enqueue(bm2, Command.Skill("summon_ball", new HexCoord(4, 3))));
        Run(s2, 3);
        Assert.True(U(s2, "ally").Hp < knightHp, "союзник задет — правило задевает союзников");
        Assert.Equal(wolfHp, wolf.Hp, 6);

        // Auras of allies reach the summoned (as any ally of the side).
        var (db3, sc3) = Scene(edit: s => s.Units[1].C = new HexCoord(5, 3));
        var s3 = Start(db3, sc3);
        Call(s3);
        var near = Summoned(s3).First(w => HexCoord.Distance(w.Pos, U(s3, "ally").Pos) <= 2);
        Run(s3, 0.5);
        double baseArmor = s3.Content.Unit("wolf")!.Stats.GetValueOrDefault(Stats.Armor);
        Assert.True(near.Stat(Stats.Armor) >= baseArmor + 2 - 1e-9, $"аура стража не дошла до призванного: броня {near.Stat(Stats.Armor)}");
    }

    [Fact]
    public void T53_InheritsOnlyWhatIsNamed()
    {
        var (db, sc) = Scene(d =>
        {
            d.InheritStats = new List<SummonInheritDef> { new() { Stat = Stats.HpMax, Share = 0.5 } };
            d.InheritTags = new List<string> { "summoner", "not_there" };
        });
        var sim = Start(db, sc);
        var bm = U(sim, "bm");
        Call(sim);
        var w = Summoned(sim).First();
        double baseHp = sim.Content.Unit("wolf")!.Stats[Stats.HpMax];
        Assert.Equal(baseHp + bm.HpMax * 0.5, w.HpMax, 6);
        Assert.Equal(w.HpMax, w.Hp, 6);
        Assert.True(w.HasTag("summoner"));
        Assert.False(w.HasTag("not_there"));
        Assert.Equal(sim.Content.Unit("wolf")!.Stats.GetValueOrDefault(Stats.Evasion), w.Stat(Stats.Evasion), 6); // not named — its own
    }

    [Fact]
    public void T54_SummonsTakeNoDeploySlots_ByDefault_TheScenarioMayCountThem()
    {
        foreach (bool count in new[] { false, true })
        {
            var (db, sc) = Scene(edit: s => s.Summons = count ? new SummonRulesDef { CountTowardsDeploy = true } : null);
            var sim = Start(db, sc);
            int before = sim.Deployment.DeployedCount;
            Call(sim);
            Assert.Equal(2, Summoned(sim).Count);
            Assert.Equal(before + (count ? 2 : 0), sim.Deployment.DeployedCount);
        }
        // An ordinary deployment is not touched: a squad fighter still counts once.
        var (dbD, scD) = Scene();
        var simD = Start(dbD, scD);
        Assert.Equal(0, simD.Deployment.DeployedCount);
    }

    [Fact]
    public void T55_SaveAndLoad_KeepTheOwner_TheScale_ThePositions_AndTheRng()
    {
        var (db, sc) = Scene(edit: s => s.Units[2].C = new HexCoord(8, 3), dbEdit: d => d.Unit("wolf")!.Scale = new ScaleUseDef { Profile = "morale" });
        var (snap, sim) = BattleSnapshot.Begin(db, sc, 7, autoPlayer: false);
        var rec = BattleReplay.Record(snap, sim);
        Assert.Null(sim.Input.Enqueue(sim.Units.First(u => u.Tag == "bm"), Command.Skill("test_call", null)));
        for (int i = 0; i < sim.TicksFor(4); i++) sim.Step();
        var wolves = Summoned(sim);
        Assert.Equal(2, wolves.Count);
        Assert.All(wolves, w => Assert.NotNull(sim.Scales.Of(w)));
        var save = BattleReplay.FromJson(rec.SaveAt(snap, sim).ToJson())!;
        var res = save.Resume();
        Assert.True(res.Matches, res.Text);
        var loaded = res.Sim!;
        Assert.Equal(sim.Summons.Digest(), loaded.Summons.Digest());
        for (int i = 0; i < sim.TicksFor(6); i++)
        {
            sim.Step();
            loaded.Step();
        }
        Assert.Equal(sim.Digest().Hash, loaded.Digest().Hash);
        Assert.Equal(sim.Summons.Digest(), loaded.Summons.Digest());
        foreach (var w in Summoned(sim))
        {
            var lw = loaded.GetUnit(w.Id)!;
            Assert.Equal((w.Pos, w.SummonerId, w.Summon!.OwnerId), (lw.Pos, lw.SummonerId, lw.Summon!.OwnerId));
            Assert.Equal(sim.Scales.Of(w)!.Value, loaded.Scales.Of(lw)!.Value, 9);
        }
    }

    [Fact]
    public void TheChecks_UnlimitedLives_TheEnd_ThePattern_AndTheShippedPresets()
    {
        var db = TestUtil.Shipped();
        foreach (var id in new[] { "wolf_pack", "risen_skeleton" })
            Assert.DoesNotContain(SummonChecks.Check(db, db.Get<SummonDef>(id)!), p => p.Error);
        var bad = new SummonDef
        {
            Id = "bad", Unit = "wolf", Count = 3, Placement = SummonPlacement.Offsets, Lifetime = SummonLifetime.Unlimited, Despawn = SummonEnd.Keep,
            MaxPerSummoner = 0, OnSummonerDeath = SummonEnd.BecomeIndependent,
        };
        var list = SummonChecks.Check(db, bad);
        Assert.Contains(list, p => p.Error && p.Message.Contains("без смещений"));
        Assert.Contains(list, p => p.Error && p.Message.Contains("нужно действие"));
        Assert.Contains(list, p => !p.Error && p.Message.Contains("число призванных не ограничено"));
        Assert.Contains(list, p => !p.Error && p.Message.Contains("переживают призывателя"));
        db.Unit("wolf")!.Stats[Stats.HpMax] = 1e6;
        Assert.Contains(SummonChecks.Check(db, bad), p => !p.Error && p.Message.Contains("неуничтожаем"));
        // The pattern of the editor's preview: the front first.
        var pack = db.Get<SummonDef>("wolf_pack")!;
        var cells = SummonSystem.PatternOf(pack, new HexCoord(0, 0), HexDirection.E, new HexCoord(0, 0), null);
        Assert.Equal(6, cells.Count);
        Assert.Equal(new HexCoord(0, 0).Neighbor(HexDirection.E), cells[0]);
        Assert.Contains("Волк ×2", SummonChecks.Summary(db, pack));
    }

    [Fact]
    public void ALegacySummon_StaysAsBefore()
    {
        // The effect without a description: one unit on the hex of the skill; the rule of the battle at the summoner's death.
        var (db, sc) = Scene(dbEdit: d =>
        {
            var s = d.Skill("test_call")!;
            s.Targeting = TargetingMode.Cell;
            s.RangeMin = 1;
            s.RangeMax = 3;
            s.Stages[0].Effects[0] = new SkillEffectDef { Type = EffectType.Summon, SummonUnit = "wolf", Duration = 5 };
        });
        var sim = Start(db, sc);
        var bm = U(sim, "bm");
        Assert.Null(sim.Enqueue(bm, Command.Skill("test_call", new HexCoord(5, 3))));
        Run(sim, 1.5);
        var w = sim.Units.Single(u => u.Alive && u.SummonerId == bm.Id);
        Assert.Null(w.Summon);
        Assert.Equal(new HexCoord(5, 3), w.Pos);
        sim.Combat.Kill(bm, null, "тест");
        Assert.False(w.Alive);
    }
}
