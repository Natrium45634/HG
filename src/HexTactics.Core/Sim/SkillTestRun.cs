using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>Test dummies and the run of the editor's skill preview.</summary>
public sealed class SkillTestOptions
{
    public int Targets { get; set; } = 3;
    public double TargetHp { get; set; } = 100;
    public double TargetArmor { get; set; }
    public double TargetEvasion { get; set; }
    /// <summary>The dummies turn their backs on the caster (rear sector: surprise, no stances).</summary>
    public bool TargetsFacingAway { get; set; }
    public ulong Seed { get; set; } = 1;
    /// <summary>Caster: a unit of the content (its stats); empty — the first unit that knows the skill, or a test fighter.</summary>
    public string CasterUnit { get; set; } = "";
}

public sealed class SkillTestResult
{
    public bool Started { get; set; }
    public string Problem { get; set; } = "";
    public string CasterName { get; set; } = "";
    public List<HitRecord> Hits { get; } = new();
    public List<string> Log { get; } = new();
    public List<string> Breakdowns { get; } = new();
    public List<(string Name, double HpBefore, double HpAfter, bool Alive)> Targets { get; } = new();
    public string StopReason { get; set; } = "";
    public double Seconds { get; set; }
    public double TotalDamage => Hits.Sum(h => h.Damage);
}

/// <summary>
/// Preview of a skill on a test map (docs/DECISIONS_3.md §45): an isolated simulation in memory — the caster,
/// test dummies, one use of the skill and the full log with the breakdown of every hit. Never touches the
/// content (it works on a copy) or a battle.
/// </summary>
public static class SkillTestRun
{
    private const string CasterId = "__preview_caster";
    private const string DummyId = "__preview_dummy";

