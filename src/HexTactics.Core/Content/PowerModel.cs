using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>The context of an evaluation (docs/DECISIONS_4.md §65): alone, in a typical group, in a group of a scenario, against a category.</summary>
public enum PowerContextKind
{
    [Ed("Один")] Solo,
    [Ed("Типичная группа")] TypicalGroup,
    [Ed("Группа сценария")] ScenarioGroup,
    [Ed("Против категории")] VsCategory,
}

public sealed class PowerContext
{
    public PowerContextKind Kind { get; init; } = PowerContextKind.Solo;
    /// <summary>Allies next to the unit (auras, «за каждого», healing of others).</summary>
    public int Allies { get; init; }
    /// <summary>Templates of those allies (for the filters of auras and «за каждого»); empty — like the unit itself.</summary>
    public List<UnitDef> AllyDefs { get; init; } = new();
    /// <summary>«Против категории»: conditions on the target's type are answered by it.</summary>
    public string VsCategory { get; init; } = "";
    /// <summary>The scenario (variables of the environment, the group).</summary>
    public ScenarioDef? Scenario { get; init; }

    public static PowerContext Solo { get; } = new();

    public static PowerContext Typical(ContentDatabase db) => new() { Kind = PowerContextKind.TypicalGroup, Allies = db.Rules.Power.TypicalAllies };

    /// <summary>The largest group of the template in the waves of a scenario: the others of that wave are its allies.</summary>
    public static PowerContext InScenario(ContentDatabase db, UnitDef def, ScenarioDef sc)
    {
        var wave = sc.Waves.Where(w => w.Groups.Any(g => g.Unit == def.Id)).OrderByDescending(w => w.Groups.Sum(g => g.Count)).FirstOrDefault();
        var allies = new List<UnitDef>();
        if (wave != null)
            foreach (var g in wave.Groups)
                if (db.Unit(g.Unit) is { } u)
                    for (int i = 0; i < Math.Min(g.Count, 12); i++)
                        allies.Add(u);
        if (allies.Contains(def)) allies.Remove(def);
        return new PowerContext { Kind = PowerContextKind.ScenarioGroup, Allies = Math.Min(12, allies.Count), AllyDefs = allies.Take(12).ToList(), Scenario = sc };
    }

    public override string ToString() => Kind switch
    {
        PowerContextKind.Solo => "один",
        PowerContextKind.TypicalGroup => $"в типичной группе ({Allies} союзн.)",
        PowerContextKind.ScenarioGroup => $"в группе сценария «{Scenario?.DisplayName}» ({Allies} союзн.)",
        _ => $"против «{VsCategory}»",
    };
}

/// <summary>A row of the table of contributions: what, how much, from where.</summary>
public sealed record PowerLine(string Key, string Label, double Value, string Detail);

/// <summary>A passive in the evaluation: its expected share of the battle and what it adds.</summary>
public sealed record PassiveShare(string Id, string Name, double Share, string Source, string Why);

/// <summary>Effective health against one damage type.</summary>
public sealed record EhpLine(string Type, string Name, double Ehp, double Ratio);

/// <summary>The evaluation of a template: components, the table of contributions, the exact power and level.</summary>
public sealed class PowerReport
{
    public string Unit { get; init; } = "";
    public PowerRole Role { get; set; }
    public bool RoleAuto { get; set; }
    public UnitRank Rank { get; set; }
    /// <summary>Components relative to the reference (the reference = 1), before weights and curves.</summary>
    public double H { get; set; }
    public double D { get; set; }
    public double R { get; set; }
    public double M { get; set; }
    /// <summary>Control in units of the project's «эталон контроля».</summary>
    public double C { get; set; }
    /// <summary>Support and synergies in ally-equivalents of power.</summary>
    public double S { get; set; }
    public double Dps { get; set; }
    public double Ehp { get; set; }
    public double MaxRange { get; set; }
    public double Speed { get; set; }
    public List<string> MoveCategories { get; } = new();
    /// <summary>Health restored per second by itself (passives, own skills).</summary>
    public double SelfHealPerSec { get; set; }
    /// <summary>The reference: damage per second, effective health, speed (for the threat model and the checks).</summary>
    public double ReferenceDps { get; set; }
    public double ReferenceEhp { get; set; }
    public double ReferenceSpeed { get; set; }
    public List<EhpLine> EhpProfile { get; } = new();
    public List<PowerLine> Lines { get; } = new();
    public List<PassiveShare> Passives { get; } = new();
    public List<string> Notes { get; } = new();
    /// <summary>The exact power (the reference = 1).</summary>
    public double Power { get; set; }
    /// <summary>The exact level.</summary>
    public double Level { get; set; }
    public int DisplayLevel => Math.Max(1, (int)Math.Round(Level, MidpointRounding.AwayFromZero));
    public string Context { get; set; } = "";

    public string LevelText => $"{DisplayLevel} ({Level.ToString("0.00", CultureInfo.InvariantCulture)})";

    public string ToText()
    {
        var lines = new List<string> { $"Уровень {LevelText}, сила {Power:0.000} · роль {EdUtil.EnumLabel(Role)}{(RoleAuto ? " (авто)" : "")} · ранг {EdUtil.EnumLabel(Rank)} · {Context}" };
        foreach (var l in Lines) lines.Add($"{l.Label}: {(l.Key == "total" ? "" : "+")}{l.Value:0.00}" + (l.Detail != "" ? $" — {l.Detail}" : ""));
        var differs = EhpProfile.Where(e => e.Type == "physical" || Math.Abs(e.Ratio - 1) > 0.01).ToList();
        if (EhpProfile.Count > 0)
            lines.Add("Эфф. здоровье по типам: " + (differs.Count <= 1 ? $"одинаково ({EhpProfile[0].Ehp:0})" : string.Join(", ", differs.Select(e => $"{e.Name} {e.Ehp:0} (×{e.Ratio:0.##})"))));
        foreach (var p in Passives) lines.Add($"  пассив «{p.Name}» ({p.Source}): доля {p.Share:P0} — {p.Why}");
        lines.AddRange(Notes.Select(n => "  ⚠ " + n));
        return string.Join("\n", lines);
    }
}

