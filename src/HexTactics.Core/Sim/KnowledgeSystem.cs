using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>What the player side has learned about one enemy (last sighting snapshot + observations).</summary>
public sealed class UnitKnowledge
{
    public bool EverSeen { get; set; }
    public long LastSeenTick { get; set; } = -1;
    public double LastHp { get; set; }
    public double LastHpMax { get; set; }
    public HexCoord LastPos { get; set; }
    public HexDirection LastFacing { get; set; }
    public List<(string Name, string Detail)> LastStatuses { get; } = new();
    public List<(string Name, double Value, double Max)> LastResources { get; } = new();
    public HashSet<string> SeenSkills { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ObservedDamageTypes { get; } = new(StringComparer.Ordinal);
    public bool Scanned { get; set; }
}

/// <summary>One category of the enemy info panel.</summary>
public sealed class InfoField
{
    public bool Known { get; init; }
    /// <summary>Value is the last known one (unit not visible now); StaleSeconds says how old.</summary>
    public bool Stale { get; init; }
    public double StaleSeconds { get; init; }
    public string Hint { get; init; } = "";
}

/// <summary>Everything the panel may show about a unit, filtered by disclosure rules and fog of war.</summary>
public sealed class EnemyInfo
{
    public bool PanelEnabled { get; init; } = true;
    public bool VisibleNow { get; init; }
    public InfoField NameField { get; init; } = new();
    public string Name { get; init; } = "Неизвестный противник";
    public string Type { get; init; } = "";
    public InfoField HealthField { get; init; } = new();
    public double Hp { get; init; }
    public double HpMax { get; init; }
    public InfoField ResourcesField { get; init; } = new();
    public List<(string Name, double Value, double Max)> Resources { get; init; } = new();
    public InfoField StatusesField { get; init; } = new();
    public List<(string Name, string Detail)> Statuses { get; init; } = new();
    public InfoField ResistancesField { get; init; } = new();
    public List<(string Name, string Value)> Resistances { get; init; } = new();
    public InfoField SkillsField { get; init; } = new();
    public List<string> Skills { get; init; } = new();
    public int TotalSkills { get; init; }
    public InfoField FacingField { get; init; } = new();
    public string Facing { get; init; } = "";
    public InfoField IntentField { get; init; } = new();
    public string Intent { get; init; } = "";
}

/// <summary>
/// The player side's knowledge about enemies (docs/DECISIONS_2.md §24): first sighting, last
/// known values under fog of war, skills seen in use, damage types observed against them.
/// The info panel never shows more than the disclosure rules and this knowledge allow.
/// </summary>
public sealed class KnowledgeSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<int, UnitKnowledge> _known = new();

    public KnowledgeSystem(Simulation sim)
    {
        _sim = sim;
    }

    public UnitKnowledge Of(Unit u)
    {
        if (!_known.TryGetValue(u.Id, out var k))
        {
            k = new UnitKnowledge();
            _known[u.Id] = k;
        }
        return k;
    }

    private bool IsPlayerSide(Unit u) => _sim.IsPlayerFaction(u.FactionId) || _sim.IsPlayerFaction(u.BaseFactionId);

    private bool IsPlayerAlly(Unit u)
    {
        foreach (var f in _sim.Options.PlayerFactions)
            if (_sim.Content.RelationOf(f, u.FactionId) == Relation.Ally)
                return true;
        return false;
    }

    /// <summary>Snapshot of every enemy the player side sees now (called after vision updates).</summary>
    public void Update()
    {
        foreach (var u in _sim.Units)
        {
            if (!u.Alive || IsPlayerSide(u)) continue;
            if (!_sim.Vision.IsVisibleToPlayer(u)) continue;
            var k = Of(u);
            k.EverSeen = true;
            k.LastSeenTick = _sim.Tick;
            k.LastHp = u.Hp;
            k.LastHpMax = u.HpMax;
            k.LastPos = u.Pos;
            k.LastFacing = u.Facing;
            k.LastStatuses.Clear();
            foreach (var s in u.Statuses) k.LastStatuses.Add((s.LevelName, StatusDetail(s)));
            k.LastResources.Clear();
            foreach (var r in u.Resources.Values) k.LastResources.Add((r.Def.DisplayName, r.Current, r.Max));
        }
    }

    private string StatusDetail(StatusInstance s) =>
        s.Def.Kind == StatusKind.Accumulating ? $"{s.Points:0}/{s.Def.MaxPoints:0}" : s.RemainingTicks > 0 ? $"{s.RemainingTicks * _sim.Dt:0.#} с" : "";

