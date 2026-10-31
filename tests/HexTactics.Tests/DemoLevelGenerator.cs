using System;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// The authoring tool of the demo level «Два прохода» (part 6, §107): <c>DEMO_OUT=dir dotnet test --filter DemoLevelGenerator</c>
/// writes the scenario and the changed library files through the content's own serializer; without DEMO_OUT it does nothing.
/// </summary>
public class DemoLevelGenerator
{
    /// <summary>
    /// The demo level of part 6 (ТЗ 15): one base, two routes, a near and a far zone of deployment, three defenders (a blocking guard,
    /// a shooter, a cheap mobile fighter), points for two calls, a drawbridge with a lever, a wave started by the bridge, a mini-boss
    /// with two phases, a harder variant and a campaign of two missions. Everything else — the waves, routes, attacks — from before.
    /// </summary>
    internal static void AddDemo(ContentDatabase db)
    {
        // The demo may be in the content already (a regeneration): replace, never keep the old one.
        void Put(Def d)
        {
            if (db.Exists(d.Category, d.Id)) db.Table(d.Category).Replace(d);
            else db.Add(d);
        }
        // The mobile fighter is cheap (ТЗ 14.3).
        db.Unit("scout")!.Deployment = new UnitDeploymentDef { Cost = 3 };
        // The mini-boss: a warlord of the orcs with two phases.
        var boss = ContentJson.Clone(db.Unit("orc_brute")!);
        boss.Id = "pass_warlord";
        boss.Name = "Вожак перевала";
        boss.Description = "Мини-босс «Двух проходов»: ниже 60 % здоровья впадает в ярость, ниже 30 % зовёт двух гоблинов.";
        boss.Stats["hp_max"] = 320;
        boss.Boss = new BossDef
        {
            IsBoss = true, Title = "Вожак перевала",
            Phases =
            {
                new BossPhaseDef
                {
                    Id = "rage", Name = "Ярость", When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.6 }, Message = "Вожак впадает в ярость!",
                    Actions = { new TriggerActionDef { Type = TriggerActionType.ApplyStatus, Target = ActionTarget.Self, Status = "frenzy", Amount = 1 } },
                },
                new BossPhaseDef
                {
                    Id = "call", Name = "Последний рёв", From = { "rage" }, When = new ConditionDef { Type = ConditionType.HpBelow, Fraction = 0.3 },
                    Message = "Вожак зовёт подмогу!",
                    Actions = { new TriggerActionDef { Type = TriggerActionType.SummonUnits, Unit = "goblin", Count = 2, Radius = 1 } },
                },
            },
        };
        Put(boss);

