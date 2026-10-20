using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>What an auto-attack of a unit is doing (unit panel, tests).</summary>
public sealed class AutoAttackState
{
    public int Index { get; init; }
    public string Skill { get; set; } = "";
    public long NextTick { get; set; }
    public string Status { get; set; } = "";
    public int TargetId { get; set; } = -1;
    public int Fired { get; set; }
    /// <summary>How the last target was chosen (diagnostics).</summary>
    public string Choice { get; set; } = "";
}

/// <summary>
/// Auto-attacks (docs/DECISIONS_2.md §28, TZ part 2 §10.3). Every tick, for each auto-attack:
/// 1 ready → 2 AP/resources (free AP only by default) → 3 moment → 4 candidates (detected by the side) →
/// 5 range/LoS/layers through the common skill check → 6 target rules → 7 the skill starts the same way
/// a command does → 8 damage and statuses through the common pipeline (source «автоатака») → 9 event and
/// the next moment. Manual commands take priority; blocking, detection and resistances are never bypassed.
/// </summary>
public sealed class AutoAttackSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<int, List<AutoAttackState>> _states = new();
    private readonly Dictionary<(string Unit, int Index), SkillDef> _skills = new();
    private readonly List<(Unit Unit, SkillCast Cast, AutoAttackState State)> _casting = new();

    public AutoAttackSystem(Simulation sim)
    {
        _sim = sim;
    }

    public int Fired { get; private set; }

    /// <summary>Per-unit switch (the player can turn auto-attacks off for a unit).</summary>
    public HashSet<int> DisabledUnits { get; } = new();

    public IReadOnlyList<AutoAttackState> StatesOf(Unit u) =>
        _states.TryGetValue(u.Id, out var l) ? l : (IReadOnlyList<AutoAttackState>)Array.Empty<AutoAttackState>();

    private List<AutoAttackState> States(Unit u)
    {
        if (!_states.TryGetValue(u.Id, out var l))
        {
            l = new List<AutoAttackState>();
            for (int i = 0; i < u.Def.AutoAttacks.Count; i++) l.Add(new AutoAttackState { Index = i });
            _states[u.Id] = l;
        }
        return l;
    }

    private readonly Dictionary<(int Unit, int Index), string> _skillOverride = new();

    /// <summary>Changes the skill of an auto-attack at runtime (boss phases, scenario actions).</summary>
    public void SetSkill(Unit u, int index, string skillId)
    {
        if (index < 0 || index >= u.Def.AutoAttacks.Count) return;
        _skillOverride[(u.Id, index)] = skillId;
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: автоатака → «{_sim.Content.Skill(skillId)?.DisplayName ?? skillId}»", u.Id);
    }

    /// <summary>The skill as used by the auto-attack: range, area and layer overrides on a private copy.</summary>
    public SkillDef? SkillOf(Unit u, int index)
    {
        var a = u.Def.AutoAttacks[index];
        var id = _skillOverride.TryGetValue((u.Id, index), out var ov) ? ov : a.Skill;
        var baseSkill = string.IsNullOrEmpty(id) ? u.BasicAttack : u.Skills.FirstOrDefault(s => s.Id == id) ?? _sim.Content.Skill(id);
        if (baseSkill == null) return null;
        if (a.Range <= 0 && a.AreaSize <= 0 && a.TargetLayers.Count == 0 && (!a.RequireLoS || baseSkill.RequiresLoS)) return baseSkill;
        var key = (u.Def.Id + "/" + baseSkill.Id, index);
        if (_skills.TryGetValue(key, out var s)) return s;
        s = ContentJson.Clone(baseSkill);
        if (a.Range > 0) s.RangeMax = Math.Max(s.RangeMin, a.Range);
        if (a.AreaSize > 0)
            foreach (var st in s.Stages)
                if (st.Area.Shape != AreaShape.Single)
                    st.Area.Size = a.AreaSize;
        if (a.TargetLayers.Count > 0) s.TargetLayers = new List<string>(a.TargetLayers);
        if (a.RequireLoS) s.RequiresLoS = true;
        _skills[key] = s;
        return s;
    }

    private bool Allowed(Unit u)
    {
        var r = _sim.Rules.AutoAttacks;
        if (!r.Enabled || u.Def.AutoAttacks.Count == 0 || DisabledUnits.Contains(u.Id)) return false;
        return u.AiControlled ? r.AiUnits : r.PlayerUnits;
    }

    public void Update()
    {
        ReleaseDue();
        foreach (var u in _sim.Units)
        {
            if (!u.Alive || !Allowed(u)) continue;
            var states = States(u);
            for (int i = 0; i < u.Def.AutoAttacks.Count && i < states.Count; i++)
                if (TryFire(u, u.Def.AutoAttacks[i], states[i]))
                    break;
        }
    }

    /// <summary>Releases prepared auto-attacks (the same re-validation as commands do).</summary>
    private void ReleaseDue()
    {
        for (int i = _casting.Count - 1; i >= 0; i--)
        {
            var (u, cast, st) = _casting[i];
            if (!u.Alive || cast.Interrupted || u.Casting != cast)
            {
                _casting.RemoveAt(i);
                continue;
            }
            if (_sim.Tick < cast.ReleaseTick) continue;
            _casting.RemoveAt(i);
            var s = cast.Skill;
            if (s.Targeting is TargetingMode.Unit or TargetingMode.Cell)
            {
                var re = _sim.Skills.Validate(u, s, cast.Anchor, cast.TargetUnitId, null, u.Pos, u.Facing, checkCosts: false);
                if (!re.Ok)
                {
                    _sim.Skills.Interrupt(u, InterruptCause.Cancelled, "автоатака: " + re.Message);
                    st.Status = "цель ушла: " + re.Message;
                    continue;
                }
            }
            _sim.Skills.Release(cast);
            u.BusyUntil = _sim.Skills.SeriesEnd(cast) + _sim.TicksFor(s.RecoveryTime);
        }
    }

    private bool TryFire(Unit u, AutoAttackDef a, AutoAttackState st)
    {
        if (!a.Enabled) return Idle(st, "выключена");
        // 1. Ready.
        if (u.Casting != null || u.BusyUntil > _sim.Tick) return Idle(st, "занят");
        if (u.Flags.BlocksActions || u.Flags.BlocksAttacks) return Idle(st, "не может атаковать");
        var cur = u.Queue.Current;
        if (cur != null && cur.Type is CommandType.Attack or CommandType.Skill or CommandType.Turn) return Idle(st, "выполняет приказ");
        bool moving = cur != null && cur.Type == CommandType.Move && !cur.IsFinished;
        if (moving && !a.WhileMoving) return Idle(st, "в движении");
        if (u.StepTo.HasValue) return Idle(st, "в движении");
        foreach (var stop in a.StopWhen)
        {
            switch (stop.When)
            {
                case AutoAttackStopKind.PlayerCommand when !u.AiControlled && cur != null && cur.Type != CommandType.Move && cur.Type != CommandType.Defend:
                    return Idle(st, "ручная команда");
                case AutoAttackStopKind.PlayerCommand when !u.AiControlled && moving && !a.WhileMoving:
                    return Idle(st, "ручная команда");
                case AutoAttackStopKind.HpBelow when u.HpFraction < stop.HpFraction:
                    return Idle(st, $"здоровье ниже {stop.HpFraction:P0}");
                case AutoAttackStopKind.HasStatus when !string.IsNullOrEmpty(stop.Status) && u.FindStatus(stop.Status) != null:
                    return Idle(st, "состояние " + stop.Status);
                case AutoAttackStopKind.Defending when u.Defending:
                    return Idle(st, "в стойке");
            }
        }
        var blocking = _sim.Blocking;
        if (!a.WhenBlocked && blocking.BlockerOf(u) != null) return Idle(st, "заблокирован");
        if (!a.WhenBlocking && blocking.Load(u) > 0) return Idle(st, "блокирует");

        // 3. Moment of the next attack.
        if (_sim.Tick < st.NextTick) return false;

        var skill = SkillOf(u, st.Index);
        if (skill == null) return Idle(st, "нет навыка");
        st.Skill = skill.DisplayName;

        // 2. AP and resources: only free AP (the queue's reserve is kept), unless allowed by the setting.
        double cost = a.ApCost >= 0 ? a.ApCost : _sim.Skills.ApCostOf(u, skill);
        double available = a.OnShortage == ResourceShortage.UseReserved ? u.Ap : u.FreeAp;
        bool resourcesOk = skill.ResourceCosts.All(rc => u.ResourceValue(rc.Resource) + 1e-9 >= rc.Amount);
        if (available + 1e-9 < cost || !resourcesOk)
        {
            if (a.OnShortage == ResourceShortage.Skip) st.NextTick = _sim.Tick + _sim.TicksFor(a.Interval);
            return Idle(st, available + 1e-9 < cost ? $"нет свободных ОД ({available:0.#}/{cost:0.#})" : "нет ресурсов");
        }
        if (u.CooldownRemaining(skill.Id, _sim.Tick) > 0) return Idle(st, "перезарядка");

        // 4–5. Candidates detected by the side, valid for the skill (range, LoS, layers, filter).
        var candidates = new List<(Unit Target, SkillCheck Check)>();
        foreach (var t in _sim.Units)
        {
            if (!t.Alive || t == u) continue;
            if (!_sim.FilterAccepts(a.Targets, u, t)) continue;
            if (a.RequireDetection && !_sim.Vision.CanFactionSee(u.FactionId, t)) continue;
            var dir = u.Pos.DirectionTo(t.Pos) ?? u.Facing;
            var chk = _sim.Skills.Validate(u, skill, t.Pos, t.Id, dir, u.Pos, u.Facing, checkCosts: false);
            if (!chk.Ok) continue;
            // Self/direction skills: the target must be inside the first stage's area (cleave, cone…).
            if (skill.Targeting is TargetingMode.Self or TargetingMode.Direction && skill.Stages.Count > 0
                && !AreaResolver.Resolve(skill.Stages[0].Area, u.Pos, dir, _sim.Map).Any(c => c.Cell == t.Pos))
                continue;
            if (t != u && !LayerResolver.CanTarget(_sim.Rules, skill, t)) continue;
            candidates.Add((t, chk));
        }
        if (candidates.Count == 0) return Idle(st, "нет целей в зоне");

        // 6. Choose.
        var (picked, check) = Choose(u, a, candidates);
        if (picked == null) return Idle(st, "нет целей по правилам выбора");
        var target = picked;

        // Facing the target follows the common turning rules (AP and time per step).
        bool wantFace = skill.FaceTarget && _sim.Rules.Turn.AutoFaceTarget && u.Def.Perception.HasFacing;
        if (wantFace && check.Orientation != u.Facing && !u.Flags.BlocksTurning)
        {
            if (available + 1e-9 < cost + _sim.Rules.Turn.ApPerStep) return Idle(st, "нет ОД на поворот");
            _sim.Commands.TurnStep(u, check.Orientation);
            st.Status = $"поворачивается к {target.Name}";
            return true;
        }

        // 7. Start the skill like a command does; 8 happens in the skill and damage pipeline.
        var cast = _sim.Skills.Begin(u, skill, check, target.Id, DamageSource.AutoAttack, cost);
        _casting.Add((u, cast, st));
        if (skill.PrepTime <= 0) ReleaseDue();

        // 9. Event and the next moment.
        st.NextTick = _sim.Tick + _sim.TicksFor(a.Interval);
        st.TargetId = target.Id;
        st.Fired++;
        st.Status = $"атакует {target.Name}";
        Fired++;
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: автоатака «{skill.DisplayName}» → {target.Name}", u.Id, target.Id, target.Pos);
        return true;
    }

    private static bool Idle(AutoAttackState st, string why)
    {
        st.Status = why;
        return false;
    }

    // ------------------------------------------------------------------ target choice

    private (Unit?, SkillCheck) Choose(Unit u, AutoAttackDef a, List<(Unit Target, SkillCheck Check)> candidates)
    {
        var st = States(u).FirstOrDefault(x => x.Index == u.Def.AutoAttacks.IndexOf(a));
        var rules = u.AutoAttackPriority is { } prio ? new[] { new AutoTargetRuleDef { Rule = prio } }.Concat(a.Rules).ToList() : a.Rules;
        // Passives «выбор целей» come first (docs/DECISIONS_3.md §46).
        var passive = _sim.Passives.TargetRules(u);
        if (passive.Count > 0) rules = passive.Concat(rules).ToList();
        var pick = _sim.Targets.Choose(u, candidates.Select(c => c.Target).ToList(), rules, a.Selection, a.RandomTies, SkillOf(u, st?.Index ?? 0),
            st?.TargetId ?? -1, out var why);
        if (st != null) st.Choice = why;
        if (pick == null) return (null, default);
        var chosen = candidates.First(c => c.Target == pick);
        return (chosen.Target, chosen.Check);
    }
}