    /// <summary>The player saw this enemy use a skill.</summary>
    public void OnSkillUsed(Unit caster, SkillDef skill)
    {
        if (IsPlayerSide(caster) || !_sim.Vision.IsVisibleToPlayer(caster)) return;
        Of(caster).SeenSkills.Add(skill.Id);
    }

    /// <summary>A player-side unit dealt damage of this type to an enemy (its reaction to the type is now known).</summary>
    public void OnDamageObserved(Unit? source, Unit target, string damageType)
    {
        if (IsPlayerSide(target) || string.IsNullOrEmpty(damageType)) return;
        if (source != null && !IsPlayerSide(source)) return;
        if (!_sim.Vision.IsVisibleToPlayer(target)) return;
        Of(target).ObservedDamageTypes.Add(damageType);
    }

    /// <summary>Scan / reveal operation: everything becomes known.</summary>
    public void Reveal(Unit u)
    {
        var k = Of(u);
        k.Scanned = true;
        foreach (var s in u.Skills) k.SeenSkills.Add(s.Id);
        if (u.BasicAttack != null) k.SeenSkills.Add(u.BasicAttack.Id);
        foreach (var d in _sim.Content.Ids(DefCategory.DamageType)) k.ObservedDamageTypes.Add(d);
    }

    /// <summary>Own units, allies and admin mode see everything; enemies and neutrals go through the disclosure rules.</summary>
    public bool IsFullyKnown(Unit u) => _sim.Options.AdminMode || IsPlayerSide(u) || IsPlayerAlly(u);

    public InfoDisclosureDef RulesFor(Unit u) => u.Def.Info.Override ? u.Def.Info.Values : _sim.Rules.Info;