        var sc = ContentJson.Clone(db.Get<ScenarioDef>("td_pass_defense")!);
        sc.Id = "two_passes";
        sc.Name = "Два прохода";
        sc.Description = "Демонстрационный уровень части 6: две дороги к одной базе, вызов бойцов за очки, блокирующий страж, подъёмный мост, вожак с двумя фазами.";
        sc.Intro = "Две дороги ведут к базе. Очков хватит на два вызова — выберите, кого и куда. Мост на южном обходе поднят: опустят — пойдёт вторая колонна.";
        sc.Mode = BattleMode.TowerDefense;
        sc.GameRules = "hybrid";
        sc.GameRulesOverride = new GameRulesOverrideDef { SquadStart = SquadStart.NoneOnField };
        sc.Squad = new ScenarioSquadDef { Enabled = true, Pool = { "knight", "archer", "scout" }, MaxSize = 3, MinSize = 1 };
        var d = ContentJson.Clone(db.Get<GameRulesDef>("hybrid")!.Deployment);
        d.StartingPoints = 10;
        d.GainAmount = 0;
        d.GainPerKill = 0;
        d.DefaultCost = 5;
        d.MaxDeployed = 3;
        sc.DeploymentOverride = d;
        var map = db.Get<MapDef>(sc.Map)!;
        bool Open(HexCoord c) => map.Cells.FirstOrDefault(x => x.C == c) is { } cell && db.Terrain(cell.T) is { } t && t.IsPassableFor("walk")
                                 && !sc.Objects.Any(o => o.Cells().Contains(c));
        var basePos = sc.Objects.First(o => o.Id == "base_1").Anchor;
        var post = sc.Objects.First(o => o.Id == "forward_post").Anchor;
        sc.DeploymentZones = new()
        {
            new DeploymentZoneDef { Id = "near", Name = "Ближняя", Tags = { "ближняя" }, Cells = HexCoord.Spiral(basePos, 2).Where(Open).ToList() },
            new DeploymentZoneDef { Id = "far", Name = "Дальняя", Tags = { "дальняя" }, Priority = 1, Cells = HexCoord.Spiral(post, 1).Where(Open).ToList() },
        };
        // The drawbridge on the southern bypass and the lever by the forward post.
        var bridge = ObjectPresets.Place(sc, db.Get<ObjectPresetDef>("obj_drawbridge")!, new HexCoord(1, 7));
        bridge.Id = "bridge";
        bridge.Name = "Южный мост";
        var lever = ObjectPresets.Place(sc, db.Get<ObjectPresetDef>("obj_lever")!, new HexCoord(5, 6));
        lever.Id = "lever";
        lever.Interaction!.Target = "bridge";
        // The waves: the bypass waits for the bridge; the warlord comes last.
        var bypass = sc.Waves.First(w => w.Name == "Обход");
        bypass.Start = WaveStart.Manual;
        int bypassIndex = sc.Waves.IndexOf(bypass);
        var last = ContentJson.Clone(sc.Waves.First(w => w.Name == "Таран"));
        last.Name = "Вожак";
        last.Time = 70;
        foreach (var g in last.Groups) g.Unit = "pass_warlord";
        foreach (var g in last.Groups) g.Count = 1;
        sc.Waves.Add(last);
        // The southern spawn opens with the bridge (not after the second wave as in «Перевал: оборона»).
        sc.Triggers.RemoveAll(t => t.Actions.Any(a => a.Type == TriggerActionType.ActivateBase && a.Object == "spawn_2"));
        sc.Objects.First(o => o.Id == "spawn_2").Spawn!.Active = true;
        sc.Triggers.Add(new TriggerDef
        {
            Name = "Мост опущен — обход", Event = ScenarioEventType.ObjectStateChanged, EventObject = "bridge", Once = true,
            Condition = new ConditionDef { Type = ConditionType.Always },
            Actions =
            {
                new TriggerActionDef { Type = TriggerActionType.Message, Text = "Южный мост опущен — по обходу идёт вторая колонна!" },
                new TriggerActionDef { Type = TriggerActionType.SpawnWave, Wave = bypassIndex },
            },
        });
        sc.Triggers.Add(new TriggerDef
        {
            Name = "Сапёры у моста", Event = ScenarioEventType.Timer, Interval = 40, Once = true, Condition = new ConditionDef { Type = ConditionType.Always },
            Actions = { new TriggerActionDef { Type = TriggerActionType.SwitchObjectState, Object = "bridge", ObjectStateId = "lowered" } },
        });
        Put(sc);

        var mods = new ModifierSetDef
        {
            Id = "two_passes_hard_mods", Name = "Два прохода — тяжёлый",
            Modifiers =
            {
                new MissionModifierDef
                {
                    Id = "tough_enemies", Label = "Закалённые враги", Description = "+30 % здоровья врагов", Op = MissionModOp.Stat,
                    Selector = new MissionSelectorDef { Faction = "enemy" }, Stat = new StatModDef { Stat = "hp_max", Mul = 0.3 },
                },
            },
        };
        Put(mods);
        Put(new DifficultyVariantDef { Id = "two_passes_hard", Name = "Два прохода — тяжёлый", Scenario = "two_passes", ModifierSets = { mods.Id } });
        Put(new CampaignDef
        {
            Id = "two_passes_campaign", Name = "Два прохода", Intro = "Простейшая кампания демоуровня: удержать два прохода, затем — развилку.",
            StartNodes = { "hold" },
            Nodes =
            {
                new CampaignNodeDef { Id = "hold", Scenario = "two_passes", StageLabel = "Задание 1", Next = { "fork" }, X = 0, Y = 0 },
                new CampaignNodeDef { Id = "fork", Scenario = "td_pass_fork", StageLabel = "Задание 2", Description = "Открывается после «Двух проходов».", X = 260, Y = 0 },
            },
        });
    }

    [Fact]
    public void Generate()
    {
        var dir = Environment.GetEnvironmentVariable("DEMO_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        var db = TestUtil.Shipped();
        AddDemo(db);
        var (plan, _) = ContentIO.BuildPlan(db, new MemoryStorage());
        foreach (var file in new[] { "scenarios/two_passes.json", "units.json", "modifier_sets.json", "difficulty_variants.json", "campaigns.json", "skills.json", "summons.json" })
        {
            var path = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, plan[file]);
        }
    }

}
