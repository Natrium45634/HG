using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// Part 4, stage 2: groups and subgroups, several bases, repeats, the dispatcher's limits of the living and the
/// created, cancel conditions and duration, difficulty and local modifiers, the threat budget with pinning, group
/// presets, the migration of old waves to format 5 (docs/DECISIONS_4.md §58, §73).
/// </summary>
public class TdWaveTests
{
    private static ContentDatabase Db()
    {
        var db = TestUtil.Minimal();
        db.Add(new AiProfileDef { Id = "runner", Name = "Бегущий к базе", RouteFocus = true });
        db.Add(new AiProfileDef { Id = "idle", Name = "Стоит", HoldPosition = true, HoldRadius = 0, Aggression = 0 });
        db.Add(new UnitDef
        {
            Id = "wolf", Name = "Волк", Faction = "enemy", BasicAttack = "hit", AiProfile = "idle", CreatureCategory = "beast", Tags = { "pack" },
            Stats = { [Stats.HpMax] = 50, [Stats.MoveSpeed] = 3, [Stats.Evasion] = 0.2 },
        });
        db.Add(new UnitDef
        {
            Id = "goblin", Name = "Гоблин", Faction = "enemy", BasicAttack = "hit", AiProfile = "idle", CreatureCategory = "humanoid",
            Stats = { [Stats.HpMax] = 40, [Stats.MoveSpeed] = 2 },
        });
        db.Add(new UnitDef
        {
            Id = "brute", Name = "Громила", Faction = "enemy", BasicAttack = "hit", AiProfile = "idle", CreatureCategory = "humanoid", Tags = { "elite" },
            Stats = { [Stats.HpMax] = 200, [Stats.Armor] = 4, [Stats.Power] = 4, [Stats.MoveSpeed] = 1 },
        });
        return db;
    }

