using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 6, stage 6.5 — interactive objects (ТЗ ч. 6, 6; T19–T20; docs/DECISIONS_6.md §100).</summary>
public class Part6ObjectTests
{
    private static readonly HexCoord Gate = new(5, 2);

    /// <summary>A field with a gate (open → wall), a lever that signals it, and a turret of the enemy that can be switched off.</summary>
    private static (ContentDatabase Db, ScenarioDef Sc) Scene(OccupiedCellPolicy policy = OccupiedCellPolicy.Defer, string closedTerrain = "wall")
    {
        var db = TestUtil.Shipped();
        var map = TestUtil.Field(10, 5);
        map.Id = "objects_field";
        db.Add(map);
        var sc = new ScenarioDef
        {
            Id = "objects_test", Name = "Объекты", Map = map.Id,
            Victory = new List<ConditionDef> { new() { Type = ConditionType.SurviveTime, Seconds = 9999 } },
            Defeat = new List<ConditionDef>(),
            Objects =
            {
                new ScenarioObjectDef
                {
                    Id = "gate", Name = "Ворота", Type = ScenarioObjectType.Custom, Anchor = Gate, Footprint = { new HexCoord(0, 1) },
                    States = new ObjectStatesDef
                    {
                        Initial = "open", WhenOccupied = policy,
                        States =
                        {
                            new ObjectStateDef { Id = "open", Name = "Открыты" },
                            new ObjectStateDef { Id = "closed", Name = "Закрыты", Terrain = closedTerrain },
                        },
                    },
                },
                new ScenarioObjectDef
                {
                    Id = "lever", Name = "Рычаг", Type = ScenarioObjectType.Interaction, Anchor = new HexCoord(2, 1),
                    Interaction = new InteractionDef { Prompt = "Потянуть рычаг", Range = 1, ApCost = 1, Duration = 1, Cooldown = 3, Effect = InteractionEffect.Signal, Target = "gate" },
                },
                new ScenarioObjectDef
                {
                    Id = "turret", Name = "Турель", Type = ScenarioObjectType.Defense, Faction = "enemy", Anchor = new HexCoord(8, 0),
                    Turret = new TurretDef { Skill = "zap", Interval = 1 },
                    States = new ObjectStatesDef { States = { new ObjectStateDef { Id = "on", Name = "Работает" }, new ObjectStateDef { Id = "off", Name = "Выключена", TurretActive = false } } },
                },
            },
        };
        db.Add(sc);
        return (db, sc);
    }

    private static Simulation Battle(ContentDatabase db, ScenarioDef sc) => ScenarioSystem.Create(db, sc, 3);

    private static bool Passable(Simulation sim, HexCoord a, HexCoord b, Unit u) =>
        sim.Pathfinder.Find(sim.Map, MoveContext.For(sim, u), a, b, new PathOptions { IgnoreUnits = true }).Found;

    [Fact]
    public void T19_TheGate_ChangesPassability_OnlyThroughTheMapService_AndWaitsForTheUnitStandingInIt()
    {
        var (db, sc) = Scene(OccupiedCellPolicy.Defer);
        var sim = Battle(db, sc);
        var knight = sim.Spawn(db.Unit("knight")!, "player", Gate, HexDirection.E);
        Assert.Equal("open", sim.Interactive.StateOf("gate"));
        int version = sim.Map.TerrainVersion;
        int terrainEvents = 0;
        sim.Observed += e => { if (e.Type == SimEventType.TerrainChanged) terrainEvents++; };

        // Someone stands in the gate: the change waits (no silent teleport).
        Assert.False(sim.Interactive.SetState("gate", "closed", "тест", out var why));
        Assert.Contains("отложена", why);
        Assert.Equal("open", sim.Interactive.StateOf("gate"));
        Assert.Equal("closed", sim.Interactive.PendingOf("gate"));
        Assert.Equal("grass", sim.Map.Get(Gate)!.Terrain.Id);
        sim.Step();
        Assert.Equal("open", sim.Interactive.StateOf("gate"));

        // The knight steps out — the gate closes through the common service: a terrain event, a new version of the map.
        Assert.True(sim.Relocate(knight, new HexCoord(3, 2)));
        sim.Step();
        Assert.Equal("closed", sim.Interactive.StateOf("gate"));
        Assert.Equal("wall", sim.Map.Get(Gate)!.Terrain.Id);
        Assert.Equal("wall", sim.Map.Get(Gate + new HexCoord(0, 1))!.Terrain.Id);
        Assert.True(sim.Map.TerrainVersion > version);
        Assert.Equal(2, terrainEvents);
        // Opening gives back the terrain of the map.
        Assert.True(sim.Interactive.SetState("gate", "", "тест", out _));
        Assert.Equal("grass", sim.Map.Get(Gate)!.Terrain.Id);
    }