/// <summary>
/// The power model (docs/DECISIONS_4.md §65, TZ §11–13): no simulation, only the data — explainable. Components
/// relative to the reference of level 1: H — effective health by a profile of damage types (resistances, armor
/// against the reference hit, evasion against the reference accuracy, block); D — sustained damage per second
/// (all hits of a series, area, hit chance, crits, preparation, recovery, AP, cooldowns, resources); R — range by
/// zones; M — mobility; C — control per second; S — support and synergies (healing of allies, auras, «за каждого»,
/// summons) — only as far as the context has allies. Passives act with their expected share of the battle
/// (a condition «здоровье ниже 10 %» ≈ 10 %, a trigger — by its frequency). Categories and tags count only through
/// their rules. P = Σ weights × curves; the reference gives 1; the level comes from the project's scale.
/// </summary>
public static class PowerModel
{
    /// <summary>A built reference: the unit itself when the rules name one, else a plain fighter.</summary>
    public static UnitDef ReferenceDef(ContentDatabase db)
    {
        var id = db.Rules.Power.ReferenceUnit;
        if (id != "" && db.Unit(id) is { } u) return u;
        return new UnitDef
        {
            Id = "__reference", Name = "Эталон", Faction = "enemy", BasicAttack = "__reference_hit",
            Stats = { [Stats.HpMax] = 100, [Stats.ApMax] = 10, [Stats.ApRegen] = 1, [Stats.MoveSpeed] = 2 },
        };
    }

    private static readonly SkillDef ReferenceHit = new()
    {
        Id = "__reference_hit", Name = "Эталонный удар", Targeting = TargetingMode.Unit, ApCost = 2, PrepTime = 0.3, RecoveryTime = 0.2,
        Stages = { new SkillStageDef { Effects = { new SkillEffectDef { Type = EffectType.Damage, DamageType = "physical", Amount = 10 } } } },
    };

    internal static SkillDef? Skill(ContentDatabase db, string id) => id == ReferenceHit.Id ? ReferenceHit : db.Skill(id);

    public static PowerReport Evaluate(ContentDatabase db, UnitDef def, PowerContext? context = null) => new Evaluator(db, context ?? PowerContext.Solo, 0).Run(def);

    /// <summary>The level of a power for a role and rank (the project scale).</summary>
    public static double LevelOf(ContentDatabase db, double power, PowerRole role = PowerRole.Universal, UnitRank rank = UnitRank.Normal) =>
        db.Rules.Power.ScaleFor(role, rank).LevelOf(power);

    public static double PowerOf(ContentDatabase db, double level, PowerRole role = PowerRole.Universal, UnitRank rank = UnitRank.Normal) =>
        db.Rules.Power.ScaleFor(role, rank).PowerOf(level);

    // ------------------------------------------------------------------ the build of a unit

    /// <summary>What the unit has after its passives (each at its share): stats, defences, attack bonuses, extras.</summary>
    internal sealed class Build
    {
        public Dictionary<string, double> Stats = new(StringComparer.Ordinal);
        public Dictionary<string, double> Incoming = new(StringComparer.Ordinal);
        public Dictionary<string, double> Outgoing = new(StringComparer.Ordinal);
        public double HitPower, HitCount, SkillCost, Absorb, Reflect;
        public double SelfHealPerSec, SelfShield, ExtraDps;
        public HashSet<string> MoveCats = new(StringComparer.Ordinal);
        public bool NoBasic, NoSkills, NoMove;
        public HashSet<string> ForbiddenSkillTags = new(StringComparer.Ordinal);

        public Build Clone() => new()
        {
            Stats = new(Stats), Incoming = new(Incoming), Outgoing = new(Outgoing), HitPower = HitPower, HitCount = HitCount, SkillCost = SkillCost,
            Absorb = Absorb, Reflect = Reflect, SelfHealPerSec = SelfHealPerSec, SelfShield = SelfShield, ExtraDps = ExtraDps,
            MoveCats = new(MoveCats), NoBasic = NoBasic, NoSkills = NoSkills, NoMove = NoMove, ForbiddenSkillTags = new(ForbiddenSkillTags),
        };

        public double S(string id) => Stats.TryGetValue(id, out var v) ? v : 0;
    }

    /// <summary>The raw components of a build (before normalisation).</summary>
    internal sealed class Raw
    {
        public double Ehp, Dps, Range, Speed, Control, HitsPerSec, CastsPerSec, AvgHit, Mobility;
        public List<(string Type, double Ehp)> Profile = new();
    }

    private sealed class Evaluator
    {
        private readonly ContentDatabase _db;
        private readonly PowerContext _ctx;
        private readonly PowerRulesDef _p;
        private readonly DamageTaxonomy _tax;
        private readonly List<(string Type, double Weight)> _types;
        private readonly int _depth;
        private Raw _ref = null!;
        private Build _refBuild = null!;
        private UnitDef _refDef = null!;

        public Evaluator(ContentDatabase db, PowerContext ctx, int depth)
        {
            _db = db;
            _ctx = ctx;
            _p = db.Rules.Power;
            _tax = new DamageTaxonomy(db);
            _depth = depth;
            _types = DamageTypes();
        }

        private List<(string, double)> DamageTypes()
        {
            if (_p.DamageTypes.Count > 0) return _p.DamageTypes.Where(t => t.Weight > 0).Select(t => (t.Type, t.Weight)).ToList();
            var roots = _db.All<DamageTypeDef>().Where(t => t.Parent == "").Select(t => t.Id).ToList();
            if (roots.Count == 0) return new() { ("physical", 1) };
            var others = roots.Where(t => t != "physical").ToList();
            var list = new List<(string, double)>();
            if (roots.Contains("physical")) list.Add(("physical", others.Count == 0 ? 1 : 0.5));
            foreach (var t in others) list.Add((t, (roots.Contains("physical") ? 0.5 : 1.0) / others.Count));
            return list;
        }

        private double DefaultStat(string id) => _db.Stat(id)?.Default ?? (id switch
        {
            Stats.HpMax => 100, Stats.ApMax => 10, Stats.ApRegen => 1, Stats.MoveSpeed => 2, Stats.CritMult => 1.5, Stats.BlockReduction => 0.5, _ => 0,
        });

        // ---------------------------------------------------------------- run

