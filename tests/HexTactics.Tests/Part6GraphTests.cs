using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.6 — the graph of events over the triggers (ТЗ ч. 6, 7; T21–T23; docs/DECISIONS_6.md §101).</summary>
public class Part6GraphTests
{
    /// <summary>A field with a boss tagged «boss1», a knight far away, and two waves that start only from a trigger.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene()
    {
        var db = TestUtil.Shipped();
        var map = TestUtil.Field(12, 6);
        map.Id = "graph_field";
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "graph_test", Name = "Граф", Map = map.Id,
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 9999 } },
            Defeat = new List<ConditionDef>(),
            Units =
            {
                new ScenarioUnitDef { Unit = "orc_brute", Faction = "enemy", C = new HexCoord(10, 2), Facing = HexDirection.W, Tag = "boss1" },
                new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(0, 2), Facing = HexDirection.E },
            },
            Waves =
            {
                new WaveDef { Name = "Элитный резерв", Start = WaveStart.Manual, Units = { new WaveUnitDef { Unit = "goblin", Count = 3 } } },
                new WaveDef { Name = "Обычный резерв", Start = WaveStart.Manual, Units = { new WaveUnitDef { Unit = "goblin", Count = 1 } } },
            },
        };
        db.Add(sc);
        return (db, sc);
    }

    private static RuleNodeDef N(string id, RuleNodeKind k) => new() { Id = id, Kind = k };
    private static RuleLinkDef L(string from, string to, string fromPort = "out", string toPort = "in") => new() { From = from, FromPort = fromPort, To = to, ToPort = toPort };

    /// <summary>ТЗ 7.7: Event(UnitDied, boss1) → Condition(simTime &lt; 120 s) → да: elite reserve, нет: normal reserve.</summary>
    private static RuleGraphDef BossGraph() => new()
    {
        Id = "boss", Name = "Смерть босса",
        Nodes =
        {
            new RuleNodeDef { Id = "died", Kind = RuleNodeKind.Event, Event = ScenarioEventType.UnitDied, EventUnit = "boss1" },
            new RuleNodeDef { Id = "t120", Kind = RuleNodeKind.Condition, Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 120 } },
            N("early", RuleNodeKind.Not),
            N("branch", RuleNodeKind.Branch),
            new RuleNodeDef { Id = "elite", Kind = RuleNodeKind.Action, Action = new TriggerActionDef { Type = TriggerActionType.SpawnWave, Wave = 0 } },
            new RuleNodeDef { Id = "normal", Kind = RuleNodeKind.Action, Action = new TriggerActionDef { Type = TriggerActionType.SpawnWave, Wave = 1 } },
        },
        Links =
        {
            L("died", "branch"), L("t120", "early", "value", "in"), L("early", "branch", "value", "if"),
            L("branch", "elite", "yes"), L("branch", "normal", "no"),
        },
    };

    private static Simulation KillBossAt(ContentDatabase db, ScenarioDef sc, double seconds)
    {
        var sim = ScenarioSystem.Create(db, sc, 4);
        while (sim.Scenario.BattleTime < seconds) sim.Step();
        var boss = sim.Units.First(u => u.Tag == "boss1");
        sim.Combat.Kill(boss, null, "тест");
        for (int i = 0; i < 3; i++) sim.Step();
        return sim;
    }

    [Fact]
    public void T21_TheBossDiesBefore120s_TheEliteWave_OtherwiseTheNormalOne_EachBranchInTheLog()
    {
        var (db, sc) = Scene();
        sc.Graphs.Add(BossGraph());
        Assert.Empty(RuleGraphs.Check(sc, sc.Graphs[0]).Where(p => p.Error));
        Assert.Empty(sc.Triggers); // the graph is not copied into the triggers of the scenario: it is compiled for the battle

        var early = KillBossAt(db, sc, 60);
        Assert.NotEqual(WaveState.Pending, early.Scenario.Waves[0].State); // the elite reserve is on its way (with its warning)
        Assert.Equal(WaveState.Pending, early.Scenario.Waves[1].State);
        Assert.Contains(early.Scenario.GraphTrace, e => e.Node == "branch" && e.Text.StartsWith("ветка «да»"));
        Assert.Contains(early.Scenario.GraphTrace, e => e.Node == "elite");
        Assert.Contains(early.Log.Entries, e => e.Text.Contains("узел branch") && e.Text.Contains("ветка «да»"));

        var late = KillBossAt(db, sc, 125);
        Assert.NotEqual(WaveState.Pending, late.Scenario.Waves[1].State);
        Assert.Equal(WaveState.Pending, late.Scenario.Waves[0].State);
        Assert.Contains(late.Scenario.GraphTrace, e => e.Node == "branch" && e.Text.StartsWith("ветка «нет»"));
        Assert.Contains(late.Scenario.GraphTrace, e => e.Node == "normal");
    }

    [Fact]
    public void T22_AnInstantEndlessLoopIsRejected_ALimitedRepeatOfThreeRunsExactlyThreeTimes()
    {
        var (db, sc) = Scene();
        var loop = new RuleGraphDef
        {
            Id = "loop", Name = "Петля",
            Nodes =
            {
                new RuleNodeDef { Id = "start", Kind = RuleNodeKind.Timer, Seconds = 1 },
                new RuleNodeDef { Id = "say", Kind = RuleNodeKind.Action, Action = new TriggerActionDef { Type = TriggerActionType.Message, Text = "снова" } },
                N("seq", RuleNodeKind.Sequence),
            },
            Links = { L("start", "say"), L("say", "seq"), L("seq", "say") },
        };
        sc.Graphs.Add(loop);
        Assert.Contains(RuleGraphs.Check(sc, loop), p => p.Error && p.Message.StartsWith("мгновенный бесконечный цикл"));
        Assert.Contains(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Message.Contains("мгновенный бесконечный цикл"));
        Assert.Empty(RuleGraphs.Compile(sc)); // left out of the battle
        var sim0 = ScenarioSystem.Create(db, sc, 1);
        for (int i = 0; i < sim0.TicksFor(3); i++) sim0.Step(); // the battle runs without it
        Assert.Empty(sim0.Scenario.GraphTrace);

        sc.Graphs.Clear();
        sc.Graphs.Add(new RuleGraphDef
        {
            Id = "rep", Name = "Три раза",
            Nodes =
            {
                new RuleNodeDef { Id = "start", Kind = RuleNodeKind.Timer, Seconds = 1 },
                new RuleNodeDef { Id = "rep", Kind = RuleNodeKind.LimitedRepeat, Count = 3, Seconds = 0.5 },
                new RuleNodeDef { Id = "say", Kind = RuleNodeKind.Action, Action = new TriggerActionDef { Type = TriggerActionType.Message, Text = "раз" } },
            },
            Links = { L("start", "rep"), L("rep", "say"), L("say", "rep") }, // a ring through the repeat: allowed, limited
        });
        Assert.DoesNotContain(RuleGraphs.Check(sc, sc.Graphs[0]), p => p.Error);
        var sim = ScenarioSystem.Create(db, sc, 1);
        for (int i = 0; i < sim.TicksFor(10); i++) sim.Step();
        Assert.Equal(3, sim.Scenario.GraphTrace.Count(e => e.Node == "say"));
    }

    [Fact]
    public void T23_TheDebuggerSaysWhyAConditionIsFalse_AndListeningChangesNothing()
    {
        var (db, sc) = Scene();
        sc.Graphs.Add(new RuleGraphDef
        {
            Id = "dbg", Name = "Отладка",
            Nodes =
            {
                new RuleNodeDef { Id = "tick", Kind = RuleNodeKind.Timer, Seconds = 2 },
                new RuleNodeDef { Id = "alarm", Kind = RuleNodeKind.Condition, Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = "тревога" } },
                new RuleNodeDef { Id = "late", Kind = RuleNodeKind.Condition, Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 30 } },
                N("both", RuleNodeKind.And),
                N("branch", RuleNodeKind.Branch),
                new RuleNodeDef { Id = "msg", Kind = RuleNodeKind.Action, Action = new TriggerActionDef { Type = TriggerActionType.Message, Text = "тревога!" } },
            },
            Links = { L("tick", "branch"), L("alarm", "both", "value", "in"), L("late", "both", "value", "in"), L("both", "branch", "value", "if"), L("branch", "msg", "yes") },
        });
        Simulation Run(bool listen)
        {
            var sim = ScenarioSystem.Create(db, sc, 9);
            var seen = new List<GraphTraceEntry>();
            if (listen) sim.Scenario.GraphNodeRan += seen.Add;
            for (int i = 0; i < sim.TicksFor(5); i++) sim.Step();
            if (listen) Assert.NotEmpty(seen);
            return sim;
        }
        var a = Run(true);
        var no = a.Scenario.GraphTrace.Single(e => e.Node == "branch" && e.Text.StartsWith("ветка"));
        Assert.StartsWith("ветка «нет»", no.Text);
        Assert.Contains("Событие «тревога»: нет", no.Text);
        Assert.Contains("Прошло 30 с: нет (сейчас 2", no.Text);
        Assert.Equal(a.TicksFor(2), no.Tick - (a.Scenario.BattleStartTick));
        Assert.DoesNotContain(a.Scenario.GraphTrace, e => e.Node == "msg");
        var b = Run(false);
        Assert.Equal(a.Digest().Hash, b.Digest().Hash); // the trace and its listeners are outside the battle
    }

    [Fact]
    public void GatesSignalsOnceCooldown_AndTheRuntimeStateSurvivesASave()
    {
        var (db, sc) = Scene();
        sc.Graphs.Add(new RuleGraphDef
        {
            Id = "ctl", Name = "Управление",
            Nodes =
            {
                new RuleNodeDef { Id = "every", Kind = RuleNodeKind.Timer, Seconds = 1, Repeat = true },
                new RuleNodeDef { Id = "gate", Kind = RuleNodeKind.Gate, Name = "g", Open = false },
                new RuleNodeDef { Id = "once", Kind = RuleNodeKind.Once },
                new RuleNodeDef { Id = "send", Kind = RuleNodeKind.EmitSignal, Name = "ping" },
                new RuleNodeDef { Id = "opener", Kind = RuleNodeKind.Timer, Seconds = 3 },
                new RuleNodeDef { Id = "open", Kind = RuleNodeKind.GateOpen, Name = "g" },
                new RuleNodeDef { Id = "cool", Kind = RuleNodeKind.Cooldown, Seconds = 5 },
                new RuleNodeDef { Id = "mark", Kind = RuleNodeKind.Action, Action = new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "сквозь ворота" } },
                new RuleNodeDef { Id = "late", Kind = RuleNodeKind.Delay, Seconds = 4 },
            },
            Links = { L("every", "gate"), L("gate", "once"), L("once", "send"), L("opener", "open"), L("every", "cool"), L("cool", "mark"), L("opener", "late") },
        });
        sc.Graphs.Add(new RuleGraphDef
        {
            Id = "rx", Name = "Приёмник",
            Nodes =
            {
                new RuleNodeDef { Id = "ping", Kind = RuleNodeKind.Signal, Name = "ping" },
                new RuleNodeDef { Id = "got", Kind = RuleNodeKind.Action, Action = new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "сигнал" } },
            },
            Links = { L("ping", "got") },
        });
        Assert.DoesNotContain(sc.Graphs.SelectMany(g => RuleGraphs.Check(sc, g)), p => p.Error);
        var (snap, sim) = BattleSnapshot.Begin(db, sc, 2);
        var rec = BattleReplay.Record(snap, sim, withContent: true);
        for (int i = 0; i < sim.TicksFor(2.5); i++) sim.Step();
        Assert.DoesNotContain("сигнал", sim.Scenario.Flags); // the gate is closed
        Assert.Contains(sim.Scenario.GraphTrace, e => e.Node == "gate" && e.Text.StartsWith("условие ложно"));
        for (int i = 0; i < sim.TicksFor(2); i++) sim.Step();
        Assert.Contains("сигнал", sim.Scenario.Flags); // opened at 3 s, the next tick of «every» went through, once
        Assert.Equal(1, sim.Scenario.GraphTrace.Count(e => e.Node == "once" && e.Text.StartsWith("запущен")));
        Assert.Equal(1, sim.Scenario.GraphTrace.Count(e => e.Node == "cool" && e.Text.StartsWith("запущен"))); // not again within 5 s
        Assert.Contains(sim.Scenario.DelayedTriggers, d => d.Contains("ctl:late"));

        // Saved with the delay pending: the load waits for it, it does not start anything again.
        var save = BattleReplay.FromJson(rec.SaveAt(snap, sim).ToJson())!;
        var res = save.Resume();
        Assert.True(res.Matches, res.Text);
        var loaded = res.Sim!;
        Assert.Equal(sim.Scenario.DelayedTriggers, loaded.Scenario.DelayedTriggers);
        for (int i = 0; i < sim.TicksFor(4); i++)
        {
            sim.Step();
            loaded.Step();
        }
        Assert.Equal(sim.Digest().Hash, loaded.Digest().Hash);
        // «Однократно» ran once in both — the load replayed it, it did not start it a second time.
        Assert.Equal(1, loaded.Scenario.GraphTrace.Count(e => e.Node == "once" && e.Text.StartsWith("запущен")));
        Assert.Equal(1, sim.Scenario.GraphTrace.Count(e => e.Node == "once" && e.Text.StartsWith("запущен")));
    }

    [Fact]
    public void TypedPorts_RejectAWrongLink_AndHandWrittenTriggersAreUnchanged()
    {
        var (db, sc) = Scene();
        var g = new RuleGraphDef
        {
            Id = "types",
            Nodes = { new RuleNodeDef { Id = "c", Kind = RuleNodeKind.Condition, Condition = new() }, new RuleNodeDef { Id = "a", Kind = RuleNodeKind.Action, Action = new() }, N("b", RuleNodeKind.Branch) },
        };
        Assert.Contains("типы не совпадают", RuleGraphs.CanLink(g, L("c", "a", "value", "in"), adding: true));
        Assert.Null(RuleGraphs.CanLink(g, L("c", "b", "value", "if"), adding: true));
        g.Links.Add(L("c", "b", "value", "if"));
        Assert.Contains("уже занят", RuleGraphs.CanLink(g, L("c", "b", "value", "if"), adding: false) ?? RuleGraphs.CanLink(g, new RuleLinkDef { From = "c", FromPort = "value", To = "b", ToPort = "if" }) ?? "");
        Assert.Contains("нет выхода", RuleGraphs.CanLink(g, L("a", "b", "nope", "in")));
        // A scenario without graphs is the same object for the battle.
        Assert.Same(sc, RuleGraphs.WithCompiled(sc));
    }

    [Fact]
    public void EditorHelpers_Palette_Ids_Duplicate_Uses_DryRun_AndTheRunsOfTheNodes()
    {
        // The palette offers every kind of node, the actions of the catalog without the ones the graph has nodes for, and the
        // unavailable items with the reason and without a way to create them (ТЗ 7.2).
        var palette = RuleGraphs.Palette();
        foreach (var k in System.Enum.GetValues<RuleNodeKind>())
            Assert.Contains(palette, p => p.Available && p.Create!().Kind == k);
        Assert.DoesNotContain(palette, p => p.Available && p.Create!().Action?.Type is TriggerActionType.IfElse or TriggerActionType.DelayedTrigger);
        var off = palette.Where(p => !p.Available).ToList();
        Assert.NotEmpty(off);
        Assert.All(off, p => Assert.Equal(RuleGraphs.GroupUnavailable, p.Group));
        Assert.All(off, p => Assert.NotEqual("", p.Why));
        Assert.Contains(RuleGraphs.Palette("волн"), p => p.Group == RuleGraphs.GroupActions);
        Assert.All(RuleGraphs.Palette("ворота"), p => Assert.True(p.Name.Contains("ворота", System.StringComparison.OrdinalIgnoreCase) || p.Description.Contains("ворота", System.StringComparison.OrdinalIgnoreCase)
                                                                   || p.Group.Contains("ворота", System.StringComparison.OrdinalIgnoreCase) || p.Why.Contains("ворота", System.StringComparison.OrdinalIgnoreCase)));

        var (db, sc) = Scene();
        var g = BossGraph();
        sc.Graphs.Add(g);
        Assert.Equal("branch1", RuleGraphs.NewNodeId(g, RuleNodeKind.Branch));
        var copies = RuleGraphs.Duplicate(g, new[] { "branch", "elite" });
        Assert.Equal(2, copies.Count);
        Assert.Contains(g.Links, l => l.From == copies[0].Id && l.To == copies[1].Id && l.FromPort == "yes");
        Assert.Equal(2, RuleGraphs.RemoveNodes(g, copies.Select(c => c.Id).ToList()));
        Assert.DoesNotContain(g.Links, l => copies.Any(c => c.Id == l.From || c.Id == l.To));
        Assert.False(RuleGraphs.RenameNode(g, "branch", "elite")); // taken
        Assert.False(RuleGraphs.RenameNode(g, "branch", "a:b"));   // «:» separates the branch in the trace
        Assert.True(RuleGraphs.RenameNode(g, "branch", "fork"));
        Assert.Contains(g.Links, l => l.From == "fork" && l.FromPort == "yes");
        RuleGraphs.RenameNode(g, "fork", "branch");

        // Uses: the tag of the boss, the wave — by the text of the action.
        Assert.Equal(new[] { "died" }, RuleGraphs.Uses(g, "boss1").Select(n => n.Id));
        Assert.Contains(RuleGraphs.UsesIn(sc, "волна 1"), x => x.Node.Id == "elite");
        Assert.Equal(new[] { "elite" }, RuleGraphs.Uses(g, "elite").Select(n => n.Id)); // not the names of the fields («maxEliteShare»…)

        // Dry run: the triggers of one graph; none with an error; a ring of logic is an error.
        var triggers = RuleGraphs.CompileOne(sc, g);
        Assert.Contains(triggers, t => t.Name == RuleGraphs.TriggerName(g, "died") && t.Event == ScenarioEventType.UnitDied);
        Assert.Contains(triggers, t => t.Actions.Any(a => a.Type == TriggerActionType.IfElse));
        g.Enabled = false;
        Assert.NotEmpty(RuleGraphs.CompileOne(sc, g)); // shown even for a graph switched off
        g.Enabled = true;
        g.Nodes.Add(N("and", RuleNodeKind.And));
        g.Nodes.Add(N("or", RuleNodeKind.Or));
        g.Links.Add(L("and", "or", "value", "in"));
        g.Links.Add(L("or", "and", "value", "in"));
        Assert.Contains(RuleGraphs.Check(sc, g), p => p.Error && p.Message.StartsWith("кольцо условий"));
        Assert.Empty(RuleGraphs.CompileOne(sc, g));
        RuleGraphs.RemoveNodes(g, new[] { "and", "or" });

        // The runs of the nodes from the trace of a battle: the branch ran once with «нет», the normal reserve once.
        var sim = KillBossAt(db, sc, 125);
        var runs = RuleGraphs.Runs(sim.Scenario.GraphTrace, g.Id);
        Assert.Equal(1, runs["branch"].Count);
        Assert.False(runs["branch"].Blocked);
        Assert.StartsWith("ветка «нет»", runs["branch"].LastText);
        Assert.Equal(1, runs["normal"].Count);
        Assert.False(runs.ContainsKey("elite"));
        Assert.True(runs["normal"].LastTick > 0);
    }

    [Fact]
    public void ABreakpointOnANodeOfTheGraph_StopsTheBattleWithTheNodeAndItsReason()
    {
        var (db, sc) = Scene();
        sc.Graphs.Add(BossGraph());
        var sim = ScenarioSystem.Create(db, sc, 4);
        var breaks = Breakpoints.Attach(sim, new List<BreakpointDef>());
        breaks.GraphNodes.Add("boss/branch");
        while (sim.Scenario.BattleTime < 125) sim.Step();
        Assert.Null(breaks.Consume());
        sim.Combat.Kill(sim.Units.First(u => u.Tag == "boss1"), null, "тест");
        for (int i = 0; i < 3; i++) sim.Step();
        var hit = breaks.Consume();
        Assert.NotNull(hit);
        Assert.Contains("узел branch", hit);
        Assert.Contains("ветка «нет»", hit);
        Assert.Contains("граф «boss», узел branch", sim.ConsumeAutopause());

        // The same through the list of «остановка на событии»: the whole graph.
        var sim2 = ScenarioSystem.Create(db, sc, 4);
        var b2 = Breakpoints.Attach(sim2, new List<BreakpointDef> { new() { Kind = BreakpointKind.GraphNode, Value = "boss" } });
        sim2.Combat.Kill(sim2.Units.First(u => u.Tag == "boss1"), null, "тест");
        for (int i = 0; i < 3; i++) sim2.Step();
        Assert.Contains("граф «boss»", b2.Consume());
    }
}
