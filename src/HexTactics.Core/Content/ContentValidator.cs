using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HexTactics.Core.Expr;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>
/// Checks ids, references, numeric ranges and semantic rules. Never silently fixes anything:
/// it reports issues. Skills with errors are added to <see cref="ContentDatabase.DisabledSkills"/>.
/// </summary>
public static class ContentValidator
{
    private static readonly Regex IdRegex = new("^[A-Za-z0-9_.\\-]+$", RegexOptions.Compiled);

    public static List<ContentIssue> Validate(ContentDatabase db)
    {
        var issues = new List<ContentIssue>();
        db.DisabledSkills.Clear();

        foreach (var d in db.AllDefs())
        {
            if (!IdRegex.IsMatch(d.Id))
                issues.Add(Err(d, "Id", $"Недопустимые символы в ID '{d.Id}' (разрешены латиница, цифры, _ . -)"));
        }

        // References.
        ReferenceWalker.Walk(db, (owner, path, cat, value, _, attr) =>
        {
            if (string.IsNullOrEmpty(value))
            {
                if (attr != null && !attr.AllowEmpty)
                    issues.Add(new ContentIssue(IssueSeverity.Error, Src(owner), path, $"Не указана ссылка ({CatName(cat)})"));
                return;
            }
            if (attr != null && attr.AllowTags && value.StartsWith("tag:", StringComparison.Ordinal)) return;
            if (!db.Exists(cat, value))
                issues.Add(new ContentIssue(IssueSeverity.Error, Src(owner), path, $"Ссылка на несуществующий объект {CatName(cat)} '{value}'"));
        });

        // Numeric ranges from [EdRange].
        foreach (var d in db.AllDefs()) CheckRanges(d, d, d.Id, issues, 0);
        CheckRanges(null, db.Rules, "rules", issues, 0);

        CheckRules(db, issues);
        foreach (var t in db.All<TerrainDef>()) CheckTerrain(db, t, issues);
        foreach (var u in db.All<UnitDef>()) CheckUnit(db, u, issues);
        foreach (var s in db.All<StatusDef>()) CheckStatus(s, issues);
        foreach (var c in db.All<CellEffectDef>()) CheckCellEffect(c, issues);
        foreach (var s in db.All<SynergyDef>()) CheckSynergy(db, s, issues);
        foreach (var s in db.All<SkillDef>()) CheckSkill(db, s, issues);
        foreach (var m in db.All<MapDef>()) CheckMap(db, m, issues);
        foreach (var s in db.All<ScenarioDef>()) CheckScenario(db, s, issues);

        // Formulas anywhere.
        foreach (var d in db.AllDefs()) CheckFormulas(d, d, d.Id, issues, 0);
        CheckFormulas(null, db.Rules, "rules", issues, 0);

        // Disable skills that have errors.
        foreach (var i in issues.Where(i => i.Severity == IssueSeverity.Error))
        {
            var skillId = SkillOfSource(i.Source);
            if (skillId != null && db.Skill(skillId) != null && !db.DisabledSkills.ContainsKey(skillId))
                db.DisabledSkills[skillId] = i.Message;
        }
        foreach (var kv in db.DisabledSkills)
            issues.Add(new ContentIssue(IssueSeverity.Warning, "Skill:" + kv.Key, "", "Навык отключён до исправления ошибок: " + kv.Value));

        return issues;
    }

    private static string? SkillOfSource(string src) => src.StartsWith("Skill:", StringComparison.Ordinal) ? src.Substring(6) : null;

    private static string Src(Def? d) => d == null ? "Правила" : $"{d.Category}:{d.Id}";

    private static ContentIssue Err(Def d, string path, string msg) => new(IssueSeverity.Error, Src(d), path, msg);
    private static ContentIssue Warn(Def d, string path, string msg) => new(IssueSeverity.Warning, Src(d), path, msg);