        public PowerReport Run(UnitDef def)
        {
            _refDef = ReferenceDef(_db);
            _refBuild = FullBuild(_refDef, null);
            // Twice: the first pass finds the reference hit (armor counts against it), the second uses it.
            _ref = Measure(_refDef, _refBuild, _refBuild);
            _ref = Measure(_refDef, _refBuild, _refBuild);
            var report = new PowerReport { Unit = def.Id, Rank = def.Rank, Context = _ctx.ToString() };

            var baseBuild = BaseBuild(def);
            var rawBase = Measure(def, baseBuild, _refBuild);
            var full = FullBuild(def, report, rawBase);
            var raw = Measure(def, full, _refBuild);

            double H(Raw r) => r.Ehp / Math.Max(1e-9, _ref.Ehp);
            double D(Raw r) => r.Dps / Math.Max(1e-9, _ref.Dps);
            double R(Raw r) => r.Range / Math.Max(1e-9, _ref.Range);
            double M(Raw r) => r.Mobility / Math.Max(1e-9, _ref.Mobility);
            report.H = H(raw);
            report.D = D(raw);
            report.R = R(raw);
            report.M = M(raw);
            report.C = raw.Control / Math.Max(1e-9, _p.ControlReference);
            report.S = Support(def, full, report, _ctx.Allies, true);
            // The role sees what the unit can do for a group even when the context has no allies.
            double potentialS = _ctx.Allies > 0 ? report.S : Support(def, full, report, _p.TypicalAllies, false);
            report.Dps = raw.Dps;
            report.Ehp = raw.Ehp;
            report.MaxRange = raw.Range;
            report.Speed = raw.Speed;
            report.MoveCategories.AddRange(full.MoveCats);
            report.SelfHealPerSec = full.SelfHealPerSec;
            report.ReferenceDps = _ref.Dps;
            report.ReferenceEhp = _ref.Ehp;
            report.ReferenceSpeed = _ref.Speed;
            double phys = raw.Profile.Where(x => x.Type == "physical").Select(x => x.Ehp).DefaultIfEmpty(raw.Ehp).First();
            foreach (var (t, e) in raw.Profile)
                report.EhpProfile.Add(new EhpLine(t, _db.DamageType(t)?.DisplayName ?? t, e, e / Math.Max(1e-9, phys)));

            report.Role = def.PowerRole != PowerRole.Auto ? def.PowerRole : AutoRole(report, def, potentialS);
            report.RoleAuto = def.PowerRole == PowerRole.Auto;
            var w = _p.Weights(report.Role);
            double norm = Math.Max(1e-9, w.H + w.D + w.R + w.M);
            (double h, double d, double rm) Parts(Raw r) =>
                (w.H / norm * _p.CurveH.Apply(H(r)), w.D / norm * _p.CurveD.Apply(D(r)), w.R / norm * _p.CurveR.Apply(R(r)) + w.M / norm * _p.CurveM.Apply(M(r)));
            var b = Parts(rawBase);
            var f = Parts(raw);
            double control = w.C * report.C;
            double support = w.S * report.S;
            double passives = f.h + f.d + f.rm - (b.h + b.d + b.rm);
            double bonus = CategoryBonus(def);
            double sum = b.h + b.d + b.rm + passives + control + support;
            double manual = sum * bonus / 100;
            // The reference is 1: its own control and support (if any) are part of «1».
            var rw = _p.Weights(PowerRole.Universal);
            double refExtra = rw.C * _ref.Control / Math.Max(1e-9, _p.ControlReference);
            double k = 1 / (1 + refExtra);

            report.Lines.Add(new PowerLine("health", "Здоровье и защита", k * b.h, $"эфф. здоровье {rawBase.Ehp:0} (эталон {_ref.Ehp:0})"));
            report.Lines.Add(new PowerLine("damage", "Урон", k * b.d, $"урон в секунду {rawBase.Dps:0.#} (эталон {_ref.Dps:0.#})"));
            report.Lines.Add(new PowerLine("range", "Дальность и мобильность", k * b.rm, $"дальность {rawBase.Range:0.##}, скорость {rawBase.Speed:0.#}" + (full.MoveCats.Count > 1 ? $", перемещение {string.Join("/", full.MoveCats)}" : "")));
            report.Lines.Add(new PowerLine("passives", "Пассивные способности", k * passives, report.Passives.Count == 0 ? "нет" : string.Join(", ", report.Passives.Select(p => $"{p.Name} {p.Share:P0}"))));
            report.Lines.Add(new PowerLine("control", "Контроль", k * control, $"{raw.Control:0.##} с контроля в секунду"));
            report.Lines.Add(new PowerLine("synergy", "Синергия и поддержка", k * support, _ctx.Allies > 0 ? $"союзников рядом: {_ctx.Allies}" : "союзники не предполагаются (контекст «один»)"));
            if (bonus != 0) report.Lines.Add(new PowerLine("manual", "Ручной вклад категорий", k * manual, $"{bonus:+0;−0} %"));
            report.Power = Math.Max(0, k * (sum + manual));
            report.Lines.Add(new PowerLine("total", "Итог", report.Power, ""));
            report.Level = report.Power <= 0 ? 0 : _p.ScaleFor(report.Role, def.Rank).LevelOf(report.Power);
            return report;
        }

        private PowerRole AutoRole(PowerReport r, UnitDef def, double support)
        {
            if (def.Rank == UnitRank.Boss || def.Boss.IsBoss) return PowerRole.Boss;
            if (support >= 0.4) return PowerRole.Support;
            if (r.C >= 0.6) return PowerRole.Controller;
            if (r.R >= 1.3) return PowerRole.Shooter;
            if (r.M >= 1.3 && r.D >= r.H) return PowerRole.Assassin;
            if (r.H >= 1.6 * Math.Max(0.1, r.D)) return PowerRole.Tank;
            return PowerRole.Fighter;
        }

        private double CategoryBonus(UnitDef def)
        {
            if (def.Id.StartsWith("__", StringComparison.Ordinal)) return 0;
            return CategoryTree.Resolve(_db, def).PowerBonus;
        }

        // ---------------------------------------------------------------- builds

        private Build BaseBuild(UnitDef def)
        {
            var b = new Build();
            foreach (var s in _db.All<StatDef>()) b.Stats[s.Id] = s.Default;
            foreach (var id in new[] { Stats.HpMax, Stats.ApMax, Stats.ApRegen, Stats.MoveSpeed, Stats.CritMult, Stats.BlockReduction })
                if (!b.Stats.ContainsKey(id)) b.Stats[id] = DefaultStat(id);
            foreach (var kv in def.Stats) b.Stats[kv.Key] = kv.Value;
            foreach (var (t, _) in _types)
            {
                b.Incoming[t] = def.Defense.Modifiers.Where(m => _tax.Matches(m.Match, t)).Sum(m => m.Percent);
                if (def.Defense.Immune.Any(m => _tax.Matches(m, t))) b.Incoming[t] = -100;
                b.Outgoing[t] = def.Offense.Bonuses.Where(m => _tax.Matches(m.Match, t)).Sum(m => m.Percent);
            }
            b.MoveCats.Add(def.MoveCategory);
            if (def.Id.StartsWith("__", StringComparison.Ordinal)) return b;
            var cats = CategoryTree.Resolve(_db, def);
            foreach (var mc in cats.MoveCategories) b.MoveCats.Add(mc);
            foreach (var f in cats.Forbids)
                switch (f.Rule.What)
                {
                    case ForbidWhat.BasicAttack: b.NoBasic = true; break;
                    case ForbidWhat.AllSkills: b.NoSkills = true; break;
                    case ForbidWhat.Movement: b.NoMove = true; break;
                    case ForbidWhat.SkillsWithTag: b.ForbiddenSkillTags.Add(f.Rule.SkillTag); break;
                }
            foreach (var mt in def.MoveTypes)
                if (mt.Condition == null || ConditionShare(mt.Condition, def) >= 0.5)
                    b.MoveCats.Add(mt.Category);
            return b;
        }

