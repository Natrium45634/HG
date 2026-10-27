using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Structures of the scenario in battle (docs/DECISIONS_4.md §56): every object with the «health» component becomes a
/// motionless unit that occupies all its hexes — the same damage, statuses, shields and log as any unit; ranges and
/// approaches count to its nearest hex. Enemies that reach their target object damage it by «урон по базе» and, if the
/// object says so, leave the field. The defeat rule of the scenario counts the destroyed bases.
/// </summary>
public sealed class StructureSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<string, Unit> _byObject = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reached = new(StringComparer.Ordinal);
    private readonly Dictionary<int, double> _regen = new();

    public StructureSystem(Simulation sim)
    {
        _sim = sim;
    }

    public IReadOnlyDictionary<string, Unit> ByObject => _byObject;

    /// <summary>Enemies that reached each object (by object ID).</summary>
    public IReadOnlyDictionary<string, int> Reached => _reached;

    public Unit? Of(string objectId) => _byObject.TryGetValue(objectId, out var u) ? u : null;

    /// <summary>The unit template of a structure (not stored in the content: the object is its source).</summary>
    public static UnitDef DefOf(ScenarioObjectDef o)
    {
        var h = o.Health ?? new StructureDef();
        return new UnitDef
        {
            Id = "structure:" + o.Id, Name = o.DisplayName, Faction = o.Faction, CreatureCategory = "structure", MoveCategory = "walk",
            Tags = new List<string>(o.Tags) { "structure", o.Type == ScenarioObjectType.PlayerBase ? "base" : o.Type.ToString().ToLowerInvariant() },
            Stats = new Dictionary<string, double>
            {
                [Stats.HpMax] = h.MaxHp, [Stats.Armor] = h.Armor, [Stats.MoveSpeed] = 0, [Stats.ApMax] = 0, [Stats.ApRegen] = 0,
                [Stats.DetectionRange] = 2, [Stats.Evasion] = 0, [Stats.BlockChance] = 0,
            },
            Defense = ContentJson.Clone(h.Defense),
            Stacking = new StackingDef { CanShare = false },
            Blockable = new BlockableDef { CanBeBlocked = false },
            AiProfile = "",
            SpawnWarning = false,
            Visual = new UnitVisualDef { Model = o.Model, Color = o.Color },
        }.Also(d => Turret(d, o));
    }

    /// <summary>Component «Турель» (part 6, §100): the skill of the structure as its auto-attack — the common hit, resistances and cooldowns.</summary>
    private static void Turret(UnitDef d, ScenarioObjectDef o)
    {
        if (o.Turret is not { Skill.Length: > 0 } t) return;
        d.Skills.Add(t.Skill);
        d.BasicAttack = t.Skill;
        d.Stats[Stats.DetectionRange] = t.Sight;
        d.Stats[Stats.ApMax] = 100;
        d.Stats[Stats.ApRegen] = 100;
        d.Tags.Add("turret");
        d.AutoAttacks.Add(new AutoAttackDef
        {
            Skill = t.Skill, Interval = t.Interval, Range = t.Range, Targets = ContentJson.Clone(t.Targets), RequireDetection = false, RequireLoS = true,
            ApCost = 0, StopWhen = new(),
        });
    }

    public void Load(ScenarioDef sc)
    {
        foreach (var o in sc.Objects)
        {
            if (o.Health == null && o.Turret == null || string.IsNullOrEmpty(o.Id)) continue;
            var cells = o.Cells().Where(_sim.Map.Contains).ToList();
            if (cells.Count == 0 || !cells.Contains(o.Anchor))
            {
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Объект «{o.DisplayName}»: положение вне карты — пропущен");
                continue;
            }
            var u = _sim.Spawn(DefOf(o), o.Faction, o.Anchor, o.Facing, o.DisplayName, o.Id);
            u.IsStructure = true;
            u.ObjectId = o.Id;
            u.Footprint = cells;
            u.Indestructible = o.Health is not { Destructible: true }; // a turret without «Здоровье» cannot be destroyed
            u.AiControlled = false;
            foreach (var c in cells.Skip(1)) _sim.Map.Get(c)?.Occupants.Add(u);
            _byObject[o.Id] = u;
        }
        if (_byObject.Count > 0) _sim.Map.Touch(false);
    }

    /// <summary>The hex of a target to aim at from a place: its nearest occupied hex (the position for a unit).</summary>
    public static HexCoord AimCell(Unit target, HexCoord from)
    {
        if (target.Footprint == null || target.Footprint.Count <= 1) return target.Pos;
        var best = target.Pos;
        int bd = HexCoord.Distance(from, best);
        foreach (var c in target.Footprint)
        {
            int d = HexCoord.Distance(from, c);
            if (d < bd)
            {
                bd = d;
                best = c;
            }
        }
        return best;
    }

    /// <summary>Distance from a hex to a unit: to its nearest occupied hex.</summary>
    public static int DistanceTo(HexCoord from, Unit target) => HexCoord.Distance(from, AimCell(target, from));

    private static int DistanceTo(HexCoord from, ScenarioObjectDef o) => o.Cells().Min(c => HexCoord.Distance(from, c));

    public void Update()
    {
        var sc = _sim.Scenario.Def;
        if (sc == null) return;
        // Regeneration of structures.
        foreach (var (id, s) in _byObject)
        {
            if (!s.Alive) continue;
            var o = sc.Object(id);
            double regen = o?.Health?.Regen ?? 0;
            if (regen <= 0 || s.Hp >= s.HpMax) continue;
            s.Hp = Math.Min(s.HpMax, s.Hp + regen * _sim.Dt);
        }
        // Enemies at their target object.
        foreach (var u in _sim.Units)
        {
            if (!u.Alive || u.IsStructure || u.TargetObject.Length == 0 || u.StepTo.HasValue) continue;
            var o = sc.Object(u.TargetObject);
            if (o == null || DistanceTo(u.Pos, o) > 1) continue;
            OnReached(u, o);
        }
    }

    private void OnReached(Unit u, ScenarioObjectDef o)
    {
        u.TargetObject = "";
        _reached[o.Id] = (_reached.TryGetValue(o.Id, out var n) ? n : 0) + 1;
        _sim.Emit(SimEventType.GoalReached, LogLevel.Normal, $"{u.Name} достигает «{o.DisplayName}»", u.Id, Of(o.Id)?.Id ?? -1, u.Pos, damageType: o.Id);
        _sim.Scenario.Raise(ScenarioEventType.GoalReached, u, o.Id);
        var s = Of(o.Id);
        if (s != null && s.Alive && _sim.IsHostile(u, s))
        {
            double dmg = Math.Max(0, u.Stat(Stats.BaseDamage));
            if (dmg > 0) DamageStructure(s, dmg, u, "прорыв");
            if (o.Health?.RemoveLeaked == true)
            {
                u.DeathCause = DeathCause.Removed;
                _sim.Combat.Kill(u, null, $"прорвался к «{o.DisplayName}»");
            }
        }
    }

    /// <summary>Damage to a structure outside the skill pipeline (a leak): logged, kills unless indestructible.</summary>
    public void DamageStructure(Unit s, double amount, Unit? by, string cause, bool force = false)
    {
        if (!s.Alive || amount <= 0) return;
        double before = s.Hp;
        s.Hp = Math.Max(s.Indestructible && !force ? 1 : 0, s.Hp - amount);
        s.LastDamagedTick = _sim.Tick;
        _sim.Emit(SimEventType.Damage, LogLevel.Normal, $"«{s.Name}»: −{before - s.Hp:0} ({cause}{(by != null ? $": {by.Name}" : "")})", by?.Id ?? -1, s.Id, s.Pos, before - s.Hp, "true");
        if (s.Hp <= 0) _sim.Combat.Kill(s, by, cause);
    }

    /// <summary>The structure died: its other hexes are free again; a destroyed base is reported.</summary>
    internal void OnKilled(Unit s)
    {
        if (s.Footprint == null) return;
        foreach (var c in s.Footprint) _sim.Map.Get(c)?.Occupants.Remove(s);
        var o = _sim.Scenario.Def?.Object(s.ObjectId);
        if (o != null)
        {
            _sim.Emit(SimEventType.StructureDestroyed, LogLevel.Normal, $"«{o.DisplayName}» разрушена", -1, s.Id, s.Pos);
            _sim.Scenario.Raise(ScenarioEventType.BaseDestroyed, s, o.Id);
        }
    }

    /// <summary>Bases of the player side (objects of type «база игрока» with health).</summary>
    public List<(ScenarioObjectDef Def, Unit? Unit)> PlayerBases(ScenarioDef sc) =>
        sc.Objects.Where(o => o.Type == ScenarioObjectType.PlayerBase && o.Health != null).Select(o => (o, Of(o.Id))).ToList();

    /// <summary>The defeat by bases (docs/DECISIONS_4.md §56): null — not lost.</summary>
    public string? BaseDefeat(ScenarioDef sc)
    {
        var rule = sc.BaseDefeat;
        if (rule.Mode == BaseDefeatMode.None) return null;
        var bases = PlayerBases(sc);
        if (bases.Count == 0) return null;
        var lost = bases.Where(b => b.Unit == null || !b.Unit.Alive).ToList();
        switch (rule.Mode)
        {
            case BaseDefeatMode.AnyBase when lost.Count > 0:
                return $"база «{lost[0].Def.DisplayName}» уничтожена";
            case BaseDefeatMode.AllBases when lost.Count == bases.Count:
                return "все базы уничтожены";
            case BaseDefeatMode.MainBase:
            {
                var main = rule.MainBase != "" ? bases.FirstOrDefault(b => b.Def.Id == rule.MainBase) : bases.FirstOrDefault(b => b.Def.Health!.Main);
                if (main.Def == null) main = bases[0];
                return main.Unit == null || !main.Unit.Alive ? $"главная база «{main.Def.DisplayName}» уничтожена" : null;
            }
            case BaseDefeatMode.LostCount when lost.Count >= rule.Count:
                return $"потеряно баз: {lost.Count}";
            case BaseDefeatMode.Custom when rule.Condition != null && _sim.Scenario.Evaluate(rule.Condition):
                return rule.Condition.Describe();
        }
        return null;
    }
}

internal static class StructureDefExt
{
    public static T Also<T>(this T value, System.Action<T> a)
    {
        a(value);
        return value;
    }
}