    public static string CatName(DefCategory c) => c switch
    {
        DefCategory.Terrain => "местность",
        DefCategory.Stat => "характеристика",
        DefCategory.Resource => "ресурс",
        DefCategory.DamageType => "тип урона",
        DefCategory.Faction => "фракция",
        DefCategory.Unit => "юнит",
        DefCategory.Skill => "навык",
        DefCategory.Passive => "пассивная способность",
        DefCategory.CellEffect => "эффект клетки",
        DefCategory.Synergy => "синергия",
        DefCategory.Status => "состояние",
        DefCategory.AiProfile => "профиль ИИ",
        DefCategory.Map => "карта",
        DefCategory.Scenario => "сценарий",
        DefCategory.MoveCategory => "категория движения",
        _ => c.ToString(),
    };

    private static void CheckRanges(Def? owner, object obj, string path, List<ContentIssue> issues, int depth)
    {
        if (depth > 16) return;
        foreach (var prop in ReferenceWalker.PropsOf(obj.GetType()))
        {
            object? value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch
            {
                continue;
            }
            if (value == null) continue;
            if (!EdUtil.IsShown(obj, prop)) continue;
            var range = prop.GetCustomAttribute<EdRangeAttribute>();
            string p = path + "." + prop.Name;
            if (range != null)
            {
                double? num = value switch
                {
                    int i => i,
                    double dd => dd,
                    float f => f,
                    long l => l,
                    _ => null,
                };
                if (num is double n && (double.IsNaN(n) || n < range.Min || n > range.Max))
                    issues.Add(new ContentIssue(IssueSeverity.Error, Src(owner), p, $"Значение {n} вне диапазона [{range.Min}; {range.Max}]"));
                continue;
            }
            if (value is double dv && !double.IsFinite(dv))
            {
                issues.Add(new ContentIssue(IssueSeverity.Error, Src(owner), p, "Некорректное число"));
                continue;
            }
            if (value is string) continue;
            if (value is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is { } item && item.GetType().IsClass && item is not string)
                        CheckRanges(owner, item, $"{p}[{i}]", issues, depth + 1);
                continue;
            }
            if (value.GetType().IsClass && value.GetType().Namespace?.StartsWith("HexTactics.Core.Content") == true)
                CheckRanges(owner, value, p, issues, depth + 1);
        }
    }

    private static void CheckFormulas(Def? owner, object obj, string path, List<ContentIssue> issues, int depth)
    {
        if (depth > 16) return;
        foreach (var prop in ReferenceWalker.PropsOf(obj.GetType()))
        {
            object? value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch
            {
                continue;
            }
            if (value == null) continue;
            if (!EdUtil.IsShown(obj, prop)) continue;
            string p = path + "." + prop.Name;
            var f = prop.GetCustomAttribute<EdFormulaAttribute>();
            if (f != null && value is string s)
            {
                if (s.Trim().Length > 0 && !SafeExpression.TryParse(s, f.Variables, out _, out var err))
                    issues.Add(new ContentIssue(IssueSeverity.Error, Src(owner), p, "Ошибка в формуле: " + err));
                continue;
            }
            if (value is string) continue;
            if (value is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is { } item && item.GetType().IsClass && item is not string)
                        CheckFormulas(owner, item, $"{p}[{i}]", issues, depth + 1);
                continue;
            }
            if (value.GetType().IsClass && value.GetType().Namespace?.StartsWith("HexTactics.Core.Content") == true)
                CheckFormulas(owner, value, p, issues, depth + 1);
        }
    }

    private static void CheckRules(ContentDatabase db, List<ContentIssue> issues)
    {
        var r = db.Rules;
        if (r.Time.SpeedPresets.Count == 0)
            issues.Add(new ContentIssue(IssueSeverity.Error, "Правила", "Time.SpeedPresets", "Нужна хотя бы одна скорость"));
        if (r.Time.SpeedPresets.Any(s => s <= 0 || s > 64))
            issues.Add(new ContentIssue(IssueSeverity.Error, "Правила", "Time.SpeedPresets", "Скорость должна быть в (0; 64]"));
        if (r.Time.DefaultSpeedIndex >= r.Time.SpeedPresets.Count)
            issues.Add(new ContentIssue(IssueSeverity.Warning, "Правила", "Time.DefaultSpeedIndex", "Индекс скорости вне списка — будет использована первая"));
        if (r.Detection.Sectors.Select(s => s.Sector).Distinct().Count() != 4)
            issues.Add(new ContentIssue(IssueSeverity.Warning, "Правила", "Detection.Sectors", "Должны быть заданы все 4 сектора (недостающие = 0%)"));
        if (r.Move.MovementCategories.Count == 0)
            issues.Add(new ContentIssue(IssueSeverity.Error, "Правила", "Move.MovementCategories", "Нужна хотя бы одна категория движения"));
        foreach (var stat in Stats.All)
            if (db.Stat(stat) == null)
                issues.Add(new ContentIssue(IssueSeverity.Warning, "Правила", "stats", $"Нет определения базовой характеристики '{stat}' — значение по умолчанию 0"));
    }

    private static void CheckTerrain(ContentDatabase db, TerrainDef t, List<ContentIssue> issues)
    {
        if (t.DestructibleHp > 0 && string.IsNullOrEmpty(t.DestroyedTerrain))
            issues.Add(Err(t, "DestroyedTerrain", "Разрушаемая местность должна указывать, во что превращается"));
        if (t.DestroyedTerrain == t.Id && t.Id != "")
            issues.Add(Err(t, "DestroyedTerrain", "Местность не может разрушаться сама в себя"));
    }

    private static void CheckUnit(ContentDatabase db, UnitDef u, List<ContentIssue> issues)
    {
        double hp = u.Stats.TryGetValue(Stats.HpMax, out var h) ? h : db.StatDefault(Stats.HpMax);
        if (hp <= 0) issues.Add(Err(u, "Stats.hp_max", "Здоровье должно быть больше 0"));
        double sp = u.Stats.TryGetValue(Stats.MoveSpeed, out var s) ? s : db.StatDefault(Stats.MoveSpeed);
        if (sp <= 0) issues.Add(Warn(u, "Stats.move_speed", "Скорость 0 — юнит не сможет двигаться"));
        foreach (var kv in u.Stats)
        {
            var sd = db.Stat(kv.Key);
            if (sd != null && (kv.Value < sd.Min || kv.Value > sd.Max))
                issues.Add(Err(u, $"Stats[{kv.Key}]", $"Значение {kv.Value} вне диапазона характеристики [{sd.Min}; {sd.Max}]"));
        }
        foreach (var kv in u.Resistances)
            if (kv.Value < -1 || kv.Value > 1)
                issues.Add(Err(u, $"Resistances[{kv.Key}]", "Сопротивление должно быть в [-1; 1]"));
        var resIds = new HashSet<string>();
        foreach (var r in u.Resources)
        {
            if (!resIds.Add(r.Resource)) issues.Add(Err(u, "Resources", $"Ресурс '{r.Resource}' указан дважды"));
            if (r.Start > r.Max) issues.Add(Warn(u, "Resources", $"Начальное значение '{r.Resource}' больше максимума"));
        }
        if (u.Skills.Distinct().Count() != u.Skills.Count) issues.Add(Warn(u, "Skills", "Навык указан несколько раз"));
        if (!db.Rules.Move.MovementCategories.Contains(u.MoveCategory))
            issues.Add(Err(u, "MoveCategory", $"Неизвестная категория движения '{u.MoveCategory}'"));
    }

    private static void CheckStatus(StatusDef s, List<ContentIssue> issues)
    {
        if (s.Levels.Count == 0) issues.Add(Err(s, "Levels", "Нужен хотя бы один уровень"));
        for (int i = 1; i < s.Levels.Count; i++)
            if (s.Levels[i].Threshold <= s.Levels[i - 1].Threshold)
                issues.Add(Err(s, $"Levels[{i}]", "Пороги уровней должны возрастать"));
        if (s.Kind == StatusKind.Accumulating && s.Levels.Count > 0 && s.Levels[^1].Threshold > s.MaxPoints)
            issues.Add(Warn(s, "Levels", "Последний порог больше максимума очков — уровень недостижим"));
        if (s.Cancels.Contains(s.Id)) issues.Add(Err(s, "Cancels", "Состояние не может снимать само себя"));
    }

    private static void CheckCellEffect(CellEffectDef c, List<ContentIssue> issues)
    {
        foreach (var e in c.OnTick.Concat(c.OnEnter))
            if (!e.TargetsUnits)
                issues.Add(Warn(c, "OnTick", $"Действие '{e.Type}' в эффекте клетки не применяется к юнитам и будет проигнорировано"));
        if (c.Spread.Enabled && c.Duration <= 0)
            issues.Add(Warn(c, "Spread", "Постоянный эффект с распространением может занять всю карту (ограничено поколениями)"));
    }

    private static void CheckSynergy(ContentDatabase db, SynergyDef s, List<ContentIssue> issues)
    {
        if (string.IsNullOrEmpty(s.A.Value) || string.IsNullOrEmpty(s.B.Value))
            issues.Add(Err(s, "A/B", "Оба операнда должны быть заданы"));
        if (s.A.Kind == SynergyOperandKind.TerrainTag)
            issues.Add(Err(s, "A", "Операнд A должен быть эффектом клетки (местность — только B)"));
        if (s.A.Kind == s.B.Kind && s.A.Value == s.B.Value)
            issues.Add(Warn(s, "A/B", "Операнды совпадают"));
        foreach (var e in s.AddEffects)
        {
            if ((s.A.Kind == SynergyOperandKind.CellEffect && e == s.A.Value && !s.RemoveA) ||
                (s.B.Kind == SynergyOperandKind.CellEffect && e == s.B.Value && !s.RemoveB))
                issues.Add(Warn(s, "AddEffects", "Синергия добавляет свой же операнд — защищено кулдауном, но может зациклиться"));
        }
    }

    private static void CheckSkill(ContentDatabase db, SkillDef s, List<ContentIssue> issues)
    {
        if (s.Stages.Count == 0) issues.Add(Err(s, "Stages", "Навык без этапов ничего не делает"));
        if ((s.Targeting == TargetingMode.Cell || s.Targeting == TargetingMode.Unit) && s.RangeMin > s.RangeMax)
            issues.Add(Err(s, "RangeMin", "Минимальная дальность больше максимальной"));
        for (int i = 0; i < s.Stages.Count; i++)
        {
            var st = s.Stages[i];
            var path = $"Stages[{i}]";
            int effects = st.Effects.Count + st.Area.Cells.Sum(c => c.Effects.Count);
            if (effects == 0) issues.Add(Warn(s, path, "Этап без эффектов"));
            if (st.Area.Shape == AreaShape.Mask)
            {
                if (st.Area.Cells.Count == 0) issues.Add(Err(s, path + ".Area", "Маска без клеток"));
                var seen = new HashSet<HexCoord>();
                foreach (var c in st.Area.Cells)
                    if (!seen.Add(c.Offset))
                        issues.Add(Err(s, path + ".Area.Cells", $"Клетка маски {c.Offset} указана дважды"));
            }
            if (st.Area.Shape == AreaShape.Global && !db.Rules.Limits.AllowGlobalSkills)
                issues.Add(Err(s, path + ".Area", "Глобальные навыки запрещены правилами"));
            if (st.Area.Shape == AreaShape.Ring && st.Area.InnerRadius > st.Area.Size)
                issues.Add(Err(s, path + ".Area", "Внутренний радиус кольца больше внешнего"));
            if (st.Area.Shape == AreaShape.Chain && s.Targeting != TargetingMode.Unit && s.Targeting != TargetingMode.Cell)
                issues.Add(Err(s, path + ".Area", "Цепь требует наведения на юнита или клетку"));
            foreach (var e in st.Effects.Concat(st.Area.Cells.SelectMany(c => c.Effects)))
            {
                if (e.Type == EffectType.Summon && db.Unit(e.SummonUnit) == null)
                    issues.Add(Err(s, path, $"Призыв несуществующего юнита '{e.SummonUnit}'"));
                if (e.TargetsUnits && !e.Filter.Allies && !e.Filter.Enemies && !e.Filter.Neutrals && !e.Filter.Self)
                    issues.Add(Warn(s, path, $"Эффект '{e.Describe()}' ни на кого не действует (фильтр пуст)"));
            }
        }
        if (s.ApCost > 0 && s.ApCost > 1000) issues.Add(Err(s, "ApCost", "Слишком большая стоимость"));
    }

    private static void CheckMap(ContentDatabase db, MapDef m, List<ContentIssue> issues)
    {
        if (m.Cells.Count == 0) issues.Add(Err(m, "Cells", "Карта без клеток"));
        if (m.Cells.Count > db.Rules.Limits.MaxMapCells) issues.Add(Err(m, "Cells", $"Клеток больше лимита {db.Rules.Limits.MaxMapCells}"));
        var seen = new HashSet<HexCoord>();
        foreach (var c in m.Cells)
            if (!seen.Add(c.C))
                issues.Add(Err(m, "Cells", $"Клетка {c.C} указана дважды"));
        var markerIds = new HashSet<string>();
        foreach (var mk in m.Markers)
        {
            if (!string.IsNullOrEmpty(mk.Id) && !markerIds.Add(mk.Id)) issues.Add(Err(m, "Markers", $"Метка '{mk.Id}' указана дважды"));
            if (!seen.Contains(mk.C)) issues.Add(Err(m, "Markers", $"Метка '{mk.Label}{mk.Id}' вне карты {mk.C}"));
        }
        foreach (var issue in MapAnalysis.Analyze(db, m)) issues.Add(issue);
    }

    private static void CheckScenario(ContentDatabase db, ScenarioDef s, List<ContentIssue> issues)
    {
        var map = db.Get<MapDef>(s.Map);
        if (map == null) return; // reference error already reported
        var cells = map.Cells.ToDictionary(c => c.C);
        var occupied = new HashSet<HexCoord>();
        foreach (var u in s.Units)
        {
            if (!cells.TryGetValue(u.C, out var cell))
            {
                issues.Add(Err(s, "Units", $"Юнит '{u.Unit}' вне карты {u.C}"));
                continue;
            }
            var t = db.Terrain(cell.T);
            var ud = db.Unit(u.Unit);
            if (t != null && ud != null && (!t.IsPassableFor(ud.MoveCategory) || cell.Blocked == true || !(cell.AllowPlacement ?? t.AllowsPlacement)))
                issues.Add(Err(s, "Units", $"Юнит '{u.Unit}' стоит на недопустимой клетке {u.C} ({t.DisplayName})"));
            if (!occupied.Add(u.C) && !s.Limits.AllowStacking)
                issues.Add(Err(s, "Units", $"Несколько юнитов на клетке {u.C}"));
        }
        if (s.Sides.Select(x => x.Faction).Distinct().Count() != s.Sides.Count)
            issues.Add(Err(s, "Sides", "Фракция указана в сторонах дважды"));
        if (!s.Sides.Any(x => x.Controller == SideController.Player))
            issues.Add(Warn(s, "Sides", "Нет стороны игрока — бой будет только наблюдаемым"));
        for (int i = 0; i < s.Waves.Count; i++)
        {
            var w = s.Waves[i];
            if (map.FindMarker(w.SpawnMarker) == null)
                issues.Add(Err(s, $"Waves[{i}]", $"Метка появления '{w.SpawnMarker}' не найдена на карте"));
        }
        foreach (var c in s.Victory.Concat(s.Defeat).Concat(s.Triggers.Select(t => t.Condition)))
        {
            if (c.Type == ConditionType.ReachMarker && map.FindMarker(c.Marker) == null)
                issues.Add(Err(s, "Conditions", $"Метка '{c.Marker}' не найдена на карте"));
            if ((c.Type == ConditionType.UnitDead || c.Type == ConditionType.UnitAlive) && s.Units.All(u => u.Tag != c.UnitTag))
                issues.Add(Err(s, "Conditions", $"Нет юнита с меткой '{c.UnitTag}'"));
            if (c.Type == ConditionType.WaveSpawned && (c.Wave < 0 || c.Wave >= s.Waves.Count))
                issues.Add(Err(s, "Conditions", $"Нет волны с индексом {c.Wave}"));
        }
        if (s.Victory.Count == 0) issues.Add(Warn(s, "Victory", "Нет условий победы"));
    }
}