    [Theory]
    [InlineData(OccupiedCellPolicy.Evacuate)]
    [InlineData(OccupiedCellPolicy.Forbid)]
    [InlineData(OccupiedCellPolicy.Fall)]
    public void T19_ThePolicyForStandingUnits_IsTheAuthorsChoice(OccupiedCellPolicy policy)
    {
        var (db, sc) = Scene(policy, policy == OccupiedCellPolicy.Fall ? "chasm" : "wall");
        var sim = Battle(db, sc);
        var knight = sim.Spawn(db.Unit("knight")!, "player", Gate, HexDirection.E);
        bool ok = sim.Interactive.SetState("gate", "closed", "тест", out var why);
        switch (policy)
        {
            case OccupiedCellPolicy.Evacuate:
                Assert.True(ok, why);
                Assert.Equal("closed", sim.Interactive.StateOf("gate"));
                Assert.True(knight.Alive);
                Assert.NotEqual(Gate, knight.Pos);
                Assert.Equal(1, HexCoord.Distance(knight.Pos, Gate));
                Assert.True(sim.Map.Get(knight.Pos)!.Terrain.IsPassableFor(knight.Def.MoveCategory));
                break;
            case OccupiedCellPolicy.Forbid:
                Assert.False(ok);
                Assert.Contains("переключение запрещено", why);
                Assert.Equal("open", sim.Interactive.StateOf("gate"));
                Assert.Equal("", sim.Interactive.PendingOf("gate"));
                Assert.Equal(Gate, knight.Pos);
                break;
            case OccupiedCellPolicy.Fall:
                Assert.True(ok, why);
                Assert.Equal("chasm", sim.Map.Get(Gate)!.Terrain.Id);
                Assert.False(knight.Alive && knight.Pos == Gate && sim.Map.Get(Gate)!.Occupants.Contains(knight), "в пропасти никто не стоит");
                break;
        }
    }

    [Fact]
    public void T20_TheLeverSignalsTheGate_WithDistanceAPAndCooldown_AndTheCheckFindsAMissingReceiver()
    {
        var (db, sc) = Scene();
        var sim = Battle(db, sc);
        var knight = sim.Spawn(db.Unit("knight")!, "player", new HexCoord(0, 1), HexDirection.E);
        Assert.Contains("далеко", sim.Interactive.CanInteract(knight, "lever"));
        Assert.True(sim.Relocate(knight, new HexCoord(1, 1)));
        double ap = knight.Ap;
        Assert.True(sim.Input.Interact(knight, "lever", out var why), why);
        Assert.Equal(ap - 1, knight.Ap, 6);
        Assert.Contains("занят", sim.Interactive.CanInteract(knight, "lever"));
        Assert.Equal("open", sim.Interactive.StateOf("gate")); // takes a second
        for (int i = 0; i < sim.TicksFor(1.1); i++) sim.Step();
        Assert.Equal("closed", sim.Interactive.StateOf("gate"));
        Assert.Contains("перезарядка", sim.Interactive.CanInteract(knight, "lever"));
        for (int i = 0; i < sim.TicksFor(3); i++) sim.Step();
        Assert.Null(sim.Interactive.CanInteract(knight, "lever"));

        // A stun interrupts: nothing happens.
        Assert.True(sim.Input.Interact(knight, "lever", out _));
        knight.Flags.BlocksActions = true;
        sim.Interactive.Update();
        Assert.Empty(sim.Interactive.InProgress);
        Assert.Equal("closed", sim.Interactive.StateOf("gate"));

        // The receiver removed — found by the check before any battle.
        sc.Objects.RemoveAll(o => o.Id == "gate");
        Assert.Contains(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Message.Contains("сигнал") && i.Message.Contains("«gate» нет"));
    }