        /// <summary>The base build plus every passive (template, categories, starting statuses) at its share.</summary>
        private Build FullBuild(UnitDef def, PowerReport? report, Raw? rawBase = null)
        {
            var b = BaseBuild(def);
            if (def.Id.StartsWith("__", StringComparison.Ordinal)) return b;
            rawBase ??= Measure(def, b, _refBuild ?? b);
            foreach (var (p, share, source, why) in PassivesOf(def, rawBase))
            {
                if (share <= 0) continue;
                Apply(b, p, share, def, rawBase, out var effect);
                report?.Passives.Add(new PassiveShare(p.Id, p.DisplayName, share, source, why + (effect != "" ? "; " + effect : "")));
            }
            // Healing itself with its skills (used when wounded: half of what the cycle allows).
            if (!b.NoSkills)
                foreach (var id in def.Skills)
                    if (Skill(_db, id) is { } sk)
                    {
                        double cyc = Math.Max(0.1, Math.Max(Math.Max(CastTime(sk, b), ApCycle(sk, b)), sk.Cooldown));
                        foreach (var e in sk.Stages.SelectMany(st => st.Effects).Where(e => e.Type == EffectType.Heal && (e.Filter.Self || sk.Targeting == TargetingMode.Self)))
                            b.SelfHealPerSec += Amount(e, b) * Math.Max(0, 1 + b.S(Stats.HealPower)) * e.Chance / cyc * 0.5;
                    }
            foreach (var st in def.StartingStatuses)
            {
                var sd = _db.Status(st.Status);
                if (sd == null || sd.Levels.Count == 0) continue;
                double dur = st.Duration > 0 ? st.Duration : sd.Duration;
                double share = sd.Lasts != StatusLasts.Timed || dur <= 0 ? 1 : Math.Min(1, dur / _p.FightSeconds);
                var lvl = sd.Levels[0];
                foreach (var m in lvl.StatMods) StatMod(b, m.Stat, m.Add, m.Mul, share);
                DefenseOffense(b, lvl.Defense, lvl.Offense, share);
            }
            return b;
        }