    /// <summary>Information about a unit as the player side may see it (own units and admin mode: everything).</summary>
    public EnemyInfo GetInfo(Unit u)
    {
        bool full = IsFullyKnown(u);
        var rules = RulesFor(u);
        bool visible = u.Alive && _sim.Vision.IsVisibleToPlayer(u);
        var k = Of(u);
        double age = k.LastSeenTick < 0 ? 0 : (_sim.Tick - k.LastSeenTick) * _sim.Dt;

        InfoField Field(Disclosure d, bool observed, string observeHint)
        {
            if (full || k.Scanned) return new InfoField { Known = true };
            switch (d)
            {
                case Disclosure.Always:
                    return new InfoField { Known = true };
                case Disclosure.WhenVisible:
                    return visible ? new InfoField { Known = true } : new InfoField { Hint = k.EverSeen ? "не виден сейчас" : "не обнаружен" };
                case Disclosure.AfterSeen:
                    if (visible) return new InfoField { Known = true };
                    return k.EverSeen ? new InfoField { Known = true, Stale = true, StaleSeconds = age } : new InfoField { Hint = "не обнаружен" };
                case Disclosure.AfterObserved:
                    return observed ? new InfoField { Known = true, Stale = !visible, StaleSeconds = visible ? 0 : age } : new InfoField { Hint = observeHint };
                default:
                    return new InfoField { Hint = "скрыто" };
            }
        }

        var nameF = Field(rules.NameAndType, k.EverSeen, "не обнаружен");
        var hpF = Field(rules.Health, k.EverSeen, "не обнаружен");
        var resF = Field(rules.Resources, k.EverSeen, "не обнаружен");
        var stF = Field(rules.Statuses, k.EverSeen, "не обнаружен");
        var resistF = Field(rules.Resistances, k.ObservedDamageTypes.Count > 0, "атакуйте, чтобы узнать реакцию на урон");
        var skillF = Field(rules.Skills, k.SeenSkills.Count > 0, "навыки ещё не применялись на виду");
        var facingF = Field(rules.Facing, k.EverSeen, "не обнаружен");
        var intentF = Field(rules.Intent, k.EverSeen, "не обнаружен");
        bool liveValues = full || visible || k.Scanned;

        var resist = new List<(string, string)>();
        if (resistF.Known)
        {
            bool all = full || k.Scanned || rules.Resistances == Disclosure.Always || rules.Resistances == Disclosure.WhenVisible || rules.Resistances == Disclosure.AfterSeen;
            foreach (var dt in _sim.Content.All<DamageTypeDef>())
            {
                if (!all && !k.ObservedDamageTypes.Contains(dt.Id)) continue;
                // Same numbers the damage pipeline uses: V % (negative — resistance, positive — vulnerability).
                double v = _sim.Damage.ModifierPercent(u, dt.Id);
                bool immune = _sim.Damage.IsImmune(u, dt.Id) || v <= -100;
                if (immune) resist.Add((dt.DisplayName, _sim.Damage.IsImmune(u, dt.Id) ? "иммунитет" : $"{v:+0;-0} % — урон не проходит"));
                else if (Math.Abs(v) > 0.5) resist.Add((dt.DisplayName, v < 0 ? $"{v:0} % урона (сопротивление)" : $"+{v:0} % урона (уязвимость)"));
                else if (!all) resist.Add((dt.DisplayName, "обычный урон"));
            }
        }

        var skills = new List<string>();
        int total = u.Skills.Count + (u.BasicAttack != null ? 1 : 0);
        if (skillF.Known)
        {
            bool all = full || k.Scanned || rules.Skills is Disclosure.Always or Disclosure.WhenVisible or Disclosure.AfterSeen;
            if (u.BasicAttack != null && (all || k.SeenSkills.Contains(u.BasicAttack.Id))) skills.Add(u.BasicAttack.DisplayName + " (обычная атака)");
            foreach (var s in u.Skills)
                if (all || k.SeenSkills.Contains(s.Id))
                    skills.Add(s.DisplayName);
        }

        var p = u.Def.Perception;
        var facingDir = liveValues ? u.Facing : k.LastFacing;
        string facing = p.Model switch
        {
            FacingModel.None => "нет направления (секторы не действуют)",
            FacingModel.Omni => "круговое восприятие (секторы не действуют)",
            FacingModel.Multiple => $"{facingDir.RuName()} + доп. направления: {string.Join(", ", p.FacingsOf(facingDir).Skip(1).Select(d => d.RuShortName()))}",
            _ => facingDir.RuName(),
        };
        if (p.Zone != DetectionZoneShape.None) facing += $"; зона обнаружения — {p.Zone} {p.ZoneSize}";

        return new EnemyInfo
        {
            PanelEnabled = full || rules.PanelEnabled,
            VisibleNow = visible,
            NameField = nameF,
            Name = nameF.Known ? u.Name : "Неизвестный противник",
            Type = nameF.Known ? $"{u.Def.CreatureCategory} · {u.LayerDef.DisplayName.ToLowerInvariant()} слой" : "",
            HealthField = hpF,
            Hp = liveValues ? u.Hp : k.LastHp,
            HpMax = liveValues ? u.HpMax : k.LastHpMax,
            ResourcesField = resF,
            Resources = liveValues ? u.Resources.Values.Select(r => (r.Def.DisplayName, r.Current, r.Max)).ToList() : new List<(string, double, double)>(k.LastResources),
            StatusesField = stF,
            Statuses = liveValues ? u.Statuses.Select(s => (s.LevelName, StatusDetail(s))).ToList() : new List<(string, string)>(k.LastStatuses),
            ResistancesField = resistF,
            Resistances = resist,
            SkillsField = skillF,
            Skills = skills,
            TotalSkills = total,
            FacingField = facingF,
            Facing = facing,
            IntentField = intentF.Known && (visible || full) ? intentF : new InfoField { Hint = visible ? intentF.Hint : "намерение видно только пока враг на виду" },
            Intent = visible || full ? IntentOf(u) : "",
        };
    }

    /// <summary>Observed intention: preparing a skill, moving, attacking someone, being blocked, waiting.</summary>
    public string IntentOf(Unit u)
    {
        if (!u.Alive) return "выбыл";
        if (u.Casting != null && !u.Casting.Released)
        {
            var t = _sim.GetUnit(u.Casting.TargetUnitId);
            return $"готовит «{u.Casting.Skill.DisplayName}»" + (t != null ? $" → {t.Name}" : $" → {u.Casting.Anchor}");
        }
        var blocker = _sim.Blocking.BlockerOf(u);
        if (blocker != null) return $"заблокирован: {blocker.Name}";
        var cur = u.Queue.Current;
        if (cur != null)
        {
            switch (cur.Type)
            {
                case CommandType.Move when cur.TargetCell.HasValue:
                    return $"движется к {cur.TargetCell.Value}";
                case CommandType.Attack:
                case CommandType.Skill:
                    var t = _sim.GetUnit(cur.TargetUnitId);
                    return t != null ? $"атакует: {t.Name}" : "атакует";
                case CommandType.Defend:
                    return "в защитной стойке";
            }
        }
        var target = _sim.GetUnit(u.Ai.TargetUnitId);
        if (target != null && target.Alive) return $"цель: {target.Name}";
        return "ожидает";
    }
}