    [Fact]
    public void ATurret_UsesItsSkillThroughTheAutoAttacks_AndItsStateSwitchesItOff()
    {
        var (db, sc) = Scene();
        var sim = Battle(db, sc);
        var turret = sim.Structures.Of("turret")!;
        Assert.True(turret.Indestructible);
        Assert.Contains(turret.Def.AutoAttacks, a => a.Skill == "zap");
        var knight = sim.Spawn(db.Unit("knight")!, "player", new HexCoord(6, 1), HexDirection.E);
        double hp = knight.Hp;
        for (int i = 0; i < sim.TicksFor(4); i++) sim.Step();
        Assert.True(knight.Hp < hp, "турель должна стрелять навыком «zap»");
        Assert.True(sim.Interactive.SetState("turret", "off", "тест", out _));
        for (int i = 0; i < sim.TicksFor(1.5); i++) sim.Step();
        hp = knight.Hp;
        for (int i = 0; i < sim.TicksFor(4); i++) sim.Step();
        Assert.Equal(hp, knight.Hp);
    }

    [Fact]
    public void StatesRunTheirActions_AChainIsCut_TheConditionAndEventWork_AndTheReplayRepeats()
    {
        var (db, sc) = Scene();
        // The gate closing opens a door, the door switches the gate back… — a ring: the check warns, the battle cuts it.
        sc.Objects.Add(new ScenarioObjectDef
        {
            Id = "door", Name = "Дверь", Type = ScenarioObjectType.Custom, Anchor = new HexCoord(7, 3),
            States = new ObjectStatesDef
            {
                States =
                {
                    new ObjectStateDef { Id = "a", OnEnter = { new TriggerActionDef { Type = TriggerActionType.SwitchObjectState, Object = "gate" } } },
                    new ObjectStateDef { Id = "b", OnEnter = { new TriggerActionDef { Type = TriggerActionType.SwitchObjectState, Object = "gate" } } },
                },
            },
        });
        foreach (var s in sc.Objects[0].States!.States) s.OnEnter.Add(new TriggerActionDef { Type = TriggerActionType.SwitchObjectState, Object = "door" });
        Assert.Contains(ContentValidator.Validate(db), i => i.Message.Contains("переключают друг друга по кругу"));
        sc.Triggers.Add(new TriggerDef
        {
            Name = "Ворота закрыты", Event = ScenarioEventType.ObjectStateChanged, EventObject = "gate",
            Condition = new ConditionDef { Type = ConditionType.ObjectInState, ObjectId = "gate", ObjectState = "closed" },
            Actions = { new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = "gate_closed" } },
        });
        sc.Units.Add(new ScenarioUnitDef { Unit = "knight", Faction = "player", C = new HexCoord(1, 1), Facing = HexDirection.E });
        var (snap, sim) = BattleSnapshot.Begin(db, sc, 5);
        var rec = BattleReplay.Record(snap, sim, withContent: true);
        var knight = sim.Units.First(u => u.Def.Id == "knight");
        Assert.True(sim.Input.Interact(knight, "lever", out var why), why);
        for (int i = 0; i < sim.TicksFor(2); i++) sim.Step();
        // The ring was cut after a fixed number of steps; the battle goes on.
        Assert.True(sim.Interactive.StateOf("gate") is "open" or "closed");
        Assert.True(sim.Interactive.StateOf("door") is "a" or "b");
        Assert.Contains(sim.Log.Entries, e => e.Text.Contains("цепочка сигналов длиннее"));
        rec.Finish(sim);
        Assert.Contains(rec.Entries, e => e.Op == ReplayOp.Interact);
        var play = BattleReplay.FromJson(rec.ToJson())!.Play();
        Assert.True(play.Matches, play.Text);
        Assert.Equal(sim.Interactive.StateOf("gate"), play.Sim!.Interactive.StateOf("gate"));