    public static SkillTestResult Run(ContentDatabase source, SkillDef skill, SkillTestOptions o)
    {
        var res = new SkillTestResult();
        var db = source.Clone();
        db.Rules.Time.StartPaused = false;
        db.Rules.AutoAttacks.Enabled = false; // only the tested skill acts
        var s = db.Skill(skill.Id) ?? ContentJson.Clone(skill);
        if (db.Skill(s.Id) == null) db.Add(s);
        db.Add(new FactionDef { Id = "__p", Name = "Исполнитель", HostileTo = { "__e" } });
        db.Add(new FactionDef { Id = "__e", Name = "Манекены", HostileTo = { "__p" } });

        var proto = !string.IsNullOrEmpty(o.CasterUnit) ? db.Unit(o.CasterUnit) : db.All<UnitDef>().FirstOrDefault(u => u.Skills.Contains(s.Id) || u.BasicAttack == s.Id || u.DefenseSkill == s.Id);
        var caster = proto != null ? ContentJson.Clone(proto) : new UnitDef { Name = "Тестовый боец", Stats = { [Stats.Power] = 10 } };
        caster.Id = CasterId;
        caster.Faction = "__p";
        caster.AiProfile = "";
        caster.Stats[Stats.ApMax] = Math.Max(caster.Stats.GetValueOrDefault(Stats.ApMax, 10), s.ApCost + 10);
        if (!caster.Skills.Contains(s.Id)) caster.Skills.Add(s.Id);
        // Resources the skill uses, full.
        var used = s.ResourceCosts.Select(r => r.Resource).Concat(s.Stages.SelectMany(st => st.Costs).Select(c => c.Resource)).Concat(s.Defense.Upkeep.Select(c => c.Resource))
            .Where(r => !string.IsNullOrEmpty(r) && db.Get<ResourceDef>(r) != null).Distinct();
        foreach (var r in used)
            if (caster.Resources.All(x => x.Resource != r))
                caster.Resources.Add(new UnitResourceDef { Resource = r, Max = 100, Start = 100 });
        db.Add(caster);
        var dummy = new UnitDef
        {
            Id = DummyId, Name = "Манекен", Faction = "__e",
            Stats = { [Stats.HpMax] = o.TargetHp, [Stats.Armor] = o.TargetArmor, [Stats.Evasion] = o.TargetEvasion, [Stats.MoveSpeed] = 0, [Stats.ApMax] = 10 },
        };
        db.Add(dummy);
        res.CasterName = caster.DisplayName;

        var floor = db.All<TerrainDef>().FirstOrDefault(t => t.Kind == TerrainKind.Floor && t.IsPassableFor(caster.MoveCategory) && t.AllowsPlacement)
                    ?? db.All<TerrainDef>().First();
        var map = MapDef.CreateRhombus("__preview", 11, 7, floor.Id);
        Simulation sim;
        try
        {
            sim = new Simulation(db, map, new SimOptions { Seed = o.Seed, PlayerFactions = new() { "__p" }, AiFactions = new(), FogOfWar = false });
        }
        catch (Exception ex)
        {
            res.Problem = "тестовая карта не создана: " + ex.Message;
            return res;
        }
        var start = new HexCoord(2, 3);
        var u = sim.Spawn(sim.Content.Unit(CasterId)!, null, start, HexDirection.E);
        int dist = s.Targeting is TargetingMode.Unit or TargetingMode.Cell ? Math.Clamp(Math.Min(s.RangeMax, 3), Math.Max(1, s.RangeMin), 6) : 1;
        var main = start + HexDirection.E.Offset() * dist;
        var spots = new List<HexCoord> { main, main + HexDirection.NE.Offset(), main + HexDirection.SE.Offset(), main + HexDirection.E.Offset(), main + HexDirection.NW.Offset(), main + HexDirection.SW.Offset() };
        var targets = new List<Unit>();
        foreach (var c in spots.Where(sim.Map.Contains).Where(c => c != start).Take(Math.Clamp(o.Targets, 0, 6)))
        {
            var t = sim.Spawn(sim.Content.Unit(DummyId)!, null, c, o.TargetsFacingAway ? HexDirection.E : HexDirection.W);
            targets.Add(t);
        }
        foreach (var t in targets) res.Targets.Add((t.Name + $" {t.Pos}", t.Hp, t.Hp, true));

        Command cmd;
        if (u.DefenseSkill == s || s.Kind == SkillKind.Defense && s.Targeting == TargetingMode.Self)
            cmd = Command.Skill(s.Id, null);
        else
            cmd = s.Targeting switch
            {
                TargetingMode.Unit => targets.Count > 0 ? Command.Skill(s.Id, null, targets[0].Id) : Command.Skill(s.Id, null, u.Id),
                TargetingMode.Cell => Command.Skill(s.Id, main),
                TargetingMode.Direction => Command.Skill(s.Id, null, -1, HexDirection.E),
                _ => Command.Skill(s.Id, null),
            };
        var err = sim.Enqueue(u, cmd);
        if (err != null)
        {
            res.Problem = "приказ не принят: " + err;
            return res;
        }
        double limit = Math.Min(30, s.PrepTime + SkillText.Duration(s) + s.RecoveryTime + 1.5 + (s.Defense.Enabled ? Math.Min(5, s.Defense.Duration) : 0));
        long until = sim.Tick + sim.TicksFor(limit);
        // Defensive skills are checked under attack: each dummy in front strikes once a second.
        SkillCast? cast = null;
        while (sim.Tick < until)
        {
            sim.Step();
            if (cast == null && sim.Skills.LastReleased is { } lr && lr.Caster == u && lr.Skill.Id == s.Id) cast = lr;
            if (s.Defense.Enabled && u.Stance != null && sim.Tick % sim.TicksPerSecond == 0)
                foreach (var t in targets.Where(t => t.Alive))
                    sim.Damage.Apply(new DamageRequest { Attacker = t, Target = u, Amount = 20, DamageType = "physical", Source = DamageSource.Skill, Cause = "проверка защиты",
                        Roll = new AttackRoll { Hit = true, Noticed = true, Sector = CombatSystem.SectorFor(u, t.Pos) } });
        }
        res.Started = cast != null;
        res.Seconds = limit;
        if (cast != null)
        {
            res.Hits.AddRange(cast.Series.Hits);
            res.StopReason = cast.Series.StopReason;
        }
        if (!res.Started)
        {
            var failed = sim.Log.Entries.LastOrDefault(e => e.Type is SimEventType.CommandFailed or SimEventType.CommandSkipped or SimEventType.CastInterrupted);
            res.Problem = failed?.Text ?? "навык не сработал";
        }
        for (int i = 0; i < targets.Count; i++) res.Targets[i] = (res.Targets[i].Name, res.Targets[i].HpBefore, Math.Max(0, targets[i].Hp), targets[i].Alive);
        foreach (var e in sim.Log.Entries)
        {
            if (e.Level == LogLevel.Diagnostic || e.Type is SimEventType.CommandStarted or SimEventType.CommandCompleted or SimEventType.ApFull) continue;
            res.Log.Add($"{e.Tick * sim.Dt:0.00} с · {e.Text}");
        }
        foreach (var (tick, _, target, r) in sim.Damage.Recent)
            res.Breakdowns.Add($"{tick * sim.Dt:0.00} с → {sim.GetUnit(target)?.Name}: " +
                               string.Join(" → ", r.Steps.Select(st => $"{st.Name} {st.Value:0.#}" + (string.IsNullOrWhiteSpace(st.Note) ? "" : $" ({st.Note.Trim()})"))));
        return res;
    }
}
