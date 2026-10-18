// One-time bootstrap of the built-in content pack (game/content).
// The JSON files it writes are the source of truth afterwards and are edited in the game editor.
// Usage: dotnet run --project tools/ContentBootstrap -- <output dir> [--force]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace ContentBootstrap;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: ContentBootstrap <output dir> [--force]");
            return 2;
        }
        var outDir = args[0];
        bool force = args.Contains("--force");
        if (File.Exists(Path.Combine(outDir, "rules.json")) && !force)
        {
            Console.Error.WriteLine($"{outDir} уже содержит контент. Используйте --force для перезаписи.");
            return 1;
        }
        var db = Build();
        var issues = ContentValidator.Validate(db);
        foreach (var i in issues) Console.WriteLine(i);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
        {
            Console.Error.WriteLine("Контент содержит ошибки — не записан.");
            return 3;
        }
        var storage = new FileSystemStorage(outDir);
        var written = ContentIO.Save(db, storage);
        Console.WriteLine($"Записано файлов: {written.Count}");
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static SkillEffectDef Dmg(string type, double amount, TargetFilterDef? f = null) =>
        new() { Type = EffectType.Damage, DamageType = type, Amount = amount, Filter = f ?? TargetFilterDef.EnemiesOnly() };

    private static SkillEffectDef Heal(double amount, TargetFilterDef? f = null) =>
        new() { Type = EffectType.Heal, Amount = amount, RequiresHit = false, Filter = f ?? TargetFilterDef.AlliesAndSelf() };

    private static SkillEffectDef Status(string id, double amount = 1, double duration = 0, double chance = 1, TargetFilterDef? f = null, bool requiresHit = true) =>
        new() { Type = EffectType.ApplyStatus, Status = id, Amount = amount, Duration = duration, Chance = chance, Filter = f ?? TargetFilterDef.EnemiesOnly(), RequiresHit = requiresHit };

    private static SkillEffectDef Cell(string id, double duration = 0, int level = 1, double chance = 1) =>
        new() { Type = EffectType.AddCellEffect, CellEffect = id, Duration = duration, Level = level, Chance = chance };

    private static TargetFilterDef All() => TargetFilterDef.Everyone();
    private static TargetFilterDef AllButSelf() => new() { Enemies = true, Allies = true, Neutrals = true, Self = false };
    private static TargetFilterDef Self() => new() { Enemies = false, Allies = false, Neutrals = false, Self = true };

    private static StatusLevelDef Level(string name, double threshold, Action<StatusLevelDef>? setup = null)
    {
        var l = new StatusLevelDef { Name = name, Threshold = threshold };
        setup?.Invoke(l);
        return l;
    }

    private static StatModDef Mod(string stat, double add = 0, double mul = 0) => new() { Stat = stat, Add = add, Mul = mul };

    private static SkillStageDef Stage(AreaDef area, params SkillEffectDef[] effects) => new() { Area = area, Effects = effects.ToList() };

    private static AreaDef Single() => new() { Shape = AreaShape.Single };
    private static AreaDef Radius(int r, bool center = true) => new() { Shape = AreaShape.Radius, Size = r, IncludeCenter = center };

    // ------------------------------------------------------------------ content

    public static ContentDatabase Build()
    {
        var db = new ContentDatabase { Rules = new RulesDef() };
        Stats(db);
        Resources(db);
        DamageTypes(db);
        Factions(db);
        Terrains(db);
        CellEffects(db);
        Synergies(db);
        Statuses(db);
        Skills(db);
        Passives(db);
        AiProfiles(db);
        Units(db);
        Maps(db);
        Scenarios(db);
        return db;
    }

    private static void Stats(ContentDatabase db)
    {
        void S(string id, string name, double def, double min, double max, StatFormat f = StatFormat.Number, bool show = true, string desc = "") =>
            db.Add(new StatDef { Id = id, Name = name, Default = def, Min = min, Max = max, Format = f, ShowInPanel = show, Description = desc });
        S(HexTactics.Core.Content.Stats.HpMax, "Макс. здоровье", 100, 1, 100000);
        S(HexTactics.Core.Content.Stats.MoveSpeed, "Скорость", 1.5, 0, 20, desc: "Единиц стоимости перемещения в секунду");
        S(HexTactics.Core.Content.Stats.ApMax, "Макс. ОД", 10, 0, 1000);
        S(HexTactics.Core.Content.Stats.ApRegen, "Восстановление ОД/с", 1, 0, 100);
        S(HexTactics.Core.Content.Stats.DetectionRange, "Дальность обнаружения", 7, 0, 50);
        S(HexTactics.Core.Content.Stats.Accuracy, "Точность", 0, -1, 1, StatFormat.Percent, desc: "Прибавка к шансу попадания");
        S(HexTactics.Core.Content.Stats.Evasion, "Уклонение", 0.1, 0, 0.95, StatFormat.Percent, desc: "Вычитается из шанса попадания по юниту, если атака замечена");
        S(HexTactics.Core.Content.Stats.BlockChance, "Шанс блока", 0, 0, 1, StatFormat.Percent);
        S(HexTactics.Core.Content.Stats.BlockReduction, "Снижение урона блоком", 0.5, 0, 1, StatFormat.Percent);
        S(HexTactics.Core.Content.Stats.Armor, "Броня", 0, 0, 1000, desc: "Вычитается из урона типов, учитывающих броню");
        S(HexTactics.Core.Content.Stats.CritChance, "Шанс крита", 0.05, 0, 1, StatFormat.Percent);
        S(HexTactics.Core.Content.Stats.CritMult, "Множитель крита", 1.5, 1, 10);
        S(HexTactics.Core.Content.Stats.Perception, "Внимательность", 0, -1, 1, StatFormat.Percent, desc: "Прибавка к шансу заметить атаку");
        S(HexTactics.Core.Content.Stats.Stealth, "Скрытность", 0, 0, 1, StatFormat.Percent, desc: "Снижает дальность обнаружения юнита и шанс заметить его атаку");
        S(HexTactics.Core.Content.Stats.InterruptResist, "Устойчивость к прерыванию", 0, 0, 1, StatFormat.Percent);
        S(HexTactics.Core.Content.Stats.Power, "Сила", 0, 0, 1000, desc: "Используется масштабированием урона навыков");
        S(HexTactics.Core.Content.Stats.HealPower, "Сила лечения", 0, -1, 10, StatFormat.Percent);
        foreach (var (id, name) in new[] { ("physical", "физическому"), ("fire", "огню"), ("ice", "холоду"), ("electric", "электричеству"), ("poison", "яду"), ("magic", "магии"), ("true", "чистому урону") })
            S("resist_" + id, "Сопротивление " + name, 0, -1, 1, StatFormat.Percent, false);
    }

    private static void Resources(ContentDatabase db)
    {
        db.Add(new ResourceDef { Id = "mana", Name = "Мана", Color = "#4a7fd0", DefaultMax = 50, DefaultRegen = 1 });
        db.Add(new ResourceDef { Id = "energy", Name = "Энергия", Color = "#d0b030", DefaultMax = 100, DefaultRegen = 5 });
        db.Add(new ResourceDef { Id = "ammo", Name = "Боеприпасы", Color = "#9a9a9a", DefaultMax = 20, DefaultRegen = 0 });
    }

    private static void DamageTypes(ContentDatabase db)
    {
        db.Add(new DamageTypeDef { Id = "physical", Name = "Физический", Color = "#d8d8d8", ArmorApplies = true });
        db.Add(new DamageTypeDef { Id = "fire", Name = "Огненный", Color = "#ff7a2a" });
        db.Add(new DamageTypeDef { Id = "ice", Name = "Ледяной", Color = "#8fd8ff" });
        db.Add(new DamageTypeDef { Id = "electric", Name = "Электрический", Color = "#ffe04a" });
        db.Add(new DamageTypeDef { Id = "poison", Name = "Ядовитый", Color = "#7ad04a" });
        db.Add(new DamageTypeDef { Id = "magic", Name = "Магический", Color = "#c08aff" });
        db.Add(new DamageTypeDef { Id = "true", Name = "Чистый", Color = "#ff4a6a", Description = "Не снижается бронёй" });
    }

    private static void Factions(ContentDatabase db)
    {
        db.Add(new FactionDef { Id = "player", Name = "Отряд", Color = "#3c78d8", Shape = FactionShape.Circle, HostileTo = new() { "enemy" } });
        db.Add(new FactionDef { Id = "enemy", Name = "Противник", Color = "#d04a3a", Shape = FactionShape.Diamond, HostileTo = new() { "player" } });
        db.Add(new FactionDef { Id = "neutral", Name = "Нейтралы", Color = "#c8a848", Shape = FactionShape.Square });
    }

    private static void Terrains(ContentDatabase db)
    {
        TerrainDef T(string id, string name, string color, TerrainVisual vis, double cost = 1, params string[] tags)
        {
            var t = new TerrainDef { Id = id, Name = name, Color = color, Visual = vis, MoveCost = cost, Tags = tags.ToList() };
            db.Add(t);
            return t;
        }

        T("grass", "Трава", "#86a35c", TerrainVisual.Grass, 1, "ground", "grass").Description = "Обычная земля";
        T("dirt", "Земля", "#9c8763", TerrainVisual.Ground, 1, "ground");
        T("stone", "Каменный пол", "#9a9894", TerrainVisual.Stone, 1, "ground", "stone");
        T("sand", "Песок", "#cdb98a", TerrainVisual.Sand, 1.5, "ground", "sand");
        var hl = T("highland", "Возвышенность", "#9aa36e", TerrainVisual.Grass, 1, "ground", "highland");
        hl.DefaultHeight = 1;
        hl.Description = "Даёт бонус точности и дальности сверху";
        var low = T("lowland", "Низина", "#6f8c55", TerrainVisual.Grass, 1.3, "ground", "lowland");
        low.DefaultHeight = -1;
        var shallow = T("shallow_water", "Мелководье", "#5d93be", TerrainVisual.Water, 2.5, "water", "shallow", "liquid");
        shallow.Description = "Проходимо, но медленно. Проводит электричество";
        var deep = T("deep_water", "Глубокая вода", "#3b6b9a", TerrainVisual.Water, 3, "water", "deep", "liquid");
        deep.PassableFor = new() { "swim", "fly" };
        deep.AllowsPlacement = false;
        deep.DefaultHeight = -1;
        var pit = T("pit", "Пропасть", "#2a2622", TerrainVisual.Pit, 1, "pit");
        pit.PassableFor = new() { "fly" };
        pit.AllowsPlacement = false;
        pit.DefaultHeight = -3;
        pit.Description = "Яма/пропасть: непроходима для пеших";
        var swamp = T("toxic_swamp", "Ядовитое болото", "#6f7d3a", TerrainVisual.Hazard, 2, "ground", "swamp");
        swamp.Hazard = true;
        swamp.IntrinsicEffects = new() { "miasma" };
        swamp.Description = "Опасная зона: накапливает отравление";
        var wall = T("wall", "Стена", "#787470", TerrainVisual.Wall, 1, "stone", "wall");
        wall.PassableFor = new();
        wall.BlocksLoS = true;
        wall.ObstacleHeight = 3;
        wall.AllowsPlacement = false;
        var rock = T("rock", "Скала", "#7d7a72", TerrainVisual.Rock, 1, "stone", "obstacle");
        rock.PassableFor = new() { "fly" };
        rock.BlocksLoS = true;
        rock.ObstacleHeight = 1.6;
        rock.AllowsPlacement = false;
        rock.Description = "Непроходимое препятствие";
        var crate = T("crates", "Ящики", "#a0784a", TerrainVisual.Crate, 1, "wood", "burnable", "obstacle");
        crate.PassableFor = new() { "fly" };
        crate.BlocksLoS = true;
        crate.ObstacleHeight = 1.1;
        crate.Cover = 0.4;
        crate.AllowsPlacement = false;
        crate.DestructibleHp = 30;
        crate.DestroyedTerrain = "rubble";
        crate.Description = "Разрушаемое препятствие";
        var bushes = T("bushes", "Кусты", "#5f8a42", TerrainVisual.Vegetation, 1.5, "ground", "vegetation", "burnable");
        bushes.Cover = 0.35;
        var trees = T("trees", "Деревья", "#4d7a3a", TerrainVisual.Vegetation, 2, "ground", "vegetation", "burnable", "tall");
        trees.BlocksLoS = true;
        trees.ObstacleHeight = 2.5;
        trees.Cover = 0.5;
        T("rubble", "Обломки", "#8a7f70", TerrainVisual.Rubble, 2, "ground", "stone").Cover = 0.2;
        T("ash", "Пепелище", "#5a5450", TerrainVisual.Ground, 1, "ground", "ash");
        T("ice_floor", "Лёд", "#bfe3f0", TerrainVisual.Ice, 1.5, "ground", "ice").Description = "Замёрзшая вода";
        var altar = T("altar", "Алтарь", "#c9a227", TerrainVisual.Special, 1, "ground", "special");
        altar.Description = "Специальная клетка сценария";
    }

    private static void CellEffects(ContentDatabase db)
    {
        db.Add(new CellEffectDef
        {
            Id = "fire", Name = "Огонь", Duration = 6, TickInterval = 1, Color = "#ff7a2a", Visual = CellEffectVisual.Flames, Icon = "О",
            Tags = new() { "fire", "hot" }, Hazard = true, ForbiddenTerrainTags = new() { "water", "pit" }, MaxLevel = 3, StackPolicy = CellStackPolicy.Refresh,
            OnTick = new() { Dmg("fire", 4, All()), Status("burning", 1, 3, 0.35, All(), false) },
            OnEnter = new() { Dmg("fire", 3, All()) },
            Spread = new SpreadDef { Enabled = true, Interval = 1.5, Chance = 0.3, ToTerrainTags = new() { "burnable", "grass" }, MaxGenerations = 3, ChildDurationFactor = 0.8 },
            Description = "Наносит урон огнём, распространяется по растительности",
        });
        db.Add(new CellEffectDef
        {
            Id = "poison_cloud", Name = "Ядовитое облако", Duration = 10, TickInterval = 0.5, Color = "#8acc3a", Visual = CellEffectVisual.Gas, Icon = "Я",
            Tags = new() { "poison", "gas" }, Hazard = true, NoticeModifier = -0.1, ForbiddenTerrainTags = new() { "pit" },
            ImmuneCreatureCategories = new() { "construct" },
            OnTick = new() { Status("poison", 7, 0, 1, All(), false) },
            Spread = new SpreadDef { Enabled = true, Interval = 2, Chance = 0.12, MaxGenerations = 2, ChildDurationFactor = 0.6 },
        });
        db.Add(new CellEffectDef
        {
            Id = "miasma", Name = "Миазмы", Duration = 0, TickInterval = 0.5, Color = "#9aa03a", Visual = CellEffectVisual.Gas, Icon = "м",
            Tags = new() { "poison" }, Hazard = true, ImmuneCreatureCategories = new() { "construct" },
            OnTick = new() { Status("poison", 4, 0, 1, All(), false) },
            Description = "Постоянные испарения ядовитого болота",
        });
        db.Add(new CellEffectDef
        {
            Id = "frost", Name = "Изморозь", Duration = 10, TickInterval = 1, Color = "#a8e4ff", Visual = CellEffectVisual.Ice, Icon = "И",
            Tags = new() { "ice", "cold" }, MoveCostAdd = 1, ForbiddenTerrainTags = new() { "pit", "deep" },
            OnEnter = new() { Status("chilled", 20, 0, 1, All(), false) },
            OnTick = new() { Status("chilled", 6, 0, 1, All(), false) },
        });
        db.Add(new CellEffectDef
        {
            Id = "electric", Name = "Электричество", Duration = 3, TickInterval = 0.5, Color = "#ffe04a", Visual = CellEffectVisual.Sparks, Icon = "Э",
            Tags = new() { "electric" }, Hazard = true, ForbiddenTerrainTags = new() { "pit" },
            OnTick = new() { Dmg("electric", 3, All()) },
            Spread = new SpreadDef { Enabled = true, Interval = 0.25, Chance = 1.0, ToTerrainTags = new() { "water" }, ToCellsWithEffects = new() { "wet" }, MaxGenerations = 6, ChildDurationFactor = 0.85 },
            Description = "Распространяется по воде и мокрым клеткам",
        });
        db.Add(new CellEffectDef
        {
            Id = "smoke", Name = "Дым", Duration = 8, TickInterval = 1, Color = "#9a9a9a", Visual = CellEffectVisual.Smoke, Icon = "Д",
            Tags = new() { "smoke", "gas" }, BlocksLoS = true, NoticeModifier = -0.3,
        });
        db.Add(new CellEffectDef
        {
            Id = "steam", Name = "Пар", Duration = 5, TickInterval = 1, Color = "#e8eef0", Visual = CellEffectVisual.Smoke, Icon = "П",
            Tags = new() { "gas", "hot" }, BlocksLoS = true, NoticeModifier = -0.2,
            OnTick = new() { Dmg("fire", 2, All()) },
        });
        db.Add(new CellEffectDef
        {
            Id = "wet", Name = "Влага", Duration = 12, Color = "#6aa8e0", Visual = CellEffectVisual.Liquid, Icon = "В",
            Tags = new() { "wet", "water" }, ForbiddenTerrainTags = new() { "pit" },
        });
        db.Add(new CellEffectDef
        {
            Id = "holy", Name = "Святая земля", Duration = 12, TickInterval = 1, Color = "#ffe9a0", Visual = CellEffectVisual.Runes, Icon = "С",
            Tags = new() { "holy" },
            OnTick = new() { Heal(4), Status("regeneration", 1, 2, 1, TargetFilterDef.AlliesAndSelf(), false) },
        });
        db.Add(new CellEffectDef
        {
            Id = "cursed", Name = "Проклятая земля", Duration = 12, TickInterval = 1, Color = "#8a4ac0", Visual = CellEffectVisual.Runes, Icon = "П",
            Tags = new() { "curse" }, Hazard = true,
            OnTick = new() { Dmg("magic", 2), Status("vulnerable", 1, 2, 1, null, false) },
        });
        db.Add(new CellEffectDef
        {
            Id = "mire", Name = "Трясина", Duration = 10, Color = "#6a5a3a", Visual = CellEffectVisual.Liquid, Icon = "Т",
            Tags = new() { "slow" }, MoveCostAdd = 2, ForbiddenTerrainTags = new() { "pit", "deep" },
        });
        db.Add(new CellEffectDef
        {
            Id = "trap", Name = "Капкан", Duration = 0, Color = "#b0a090", Visual = CellEffectVisual.Runes, Icon = "К",
            Tags = new() { "trap" }, ConsumeOnEnter = true, Hazard = true,
            OnEnter = new() { Dmg("physical", 12, All()), Status("rooted", 1, 2.5, 1, All(), false) },
        });
        db.Add(new CellEffectDef
        {
            Id = "barrier", Name = "Барьер", Duration = 6, Color = "#80d0ff", Visual = CellEffectVisual.Barrier, Icon = "Б",
            Tags = new() { "barrier" }, BlocksMovement = true,
        });
    }

    private static void Synergies(ContentDatabase db)
    {
        SynergyOperandDef E(string id) => new() { Kind = SynergyOperandKind.CellEffect, Value = id };
        SynergyOperandDef ET(string tag) => new() { Kind = SynergyOperandKind.CellEffectTag, Value = tag };
        SynergyOperandDef TT(string tag) => new() { Kind = SynergyOperandKind.TerrainTag, Value = tag };

        db.Add(new SynergyDef
        {
            Id = "fire_poison_blast", Name = "Взрыв ядовитого облака", A = E("fire"), B = E("poison_cloud"), RemoveA = true, RemoveB = true, Radius = 1,
            UnitEffects = new() { Dmg("fire", 16, All()) }, RadiusCellEffects = new() { "fire" }, CooldownPerCell = 3,
            LogText = "ядовитое облако вспыхивает и взрывается!",
        });
        db.Add(new SynergyDef
        {
            Id = "fire_miasma", Name = "Вспышка миазмов", A = E("fire"), B = E("miasma"), RemoveA = true, RemoveB = false, Radius = 0,
            UnitEffects = new() { Dmg("fire", 10, All()) }, CooldownPerCell = 4, LogText = "болотный газ вспыхивает",
        });
        db.Add(new SynergyDef
        {
            Id = "electric_water", Name = "Разряд по воде", A = E("electric"), B = TT("water"), RemoveA = false, RemoveB = false,
            UnitEffects = new() { Dmg("electric", 6, All()), Status("shocked", 1, 2, 1, All(), false) }, CooldownPerCell = 1,
            LogText = "электричество бьёт по воде",
        });
        db.Add(new SynergyDef
        {
            Id = "electric_wet", Name = "Разряд по влаге", A = E("electric"), B = E("wet"), RemoveA = false, RemoveB = false,
            UnitEffects = new() { Dmg("electric", 5, All()), Status("shocked", 1, 2, 1, All(), false) }, CooldownPerCell = 1,
            LogText = "электричество проходит по мокрой земле",
        });
        db.Add(new SynergyDef
        {
            Id = "fire_frost_steam", Name = "Таяние", A = E("fire"), B = E("frost"), RemoveA = true, RemoveB = true, AddEffects = new() { "steam" },
            CooldownPerCell = 1, LogText = "огонь растапливает лёд — пар",
        });
        db.Add(new SynergyDef
        {
            Id = "frost_water_freeze", Name = "Замерзание", A = E("frost"), B = TT("shallow"), RemoveA = true, RemoveB = false, ConvertTerrain = "ice_floor",
            CooldownPerCell = 1, LogText = "вода замерзает",
        });
        db.Add(new SynergyDef
        {
            Id = "fire_ice_melt", Name = "Оттаивание", A = E("fire"), B = TT("ice"), RemoveA = true, RemoveB = false, ConvertTerrain = "shallow_water",
            AddEffects = new() { "steam" }, CooldownPerCell = 1, LogText = "лёд тает — вода и пар",
        });
        db.Add(new SynergyDef
        {
            Id = "fire_burnable", Name = "Горение", A = E("fire"), B = TT("burnable"), RemoveA = false, RemoveB = false, ConvertTerrain = "ash",
            CooldownPerCell = 1, LogText = "растительность/дерево сгорает",
        });
        db.Add(new SynergyDef
        {
            Id = "water_wash_poison", Name = "Смывание яда", A = ET("poison"), B = TT("water"), RemoveA = true, RemoveB = false,
            CooldownPerCell = 0.5, LogText = "вода смывает яд",
        });
        db.Add(new SynergyDef
        {
            Id = "wet_poison", Name = "Смывание яда влагой", A = ET("poison"), B = E("wet"), RemoveA = true, RemoveB = false,
            CooldownPerCell = 0.5, LogText = "влага смывает яд",
        });
        db.Add(new SynergyDef
        {
            Id = "wet_fire", Name = "Тушение", A = E("fire"), B = E("wet"), RemoveA = true, RemoveB = true, AddEffects = new() { "steam" },
            CooldownPerCell = 1, LogText = "огонь гаснет — пар",
        });
    }

    private static void Statuses(ContentDatabase db)
    {
        db.Add(new StatusDef
        {
            Id = "poison", Name = "Отравление", Kind = StatusKind.Accumulating, MaxPoints = 100, DecayPerSecond = 3, Color = "#7ad04a", Icon = "Я",
            ImmuneCreatureCategories = new() { "construct" },
            Levels = new()
            {
                Level("лёгкое", 20, l => { l.OnTick.Add(Dmg("poison", 2, All())); }),
                Level("сильное", 50, l => { l.OnTick.Add(Dmg("poison", 4, All())); l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.Accuracy, -0.1)); }),
                Level("критическое", 85, l =>
                {
                    l.OnTick.Add(Dmg("poison", 7, All()));
                    l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.Accuracy, -0.15));
                    l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.MoveSpeed, 0, -0.3));
                    l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.ApRegen, 0, -0.3));
                }),
            },
            Description = "Накапливается в ядовитых клетках, спадает со временем",
        });
        db.Add(new StatusDef
        {
            Id = "burning", Name = "Горение", Kind = StatusKind.Timed, Duration = 4, Reapply = ReapplyPolicy.Intensify, MaxStacks = 3, Color = "#ff7a2a", Icon = "Г",
            Cancels = new() { "chilled" },
            Levels = new()
            {
                Level("", 1, l => l.OnTick.Add(Dmg("fire", 3, All()))),
                Level("сильное", 2, l => l.OnTick.Add(Dmg("fire", 5, All()))),
                Level("пылает", 3, l => l.OnTick.Add(Dmg("fire", 7, All()))),
            },
        });
        db.Add(new StatusDef
        {
            Id = "bleeding", Name = "Кровотечение", Kind = StatusKind.Timed, Duration = 6, Reapply = ReapplyPolicy.Stack, MaxStacks = 5, Color = "#c03030", Icon = "К",
            ImmuneCreatureCategories = new() { "construct" },
            Levels = new() { Level("", 1, l => l.OnTick.Add(Dmg("true", 2, All()))) },
        });
        db.Add(new StatusDef
        {
            Id = "stunned", Name = "Оглушение", Duration = 1.5, Reapply = ReapplyPolicy.Refresh, Color = "#f0e060", Icon = "О",
            Levels = new() { Level("", 1, l => { l.Flags.BlocksActions = true; l.Flags.InterruptsCast = true; }) },
        });
        db.Add(new StatusDef
        {
            Id = "blinded", Name = "Ослепление", Duration = 4, Color = "#e0e0e0", Icon = "С",
            Levels = new() { Level("", 1, l => { l.Flags.Blind = true; l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.Accuracy, -0.3)); }) },
        });
        db.Add(new StatusDef
        {
            Id = "rooted", Name = "Обездвиживание", Duration = 3, Color = "#a07850", Icon = "Ц",
            Levels = new() { Level("", 1, l => l.Flags.BlocksMovement = true) },
        });
        db.Add(new StatusDef
        {
            Id = "slowed", Name = "Замедление", Duration = 4, Reapply = ReapplyPolicy.ReplaceIfStronger, Color = "#80a0c0", Icon = "З",
            Levels = new() { Level("", 1, l => l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.MoveSpeed, 0, -0.4))) },
        });
        db.Add(new StatusDef
        {
            Id = "chilled", Name = "Охлаждение", Kind = StatusKind.Accumulating, MaxPoints = 100, DecayPerSecond = 8, Color = "#8fd8ff", Icon = "Х",
            ImmuneCreatureCategories = new() { "construct" }, Cancels = new() { "burning" },
            Levels = new()
            {
                Level("озноб", 25, l => l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.MoveSpeed, 0, -0.3))),
                Level("обледенение", 60, l =>
                {
                    l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.MoveSpeed, 0, -0.6));
                    l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.ApRegen, 0, -0.4));
                    l.Flags.BlocksTurning = true;
                }),
                Level("заморожен", 95, l => { l.Flags.BlocksActions = true; l.Flags.InterruptsCast = true; }),
            },
        });
        db.Add(new StatusDef
        {
            Id = "fear", Name = "Страх", Duration = 4, Color = "#9a60c0", Icon = "!",
            Levels = new() { Level("", 1, l => { l.Flags.Fear = true; l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.Accuracy, -0.1)); }) },
        });
        db.Add(new StatusDef
        {
            Id = "silenced", Name = "Немота", Duration = 4, Color = "#6070a0", Icon = "Н",
            Levels = new() { Level("", 1, l => l.Flags.BlocksSkills = true) },
        });
        db.Add(new StatusDef
        {
            Id = "vulnerable", Name = "Уязвимость", Duration = 5, Color = "#d06080", Icon = "У",
            Levels = new()
            {
                Level("", 1, l =>
                {
                    l.StatMods.Add(Mod("resist_physical", -0.25));
                    l.StatMods.Add(Mod("resist_magic", -0.25));
                    l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.Armor, 0, -0.5));
                }),
            },
        });
        db.Add(new StatusDef
        {
            Id = "shielded", Name = "Защита", Duration = 6, Negative = false, Color = "#60b0ff", Icon = "З",
            Levels = new() { Level("", 1, l => { l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.Armor, 5)); l.StatMods.Add(Mod("resist_magic", 0.2)); }) },
        });
        db.Add(new StatusDef
        {
            Id = "invisible", Name = "Невидимость", Duration = 6, Negative = false, Color = "#b0b0d0", Icon = "Н",
            Levels = new() { Level("", 1, l => { l.Flags.Invisible = true; l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.Stealth, 0.5)); }) },
        });
        db.Add(new StatusDef
        {
            Id = "resistant", Name = "Сопротивление стихиям", Duration = 8, Negative = false, Color = "#c0c060", Icon = "С",
            Levels = new()
            {
                Level("", 1, l =>
                {
                    foreach (var t in new[] { "fire", "ice", "electric", "poison" }) l.StatMods.Add(Mod("resist_" + t, 0.3));
                }),
            },
        });
        db.Add(new StatusDef
        {
            Id = "mind_control", Name = "Контроль разума", Duration = 5, Color = "#e050e0", Icon = "К",
            ImmuneCreatureCategories = new() { "construct" },
            Levels = new() { Level("", 1, l => { l.Flags.MindControl = true; l.Flags.InterruptsCast = true; }) },
        });
        db.Add(new StatusDef
        {
            Id = "shocked", Name = "Шок", Duration = 2, Color = "#ffe04a", Icon = "Ш",
            Levels = new() { Level("", 1, l => { l.StatMods.Add(Mod(HexTactics.Core.Content.Stats.ApRegen, 0, -0.5)); l.Flags.InterruptsCast = true; }) },
        });
        db.Add(new StatusDef
        {
            Id = "regeneration", Name = "Регенерация", Duration = 8, Negative = false, Color = "#60e080", Icon = "Р", Reapply = ReapplyPolicy.Refresh,
            Levels = new() { Level("", 1, l => l.OnTick.Add(Heal(3, Self()))) },
        });
    }

    private static void Skills(ContentDatabase db)
    {
        SkillDef S(string id, string name, SkillKind kind, TargetingMode t, double ap, double prep, double recovery, params SkillStageDef[] stages)
        {
            var s = new SkillDef { Id = id, Name = name, Kind = kind, Targeting = t, ApCost = ap, PrepTime = prep, RecoveryTime = recovery, Stages = stages.ToList() };
            db.Add(s);
            return s;
        }

        var strike = S("strike", "Удар", SkillKind.Attack, TargetingMode.Unit, 2, 0.35, 0.25, Stage(Single(), Dmg("physical", 10)));
        strike.Stages[0].Effects[0].ScalingStat = HexTactics.Core.Content.Stats.Power;
        strike.Stages[0].Effects[0].ScalingFactor = 1;
        strike.Icon = "У";
        strike.Description = "Обычная атака ближнего боя";

        var shot = S("shot", "Выстрел", SkillKind.Attack, TargetingMode.Unit, 2.5, 0.5, 0.3, Stage(Single(), Dmg("physical", 9)));
        shot.RangeMin = 2;
        shot.RangeMax = 6;
        shot.Ranged = true;
        shot.Vfx = SkillVfx.Projectile;
        shot.ResourceCosts.Add(new ResourceCostDef { Resource = "ammo", Amount = 1 });
        shot.Icon = "В";

        var bite = S("bite", "Укус", SkillKind.Attack, TargetingMode.Unit, 1.5, 0.25, 0.2, Stage(Single(), Dmg("physical", 7), Status("bleeding", 1, 0, 0.35)));
        bite.Icon = "К";

        var cleave = S("cleave", "Рассекающий удар", SkillKind.Attack, TargetingMode.Direction, 4, 0.6, 0.3,
            Stage(new AreaDef { Shape = AreaShape.Sector, Size = 1, Angle = 120 }, Dmg("physical", 9, AllButSelf())));
        cleave.Cooldown = 4;
        cleave.Icon = "Р";
        cleave.Description = "Бьёт три клетки перед собой. Задевает союзников!";

        var bash = S("shield_bash", "Удар щитом", SkillKind.Attack, TargetingMode.Unit, 3, 0.4, 0.3,
            Stage(Single(), Dmg("physical", 5), new SkillEffectDef { Type = EffectType.Push, Distance = 1 }, Status("stunned", 1, 1.2, 0.6)));
        bash.Cooldown = 6;
        bash.Icon = "Щ";
        bash.Interrupt.ChanceOnDamage = 0.1;

        var fireball = S("fireball", "Огненный шар", SkillKind.Spell, TargetingMode.Cell, 4, 1.0, 0.4,
            Stage(Radius(1), Dmg("fire", 13, All()), Status("burning", 1, 0, 0.5, All()), Cell("fire")));
        fireball.RangeMin = 2;
        fireball.RangeMax = 7;
        fireball.Ranged = true;
        fireball.Cooldown = 5;
        fireball.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 15 });
        fireball.Vfx = SkillVfx.Projectile;
        fireball.Color = "#ff7a2a";
        fireball.Icon = "Ш";
        fireball.Interrupt.ChanceOnDamage = 0.4;

        var frost = S("frost_cone", "Ледяной конус", SkillKind.Spell, TargetingMode.Direction, 4, 0.7, 0.3,
            Stage(new AreaDef { Shape = AreaShape.Cone, Size = 3 }, Dmg("ice", 8, AllButSelf()), Status("chilled", 35, 0, 1, AllButSelf()), Cell("frost", 0, 1, 0.7)));
        frost.Cooldown = 6;
        frost.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 12 });
        frost.Color = "#8fd8ff";
        frost.Vfx = SkillVfx.Burst;
        frost.Icon = "Л";

        var chain = S("chain_lightning", "Цепная молния", SkillKind.Spell, TargetingMode.Unit, 4, 0.8, 0.3,
            Stage(new AreaDef { Shape = AreaShape.Chain, ChainJumps = 3, ChainRange = 2 }, Dmg("electric", 10), Status("shocked", 1, 0, 0.6)));
        chain.RangeMin = 1;
        chain.RangeMax = 6;
        chain.Ranged = true;
        chain.Cooldown = 7;
        chain.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 15 });
        chain.Color = "#ffe04a";
        chain.Vfx = SkillVfx.Beam;
        chain.Icon = "М";

        var poison = S("poison_cloud", "Ядовитое облако", SkillKind.Spell, TargetingMode.Cell, 3, 0.8, 0.3, Stage(Radius(1), Cell("poison_cloud")));
        poison.RangeMin = 2;
        poison.RangeMax = 6;
        poison.Ranged = true;
        poison.RequiresLoS = false;
        poison.Cooldown = 8;
        poison.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 10 });
        poison.Color = "#8acc3a";
        poison.Vfx = SkillVfx.Burst;
        poison.Icon = "Я";
        poison.Description = "Создаёт облако яда. Огонь взорвёт его!";

        var heal = S("heal", "Лечение", SkillKind.Support, TargetingMode.Unit, 3, 0.6, 0.3, Stage(Single(), Heal(25)));
        heal.RangeMin = 0;
        heal.RangeMax = 5;
        heal.Ranged = true;
        heal.HitMode = HitMode.AlwaysHit;
        heal.ValidTargets = TargetFilterDef.AlliesAndSelf();
        heal.Cooldown = 3;
        heal.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 10 });
        heal.Color = "#60e080";
        heal.Vfx = SkillVfx.Glow;
        heal.Icon = "+";

        var smoke = S("smoke_bomb", "Дымовая шашка", SkillKind.Utility, TargetingMode.Cell, 2, 0.4, 0.2, Stage(Radius(1), Cell("smoke")));
        smoke.RangeMin = 1;
        smoke.RangeMax = 5;
        smoke.Ranged = true;
        smoke.RequiresLoS = false;
        smoke.Cooldown = 10;
        smoke.Icon = "Д";

        var charge = S("charge", "Рывок", SkillKind.Movement, TargetingMode.Cell, 4, 0.2, 0.3, Stage(Single(), new SkillEffectDef { Type = EffectType.TeleportCaster }));
        charge.RangeMin = 2;
        charge.RangeMax = 4;
        charge.Cooldown = 8;
        charge.Telegraph = false;
        charge.Icon = "»";
        charge.Description = "Специальный навык: мгновенное перемещение в свободную клетку";

        var meteor = S("meteor", "Метеор", SkillKind.Spell, TargetingMode.Cell, 6, 1.5, 0.5,
            new SkillStageDef { Name = "Тень", Area = Radius(2), Effects = new() { Cell("smoke", 2) } },
            new SkillStageDef
            {
                Name = "Удар", Delay = 1.0, Area = Radius(1),
                Effects = new() { Dmg("fire", 22, All()), new SkillEffectDef { Type = EffectType.Push, Distance = 1, Filter = All() }, Cell("fire") },
            });
        meteor.RangeMin = 3;
        meteor.RangeMax = 8;
        meteor.Ranged = true;
        meteor.RequiresLoS = false;
        meteor.Cooldown = 15;
        meteor.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 25 });
        meteor.Color = "#ff5a1a";
        meteor.Icon = "☄";
        meteor.Description = "Двухэтапный навык: тень, затем удар через 1 с";

        var quake = S("quake", "Сотрясение", SkillKind.Attack, TargetingMode.Direction, 5, 0.9, 0.5,
            new SkillStageDef
            {
                Area = new AreaDef
                {
                    Shape = AreaShape.Mask,
                    Cells = new()
                    {
                        new MaskCellDef { Offset = new HexCoord(1, 0), Effects = new() { Dmg("physical", 8, AllButSelf()), Status("stunned", 1, 1.5, 0.7, AllButSelf()) } },
                        new MaskCellDef { Offset = new HexCoord(2, 0) },
                        new MaskCellDef { Offset = new HexCoord(2, -1) },
                        new MaskCellDef { Offset = new HexCoord(1, 1) },
                        new MaskCellDef { Offset = new HexCoord(3, -1) },
                        new MaskCellDef { Offset = new HexCoord(2, 1) },
                    },
                },
                Effects = new() { Dmg("physical", 7, AllButSelf()) },
            });
        quake.Cooldown = 8;
        quake.Icon = "С";
        quake.Description = "Маска с особой центральной клеткой (оглушение)";

        var wave = S("water_wave", "Волна", SkillKind.Utility, TargetingMode.Direction, 3, 0.5, 0.3,
            Stage(new AreaDef { Shape = AreaShape.Line, Size = 4 }, Dmg("physical", 3, AllButSelf()),
                new SkillEffectDef { Type = EffectType.Push, Distance = 1, Filter = AllButSelf() }, Cell("wet"),
                new SkillEffectDef { Type = EffectType.RemoveCellEffect, CellEffect = "fire" }));
        wave.Cooldown = 6;
        wave.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 8 });
        wave.Color = "#5aa0e0";
        wave.Icon = "~";
        wave.Description = "Мочит клетки, гасит огонь, отталкивает";

        var barrier = S("barrier", "Барьер", SkillKind.Utility, TargetingMode.Cell, 3, 0.5, 0.2,
            Stage(new AreaDef
            {
                Shape = AreaShape.Mask,
                Cells = new() { new MaskCellDef { Offset = new HexCoord(0, 0) }, new MaskCellDef { Offset = new HexCoord(0, -1) }, new MaskCellDef { Offset = new HexCoord(0, 1) } },
            }, Cell("barrier")));
        barrier.RangeMin = 1;
        barrier.RangeMax = 4;
        barrier.Cooldown = 12;
        barrier.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 8 });
        barrier.Icon = "Б";

        var sneak = S("sneak", "Скрытность", SkillKind.Utility, TargetingMode.Self, 3, 0.3, 0.1, Stage(Single(), Status("invisible", 1, 0, 1, Self(), false)));
        sneak.Cooldown = 15;
        sneak.HitMode = HitMode.AlwaysHit;
        sneak.FaceTarget = false;
        sneak.Telegraph = false;
        sneak.Icon = "Т";

        var cry = S("war_cry", "Боевой клич", SkillKind.Utility, TargetingMode.Self, 3, 0.5, 0.3,
            Stage(Radius(2, false), Status("fear", 1, 0, 0.6, null, false), Status("shielded", 1, 0, 1, TargetFilterDef.AlliesAndSelf(), false)));
        cry.Stages[0].Area.IncludeCenter = true;
        cry.Cooldown = 15;
        cry.HitMode = HitMode.AlwaysHit;
        cry.FaceTarget = false;
        cry.Icon = "!";

        var dominate = S("dominate", "Подчинение", SkillKind.Spell, TargetingMode.Unit, 5, 1.2, 0.5, Stage(Single(), Status("mind_control", 1, 5)));
        dominate.RangeMax = 4;
        dominate.Ranged = true;
        dominate.Cooldown = 25;
        dominate.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 30 });
        dominate.Color = "#e050e0";
        dominate.Icon = "П";

        var flash = S("blinding_flash", "Ослепляющая вспышка", SkillKind.Spell, TargetingMode.Cell, 3, 0.5, 0.3, Stage(Radius(1), Status("blinded", 1, 0, 0.8)));
        flash.RangeMin = 1;
        flash.RangeMax = 5;
        flash.Ranged = true;
        flash.Cooldown = 10;
        flash.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 10 });
        flash.Icon = "*";

        var net = S("net", "Сеть", SkillKind.Attack, TargetingMode.Unit, 3, 0.5, 0.3, Stage(Single(), Status("rooted", 1, 3)));
        net.RangeMin = 1;
        net.RangeMax = 4;
        net.Ranged = true;
        net.Cooldown = 9;
        net.Icon = "#";

        var holy = S("consecrate", "Освящение", SkillKind.Support, TargetingMode.Cell, 4, 0.8, 0.3, Stage(Radius(1), Cell("holy")));
        holy.RangeMin = 0;
        holy.RangeMax = 4;
        holy.Cooldown = 14;
        holy.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 15 });
        holy.Icon = "О";

        var curse = S("curse_ground", "Проклятие земли", SkillKind.Spell, TargetingMode.Cell, 4, 0.8, 0.3, Stage(Radius(1), Cell("cursed")));
        curse.RangeMin = 1;
        curse.RangeMax = 5;
        curse.Cooldown = 12;
        curse.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 12 });
        curse.Icon = "П";

        var wolf = S("summon_wolf", "Призыв волка", SkillKind.Utility, TargetingMode.Cell, 5, 1.0, 0.3, Stage(Single(), new SkillEffectDef { Type = EffectType.Summon, SummonUnit = "wolf", Duration = 30 }));
        wolf.RangeMin = 1;
        wolf.RangeMax = 2;
        wolf.Cooldown = 30;
        wolf.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 20 });
        wolf.Icon = "В";

        var spark = S("spark", "Искра", SkillKind.Spell, TargetingMode.Cell, 2, 0.4, 0.2, Stage(Single(), Dmg("electric", 5, All()), Cell("electric")));
        spark.RangeMin = 1;
        spark.RangeMax = 6;
        spark.Ranged = true;
        spark.Cooldown = 3;
        spark.ResourceCosts.Add(new ResourceCostDef { Resource = "mana", Amount = 5 });
        spark.Color = "#ffe04a";
        spark.Icon = "Э";
        spark.Description = "По воде электричество разойдётся по всей луже";

        var torch = S("torch", "Поджог", SkillKind.Utility, TargetingMode.Cell, 2, 0.4, 0.2, Stage(Single(), Cell("fire")));
        torch.RangeMin = 1;
        torch.RangeMax = 3;
        torch.Cooldown = 4;
        torch.Icon = "О";

        var demolish = S("demolish", "Пролом", SkillKind.Attack, TargetingMode.Cell, 3, 0.6, 0.3,
            Stage(Single(), new SkillEffectDef { Type = EffectType.DestroyObstacle, ObstacleDamage = 40 }, Dmg("physical", 6)));
        demolish.RangeMin = 1;
        demolish.RangeMax = 1;
        demolish.Icon = "П";

        var trap = S("set_trap", "Капкан", SkillKind.Utility, TargetingMode.Cell, 2, 0.8, 0.2, Stage(Single(), Cell("trap")));
        trap.RangeMin = 1;
        trap.RangeMax = 1;
        trap.Cooldown = 10;
        trap.Telegraph = false;
        trap.Icon = "К";
    }

    private static void Passives(ContentDatabase db)
    {
        db.Add(new PassiveDef
        {
            Id = "counter", Name = "Контратака", Trigger = PassiveTrigger.OnDamaged, Action = PassiveAction.CounterAttack, Chance = 0.35, Cooldown = 3, ApCost = 1,
            Description = "После получения урона может ответить обычной атакой (тратит свободные ОД)",
        });
        db.Add(new PassiveDef { Id = "thick_hide", Name = "Толстая шкура", Trigger = PassiveTrigger.Always, StatMods = new() { Mod(HexTactics.Core.Content.Stats.Armor, 2) } });
        db.Add(new PassiveDef { Id = "vigilant", Name = "Бдительность", Trigger = PassiveTrigger.Always, StatMods = new() { Mod(HexTactics.Core.Content.Stats.Perception, 0.15) } });
        db.Add(new PassiveDef
        {
            Id = "second_wind", Name = "Второе дыхание", Trigger = PassiveTrigger.OnLowHp, HpThreshold = 0.3, Action = PassiveAction.EffectsOnSelf,
            Effects = new() { Heal(20, Self()) }, Cooldown = 60,
        });
        db.Add(new PassiveDef
        {
            Id = "thorns", Name = "Шипы", Trigger = PassiveTrigger.OnDamaged, Action = PassiveAction.EffectsOnSource,
            Effects = new() { Dmg("true", 2, All()) }, Cooldown = 0.5,
        });
        db.Add(new PassiveDef
        {
            Id = "regrowth", Name = "Восстановление тканей", Trigger = PassiveTrigger.Periodic, Interval = 2, Action = PassiveAction.EffectsOnSelf,
            Effects = new() { Heal(2, Self()) },
        });
        db.Add(new PassiveDef
        {
            Id = "pack_howl", Name = "Вой стаи", Trigger = PassiveTrigger.OnEnemySpotted, Action = PassiveAction.EffectsOnSelf, Cooldown = 20,
            Effects = new() { Status("resistant", 1, 4, 1, Self(), false) },
        });
    }

    private static void AiProfiles(ContentDatabase db)
    {
        db.Add(new AiProfileDef { Id = "aggressive", Name = "Агрессивный", Aggression = 0.9, TargetPriority = AiTargetPriority.Nearest, PreferredRange = 1 });
        db.Add(new AiProfileDef { Id = "defensive", Name = "Защитный", Aggression = 0.3, RetreatHpFraction = 0.35, TargetPriority = AiTargetPriority.MostDangerous });
        db.Add(new AiProfileDef { Id = "hold", Name = "Удерживает позицию", Aggression = 0.6, HoldPosition = true, HoldRadius = 2, Pursue = false });
        db.Add(new AiProfileDef { Id = "support", Name = "Поддержка", Aggression = 0.5, Support = true, SupportHpThreshold = 0.65, KeepDistance = true, PreferredRange = 4, RetreatHpFraction = 0.3 });
        db.Add(new AiProfileDef { Id = "ranged", Name = "Дальнобойный", Aggression = 0.7, KeepDistance = true, PreferredRange = 4, TargetPriority = AiTargetPriority.LowestHpFraction });
        db.Add(new AiProfileDef { Id = "pursuer", Name = "Преследователь", Aggression = 1.0, Pursue = true, TargetPriority = AiTargetPriority.Weakest, DecisionInterval = 0.4 });
        db.Add(new AiProfileDef { Id = "skittish", Name = "Пугливый", Aggression = 0, RetreatHpFraction = 1.0, Pursue = false, FaceThreats = true });
    }

    private static void Units(ContentDatabase db)
    {
        UnitDef U(string id, string name, string faction, UnitShape shape, string symbol, Dictionary<string, double> stats, string attack, params string[] skills)
        {
            var u = new UnitDef
            {
                Id = id, Name = name, Faction = faction, Stats = stats, BasicAttack = attack, Skills = skills.ToList(),
                Visual = new UnitVisualDef { Shape = shape, Symbol = symbol },
            };
            db.Add(u);
            return u;
        }

        const string hp = HexTactics.Core.Content.Stats.HpMax, spd = HexTactics.Core.Content.Stats.MoveSpeed, ap = HexTactics.Core.Content.Stats.ApMax,
            apr = HexTactics.Core.Content.Stats.ApRegen, det = HexTactics.Core.Content.Stats.DetectionRange, arm = HexTactics.Core.Content.Stats.Armor,
            acc = HexTactics.Core.Content.Stats.Accuracy, eva = HexTactics.Core.Content.Stats.Evasion, blk = HexTactics.Core.Content.Stats.BlockChance,
            pow = HexTactics.Core.Content.Stats.Power, st = HexTactics.Core.Content.Stats.Stealth, crit = HexTactics.Core.Content.Stats.CritChance;

        var knight = U("knight", "Рыцарь", "player", UnitShape.Soldier, "Р", new() { [hp] = 140, [spd] = 1.3, [ap] = 10, [apr] = 1.0, [det] = 6, [arm] = 4, [blk] = 0.25, [pow] = 3 },
            "strike", "shield_bash", "cleave", "war_cry", "demolish");
        knight.Passives = new() { "counter", "vigilant" };
        knight.Description = "Тяжёлый воин с щитом";
        var archer = U("archer", "Лучница", "player", UnitShape.Archer, "Л", new() { [hp] = 80, [spd] = 1.6, [ap] = 10, [apr] = 1.1, [det] = 9, [acc] = 0.05, [eva] = 0.15 },
            "shot", "net", "smoke_bomb", "sneak", "set_trap");
        archer.Resources.Add(new UnitResourceDef { Resource = "ammo", Max = 20, Start = 20 });
        var mage = U("mage", "Маг", "player", UnitShape.Mage, "М", new() { [hp] = 70, [spd] = 1.4, [ap] = 12, [apr] = 1.0, [det] = 7 },
            "strike", "fireball", "frost_cone", "chain_lightning", "meteor", "water_wave", "spark");
        mage.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 70, Start = 70, Regen = 1.5 });
        var priest = U("priestess", "Жрица", "player", UnitShape.Mage, "Ж", new() { [hp] = 85, [spd] = 1.4, [ap] = 10, [apr] = 1.0, [det] = 7 },
            "strike", "heal", "consecrate", "blinding_flash", "barrier");
        priest.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 80, Start = 80, Regen = 2 });
        priest.Passives = new() { "second_wind" };
        priest.Visual.Color = "#e8e4d8";
        var scout = U("scout", "Разведчик", "player", UnitShape.Pawn, "Р", new() { [hp] = 75, [spd] = 2.1, [ap] = 10, [apr] = 1.2, [det] = 10, [st] = 0.3, [eva] = 0.2, [crit] = 0.15 },
            "strike", "charge", "torch", "poison_cloud", "sneak");
        scout.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 30, Start = 30, Regen = 0.5 });

        var goblin = U("goblin", "Гоблин", "enemy", UnitShape.Pawn, "г", new() { [hp] = 50, [spd] = 1.7, [ap] = 8, [apr] = 1.0, [det] = 7, [eva] = 0.15 }, "strike");
        goblin.AiProfile = "aggressive";
        var gArcher = U("goblin_archer", "Гоблин-лучник", "enemy", UnitShape.Archer, "л", new() { [hp] = 40, [spd] = 1.6, [ap] = 8, [apr] = 1.0, [det] = 8 }, "shot");
        gArcher.Resources.Add(new UnitResourceDef { Resource = "ammo", Max = 15, Start = 15 });
        gArcher.AiProfile = "ranged";
        var orc = U("orc_brute", "Орк-громила", "enemy", UnitShape.Brute, "О", new() { [hp] = 160, [spd] = 1.1, [ap] = 10, [apr] = 0.9, [det] = 6, [arm] = 3, [pow] = 4 },
            "strike", "quake", "cleave");
        orc.Passives = new() { "thick_hide" };
        orc.AiProfile = "aggressive";
        var shaman = U("shaman", "Шаман", "enemy", UnitShape.Mage, "ш", new() { [hp] = 60, [spd] = 1.4, [ap] = 10, [apr] = 1.0, [det] = 8 },
            "strike", "heal", "poison_cloud", "spark", "curse_ground", "dominate");
        shaman.Resources.Add(new UnitResourceDef { Resource = "mana", Max = 70, Start = 70, Regen = 1.5 });
        shaman.AiProfile = "support";
        var w = U("wolf", "Волк", "enemy", UnitShape.Beast, "в", new() { [hp] = 55, [spd] = 2.4, [ap] = 8, [apr] = 1.2, [det] = 8, [eva] = 0.2 }, "bite");
        w.CreatureCategory = "beast";
        w.AiProfile = "pursuer";
        w.Passives = new() { "pack_howl" };
        var sentinel = U("sentinel", "Страж", "enemy", UnitShape.Construct, "С", new() { [hp] = 120, [spd] = 1.0, [ap] = 10, [apr] = 1.0, [det] = 6, [arm] = 6 }, "strike", "shield_bash");
        sentinel.CreatureCategory = "construct";
        sentinel.Resistances = new() { ["electric"] = -0.5, ["fire"] = 0.3, ["poison"] = 1.0 };
        sentinel.Immunities = new() { "poison", "bleeding", "tag:gas" };
        sentinel.Passives = new() { "thorns" };
        sentinel.AiProfile = "hold";
        var deer = U("deer", "Олень", "neutral", UnitShape.Beast, "о", new() { [hp] = 40, [spd] = 2.6, [ap] = 8, [apr] = 1.5, [det] = 9 }, "");
        deer.CreatureCategory = "beast";
        deer.AiProfile = "skittish";
        deer.Passives = new() { "regrowth" };
    }

    // ------------------------------------------------------------------ maps

    private static void Paint(MapDef m, Func<HexCoord, bool> where, string terrain, int? height = null)
    {
        foreach (var c in m.Cells)
            if (where(c.C))
            {
                c.T = terrain;
                if (height.HasValue) c.H = height.Value;
            }
    }

    private static (int Col, int Row) Offset(HexCoord h) => (h.Q + h.R / 2, h.R);

    private static void Maps(ContentDatabase db)
    {
        // Valley: 24×16, river with a ford and a bridge, hills, forest, ruined fort, chasm and a toxic swamp.
        var v = MapDef.CreateRectangle("valley", 24, 16, "grass");
        v.Name = "Долина у реки";
        v.Description = "Река с бродом и мостом, холмы, лес, руины форта, пропасть и ядовитое болото";
        v.Tags = new() { "demo" };
        bool Col(HexCoord h, int c0, int c1, int r0, int r1)
        {
            var (c, r) = Offset(h);
            return c >= c0 && c <= c1 && r >= r0 && r <= r1;
        }
        // River (columns ~10-11 meandering).
        Paint(v, h =>
        {
            var (c, r) = Offset(h);
            int center = 10 + (r % 6 < 3 ? 0 : 1);
            return c == center || c == center + 1;
        }, "shallow_water");
        Paint(v, h =>
        {
            var (c, r) = Offset(h);
            int center = 10 + (r % 6 < 3 ? 0 : 1);
            return (c == center || c == center + 1) && r >= 10;
        }, "deep_water", -1);
        // Bridge (stone) at rows 11-12 over the deep part.
        Paint(v, h => Col(h, 10, 12, 12, 12), "stone", 0);
        // Hills on the west.
        Paint(v, h => Col(h, 2, 5, 2, 4), "highland", 1);
        Paint(v, h => Col(h, 3, 4, 3, 3), "highland", 2);
        Paint(v, h => Col(h, 3, 3, 2, 2), "rock", 2);
        // Forest in the south-west.
        Paint(v, h => Col(h, 1, 6, 11, 14), "bushes");
        Paint(v, h => Col(h, 2, 4, 12, 13), "trees");
        // Ruined fort on the east.
        Paint(v, h => Col(h, 15, 21, 3, 9), "stone");
        Paint(v, h => (Col(h, 15, 21, 3, 3) || Col(h, 15, 21, 9, 9) || Col(h, 21, 21, 3, 9) || Col(h, 15, 15, 3, 9)) && !Col(h, 15, 15, 5, 7) && !Col(h, 18, 18, 9, 9), "wall");
        Paint(v, h => Col(h, 17, 17, 5, 5) || Col(h, 19, 19, 7, 7), "crates");
        Paint(v, h => Col(h, 18, 18, 6, 6), "altar");
        Paint(v, h => Col(h, 16, 20, 4, 8) && Offset(h).Row == 4 && Offset(h).Col == 20, "rubble");
        // Chasm north of the fort.
        Paint(v, h => Col(h, 16, 19, 0, 1), "pit", -3);
        // Toxic swamp south-east.
        Paint(v, h => Col(h, 16, 20, 12, 14), "toxic_swamp", -1);
        Paint(v, h => Col(h, 14, 15, 13, 14), "lowland", -1);
        // East hill with sand.
        Paint(v, h => Col(h, 22, 23, 10, 13), "sand");
        Paint(v, h => Col(h, 13, 14, 0, 2), "dirt");
        // Remove a few corner cells to make an irregular border.
        v.Cells.RemoveAll(c => { var (col, row) = Offset(c.C); return (row == 0 && col < 2) || (row == 15 && col > 21); });
        v.Markers.Add(new MapMarkerDef { Id = "p1", Type = MarkerType.PlayerStart, C = Hex(1, 7), Label = "Старт отряда", Radius = 2 });
        v.Markers.Add(new MapMarkerDef { Id = "east_gate", Type = MarkerType.EnemySpawn, C = Hex(23, 6), Label = "Восточные ворота", Radius = 1 });
        v.Markers.Add(new MapMarkerDef { Id = "south", Type = MarkerType.EnemySpawn, C = Hex(21, 14), Label = "Южный лес", Radius = 1 });
        v.Markers.Add(new MapMarkerDef { Id = "altar", Type = MarkerType.Objective, C = Hex(18, 6), Label = "Алтарь", Radius = 0 });
        db.Add(v);

        // Arena: hexagon radius 7, stone floor, pillars, pools, central raised platform.
        var a = MapDef.CreateHexagon("arena", 7, "stone");
        a.Name = "Арена";
        a.Description = "Круглая арена с колоннами, бассейнами и помостом";
        foreach (var c in a.Cells)
        {
            int d = c.C.Length;
            if (d <= 1) c.H = 1;
            if (d == 4 && (c.C.Q == 0 || c.C.R == 0 || c.C.S == 0)) c.T = "rock";
            if (d == 6 && c.C.Q == 3) c.T = "shallow_water";
            if (d == 6 && c.C.Q == -3) c.T = "shallow_water";
            if (d == 7) c.T = "sand";
        }
        a.Markers.Add(new MapMarkerDef { Id = "west", Type = MarkerType.PlayerStart, C = new HexCoord(-6, 3), Label = "Запад", Radius = 2 });
        a.Markers.Add(new MapMarkerDef { Id = "east", Type = MarkerType.EnemySpawn, C = new HexCoord(6, -3), Label = "Восток", Radius = 2 });
        db.Add(a);

        // Proving ground for the sandbox: rhombus with every terrain type.
        var p = MapDef.CreateRhombus("proving_ground", 14, 10, "dirt");
        p.Name = "Полигон";
        p.Description = "Все типы местности для экспериментов с навыками и синергиями";
        string[] strip = { "grass", "sand", "shallow_water", "deep_water", "pit", "toxic_swamp", "wall", "rock", "crates", "bushes", "trees", "ice_floor", "altar", "stone" };
        foreach (var c in p.Cells)
        {
            if (c.C.R == 8)
            {
                c.T = strip[c.C.Q % strip.Length];
                c.H = c.T == "pit" ? -3 : c.T == "deep_water" ? -1 : c.T == "toxic_swamp" ? -1 : 0;
            }
            if (c.C.R is >= 2 and <= 4 && c.C.Q is >= 9 and <= 12) c.T = "shallow_water";
            if (c.C.R == 0 && c.C.Q >= 6) c.H = 1;
            if (c.C.R == 0 && c.C.Q >= 10) c.H = 2;
        }
        p.Markers.Add(new MapMarkerDef { Id = "start", Type = MarkerType.PlayerStart, C = new HexCoord(1, 4), Label = "Старт", Radius = 1 });
        p.Markers.Add(new MapMarkerDef { Id = "spawn", Type = MarkerType.EnemySpawn, C = new HexCoord(12, 6), Label = "Мишени", Radius = 1 });
        db.Add(p);
    }

    private static HexCoord Hex(int col, int row) => new(col - row / 2, row);

    private static void Scenarios(ContentDatabase db)
    {
        var s = new ScenarioDef
        {
            Id = "valley_ambush", Name = "Засада в долине", Map = "valley", Seed = 2024,
            Description = "Отряд должен очистить долину. Через минуту подойдёт подкрепление врага.",
            Intro = "Разведка доносит: гоблины заняли руины форта за рекой. Очистите долину! (Пробел — пауза)",
        };
        void P(string unit, int col, int row, HexDirection f, string tag = "") => s.Units.Add(new ScenarioUnitDef { Unit = unit, C = Hex(col, row), Facing = f, Tag = tag });
        P("knight", 2, 7, HexDirection.E, "hero");
        P("archer", 1, 6, HexDirection.E);
        P("mage", 1, 8, HexDirection.E);
        P("priestess", 0, 7, HexDirection.E);
        P("scout", 3, 9, HexDirection.E);
        P("goblin", 16, 5, HexDirection.W);
        P("goblin", 16, 7, HexDirection.W);
        P("goblin_archer", 19, 4, HexDirection.W);
        P("orc_brute", 19, 6, HexDirection.W, "boss");
        P("shaman", 20, 8, HexDirection.W);
        P("wolf", 7, 13, HexDirection.NE);
        P("wolf", 8, 14, HexDirection.NW);
        s.Units.Add(new ScenarioUnitDef { Unit = "deer", C = Hex(13, 1), Facing = HexDirection.SW, Faction = "neutral" });
        s.Units.Add(new ScenarioUnitDef { Unit = "deer", C = Hex(14, 2), Facing = HexDirection.W, Faction = "neutral" });
        s.Sides = new()
        {
            new ScenarioSideDef { Faction = "player", Controller = SideController.Player },
            new ScenarioSideDef { Faction = "enemy", Controller = SideController.Ai },
            new ScenarioSideDef { Faction = "neutral", Controller = SideController.Ai },
        };
        s.Waves.Add(new WaveDef { Name = "Подкрепление", Time = 60, SpawnMarker = "east_gate", Faction = "enemy", Units = new() { new WaveUnitDef { Unit = "goblin", Count = 3 }, new WaveUnitDef { Unit = "goblin_archer", Count = 1 } }, Message = "С востока подходят гоблины!" });
        s.Waves.Add(new WaveDef { Name = "Стая", Time = 110, SpawnMarker = "south", Faction = "enemy", Units = new() { new WaveUnitDef { Unit = "wolf", Count = 3 } }, Message = "Из южного леса выбегает стая волков!" });
        s.Victory = new() { new ConditionDef { Type = ConditionType.AllEnemiesDead } };
        s.Defeat = new() { new ConditionDef { Type = ConditionType.AllPlayerUnitsDead }, new ConditionDef { Type = ConditionType.UnitDead, UnitTag = "hero" } };
        s.Triggers.Add(new TriggerDef
        {
            Name = "Совет", Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 3 },
            Actions = new() { new TriggerActionDef { Type = TriggerActionType.Message, Text = "Совет: огонь взрывает ядовитые облака, а искра по воде бьёт всех, кто в ней стоит." } },
        });
        s.Triggers.Add(new TriggerDef
        {
            Name = "Босс пал", Condition = new ConditionDef { Type = ConditionType.UnitDead, UnitTag = "boss" },
            Actions = new() { new TriggerActionDef { Type = TriggerActionType.Message, Text = "Орк-громила повержен! Гоблины дрогнули." } },
        });
        db.Add(s);

        var arena = new ScenarioDef
        {
            Id = "arena_duel", Name = "Арена", Map = "arena", Seed = 7,
            Description = "Небольшой бой: три героя против стражей и волков.",
            Intro = "Победите всех противников на арене.",
        };
        arena.Units.Add(new ScenarioUnitDef { Unit = "knight", C = new HexCoord(-6, 3), Facing = HexDirection.E, Tag = "hero" });
        arena.Units.Add(new ScenarioUnitDef { Unit = "mage", C = new HexCoord(-6, 4), Facing = HexDirection.E });
        arena.Units.Add(new ScenarioUnitDef { Unit = "archer", C = new HexCoord(-5, 4), Facing = HexDirection.E });
        arena.Units.Add(new ScenarioUnitDef { Unit = "sentinel", C = new HexCoord(5, -3), Facing = HexDirection.W });
        arena.Units.Add(new ScenarioUnitDef { Unit = "sentinel", C = new HexCoord(6, -4), Facing = HexDirection.W });
        arena.Units.Add(new ScenarioUnitDef { Unit = "wolf", C = new HexCoord(6, -2), Facing = HexDirection.W });
        arena.Units.Add(new ScenarioUnitDef { Unit = "goblin_archer", C = new HexCoord(5, -2), Facing = HexDirection.W });
        arena.Victory = new() { new ConditionDef { Type = ConditionType.AllEnemiesDead } };
        arena.Defeat = new() { new ConditionDef { Type = ConditionType.AllPlayerUnitsDead } };
        db.Add(arena);

        var sandbox = new ScenarioDef
        {
            Id = "sandbox", Name = "Полигон (песочница)", Map = "proving_ground", Seed = 1,
            Description = "Без победы и поражения: проверка навыков, эффектов клеток и синергий.",
            Intro = "Песочница: мишени не атакуют. Попробуйте огонь по ядовитому облаку и искру по воде.",
        };
        sandbox.Units.Add(new ScenarioUnitDef { Unit = "mage", C = new HexCoord(1, 4), Facing = HexDirection.E });
        sandbox.Units.Add(new ScenarioUnitDef { Unit = "knight", C = new HexCoord(2, 4), Facing = HexDirection.E });
        sandbox.Units.Add(new ScenarioUnitDef { Unit = "scout", C = new HexCoord(1, 5), Facing = HexDirection.E });
        sandbox.Units.Add(new ScenarioUnitDef { Unit = "priestess", C = new HexCoord(0, 5), Facing = HexDirection.E });
        sandbox.Units.Add(new ScenarioUnitDef { Unit = "sentinel", C = new HexCoord(10, 3), Facing = HexDirection.W, AiProfile = "skittish", Name = "Мишень в воде" });
        sandbox.Units.Add(new ScenarioUnitDef { Unit = "goblin", C = new HexCoord(11, 3), Facing = HexDirection.W, AiProfile = "skittish", Name = "Мишень-гоблин" });
        sandbox.Units.Add(new ScenarioUnitDef { Unit = "orc_brute", C = new HexCoord(8, 6), Facing = HexDirection.W, AiProfile = "hold", Name = "Мишень-орк" });
        sandbox.Victory = new();
        sandbox.Defeat = new() { new ConditionDef { Type = ConditionType.AllPlayerUnitsDead } };
        db.Add(sandbox);
    }
}