        // Without the ring: the lever closes the gate, the trigger on «объект сменил состояние» + «объект в состоянии» fires.
        foreach (var o in sc.Objects.Where(o => o.States != null)) foreach (var st in o.States!.States) st.OnEnter.Clear();
        var sim2 = Battle(db, sc);
        var k2 = sim2.Units.First(u => u.Def.Id == "knight");
        Assert.True(sim2.Input.Interact(k2, "lever", out why), why);
        for (int i = 0; i < sim2.TicksFor(2); i++) sim2.Step();
        Assert.Equal("closed", sim2.Interactive.StateOf("gate"));
        Assert.Contains("gate_closed", sim2.Scenario.Flags);
        Assert.Contains("gate=closed", sim2.Interactive.Digest()); // the state is part of the fingerprint (save and load, T28)
    }

    [Fact]
    public void ShippedPresets_RunOnAnEmptyMap_AndTheCoverBreaksIntoRubble()
    {
        var db = TestUtil.Shipped();
        var presets = db.All<ObjectPresetDef>().ToList();
        Assert.True(presets.Count >= 6);
        foreach (var p in presets)
        {
            var (tdb, sc) = ObjectPresets.Sandbox(db, p.Object);
            var errors = ContentValidator.Validate(tdb).Where(i => i.Severity == IssueSeverity.Error && i.Source.Contains(sc.Id)).Select(i => i.Message).ToList();
            if (p.Object.Interaction?.Effect == InteractionEffect.Signal) Assert.Contains(errors, e => e.Contains("сигнал без получателя"));
            else Assert.True(errors.Count == 0, $"{p.Id}: {string.Join("; ", errors)}");
            var sim = ScenarioSystem.Create(tdb, sc, 1);
            for (int i = 0; i < sim.TicksFor(3); i++) sim.Step();
            Assert.Equal(BattleOutcome.None, sim.Outcome);
        }
        // The cover: destroyed → «Разрушено», crates → rubble, through the map service.
        var (cdb, csc) = ObjectPresets.Sandbox(db, db.Get<ObjectPresetDef>("obj_cover")!.Object);
        var csim = ScenarioSystem.Create(cdb, csc, 1);
        var at = csc.Objects[0].Anchor;
        Assert.Equal("crates", csim.Map.Get(at)!.Terrain.Id);
        var cover = csim.Structures.Of(csc.Objects[0].Id)!;
        csim.Structures.DamageStructure(cover, cover.Hp + 1, null, "тест", force: true);
        csim.Step();
        Assert.Equal("broken", csim.Interactive.StateOf(csc.Objects[0].Id));
        Assert.Equal("rubble", csim.Map.Get(at)!.Terrain.Id);

        // Placing a preset gives a free ID; a preset made of an object drops its place.
        var scenario = new ScenarioDef { Id = "x" };
        var a = ObjectPresets.Place(scenario, db.Get<ObjectPresetDef>("obj_gate")!, new HexCoord(1, 1));
        var b = ObjectPresets.Place(scenario, db.Get<ObjectPresetDef>("obj_gate")!, new HexCoord(3, 1));
        Assert.Equal("gate", a.Id);
        Assert.Equal("gate_2", b.Id);
        var saved = ObjectPresets.From(b, "my_gate");
        Assert.Equal("", saved.Object.Id);
        Assert.Equal(HexCoord.Zero, saved.Object.Anchor);
    }
}