    /// <summary>A field 16 × 7 with two spawn bases on the west and a player base on the east.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene()
    {
        var db = Db();
        var map = TestUtil.Field(16, 7);
        map.Id = "td_field";
        db.Add(map);
        var sc = new ScenarioDef { Id = "td2", Name = "TD-2", Map = map.Id, Seed = 7, Victory = new(), Defeat = new() };
        var s1 = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 1));
        sc.Objects.Add(s1);
        var s2 = ScenarioObjects.Create(sc, ScenarioObjectType.SpawnBase, new HexCoord(0, 5));
        sc.Objects.Add(s2);
        sc.Objects.Add(ScenarioObjects.Create(sc, ScenarioObjectType.PlayerBase, new HexCoord(14, 3)));
        db.Add(sc);
        return (db, sc);
    }

    private static double Seconds(Simulation sim, PlannedSpawn p, long baseTick) => (p.Tick - baseTick) * sim.Dt;

    [Fact]
    public void Subgroups_SeveralBases_AndRepeats_MakeTheSchedule()
    {
        var (db, sc) = Scene();
        var g = new SpawnGroupDef
        {
            Id = "g1", Unit = "wolf", Count = 6, Interval = 1, SubgroupSize = 3, SubgroupPause = 4, Delay = 2,
            SpawnBase = "spawn_1", ExtraBases = { "spawn_2" }, Repeats = 1, RepeatEvery = 20,
        };
        sc.Waves.Add(new WaveDef { Name = "Стая", Start = WaveStart.Time, Time = 10, Groups = { g } });
        var sim = ScenarioSystem.Create(db, sc, 1);
        var w = sim.Scenario.Waves[0];
        Assert.Equal(12, w.Planned.Count);
        long t0 = sim.TicksFor(10);
        var times = w.Planned.Select(p => Seconds(sim, p, t0)).ToList();
        // Inside a subgroup — the interval; between subgroups — the pause; the second run 20 s later.
        Assert.Equal(new double[] { 2, 3, 4, 8, 9, 10, 22, 23, 24, 28, 29, 30 }, times.Select(t => System.Math.Round(t, 2)).ToArray());
        // The bases take the enemies in turn.
        Assert.Equal(new[] { "spawn_1", "spawn_2", "spawn_1", "spawn_2", "spawn_1", "spawn_2" }, w.Planned.Take(6).Select(p => p.SpawnBase).ToArray());
        TestUtil.RunUntil(sim, () => w.Spawned == 12, 60);
        var units = sim.Units.Where(u => u.Group == "g1").ToList();
        Assert.Equal(12, units.Count);
        Assert.Equal(6, units.Count(u => u.SpawnBase == "spawn_2"));
        Assert.Equal(12, sim.Scenario.CreatedTotal);
        Assert.Equal(6, sim.Scenario.CreatedBy("spawn_1"));
        // The schedule preview of the editor says the same.
        var lines = WaveSchedule.Preview(db, sc);
        Assert.Equal(2, lines.Count);
        Assert.Equal(12, lines[0].Start, 3);
        Assert.Equal(20, lines[0].End, 3);
        Assert.Equal(32, lines[1].Start, 3);
    }

    [Fact]
    public void Dispatcher_LimitsOfTheLiving_WaitAndReleaseSlots()
    {
        var (db, sc) = Scene();
        sc.Waves.Add(new WaveDef
        {
            Name = "Группа с лимитом", Start = WaveStart.Time, Time = 1,
            Groups = { new SpawnGroupDef { Id = "g1", Unit = "goblin", Count = 5, Interval = 0.5, SpawnBase = "spawn_1", MaxAlive = 2 } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        var w = sim.Scenario.Waves[0];
        int maxAlive = 0;
        for (int i = 0; i < sim.TicksFor(12); i++)
        {
            sim.Step();
            maxAlive = System.Math.Max(maxAlive, sim.Units.Count(u => u.Alive && u.Group == "g1"));
        }
        Assert.Equal(2, maxAlive);
        Assert.Equal(2, w.Spawned);
        var waiting = w.Planned.Where(p => !p.Done).ToList();
        Assert.Equal(3, waiting.Count);
        Assert.All(waiting, p => Assert.Contains("лимит живых группы", p.WaitReason));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("ждёт — лимит живых группы"));
        // A slot frees: the next one is planned again (with its warning line) and appears.
        sim.Combat.Kill(sim.Units.First(u => u.Alive && u.Group == "g1"), null, "тест");
        TestUtil.RunUntil(sim, () => w.Spawned == 3, 10);
        Assert.Equal(3, w.Spawned);
        Assert.Equal(2, sim.Units.Count(u => u.Alive && u.Group == "g1"));

        // The base's and the scenario's limits of the living count every wave.
        var (db2, sc2) = Scene();
        sc2.Object("spawn_1")!.Spawn!.MaxAlive = 3;
        sc2.Limits.MaxAliveEnemies = 5;
        sc2.Waves.Add(new WaveDef { Name = "А", Start = WaveStart.Time, Time = 1, Groups = { new SpawnGroupDef { Id = "a", Unit = "goblin", Count = 4, Interval = 0.2, SpawnBase = "spawn_1" } } });
        sc2.Waves.Add(new WaveDef { Name = "Б", Start = WaveStart.Time, Time = 1, Groups = { new SpawnGroupDef { Id = "b", Unit = "wolf", Count = 4, Interval = 0.2, SpawnBase = "spawn_2" } } });
        var sim2 = ScenarioSystem.Create(db2, sc2, 1);
        sim2.RunSeconds(8);
        Assert.Equal(3, sim2.Units.Count(u => u.Alive && u.SpawnBase == "spawn_1"));
        Assert.Equal(5, sim2.Units.Count(u => u.Alive && u.Wave >= 0));
        Assert.Contains(sim2.Scenario.Waves.SelectMany(x => x.Planned), p => p.WaitReason.Contains("лимит живых базы"));
        Assert.Contains(sim2.Scenario.Waves.SelectMany(x => x.Planned), p => p.WaitReason.Contains("лимит живых врагов сценария"));
    }

    [Fact]
    public void Dispatcher_LimitsOfTheCreated_CancelTheRest()
    {
        var (db, sc) = Scene();
        sc.Object("spawn_1")!.Spawn!.MaxCreated = 3;
        sc.Limits.MaxCreatedEnemies = 5;
        sc.Waves.Add(new WaveDef { Name = "А", Start = WaveStart.Time, Time = 1, Groups = { new SpawnGroupDef { Id = "a", Unit = "goblin", Count = 5, Interval = 0.2, SpawnBase = "spawn_1" } } });
        sc.Waves.Add(new WaveDef { Name = "Б", Start = WaveStart.Time, Time = 3, Groups = { new SpawnGroupDef { Id = "b", Unit = "wolf", Count = 5, Interval = 0.2, SpawnBase = "spawn_2" } } });
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(8);
        Assert.Equal(3, sim.Units.Count(u => u.SpawnBase == "spawn_1"));
        Assert.Equal(2, sim.Units.Count(u => u.SpawnBase == "spawn_2"));
        Assert.Equal(5, sim.Scenario.CreatedTotal);
        Assert.All(sim.Scenario.Waves.SelectMany(w => w.Planned), p => Assert.True(p.Done));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("лимит созданных базы") && e.Text.Contains("отменено"));
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("лимит созданных врагов сценария"));
        // The check warns before the battle.
        var problems = ScenarioObjectsCheck.Groups(db, sc);
        Assert.Contains(problems, p => p.Severity == IssueSeverity.Warning && p.Message.Contains("лимит созданных базы"));
    }

    [Fact]
    public void CancelConditions_Duration_AndMinimalGap()
    {
        var (db, sc) = Scene();
        sc.Waves.Add(new WaveDef
        {
            Name = "Первая", Start = WaveStart.Time, Time = 1, MaxDuration = 6, MinGapNext = 30,
            Groups =
            {
                new SpawnGroupDef { Id = "slow", Unit = "goblin", Count = 10, Interval = 1, SpawnBase = "spawn_1" },
                new SpawnGroupDef { Id = "cut", Unit = "wolf", Count = 10, Interval = 1, SpawnBase = "spawn_2", CancelCondition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 3.5 } },
            },
        });
        sc.Waves.Add(new WaveDef
        {
            Name = "Вторая", Start = WaveStart.AfterPrevious, Delay = 0,
            Groups = { new SpawnGroupDef { Id = "next", Unit = "goblin", Count = 1, SpawnBase = "spawn_1" } },
        });
        sc.Waves.Add(new WaveDef
        {
            Name = "Прерываемая", Start = WaveStart.Time, Time = 60, CancelCondition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 50 },
            Groups = { new SpawnGroupDef { Id = "never", Unit = "goblin", Count = 3, SpawnBase = "spawn_1" } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        var w0 = sim.Scenario.Waves[0];
        sim.RunSeconds(10);
        // The group's condition (3.5 s) cut «cut» after its third enemy; the duration (6 s from the start) cut «slow».
        Assert.Equal(3, sim.Units.Count(u => u.Group == "cut"));
        Assert.InRange(sim.Units.Count(u => u.Group == "slow"), 5, 7);
        Assert.Contains("cut", w0.CancelledGroups);
        Assert.True(w0.Expired);
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("время вышло"));
        // The wave clears once its enemies die; the next waits for the minimal gap (30 s from the first's start).
        foreach (var u in sim.Units.Where(u => u.Alive && u.Wave == 0).ToList()) sim.Combat.Kill(u, null, "тест");
        TestUtil.RunUntil(sim, () => sim.Units.Any(u => u.Group == "next"), 40);
        var next = sim.Units.First(u => u.Group == "next");
        Assert.True(next.SpawnTick * sim.Dt >= w0.StartedTick * sim.Dt + 30 - 0.01, $"вторая волна в {next.SpawnTick * sim.Dt:0.##} с");
        // The interrupt condition cancels a wave before it begins.
        sim.RunSeconds(25);
        Assert.Equal(WaveState.Cancelled, sim.Scenario.Waves[2].State);
        Assert.DoesNotContain(sim.Units, u => u.Group == "never");
    }

    [Fact]
    public void Difficulty_AndGroupModifiers_ChangeTheInstanceOnly()
    {
        var (db, sc) = Scene();
        sc.Waves.Add(new WaveDef
        {
            Name = "Тяжёлая", Start = WaveStart.Time, Time = 1, Difficulty = new WaveDifficultyDef { Health = 2, Speed = 0.5, Count = 1.5 },
            Groups = { new SpawnGroupDef { Id = "g", Name = "Бронированные", Unit = "goblin", Count = 3, Interval = 0.2, SpawnBase = "spawn_1", StatMods = { new StatModDef { Stat = Stats.Armor, Add = 5 } } } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(5);
        var gs = sim.Units.Where(u => u.Group == "g").ToList();
        Assert.Equal(5, gs.Count); // 3 × 1.5 = 4.5 → 5
        var u = gs[0];
        Assert.Equal(80, u.HpMax, 3);
        Assert.Equal(80, u.Hp, 3);
        Assert.Equal(5, u.Stat(Stats.Armor), 3);
        Assert.Equal(1, u.Stat(Stats.MoveSpeed), 3);
        // The template is not changed; the source of each contribution is named.
        Assert.Equal(40, db.Unit("goblin")!.Stats[Stats.HpMax]);
        Assert.Contains(u.Stats.Explain(Stats.Armor).Items, i => i.C.Source.Contains("Бронированные"));
        Assert.Contains(u.Stats.Explain(Stats.HpMax).Items, i => i.C.Source.Contains("сложность волны"));
        // Modifiers survive a recalculation (a status, a passive refresh).
        sim.Statuses.RecalculateModifiers(u);
        Assert.Equal(80, u.HpMax, 3);
    }

    [Fact]
    public void ThreatCost_AuthorValueWins_EstimateOrdersTemplates()
    {
        var db = TestUtil.Shipped();
        double C(string id) => ThreatCost.Of(db, db.Unit(id)!);
        Assert.True(C("orc_brute") > C("goblin"));
        Assert.True(C("titan") > C("orc_brute"));
        Assert.True(C("goblin") >= 0.5);
        db.Unit("goblin")!.ThreatCost = 7;
        Assert.Equal(7, C("goblin"));
    }

    [Fact]
    public void Budget_IsDeterministic_RespectsLimits_AndPins()
    {
        var (db, sc) = Scene();
        var w = new WaveDef
        {
            Id = "w1", Name = "По бюджету", Start = WaveStart.Time, Time = 2,
            Budget = new WaveBudgetDef { Enabled = true, Budget = 40, MinUnits = 3, MaxUnits = 8, MaxEliteShare = 0.25, Leader = "brute", SpawnBase = "spawn_1", Interval = 0.5 },
        };
        sc.Waves.Add(w);
        var a = WaveBudget.Compose(db, w, w.Budget, 11);
        var b = WaveBudget.Compose(db, w, w.Budget, 11);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal("brute", a.Rows[0].Unit.Id);
        Assert.InRange(a.Units, 3, 8);
        Assert.True(a.Spent <= 40 + 1e-9);
        Assert.True(a.Rows.Where(r => ThreatCost.IsElite(r.Unit)).Sum(r => r.Count) <= System.Math.Max(1, (int)(0.25 * a.Units)));
        Assert.Empty(a.Problems);
        // Other seeds give other mixes (with the same limits).
        var mixes = Enumerable.Range(1, 12).Select(s => WaveBudget.Compose(db, w, w.Budget, (ulong)s).ToString()).Distinct().Count();
        Assert.True(mixes > 1);
        // Required/forbidden tags and categories filter the templates.
        var tagged = new WaveBudgetDef { Enabled = true, Budget = 30, MinUnits = 1, MaxUnits = 10, RequiredTags = { "pack" } };
        Assert.All(WaveBudget.Compose(db, w, tagged, 3).Rows, r => Assert.Equal("wolf", r.Unit.Id));
        var noBeasts = new WaveBudgetDef { Enabled = true, Budget = 30, MinUnits = 1, MaxUnits = 10, Categories = { "humanoid" }, ForbiddenTags = { "elite" } };
        Assert.All(WaveBudget.Compose(db, w, noBeasts, 3).Rows, r => Assert.True(r.Unit.CreatureCategory == "humanoid" && !r.Unit.Tags.Contains("elite"), r.Unit.Id));
        // A budget too small for the minimum is reported (the check shows it before the battle).
        var poor = new WaveBudgetDef { Enabled = true, Budget = 2, MinUnits = 5, MaxUnits = 10, SpawnBase = "spawn_1" };
        Assert.Contains(WaveBudget.Compose(db, w, poor, 1).Problems, p => p.Contains("из минимума 5"));

        // In battle an unpinned budget picks by the scenario's seed — the same every run.
        var sim1 = ScenarioSystem.Create(db, sc, 100);
        var sim2 = ScenarioSystem.Create(db, sc, 200);
        Assert.Equal(sim1.Scenario.Waves[0].Planned.Select(p => p.Def.Id), sim2.Scenario.Waves[0].Planned.Select(p => p.Def.Id));
        Assert.Equal(sim1.Scenario.Waves[0].Budget!.Units, sim1.Scenario.Waves[0].Planned.Count);
        sim1.RunSeconds(10);
        Assert.Equal(sim1.Scenario.Waves[0].Planned.Count, sim1.Units.Count(u => u.Wave == 0));

        // «Закрепить»: the pick becomes groups and stays when the budget changes.
        var pinned = WaveBudget.Pin(db, sc, w);
        Assert.True(w.Budget.Pinned);
        Assert.Equal(pinned.Units, w.Groups.Where(g => g.FromBudget).Sum(g => g.Count));
        string before = string.Join(",", w.Groups.Select(g => g.ToString()));
        w.Budget.Budget = 5;
        var sim3 = ScenarioSystem.Create(db, sc, 300);
        Assert.Equal(pinned.Units, sim3.Scenario.Waves[0].Planned.Count);
        Assert.Equal(before, string.Join(",", w.Groups.Select(g => g.ToString())));
        Assert.DoesNotContain(ScenarioObjectsCheck.Groups(db, sc), p => p.Severity == IssueSeverity.Error);
        WaveBudget.Unpin(w);
        Assert.False(w.Budget.Pinned);
        Assert.DoesNotContain(w.Groups, g => g.FromBudget);
    }

    [Fact]
    public void GroupPreset_Instantiates_AndRoundTrips()
    {
        var (db, sc) = Scene();
        var preset = new GroupPresetDef
        {
            Id = "wolf_pack", Name = "Стая волков",
            Template = new SpawnGroupDef { Unit = "wolf", Count = 6, Interval = 0.5, SubgroupSize = 3, SubgroupPause = 3, MaxAlive = 4, StatMods = { new StatModDef { Stat = Stats.MoveSpeed, Mul = 0.2 } } },
        };
        db.Add(preset);
        var g = preset.Instantiate("g7", "spawn_2", "");
        Assert.Equal("g7", g.Id);
        Assert.Equal("spawn_2", g.SpawnBase);
        Assert.Equal("wolf_pack", g.Preset);
        Assert.Equal(6, g.Count);
        Assert.NotSame(preset.Template.StatMods, g.StatMods);
        var mem = new MemoryStorage();
        ContentIO.Save(db, mem);
        Assert.True(mem.Files.ContainsKey("group_presets.json"));
        var back = ContentIO.Load(mem);
        var p2 = back.Db.Get<GroupPresetDef>("wolf_pack");
        Assert.NotNull(p2);
        Assert.Equal(3, p2!.Template.SubgroupSize);
    }

    /// <summary>
    /// The migration of format 5: an old wave (two markers of the map, a route of the map, «unit × count» lines, an
    /// interval and a limit) becomes spawn bases on the markers and groups — with the same schedule, the same hexes,
    /// the same units and routes in battle.
    /// </summary>
    [Fact]
    public void Migration_OldWaves_KeepTheirScheduleAndPlaces()
    {
        var db = Db();
        var map = TestUtil.Field(16, 7);
        map.Id = "legacy_field";
        map.Markers.Add(new MapMarkerDef { Id = "north", Type = MarkerType.EnemySpawn, C = new HexCoord(0, 1) });
        map.Markers.Add(new MapMarkerDef { Id = "south", Type = MarkerType.EnemySpawn, C = new HexCoord(0, 5) });
        map.Routes.Add(new MapRouteDef { Id = "road", Points = { new HexCoord(3, 3), new HexCoord(13, 3) } });
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "legacy", Name = "Старый", Map = map.Id, Seed = 5, Victory = new(), Defeat = new(),
            Waves =
            {
                new WaveDef
                {
                    Name = "Первая", Start = WaveStart.Time, Time = 2, SpawnMarker = "north", SpawnMarkers = { "south" }, Route = "road", Interval = 1.5,
                    Units = { new WaveUnitDef { Unit = "wolf", Count = 3, Tag = "pack" }, new WaveUnitDef { Unit = "goblin", Count = 2, AiProfile = "runner" } },
                },
                new WaveDef
                {
                    Name = "Вторая", Start = WaveStart.AfterPrevious, Delay = 2, SpawnMarker = "north", Interval = 1, MaxAlive = 2,
                    Units = { new WaveUnitDef { Unit = "goblin", Count = 4 } },
                },
            },
        };
        db.Add(sc);
        // The same scenario written by format 4 and read back (migrated).
        var mem = new MemoryStorage();
        ContentIO.Save(db, mem);
        var node = JsonNode.Parse(mem.Files["scenarios/legacy.json"])!.AsObject();
        node["formatVersion"] = 4;
        mem.Files["scenarios/legacy.json"] = node.ToJsonString();
        var res = ContentIO.Load(mem);
        Assert.False(res.HasErrors, string.Join("\n", res.Issues));
        var migrated = res.Db.Get<ScenarioDef>("legacy")!;
        Assert.All(migrated.Waves, w => Assert.Empty(w.Units));
        Assert.Equal(2, migrated.Objects.Count(o => o.Type == ScenarioObjectType.SpawnBase));
        Assert.Equal(3, migrated.Waves.Sum(w => w.Groups.Count));
        Assert.Contains(res.Issues, i => i.Message.Contains("v4→v5"));
        Assert.DoesNotContain(ScenarioObjectsCheck.All(res.Db, migrated, res.Db.Get<MapDef>(map.Id)!), p => p.Severity == IssueSeverity.Error);

        static List<string> Run(ContentDatabase d, ScenarioDef s)
        {
            var sim = ScenarioSystem.Create(d, s, 9);
            var log = sim.Scenario.Waves.SelectMany(w => w.Planned).Select(p => $"план {p.Wave} {p.Def.Id} {p.From} {p.Tick} {p.Marker} {p.Route} {p.Tag}").ToList();
            for (int i = 0; i < sim.TicksFor(40); i++)
            {
                sim.Step();
                if (i == sim.TicksFor(12)) foreach (var u in sim.Units.Where(u => u.Alive && u.Wave == 0).ToList()) sim.Combat.Kill(u, null, "тест");
            }
            log.AddRange(sim.Units.Select(u => $"юнит {u.Def.Id} {u.SpawnTick} {u.Wave} {u.Tag} {u.AiProfile?.Id} {string.Join(">", u.Route ?? new())}"));
            log.AddRange(sim.Scenario.Waves.SelectMany(w => w.Planned).Select(p => $"итог {p.Wave} {p.Def.Id} {p.Tick} {p.Done}"));
            return log;
        }
        var legacy = Run(db, sc);
        var after = Run(res.Db, migrated);
        Assert.Equal(legacy, after);
        Assert.Contains(after, l => l.StartsWith("юнит wolf"));
    }
    [Fact]
    public void WaveStarts_AfterAnObjectIsDestroyed_AndAfterAnotherWaveAppears()
    {
        var (db, sc) = Scene();
        var tower = ScenarioObjects.Create(sc, ScenarioObjectType.Defense, new HexCoord(8, 3));
        sc.Objects.Add(tower);
        sc.Waves.Add(new WaveDef
        {
            Name = "Месть", Start = WaveStart.OnCondition, StartCondition = new ConditionDef { Type = ConditionType.UnitDead, UnitTag = tower.Id },
            Groups = { new SpawnGroupDef { Id = "revenge", Unit = "goblin", Count = 1, SpawnBase = "spawn_1" } },
        });
        sc.Waves.Add(new WaveDef
        {
            Name = "Следом", Start = WaveStart.OnCondition, StartCondition = new ConditionDef { Type = ConditionType.WaveSpawned, Wave = 0 }, Delay = 1,
            Groups = { new SpawnGroupDef { Id = "after", Unit = "wolf", Count = 1, SpawnBase = "spawn_2" } },
        });
        var sim = ScenarioSystem.Create(db, sc, 1);
        sim.RunSeconds(5);
        Assert.Equal(WaveState.Pending, sim.Scenario.Waves[0].State);
        var t = sim.Structures.Of(tower.Id)!;
        sim.Structures.DamageStructure(t, 10_000, null, "тест");
        Assert.False(t.Alive);
        TestUtil.RunUntil(sim, () => sim.Units.Any(u => u.Group == "after"), 20);
        var revenge = sim.Units.Single(u => u.Group == "revenge");
        var after = sim.Units.Single(u => u.Group == "after");
        Assert.True(after.SpawnTick > revenge.SpawnTick);
    }
}
