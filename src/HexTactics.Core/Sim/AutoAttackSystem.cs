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
            u.BusyUntil = _sim.Tick + _sim.TicksFor(s.RecoveryTime);
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
        double cost = a.ApCost >= 0 ? a.ApCost : skill.ApCost;
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
        var (target, check) = Choose(u, a, candidates);

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

    private double Metric(Unit u, Unit t, AutoTargetRuleDef r, AutoAttackState? st)
    {
        // Lower is better.
        switch (r.Rule)
        {
            case AutoTargetRule.LowestHp:
                return t.Hp;
            case AutoTargetRule.LowestHpFraction:
                return t.HpFraction;
            case AutoTargetRule.MostDangerous:
                return -AiSystem.Threat(t);
            case AutoTargetRule.NearestToProtected:
            {
                var tag = _sim.Rules.AutoAttacks.ProtectedTag;
                var prot = _sim.Units.Where(x => x.Alive && !_sim.IsHostile(u, x) && (x.Tag == tag || x.Def.Tags.Contains(tag))).ToList();
                return prot.Count == 0 ? 0 : prot.Min(p => HexCoord.Distance(p.Pos, t.Pos));
            }
            case AutoTargetRule.Boss:
            {
                var tag = _sim.Rules.AutoAttacks.BossTag;
                return t.Tag == tag || t.Def.Tags.Contains(tag) ? 0 : 1;
            }
            case AutoTargetRule.WithTag:
                return !string.IsNullOrEmpty(r.Tag) && (t.Tag == r.Tag || t.Def.Tags.Contains(r.Tag)) ? 0 : 1;
            case AutoTargetRule.BlockedByMe:
                return _sim.Blocking.BlockerOf(t) == u ? 0 : 1;
            case AutoTargetRule.BlockingMe:
                return _sim.Blocking.BlockerOf(u) == t ? 0 : 1;
            case AutoTargetRule.CurrentTarget:
                return st != null && st.TargetId == t.Id || u.Ai.TargetUnitId == t.Id ? 0 : 1;
            default:
                return HexCoord.Distance(u.Pos, t.Pos);
        }
    }

    private (Unit, SkillCheck) Choose(Unit u, AutoAttackDef a, List<(Unit Target, SkillCheck Check)> candidates)
    {
        var st = States(u).FirstOrDefault(x => x.Index == u.Def.AutoAttacks.IndexOf(a));
        var rules = a.Rules.Count > 0 ? a.Rules : new List<AutoTargetRuleDef> { new() { Rule = AutoTargetRule.Nearest } };
        var pool = candidates;
        if (a.Selection == TargetSelectionMode.Weighted)
        {
            // Weighted sum of metrics normalised to [0;1] over the candidates (lower is better).
            var scores = new Dictionary<int, double>();
            foreach (var c in pool) scores[c.Target.Id] = 0;
            foreach (var r in rules)
            {
                var vals = pool.Select(c => Metric(u, c.Target, r, st)).ToList();
                double min = vals.Min(), max = vals.Max(), span = max - min;
                for (int i = 0; i < pool.Count; i++)
                    scores[pool[i].Target.Id] += r.Weight * (span < 1e-9 ? 0 : (vals[i] - min) / span);
            }
            double best = scores.Values.Min();
            pool = pool.Where(c => scores[c.Target.Id] <= best + 1e-9).ToList();
        }
        else
        {
            // Sequential: the first rule that narrows the field; ties go to the next rule.
            foreach (var r in rules)
            {
                if (pool.Count <= 1) break;
                double best = pool.Min(c => Metric(u, c.Target, r, st));
                pool = pool.Where(c => Metric(u, c.Target, r, st) <= best + 1e-9).ToList();
            }
        }
        if (pool.Count > 1 && a.RandomTies)
        {
            var sorted = pool.OrderBy(c => c.Target.Id).ToList();
            return sorted[_sim.Rng.NextInt(0, sorted.Count)];
        }
        var pick = pool.OrderBy(c => HexCoord.Distance(u.Pos, c.Target.Pos)).ThenBy(c => c.Target.Id).First();
        return (pick.Target, pick.Check);
    }
}