        /// <summary>Passives of the template and of its categories with their expected share.</summary>
        private IEnumerable<(PassiveDef P, double Share, string Source, string Why)> PassivesOf(UnitDef def, Raw raw)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in def.Passives)
                if (_db.Get<PassiveDef>(id) is { } p && seen.Add(p.Id))
                {
                    var (s, why) = Share(p, def, raw);
                    yield return (p, s, "шаблон", why);
                }
            foreach (var r in CategoryTree.Resolve(_db, def).Of(CategoryRuleKind.Passive))
            {
                double cond = r.Rule.Mode == CategoryInherit.Conditional && r.Rule.Condition != null ? ConditionShare(r.Rule.Condition, def) : 1;
                if (_db.Get<PassiveDef>(r.Rule.Passive) is { } p && seen.Add(p.Id))
                {
                    var (s, why) = Share(p, def, raw);
                    yield return (p, s * cond, $"категория «{r.From.DisplayName}»", why + (cond < 1 ? $", условие категории {cond:P0}" : ""));
                }
                if (r.Otherwise is { Kind: CategoryRuleKind.Passive } other && cond < 1 && _db.Get<PassiveDef>(other.Passive) is { } op && seen.Add(op.Id))
                {
                    var (s, why) = Share(op, def, raw);
                    yield return (op, s * (1 - cond), $"категория «{r.From.DisplayName}» (иначе)", why);
                }
            }
        }

        /// <summary>The expected share of the battle a passive acts (its condition, its trigger).</summary>
        private (double Share, string Why) Share(PassiveDef p, UnitDef def, Raw raw)
        {
            double s = p.When != null ? ConditionShare(p.When, def) : 1;
            string why = p.When != null ? $"условие ≈ {s:P0}" : "всегда";
            if (p.Condition != null)
            {
                s *= 0.5;
                why += ", условие сценария ≈ 50 %";
            }
            if (p.Trigger == PassiveTrigger.Always) return (s, why);
            double f = Frequency(p, raw);
            if (p.ActiveSeconds > 0)
            {
                double t = Math.Min(1, f * p.ActiveSeconds) * p.Chance;
                return (s * t, $"срабатывает ≈ {f:0.##}/с на {p.ActiveSeconds:0.#} с");
            }
            return (s, $"срабатывает ≈ {f:0.##}/с");
        }

        /// <summary>How often a trigger happens per second in a typical battle.</summary>
        private double Frequency(PassiveDef p, Raw raw)
        {
            double f = p.Trigger switch
            {
                PassiveTrigger.OnDamaged => 1 / Math.Max(0.2, 1 / Math.Max(1e-9, _ref.HitsPerSec)),
                PassiveTrigger.OnAttackNoticed => 0.8,
                PassiveTrigger.OnHitEnemy => raw.HitsPerSec,
                PassiveTrigger.OnKill => 0.05,
                PassiveTrigger.OnAllyDied => 0.03,
                PassiveTrigger.OnLowHp => p.HpThreshold / _p.FightSeconds,
                PassiveTrigger.Periodic => 1 / Math.Max(0.05, p.Interval),
                PassiveTrigger.OnEnemySpotted => 0.05,
                PassiveTrigger.OnCrit => raw.HitsPerSec * 0.15,
                PassiveTrigger.OnMoved => 0.5,
                PassiveTrigger.OnSkillUsed => raw.CastsPerSec,
                PassiveTrigger.OnHpLost => _ref.Dps / Math.Max(1, p.HpLoss),
                PassiveTrigger.OnEnemyEnteredArea => 0.1,
                _ => 0,
            };
            f *= p.Chance;
            if (p.Cooldown > 0) f = Math.Min(f, 1 / p.Cooldown);
            if (p.MaxTriggers > 0) f = Math.Min(f, p.MaxTriggers / _p.FightSeconds);
            return f;
        }

        /// <summary>The share of the battle a unit condition holds (static ones answered exactly).</summary>
        public double ConditionShare(UnitConditionDef c, UnitDef def)
        {
            switch (c.Type)
            {
                case UnitConditionType.Always:
                case UnitConditionType.TargetAlive:
                    return 1;
                case UnitConditionType.All:
                    return c.Children.Aggregate(1.0, (a, x) => a * ConditionShare(x, def));
                case UnitConditionType.Any:
                    return 1 - c.Children.Aggregate(1.0, (a, x) => a * (1 - ConditionShare(x, def)));
                case UnitConditionType.Not:
                    return 1 - c.Children.Aggregate(1.0, (a, x) => a * ConditionShare(x, def));
                case UnitConditionType.HpFraction:
                    return Math.Clamp(c.Compare == CompareMode.AtMost ? c.Value : 1 - c.Value, 0, 1);
                case UnitConditionType.UnitType:
                case UnitConditionType.Category:
                {
                    var types = c.Types.Concat(c.Categories).ToList();
                    if (c.Subject == ConditionSubject.Self) return types.Any(t => CategoryTree.Matches(_db, def, t)) ? 1 : 0;
                    if (_ctx.Kind == PowerContextKind.VsCategory && _ctx.VsCategory != "") return types.Contains(_ctx.VsCategory) ? 1 : 0;
                    return 0.5;
                }
                case UnitConditionType.Layer:
                    return c.Subject == ConditionSubject.Self ? (LayerResolver.LayerIdOf(_db, def) == c.Layer ? 1 : 0) : 0.5;
                case UnitConditionType.Environment:
                {
                    var v = _ctx.Scenario?.Environment.FirstOrDefault(e => e.Name == c.Variable);
                    return v == null ? 0.5 : v.Value == c.VariableValue ? 0.75 : 0.25;
                }
                case UnitConditionType.UnitsInArea:
                    return c.Side == UnitSideFilter.Allies ? (_ctx.Allies >= c.Count ? 0.8 : 0.1) : 0.5;
                case UnitConditionType.HasStatus:
                case UnitConditionType.OnTerrain:
                    return 0.35;
                case UnitConditionType.AfterSkill:
                    return 0.4;
                case UnitConditionType.PreviousHit:
                case UnitConditionType.PreviousEffect:
                    return 0.7;
                default:
                    return 0.5;
            }
        }

        private static void StatMod(Build b, string stat, double add, double mul, double share)
        {
            if (string.IsNullOrEmpty(stat)) return;
            double v = b.S(stat) + add * share;
            b.Stats[stat] = v * (1 + mul * share);
        }

        private void DefenseOffense(Build b, DefenseDef d, OffenseDef o, double share)
        {
            foreach (var (t, _) in _types)
            {
                b.Incoming[t] = b.Incoming.GetValueOrDefault(t) + share * d.Modifiers.Where(m => _tax.Matches(m.Match, t)).Sum(m => m.Percent);
                if (d.Immune.Any(m => _tax.Matches(m, t))) b.Incoming[t] = b.Incoming.GetValueOrDefault(t) - 100 * share;
                b.Outgoing[t] = b.Outgoing.GetValueOrDefault(t) + share * o.Bonuses.Where(m => _tax.Matches(m.Match, t)).Sum(m => m.Percent);
            }
        }

        /// <summary>A passive's modifiers and actions on the build, at a share.</summary>
        private void Apply(Build b, PassiveDef p, double share, UnitDef def, Raw raw, out string effect)
        {
            effect = "";
            // An aura for others is synergy (counted in S); on the owner only if it includes it.
            bool self = p.AuraRadius <= 0 || p.AuraIncludesSelf;
            if (self)
            {
                foreach (var m in p.StatMods) StatMod(b, m.Stat, m.Add, m.Mul, share);
                DefenseOffense(b, p.Defense, p.Offense, share);
                if (p.Trigger == PassiveTrigger.Always || p.ActiveSeconds > 0)
                    foreach (var m in p.Modifiers) Modifier(b, m, share, def);
            }
            if (p.Trigger == PassiveTrigger.Always || p.Action == PassiveAction.None) return;
            double f = Frequency(p, raw) * (p.When != null ? ConditionShare(p.When, def) : 1);
            switch (p.Action)
            {
                case PassiveAction.EffectsOnSelf:
                    foreach (var e in p.Effects)
                        if (e.Type == EffectType.Heal) b.SelfHealPerSec += Amount(e, b) * f;
                        else if (e.Type == EffectType.Shield) b.SelfShield += Amount(e, b) * Math.Min(1, f * _p.FightSeconds);
                    effect = $"на себя ×{f:0.##}/с";
                    break;
                case PassiveAction.EffectsOnSource:
                    b.ExtraDps += p.Effects.Where(e => e.Type == EffectType.Damage).Sum(e => Amount(e, b)) * f;
                    effect = $"урон по источнику ×{f:0.##}/с";
                    break;
                case PassiveAction.CounterAttack:
                    b.ExtraDps += raw.AvgHit * f;
                    effect = $"контратака ×{f:0.##}/с";
                    break;
                case PassiveAction.UseSkillOnSource when Skill(_db, p.Skill) is { } s:
                    b.ExtraDps += CastDamage(s, b, _refBuild ?? b, out _, out _) * f;
                    effect = $"«{s.DisplayName}» ×{f:0.##}/с";
                    break;
            }
        }

        private void Modifier(Build b, ModifierDef m, double share, UnitDef def)
        {
            double v = m.Value;
            if (m.PerEach is { } pe)
            {
                int n = CountFor(pe, def);
                v *= n;
                if (pe.MaxValue > 0) v = Math.Sign(v) * Math.Min(Math.Abs(v), pe.MaxValue);
            }
            switch (m.Target)
            {
                case ModifierTarget.Stat:
                    if (m.Mode == ModifierMode.Multiplier) b.Stats[m.Stat] = b.S(m.Stat) * (1 + share * (m.Value - 1));
                    else if (m.Mode is ModifierMode.FlatBefore or ModifierMode.FlatAfter) b.Stats[m.Stat] = b.S(m.Stat) + share * v;
                    else b.Stats[m.Stat] = b.S(m.Stat) * (1 + share * v / 100);
                    break;
                case ModifierTarget.IncomingDamage:
                    foreach (var (t, _) in _types)
                        if (_tax.Matches(m.Match == "" ? "*" : m.Match, t))
                            b.Incoming[t] = b.Incoming.GetValueOrDefault(t) + share * (m.VsTypes.Count > 0 ? 0.5 : 1) * v;
                    break;
                case ModifierTarget.OutgoingDamage:
                    foreach (var (t, _) in _types)
                        if (_tax.Matches(m.Match == "" ? "*" : m.Match, t))
                            b.Outgoing[t] = b.Outgoing.GetValueOrDefault(t) + share * (m.VsTypes.Count > 0 || m.Skill != "" || m.SkillTag != "" ? 0.5 : 1) * v;
                    break;
                case ModifierTarget.HitPower:
                    b.HitPower += share * (m.Skill != "" || m.SkillTag != "" || m.HitTag != "" ? 0.5 : 1) * v;
                    break;
                case ModifierTarget.HitCount:
                    b.HitCount += share * v;
                    break;
                case ModifierTarget.SkillCost:
                    b.SkillCost += share * v;
                    break;
                case ModifierTarget.Immunity when m.Status == "":
                    foreach (var (t, _) in _types)
                        if (_tax.Matches(m.Match == "" ? "*" : m.Match, t))
                            b.Incoming[t] = b.Incoming.GetValueOrDefault(t) - 100 * share;
                    break;
                case ModifierTarget.Absorption:
                    b.Absorb += share * v;
                    break;
                case ModifierTarget.Reflection:
                    b.Reflect += share * v;
                    break;
            }
        }

        /// <summary>«За каждого»: allies of the context that match, capped.</summary>
        private int CountFor(PerEachDef pe, UnitDef def)
        {
            if (pe.Side == CountSide.Enemies) return Math.Min(pe.MaxCount > 0 ? pe.MaxCount : 99, 2);
            var allies = _ctx.AllyDefs.Count > 0 ? _ctx.AllyDefs : Enumerable.Repeat(def, _ctx.Allies).ToList();
            int n = allies.Count(a => pe.Types.Count == 0 || pe.Types.Any(t => CategoryTree.Matches(_db, a, t)));
            if (pe.IncludeSelf && (pe.Types.Count == 0 || pe.Types.Any(t => CategoryTree.Matches(_db, def, t)))) n++;
            if (pe.MaxCount > 0) n = Math.Min(n, pe.MaxCount);
            return n;
        }

        // ---------------------------------------------------------------- measuring

        private double Amount(SkillEffectDef e, Build b)
        {
            double a = e.Amount;
            if (!string.IsNullOrEmpty(e.ScalingStat)) a += b.S(e.ScalingStat) * e.ScalingFactor;
            return Math.Max(0, a);
        }

        private double HitChance(double acc, double eva)
        {
            var c = _db.Rules.Combat;
            return Math.Clamp(c.BaseHit + acc - eva, c.MinHit, Math.Max(c.MinHit, c.MaxHit));
        }

        private int Targets(AreaDef a, bool allies = false)
        {
            if (a.Shape == AreaShape.Chain) return 1 + a.ChainJumps;
            if (a.Shape == AreaShape.Global) return 6;
            return Math.Max(1, AreaResolver.CanonicalOffsets(a).Count);
        }

        /// <summary>Expected enemies hit by an area: diminishing with its size (enemies do not fill a big area).</summary>
        private double Expected(AreaDef a) => a.Shape == AreaShape.Chain ? 1 + a.ChainJumps * 0.6 : 1 + Math.Pow(Math.Max(0, Targets(a) - 1), 0.7) * _p.AreaDensity;

        /// <summary>A status a launch applies: duration (timed) or points (accumulating), per target and in how many targets.</summary>
        private sealed record StatusApp(StatusDef Status, double Amount, double Scale, double Targets);

        private double CastTime(SkillDef s, Build b) =>
            s.PrepTime / Math.Max(0.1, 1 + b.S(Stats.CastSpeed)) + s.RecoveryTime + s.Stages.Sum(st => st.Delay + Math.Max(0, st.Repeat - 1) * st.RepeatInterval);

        private double ApCycle(SkillDef s, Build b) => s.ApCost * Math.Max(0, 1 + b.SkillCost / 100) / Math.Max(0.05, b.S(Stats.ApRegen));

        /// <summary>The damage of one launch of a skill against a target build (every hit of the series, area, hit chance, crits).</summary>
        private double CastDamage(SkillDef s, Build b, Build target, out double hits, out double control, List<StatusApp>? apps = null)
        {
            hits = 0;
            control = 0;
            double total = 0;
            double crit = s.CanCrit && !s.CritNoExtraDamage ? 1 + Math.Clamp(b.S(Stats.CritChance) + s.CritChanceBonus, 0, 1) * Math.Max(0, b.S(Stats.CritMult) - 1) : 1;
            double refHit = Math.Max(1, _refHit);
            int stages = Math.Max(1, s.Stages.Count);
            foreach (var st in s.Stages)
            {
                int rep = Math.Max(1, st.Repeat);
                double cond = st.Condition != null ? 0.8 : 1;
                double hit = s.HitMode == HitMode.AlwaysHit && st.HitMode != StageHitMode.Roll || st.HitMode == StageHitMode.AlwaysHit ? 1 : HitChance(b.S(Stats.Accuracy) + st.AccuracyBonus, target.S(Stats.Evasion));
                double tg = Expected(st.Area);
                foreach (var e in st.Effects)
                {
                    if (!e.Filter.Enemies) continue;
                    double resist = Math.Max(0, 1 - target.S(Stats.ControlResist));
                    switch (e.Type)
                    {
                        case EffectType.Damage:
                        {
                            string type = st.DamageType != "" ? st.DamageType : e.DamageType;
                            double a = (Amount(e, b) * st.DamageMultiplier + st.FlatBonus) * e.Chance;
                            a *= Math.Max(0, 1 + (b.Outgoing.GetValueOrDefault(type, OutgoingAny(b, type))) / 100) * Math.Max(0, 1 + b.HitPower / 100);
                            // Against the target: its resistance and its armor against this hit.
                            a *= Math.Max(0, 1 + Math.Clamp(target.Incoming.GetValueOrDefault(type, 0), -100, 500) / 100);
                            if (_db.DamageType(type)?.ArmorApplies != false && target.S(Stats.Armor) > 0)
                                a = Math.Max(a - target.S(Stats.Armor), a * _db.Rules.Combat.MinArmorFraction);
                            total += a * rep * hit * crit * tg * cond;
                            hits += rep * hit * cond;
                            break;
                        }
                        case EffectType.ApplyStatus when _db.Status(e.Status) is { } sd && ControlWeight(sd) > 0:
                        {
                            double amount = sd.Kind == StatusKind.Accumulating ? e.Amount * rep : (e.Duration > 0 ? e.Duration : sd.Duration);
                            apps?.Add(new StatusApp(sd, amount, e.Chance * st.StatusChanceMultiplier * hit * cond * resist, tg));
                            break;
                        }
                        case EffectType.ModifyAccumulation when _db.Status(e.Status) is { Kind: StatusKind.Accumulating } sd && ControlWeight(sd) > 0:
                            apps?.Add(new StatusApp(sd, e.Amount * rep, e.Chance * hit * cond * resist, tg));
                            break;
                        case EffectType.Push:
                        case EffectType.Pull:
                            control += 0.3 * e.Chance * hit * tg * resist;
                            break;
                    }
                }
            }
            // «Количество ударов» adds hits to every stage of the series.
            if (b.HitCount != 0 && hits > 0) total *= Math.Max(0, 1 + b.HitCount / stages);
            return total;
        }

        private double _refHit = 10;

        private double OutgoingAny(Build b, string type) => b.Outgoing.Where(kv => _tax.Matches(kv.Key, type)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();

        private static double ControlWeight(StatusDef sd) => sd.Negative ? sd.Levels.Select(LevelWeight).DefaultIfEmpty(0).Max() : 0;

        /// <summary>How much a level of a status controls: stun 1, fear 0.7, root 0.6, silence 0.5, blind 0.4, slows by their share.</summary>
        private static double LevelWeight(StatusLevelDef l)
        {
            double w = 0;
            {
                var f = l.Flags;
                double x = 0;
                if (f.MindControl) x = Math.Max(x, 1.2);
                if (f.BlocksActions) x = Math.Max(x, 1);
                if (f.Fear) x = Math.Max(x, 0.7);
                if (f.BlocksMovement) x = Math.Max(x, 0.6);
                if (f.BlocksSkills || f.BlocksAttacks) x = Math.Max(x, 0.5);
                if (f.Blind) x = Math.Max(x, 0.4);
                foreach (var m in l.StatMods.Where(m => m.Stat == Stats.MoveSpeed && m.Mul < 0)) x = Math.Max(x, Math.Min(0.6, -m.Mul * 0.6));
                w = Math.Max(w, x);
            }
            return w;
        }

        private IEnumerable<SkillDef> Attacks(UnitDef def, Build b, out SkillDef? basic)
        {
            basic = b.NoBasic ? null : Skill(_db, def.BasicAttack);
            var list = new List<SkillDef>();
            if (!b.NoSkills)
                foreach (var id in def.Skills)
                    if (Skill(_db, id) is { } s && s.IsHarmful && !s.Tags.Any(b.ForbiddenSkillTags.Contains))
                        list.Add(s);
            return list;
        }

        public Raw Measure(UnitDef def, Build b, Build target)
        {
            var raw = new Raw();
            // Effective health: what one unit of the typical mix of damage takes from health.
            double hp = Math.Max(1, b.S(Stats.HpMax)) + b.SelfShield + b.SelfHealPerSec * _p.FightSeconds;
            double hit = HitChance(target.S(Stats.Accuracy), b.S(Stats.Evasion));
            double block = 1 - Math.Clamp(b.S(Stats.BlockChance), 0, 0.95) * Math.Clamp(b.S(Stats.BlockReduction), 0, 1);
            double absorb = 1 - Math.Clamp(b.Absorb / 100, 0, 0.9) * 0.5;
            double refHit = Math.Max(1, _refHit);
            double mix = 0, wsum = 0;
            foreach (var (t, w) in _types)
            {
                double res = Math.Max(0.02, 1 + Math.Clamp(b.Incoming.GetValueOrDefault(t), -100, 500) / 100);
                double armor = _db.DamageType(t)?.ArmorApplies != false && b.S(Stats.Armor) > 0
                    ? Math.Max(refHit - b.S(Stats.Armor), refHit * _db.Rules.Combat.MinArmorFraction) / refHit : 1;
                double m = res * armor * hit * block * absorb;
                raw.Profile.Add((t, hp / Math.Max(1e-6, m)));
                mix += w * m;
                wsum += w;
            }
            double cr = Math.Clamp(b.S(Stats.ControlResist), 0, 1);
            raw.Ehp = hp / Math.Max(1e-6, mix / Math.Max(1e-9, wsum)) * (1 + 0.25 * cr);

            // Sustained damage: the basic attack, plus what the skills add over it in their cycles.
            var skills = Attacks(def, b, out var basic).ToList();
            double basicDps = 0, hitsPerSec = 0, castsPerSec = 0, control = 0, avgHit = 0;
            var applied = new List<(StatusApp App, double Uses)>();
            SkillDef? main = basic;
            if (main == null && skills.Count > 0)
                main = skills.OrderByDescending(s => CastDamage(s, b, target, out _, out _) / Math.Max(CastTime(s, b), ApCycle(s, b))).First();
            if (main != null)
            {
                var apps = new List<StatusApp>();
                double dmg = CastDamage(main, b, target, out var hh, out var cc, apps);
                double cyc = Math.Max(0.1, Math.Max(CastTime(main, b), ApCycle(main, b)));
                if (main != basic) cyc = Math.Max(cyc, main.Cooldown);
                basicDps = dmg / cyc;
                hitsPerSec = hh / cyc;
                castsPerSec = 1 / cyc;
                control += cc / cyc;
                avgHit = hh > 0 ? dmg / hh : dmg;
                applied.AddRange(apps.Select(x => (x, 1 / cyc)));
            }
            double dps = basicDps;
            // Every other skill: its damage over the basic attack in its cycle; its control even if it does no damage.
            var others = b.NoSkills ? new List<SkillDef>() : def.Skills.Select(id => Skill(_db, id)).Where(x => x != null && x != main && !x.Tags.Any(b.ForbiddenSkillTags.Contains)).Select(x => x!).ToList();
            foreach (var s in others)
            {
                var apps = new List<StatusApp>();
                double dmg = CastDamage(s, b, target, out var hh, out var cc, apps);
                if (dmg <= 0 && apps.Count == 0 && cc <= 0) continue;
                double busy = Math.Max(CastTime(s, b), ApCycle(s, b));
                double cyc = Math.Max(0.1, Math.Max(busy, s.Cooldown));
                double uses = 1 / cyc;
                foreach (var rc in s.ResourceCosts)
                {
                    var res = def.Resources.FirstOrDefault(r => r.Resource == rc.Resource);
                    double perSec = res == null ? 0 : Math.Max(0, res.Regen) + res.Start / _p.FightSeconds;
                    uses = Math.Min(uses, rc.Amount > 0 ? perSec / rc.Amount : uses);
                }
                if (s.PrepTime > 0.5) dmg *= 1 - Math.Min(0.3, (s.PrepTime - 0.5) * 0.15);
                if (skills.Contains(s))
                {
                    dps += Math.Max(0, dmg - basicDps * busy) * uses;
                    castsPerSec += uses;
                    hitsPerSec += hh * uses;
                }
                control += cc * uses;
                applied.AddRange(apps.Select(x => (x, uses)));
            }
            raw.Dps = dps + b.ExtraDps + b.Reflect / 100 * _refDpsGuess * 0.5;
            raw.HitsPerSec = hitsPerSec;
            raw.CastsPerSec = castsPerSec;
            raw.AvgHit = avgHit;
            raw.Control = control + StatusControl(applied);

            // Range by zones; mobility by speed and ways of moving.
            int range = 1;
            bool indirect = false;
            foreach (var s in (basic != null ? skills.Prepend(basic) : skills))
            {
                int r = s.Targeting is TargetingMode.Unit or TargetingMode.Cell ? s.RangeMax : Math.Max(1, s.Stages.Select(st => st.Area.Size).DefaultIfEmpty(1).Max());
                range = Math.Max(range, r);
                if (r > 1 && !s.RequiresLoS) indirect = true;
            }
            raw.Range = Zone(range) * (indirect ? 1.1 : 1);
            raw.Speed = b.NoMove ? 0 : Math.Max(0, b.S(Stats.MoveSpeed));
            double m0 = raw.Speed / Math.Max(0.1, _refSpeed);
            double extra = 0;
            foreach (var mc in b.MoveCats)
                extra += mc switch { "fly" => 0.3, "burrow" => 0.15, "jump" => 0.1, "swim" => 0.05, _ => 0 };
            if (def.Skills.Select(id => Skill(_db, id)).Any(s => s != null && s.Stages.Any(st => st.Effects.Any(e => e.Type == EffectType.TeleportCaster)))) extra += 0.15;
            raw.Mobility = Math.Max(0.05, m0 + extra);
            if (def == _refDef)
            {
                _refHit = Math.Max(1, avgHit);
                _refSpeed = Math.Max(0.1, raw.Speed);
                _refDpsGuess = raw.Dps;
            }
            return raw;
        }

        /// <summary>
        /// Control seconds per second of the statuses applied: a timed one — its duration × uses per target (at most
        /// all the time); an accumulating one — by the balance of points in and decay out: a level is reached only if the
        /// points come faster than they decay, after θ / (in − out) seconds, and holds for the rest of the battle.
        /// </summary>
        private double StatusControl(List<(StatusApp App, double Uses)> applied)
        {
            double total = 0;
            foreach (var g in applied.GroupBy(x => x.App.Status))
            {
                var sd = g.Key;
                double targets = g.Average(x => x.App.Targets);
                if (sd.Kind != StatusKind.Accumulating)
                {
                    double uptime = Math.Min(1, g.Sum(x => x.App.Amount * x.App.Scale * x.Uses));
                    total += ControlWeight(sd) * uptime * targets;
                    continue;
                }
                double inflow = g.Sum(x => x.App.Amount * x.App.Scale * x.Uses);
                double best = 0;
                foreach (var l in sd.Levels)
                {
                    double w = LevelWeight(l);
                    if (w <= 0 || l.Threshold > sd.MaxPoints) continue;
                    double net = inflow - sd.DecayPerSecond;
                    if (net <= 0) continue;
                    double share = Math.Max(0, 1 - l.Threshold / net / _p.FightSeconds);
                    best = Math.Max(best, w * share);
                }
                total += best * targets;
            }
            return total;
        }

        private double _refSpeed = 2;
        private double _refDpsGuess = 20;

        private double Zone(int range)
        {
            var pts = _p.RangeZones.OrderBy(z => z.Level).ToList();
            double f = 1;
            foreach (var z in pts)
                if (range >= z.Level)
                    f = z.Power;
            return f;
        }

        /// <summary>Support and synergies: healing and shields of allies, auras, own summons — in ally-equivalents.</summary>
        private double Support(UnitDef def, Build b, PowerReport report, int allies, bool notes)
        {
            double s = 0;
            double refDps = Math.Max(1e-6, _ref.Dps);
            foreach (var id in b.NoSkills ? new List<string>() : def.Skills)
            {
                var sk = Skill(_db, id);
                if (sk == null) continue;
                double cyc = Math.Max(0.1, Math.Max(Math.Max(CastTime(sk, b), ApCycle(sk, b)), sk.Cooldown));
                foreach (var st in sk.Stages)
                foreach (var e in st.Effects)
                {
                    int tg = Targets(st.Area);
                    // Allies the effect reaches in this context: one for a single target, about half the area else.
                    int reach = !e.Filter.Allies || allies == 0 ? 0 : tg <= 1 ? 1 : Math.Min(allies, 1 + (int)Math.Round((tg - 1) * 0.5));
                    switch (e.Type)
                    {
                        case EffectType.Heal:
                        {
                            double heal = Amount(e, b) * Math.Max(0, 1 + b.S(Stats.HealPower)) * e.Chance / cyc;
                            if (reach > 0) s += heal * reach / refDps * 0.5;
                            break;
                        }
                        case EffectType.Shield:
                            if (reach > 0) s += Amount(e, b) * Math.Max(0, 1 + b.S(Stats.ShieldPower)) * e.Chance / cyc * reach / refDps * 0.4;
                            break;
                        case EffectType.ApplyStatus when _db.Status(e.Status) is { Negative: false } sd && reach > 0:
                            s += 0.15 * Math.Min(1, (e.Duration > 0 ? e.Duration : sd.Duration) / cyc) * reach;
                            break;
                        case EffectType.Summon when _depth < 1 && _db.Unit(e.SummonUnit) is { } su:
                        {
                            var sp = new Evaluator(_db, PowerContext.Solo, _depth + 1).Run(su).Power;
                            double life = e.Duration > 0 ? Math.Min(1, e.Duration / cyc) : 1;
                            s += sp * Math.Max(1, e.Level) * life;
                            if (notes) report.Notes.Add($"призыв «{su.DisplayName}» (сила {sp:0.##}) учтён в синергии");
                            break;
                        }
                    }
                }
            }
            // Auras for others: what the reference ally gains, times the allies in reach of the aura.
            if (allies > 0)
                foreach (var ps in report.Passives)
                {
                    var p = _db.Get<PassiveDef>(ps.Id);
                    if (p == null || p.AuraRadius <= 0 || p.AuraAffects == AuraAffects.Enemies) continue;
                    var allyDefs = _ctx.AllyDefs.Count > 0 && allies == _ctx.Allies ? _ctx.AllyDefs : Enumerable.Repeat(def, allies).ToList();
                    int n = allyDefs.Count(a => p.AuraTypes.Count == 0 || p.AuraTypes.Any(t => CategoryTree.Matches(_db, a, t)));
                    if (n == 0) continue;
                    var rb = _refBuild.Clone();
                    var gives = p.AuraPassives.Count > 0 ? p.AuraPassives.Select(id => _db.Get<PassiveDef>(id)).Where(x => x != null).Select(x => x!).ToList() : new List<PassiveDef> { p };
                    foreach (var g in gives)
                    {
                        foreach (var m in g.StatMods) StatMod(rb, m.Stat, m.Add, m.Mul, 1);
                        DefenseOffense(rb, g.Defense, g.Offense, 1);
                        foreach (var m in g.Modifiers) Modifier(rb, m, 1, _refDef);
                    }
                    var after = Measure(_refDef, rb, _refBuild);
                    double gain = 0.5 * (after.Ehp / Math.Max(1e-9, _ref.Ehp) - 1) + 0.5 * (after.Dps / Math.Max(1e-9, _ref.Dps) - 1);
                    if (gain > 0) s += gain * n * ps.Share;
                }
            return s;
        }
    }
}
