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
            if (db.Exists(cat, value)) return;
            var (bare, pin) = ContentRef.Parse(value);
            issues.Add(pin != null && db.Exists(cat, bare)
                ? new ContentIssue(IssueSeverity.Error, Src(owner), path, $"Закреплённая версия v{pin} объекта {CatName(cat)} '{bare}' не найдена в истории библиотеки — выберите текущую версию")
                : new ContentIssue(IssueSeverity.Error, Src(owner), path, $"Ссылка на несуществующий объект {CatName(cat)} '{value}'"));
        });

        // Numeric ranges from [EdRange]; damage matches ("*", type, el:, cat:, tag:).
        foreach (var d in db.AllDefs()) CheckRanges(d, d, d.Id, issues, 0);
        foreach (var d in db.AllDefs()) CheckDamageMatches(db, d, d, d.Id, issues, 0);
        CheckHierarchies(db, issues);
        CheckRanges(null, db.Rules, "rules", issues, 0);

        CheckRules(db, db.Rules, "Правила", issues);
        foreach (var p in db.All<RulesPresetDef>()) CheckRules(db, p.Rules, Src(p), issues);
        CheckIncomplete(db, issues);
        ContentChecks.Run(db, issues);
        foreach (var t in db.All<TerrainDef>()) CheckTerrain(db, t, issues);
        foreach (var u in db.All<UnitDef>()) CheckUnit(db, u, issues);
        CheckCategories(db, issues);
        foreach (var s in db.All<StatusDef>()) CheckStatus(s, issues);
        foreach (var c in db.All<CellEffectDef>()) CheckCellEffect(c, issues);
        foreach (var s in db.All<SynergyDef>()) CheckSynergy(db, s, issues);
        foreach (var s in db.All<SkillDef>()) CheckSkill(db, s, issues);
        foreach (var p in db.All<PassiveDef>()) CheckPassive(db, p, issues);
        foreach (var a in db.All<AssetDef>()) CheckAsset(db, a, issues);
        foreach (var v in db.All<VfxDef>()) CheckVfx(db, v, issues);
        double totalMb = db.All<AssetDef>().Sum(a => a.Size) / 1048576.0;
        if (totalMb > db.Rules.Assets.WarnTotalMb)
            issues.Add(new ContentIssue(IssueSeverity.Warning, "Ресурсы", "Size", $"Ресурсы пакета занимают {totalMb:0} МБ — больше {db.Rules.Assets.WarnTotalMb:0} МБ"));
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

    /// <summary>
    /// The category tree (docs/DECISIONS_4.md §61): a cycle or a missing parent is an error; a rule without its
    /// value is an error; templates report the conflicts of their categories (equal-priority forbid and allow, an
    /// «inherit» rule with the parent's ID, an «exclude» of nothing) as warnings — the battle uses a fixed order.
    /// </summary>
    private static void CheckCategories(ContentDatabase db, List<ContentIssue> issues)
    {
        foreach (var c in db.All<OriginDef>())
        {
            var problems = new List<string>();
            CategoryTree.Chain(db, c, problems);
            foreach (var p in problems.Distinct()) issues.Add(Err(c, "Parent", p));
            for (int i = 0; i < c.Rules.Count; i++)
            {
                var r = c.Rules[i];
                string path = $"Rules[{i}]";
                bool empty = r.Mode != CategoryInherit.Exclude && r.Kind switch
                {
                    CategoryRuleKind.Passive => r.Passive == "",
                    CategoryRuleKind.Tag => r.Tag == "",
                    CategoryRuleKind.MoveCategory => r.MoveCategory == "",
                    CategoryRuleKind.AiProfile => r.AiProfile == "",
                    CategoryRuleKind.Forbid or CategoryRuleKind.Allow => r.What == ForbidWhat.SkillsWithTag && r.SkillTag == "",
                    _ => false,
                };
                if (empty) issues.Add(Err(c, path, $"Правило «{(r.Id == "" ? r.Kind.ToString() : r.Id)}»: не задано, что оно даёт"));
                if (r.Mode is CategoryInherit.Override or CategoryInherit.Exclude or CategoryInherit.Conditional && r.Id == "")
                    issues.Add(Err(c, path, $"Правило с режимом «{r.Mode}» должно иметь ID — по нему находится правило родителя"));
                if (r.Mode == CategoryInherit.Conditional && r.Condition == null)
                    issues.Add(Warn(c, path, $"Правило «{r.Id}» «условно изменить» без условия — действует всегда"));
                if (r.Kind == CategoryRuleKind.MoveCategory && r.MoveCategory != "" && !db.Rules.Move.MovementCategories.Contains(r.MoveCategory)
                    && !db.All<TerrainDef>().Any(t => t.IsPassableFor(r.MoveCategory)))
                    issues.Add(Warn(c, path, $"Тип перемещения «{r.MoveCategory}» не проходим ни по одной местности"));
            }
        }
        foreach (var u in db.All<UnitDef>())
        {
            if (u.MainCategory == "" && u.Origins.Count == 0 && !db.All<OriginDef>().Any(o => o.AutoTags.Count > 0)) continue;
            var res = CategoryTree.Resolve(db, u);
            foreach (var p in res.Problems.Distinct().Where(p => !p.Contains("цикл") && !p.Contains("не найден")))
                issues.Add(Warn(u, "Categories", p));
        }
    }

    private static ContentIssue Err(Def d, string path, string msg) => new(IssueSeverity.Error, Src(d), path, msg);
    private static ContentIssue Warn(Def d, string path, string msg) => new(IssueSeverity.Warning, Src(d), path, msg);
    private static ContentIssue Info(Def d, string path, string msg) => new(IssueSeverity.Info, Src(d), path, msg);

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
        DefCategory.MapTemplate => "шаблон карты",
        DefCategory.Layer => "слой",
        DefCategory.Element => "элемент",
        DefCategory.RulesPreset => "пресет правил",
        DefCategory.Asset => "ресурс",
        DefCategory.Vfx => "визуальный эффект",
        DefCategory.Formation => "формация",
        DefCategory.Origin => "происхождение",
        DefCategory.GroupPreset => "пресет группы",
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
            var range = EdUtil.Attr<EdRangeAttribute>(prop);
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

    private static void CheckDamageMatches(ContentDatabase db, Def owner, object obj, string path, List<ContentIssue> issues, int depth)
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
            if (value == null || value is string && EdUtil.Attr<EdDamageMatchAttribute>(prop) == null) continue;
            string p = path + "." + prop.Name;
            if (EdUtil.Attr<EdDamageMatchAttribute>(prop) != null)
            {
                var values = value is string one ? new[] { one } : value is IEnumerable<string> many ? many.ToArray() : Array.Empty<string>();
                foreach (var m in values)
                    if (!DamageTaxonomy.IsKnownMatch(db, m))
                        issues.Add(Err(owner, p, $"Неизвестный тип урона, элемент или категория '{m}'"));
                continue;
            }
            if (value is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is { } item && item.GetType().IsClass && item is not string)
                        CheckDamageMatches(db, owner, item, $"{p}[{i}]", issues, depth + 1);
                continue;
            }
            if (value.GetType().IsClass && value.GetType().Namespace?.StartsWith("HexTactics.Core.Content") == true)
                CheckDamageMatches(db, owner, value, p, issues, depth + 1);
        }
    }

    /// <summary>Parents of elements and damage types must not form cycles.</summary>
    private static void CheckHierarchies(ContentDatabase db, List<ContentIssue> issues)
    {
        foreach (var e in db.All<ElementDef>())
        {
            var seen = new HashSet<string> { e.Id };
            var cur = e.Parent;
            while (!string.IsNullOrEmpty(cur) && db.Element(cur) is { } pe)
            {
                if (!seen.Add(cur))
                {
                    issues.Add(Err(e, "Parent", $"Цикл наследования элементов: {string.Join(" → ", seen)} → {cur}"));
                    break;
                }
                cur = pe.Parent;
            }
        }
        foreach (var t in db.All<DamageTypeDef>())
        {
            var seen = new HashSet<string> { t.Id };
            var cur = t.Parent;
            while (!string.IsNullOrEmpty(cur) && db.DamageType(cur) is { } pt)
            {
                if (!seen.Add(cur))
                {
                    issues.Add(Err(t, "Parent", $"Цикл родительских типов урона: {string.Join(" → ", seen)} → {cur}"));
                    break;
                }
                cur = pt.Parent;
            }
            if (t.Kind == DamageCategory.Custom && string.IsNullOrWhiteSpace(t.CustomCategory))
                issues.Add(Warn(t, "CustomCategory", "Пользовательская категория без имени — модификаторы «категория: …» к ней не применить"));
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
            var f = EdUtil.Attr<EdFormulaAttribute>(prop);
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

    /// <summary>Incomplete drafts are allowed in the library, but a scenario that depends on one cannot start.</summary>
    private static void CheckIncomplete(ContentDatabase db, List<ContentIssue> issues)
    {
        foreach (var d in db.AllDefs().Where(d => d.Incomplete))
            issues.Add(Warn(d, "Incomplete", "Объект отмечен как неполный (черновик) — проверьте зависимости и снимите отметку"));
        if (!db.AllDefs().Any(d => d.Incomplete)) return;
        foreach (var s in db.All<ScenarioDef>())
        {
            var bad = ContentDependencies.Closure(db, new Def[] { s }).Where(d => d.Incomplete && d != s).Take(5).ToList();
            if (bad.Count > 0)
                issues.Add(Err(s, "Incomplete", "Сценарий использует неполные объекты: " + string.Join(", ", bad.Select(b => $"{CatName(b.Category)} «{b.DisplayName}»"))));
        }
    }

    private static void CheckRules(ContentDatabase db, RulesDef r, string src, List<ContentIssue> issues)
    {
        if (r.Time.SpeedPresets.Count == 0)
            issues.Add(new ContentIssue(IssueSeverity.Error, src, "Time.SpeedPresets", "Нужна хотя бы одна скорость"));
        if (r.Time.SpeedPresets.Any(s => s <= 0 || s > 64))
            issues.Add(new ContentIssue(IssueSeverity.Error, src, "Time.SpeedPresets", "Скорость должна быть в (0; 64]"));
        if (r.Time.DefaultSpeedIndex >= r.Time.SpeedPresets.Count)
            issues.Add(new ContentIssue(IssueSeverity.Warning, src, "Time.DefaultSpeedIndex", "Индекс скорости вне списка — будет использована первая"));
        if (r.Detection.Sectors.Select(s => s.Sector).Distinct().Count() != 4)
            issues.Add(new ContentIssue(IssueSeverity.Warning, src, "Detection.Sectors", "Должны быть заданы все 4 сектора (недостающие = 0%)"));
        if (r.Move.MovementCategories.Count == 0)
            issues.Add(new ContentIssue(IssueSeverity.Error, src, "Move.MovementCategories", "Нужна хотя бы одна категория движения"));
        if (r != db.Rules) return;
        foreach (var stat in Stats.All)
            if (db.Stat(stat) == null)
                issues.Add(new ContentIssue(IssueSeverity.Warning, src, "stats", $"Нет определения базовой характеристики '{stat}' — значение по умолчанию 0"));
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
        var resIds = new HashSet<string>();
        foreach (var r in u.Resources)
        {
            if (!resIds.Add(r.Resource)) issues.Add(Err(u, "Resources", $"Ресурс '{r.Resource}' указан дважды"));
            if (r.Start > r.Max) issues.Add(Warn(u, "Resources", $"Начальное значение '{r.Resource}' больше максимума"));
        }
        if (u.Skills.Distinct().Count() != u.Skills.Count) issues.Add(Warn(u, "Skills", "Навык указан несколько раз"));
        if (!db.Rules.Move.MovementCategories.Contains(u.MoveCategory))
            issues.Add(Err(u, "MoveCategory", $"Неизвестная категория движения '{u.MoveCategory}'"));
        if (!string.IsNullOrEmpty(u.DefenseSkill) && db.Skill(u.DefenseSkill) is { } ds && ds.Targeting != TargetingMode.Self)
            issues.Add(Warn(u, "DefenseSkill", $"Защитный навык «{ds.DisplayName}» не наводится «на себя» — команда защиты применит его к исполнителю"));
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
            if (effects == 0 && !s.Defense.Enabled && st.Move == StageMove.None) issues.Add(Warn(s, path, "Этап без эффектов"));
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
        CheckSeries(db, s, issues);
    }

    /// <summary>Series of hits, stage conditions, costs and defensive stances (docs/DECISIONS_3.md §45).</summary>
    private static void CheckSeries(ContentDatabase db, SkillDef s, List<ContentIssue> issues)
    {
        int hits = 0;
        for (int i = 0; i < s.Stages.Count; i++)
        {
            var st = s.Stages[i];
            var path = $"Stages[{i}]";
            if (st.Repeat < 1) issues.Add(Err(s, path + ".Repeat", "Повтор этапа меньше 1"));
            if (st.Repeat > 100)
                issues.Add(Err(s, path + ".Repeat", $"Повтор этапа {st.Repeat} раз: больше 100 — последовательность без гарантированного завершения за разумное время"));
            if (st.Repeat > 1 && st.RepeatInterval <= 0)
                issues.Add(Warn(s, path + ".RepeatInterval", "Повторы без интервала срабатывают в одно мгновение — анимации и журнал их не разделят"));
            if (st.HasDamage) hits += Math.Max(1, Math.Min(st.Repeat, 100));
            if (st.DamageMultiplier <= 0 && st.FlatBonus <= 0 && st.HasDamage)
                issues.Add(Warn(s, path + ".DamageMultiplier", "Множитель урона 0 — удар не наносит урона"));
            if (st.Condition != null) CheckCondition(s, path + ".Condition", st.Condition, issues);
            if (st.Move == StageMove.DashToTarget && s.Targeting is not (TargetingMode.Unit or TargetingMode.Cell))
                issues.Add(Warn(s, path + ".Move", "Рывок к цели у навыка без цели (направление/на себя) — исполнитель останется на месте"));
            foreach (var c in st.Costs)
                if (!string.IsNullOrEmpty(c.Resource) && db.Get<ResourceDef>(c.Resource) == null)
                    issues.Add(Err(s, path + ".Costs", $"Ресурс '{c.Resource}' не найден"));
        }
        if (hits > 12)
            issues.Add(Warn(s, "Stages", $"Серия из {hits} ударов: проверьте производительность (каждый удар — бросок и расчёт урона), длительность анимаций и баланс"));
        if (s.FreeIf != null) CheckCondition(s, "FreeIf", s.FreeIf, issues);
        if (s.OnResourceOut == ResourceOutPolicy.Debt)
        {
            var used = s.Stages.SelectMany(st => st.Costs).Where(c => !string.IsNullOrEmpty(c.Resource)).Select(c => c.Resource).Distinct().ToList();
            if (!used.Any(r => db.Get<ResourceDef>(r)?.MinValue < 0))
                issues.Add(Warn(s, "OnResourceOut", "«В долг» без ресурса с отрицательным минимумом: долг невозможен, при нехватке оставшиеся этапы отменяются"));
            if (s.Stages.SelectMany(st => st.Costs).Any(c => string.IsNullOrEmpty(c.Resource)))
                issues.Add(Warn(s, "OnResourceOut", "Очки действия в долг не уходят: при нехватке ОД оставшиеся этапы отменяются"));
        }
        if (s.RetargetOnDeath == RetargetRule.Inherit) issues.Add(Err(s, "RetargetOnDeath", "«Как в серии» допустимо только у этапа"));
        var d = s.Defense;
        if (d.Enabled)
        {
            if (d.DamageReduction <= 0 && d.DodgeChance <= 0 && d.ParryChance <= 0 && d.ReflectPercent <= 0 && d.EvasionBonus <= 0 && d.BlockBonus <= 0)
                issues.Add(Warn(s, "Defense", "Защита включена, но ничего не делает (нет снижения, уклонения, парирования, отражения и бонусов)"));
            if (d.Duration <= 0 && d.AllowMove)
                issues.Add(Warn(s, "Defense.Duration", "Защита без длительности, допускающая движение, длится до нового приказа"));
            if (d.DodgeChance + d.ParryChance > 1)
                issues.Add(Warn(s, "Defense", "Сумма шансов уклонения и парирования больше 100 %: парирование срабатывает только после неудачного уклонения"));
            foreach (var c in d.Upkeep)
                if (!string.IsNullOrEmpty(c.Resource) && db.Get<ResourceDef>(c.Resource) == null)
                    issues.Add(Err(s, "Defense.Upkeep", $"Ресурс '{c.Resource}' не найден"));
        }
        else if (s.Kind == SkillKind.Defense && s.Stages.All(st => st.Effects.All(e => e.Type is not (EffectType.Shield or EffectType.ApplyStatus or EffectType.ModifyStat))))
        {
            issues.Add(Warn(s, "Defense", "Защитный навык без защиты (раздел «Защита» выключен) и без щита/состояния на себя"));
        }
    }

    /// <summary>Resources against the configured limits and their own settings (docs/DECISIONS_3.md §47).</summary>
    private static void CheckAsset(ContentDatabase db, AssetDef a, List<ContentIssue> issues)
    {
        var lim = db.Rules.Assets;
        if (a.Kind is AssetKind.Texture or AssetKind.Sprite or AssetKind.Icon && a.Width > 0)
        {
            var t = lim.For(a.Purpose);
            int side = Math.Max(a.Width, a.Height);
            if (side > t.Max) issues.Add(Err(a, "Purpose", $"{a.Width}×{a.Height} больше максимума {t.Max} для назначения «{a.Purpose}»"));
            else if (side > t.RecommendedMax * 2) issues.Add(Warn(a, "Purpose", $"{a.Width}×{a.Height} чрезмерно для «{a.Purpose}» (рекомендуется до {t.RecommendedMax})"));
            if (t.NeedsAlpha && !a.HasAlpha) issues.Add(Warn(a, "HasAlpha", "Назначению нужна прозрачность, а у изображения нет альфа-канала"));
        }
        if (a.Kind == AssetKind.Model)
        {
            if (a.Triangles > lim.WarnTriangles) issues.Add(Warn(a, "Triangles", $"Треугольников {a.Triangles:N0} — больше {lim.WarnTriangles:N0}"));
            if (a.Bones > lim.WarnBones) issues.Add(Warn(a, "Bones", $"Костей {a.Bones} — больше {lim.WarnBones}"));
            var mapped = new HashSet<string>(a.Animations.SelectMany(m => m.Variants.Append(m.Clip)));
            var extra = a.Clips.Where(c => !mapped.Contains(c)).ToList();
            if (extra.Count > 0) issues.Add(Info(a, "Animations", "Анимации без роли (не используются): " + string.Join(", ", extra)));
            for (int i = 0; i < a.Animations.Count; i++)
            {
                var m = a.Animations[i];
                foreach (var clip in m.Variants.Append(m.Clip))
                    if (!string.IsNullOrEmpty(clip) && a.Clips.Count > 0 && !a.Clips.Contains(clip))
                        issues.Add(Warn(a, $"Animations[{i}]", $"Клипа «{clip}» нет в файле модели"));
                int ci = a.Clips.IndexOf(m.Clip);
                double len = ci >= 0 && ci < a.ClipLengths.Count ? a.ClipLengths[ci] : 0;
                foreach (var mk in m.Markers)
                    if (len > 0 && mk.Time > len + 1e-6)
                        issues.Add(Warn(a, $"Animations[{i}].Markers", $"Маркер «{mk.Kind}» ({mk.Time:0.##} с) позже конца клипа ({len:0.##} с)"));
                if (len > lim.WarnAnimationSeconds) issues.Add(Warn(a, $"Animations[{i}]", $"Клип «{m.Clip}» {len:0.#} с — больше {lim.WarnAnimationSeconds:0} с"));
            }
            if (a.Fallback == a.Id || a.Simplified == a.Id) issues.Add(Err(a, "Fallback", "Модель не может быть резервной или упрощённой для самой себя"));
        }
        if (a.Kind == AssetKind.Sound && a.Duration > lim.WarnSoundSeconds)
            issues.Add(Warn(a, "Duration", $"Звук {a.Duration:0.#} с — для эффектов желательно до {lim.WarnSoundSeconds:0} с"));
        if (a.Kind == AssetKind.Material)
        {
            foreach (var (field, id) in new[] { ("NormalMap", a.NormalMap), ("RoughnessMap", a.RoughnessMap), ("MetallicMap", a.MetallicMap), ("EmissionMap", a.EmissionMap), ("DetailMask", a.DetailMask), ("DetailTexture", a.DetailTexture) })
                if (!string.IsNullOrEmpty(id) && db.Get<AssetDef>(id) is { } t && t.Kind is not (AssetKind.Texture or AssetKind.Icon or AssetKind.Sprite))
                    issues.Add(Err(a, field, $"«{t.DisplayName}» — не текстура"));
            if (!string.IsNullOrEmpty(a.DetailMask) && string.IsNullOrEmpty(a.DetailTexture))
                issues.Add(Warn(a, "DetailTexture", "Маска деталей без текстуры деталей ничего не покажет"));
        }
        if (a.Variants.Contains(a.Id)) issues.Add(Err(a, "Variants", "Звук не может быть своим вариантом"));
    }

    private static void CheckVfx(ContentDatabase db, VfxDef v, List<ContentIssue> issues)
    {
        if (v.Type == VfxType.Composite)
        {
            // Layers must not lead back to the effect itself.
            var seen = new HashSet<string>();
            var stack = new Stack<string>(v.Layers);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == v.Id)
                {
                    issues.Add(Err(v, "Layers", "Слои эффекта ссылаются на него самого (цикл)"));
                    break;
                }
                if (!seen.Add(id)) continue;
                foreach (var l in db.Get<VfxDef>(id)?.Layers ?? new List<string>()) stack.Push(l);
            }
            if (v.Layers.Count == 0) issues.Add(Warn(v, "Layers", "Комбинация без слоёв"));
        }
        if (v.Type is VfxType.Texture or VfxType.AnimatedTexture or VfxType.Decal && string.IsNullOrEmpty(v.Texture))
            issues.Add(Warn(v, "Texture", "Не выбрана текстура — будет показан цветной круг"));
        if (v.Type == VfxType.Model && string.IsNullOrEmpty(v.Model)) issues.Add(Warn(v, "Model", "Не выбрана модель — будет показана вспышка частиц"));
        if (v.Simplified == v.Id) issues.Add(Err(v, "Simplified", "Эффект не может быть своим упрощённым вариантом"));
    }

    /// <summary>Passives and talents: modifiers, conditions, triggers, auras (docs/DECISIONS_3.md §46).</summary>
    private static void CheckPassive(ContentDatabase db, PassiveDef p, List<ContentIssue> issues)
    {
        if (p.When != null) CheckCondition(p, "When", p.When, issues);
        bool triggered = p.Trigger != PassiveTrigger.Always;
        if (triggered && p.ActiveSeconds <= 0 && (p.Modifiers.Count > 0 || p.StatMods.Count > 0))
            issues.Add(Warn(p, "ActiveSeconds", "Срабатывающий пассив с модификаторами без «действует после срабатывания» — модификаторы не действуют никогда"));
        if (!triggered && p.Action != PassiveAction.None)
            issues.Add(Warn(p, "Action", "Действие задано, но пассив «всегда» — действие не выполняется (нужно событие)"));
        if (p.Modifiers.Count == 0 && p.StatMods.Count == 0 && p.Defense.IsEmpty && p.Offense.IsEmpty && p.Action == PassiveAction.None && p.AuraPassives.Count == 0)
            issues.Add(Warn(p, "Modifiers", "Пассив ничего не делает"));
        if (p.AuraRadius > 6) issues.Add(Warn(p, "AuraRadius", $"Аура радиусом {p.AuraRadius}: много юнитов — пересчёт дороже"));
        if (p.AuraPassives.Contains(p.Id)) issues.Add(Err(p, "AuraPassives", "Аура не может раздавать саму себя"));
        if (p.ChainReaction && p.MaxChainDepth > db.Rules.Limits.MaxReactionDepth)
            issues.Add(Warn(p, "MaxChainDepth", $"Глубина цепи больше общего предела реакций ({db.Rules.Limits.MaxReactionDepth}) — сработает общий предел"));
        if (triggered && p.Action != PassiveAction.None && p.Cooldown <= 0 && p.MaxTriggers == 0 && p.Chance >= 1 && p.Trigger is PassiveTrigger.OnDamaged or PassiveTrigger.OnHitEnemy or PassiveTrigger.OnMoved)
            issues.Add(Info(p, "Cooldown", "Частое событие без перезарядки и лимита — пассив сработает на каждый удар/шаг"));
        for (int i = 0; i < p.Modifiers.Count; i++)
        {
            var m = p.Modifiers[i];
            var path = $"Modifiers[{i}]";
            switch (m.Target)
            {
                case ModifierTarget.Stat when string.IsNullOrEmpty(m.Stat) || db.Stat(m.Stat) == null:
                    issues.Add(Err(p, path, $"Неизвестная характеристика «{m.Stat}»"));
                    break;
                case ModifierTarget.ResourceMax or ModifierTarget.ResourceRegen when db.Get<ResourceDef>(m.Resource) == null:
                    issues.Add(Err(p, path, $"Неизвестный ресурс «{m.Resource}»"));
                    break;
                case ModifierTarget.Immunity when string.IsNullOrEmpty(m.Status) && string.IsNullOrEmpty(m.Match):
                    issues.Add(Err(p, path, "Иммунитет: не выбрано ни состояние, ни тип урона"));
                    break;
                case ModifierTarget.TerrainCost when string.IsNullOrEmpty(m.Terrain):
                    issues.Add(Err(p, path, "Не выбрана поверхность"));
                    break;
                case ModifierTarget.TargetChoice when m.Rule is AutoTargetRule.WithTag or AutoTargetRule.WithStatus && string.IsNullOrEmpty(m.RuleTag):
                    issues.Add(Err(p, path, "Правилу выбора цели нужен тег или состояние"));
                    break;
                case ModifierTarget.HitCount when m.Value < 0 || m.Value > 20:
                    issues.Add(Warn(p, path, "Количество дополнительных ударов вне 0…20"));
                    break;
            }
            if (m.Mode == ModifierMode.Multiplier && m.Value <= 0) issues.Add(Err(p, path, "Множитель должен быть больше 0"));
            if (m.Mode == ModifierMode.Exclusive && string.IsNullOrEmpty(m.Category)) issues.Add(Warn(p, path, "Взаимоисключающий модификатор без категории"));
            if (m.IsPercentLike && m.Target is ModifierTarget.IncomingDamage && m.Value < -100)
                issues.Add(Info(p, path, "Входящий урон ниже −100 % — урон 0 (не лечит)"));
            if (m.IsPercentLike && m.Target is ModifierTarget.Stat && Math.Abs(m.Value) > 500)
                issues.Add(Warn(p, path, $"Изменение характеристики на {m.Value:0} % — проверьте баланс"));
        }
    }

    private static void CheckCondition(Def owner, string path, UnitConditionDef c, List<ContentIssue> issues, int depth = 0)
    {
        if (depth > 16)
        {
            issues.Add(Err(owner, path, "Условие вложено глубже 16 уровней"));
            return;
        }
        if (c.Type is UnitConditionType.Any or UnitConditionType.Not && c.Children.Count == 0)
            issues.Add(Warn(owner, path, $"«{c.Describe()}» без вложенных условий никогда не выполняется"));
        if (c.Type == UnitConditionType.HasStatus && string.IsNullOrEmpty(c.Status)) issues.Add(Err(owner, path, "Не выбрано состояние условия"));
        if (c.Type == UnitConditionType.AfterSkill && string.IsNullOrEmpty(c.Skill)) issues.Add(Err(owner, path, "Не выбран навык условия"));
        if (c.Type == UnitConditionType.OnTerrain && c.Terrains.Count == 0) issues.Add(Warn(owner, path, "Не выбраны поверхности условия"));
        if (c.Type == UnitConditionType.UnitType && c.Types.Count == 0) issues.Add(Warn(owner, path, "Не выбраны типы существ условия"));
        if (c.Type == UnitConditionType.Scenario && c.ScenarioCondition == null) issues.Add(Err(owner, path, "Не задано условие сценария"));
        for (int i = 0; i < c.Children.Count; i++) CheckCondition(owner, $"{path}.Children[{i}]", c.Children[i], issues, depth + 1);
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
        // Objects, routes and groups of the TD part (docs/DECISIONS_4.md §56–58).
        foreach (var p in ScenarioObjectsCheck.All(db, s, map))
            issues.Add(new ContentIssue(p.Severity, Src(s), p.Path, p.Message));
        var cells = map.Cells.ToDictionary(c => c.C);
        var occupied = new Dictionary<HexCoord, List<UnitDef>>();
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
            if (ud == null) continue;
            if (!occupied.TryGetValue(u.C, out var here)) occupied[u.C] = here = new List<UnitDef>();
            var problem = StackProblem(db, s, here, ud, u.Faction);
            if (problem != null) issues.Add(Err(s, "Units", $"Несколько юнитов на клетке {u.C}: {problem}"));
            here.Add(ud);
        }
        if (s.Sides.Select(x => x.Faction).Distinct().Count() != s.Sides.Count)
            issues.Add(Err(s, "Sides", "Фракция указана в сторонах дважды"));
        if (!s.Sides.Any(x => x.Controller == SideController.Player))
            issues.Add(Warn(s, "Sides", "Нет стороны игрока — бой будет только наблюдаемым"));
        var tags = new HashSet<string>(s.Units.Select(u => u.Tag).Concat(s.Waves.SelectMany(w => w.Units).Select(u => u.Tag))
            .Concat(s.Waves.SelectMany(w => w.Groups).Select(g => g.Tag)).Concat(s.Objects.Where(o => o.Health != null).Select(o => o.Id)).Where(t => !string.IsNullOrEmpty(t)));
        var objIds = new HashSet<string>();
        foreach (var o in s.Objectives)
            if (!objIds.Add(o.Id)) issues.Add(Err(s, "Objectives", $"ID цели '{o.Id}' повторяется"));
        for (int i = 0; i < s.Waves.Count; i++)
        {
            var w = s.Waves[i];
            // Groups come from spawn bases; the old composition needs a spawn marker.
            if (!w.AllSpawnMarkers().Any() && (w.Units.Count > 0 || w.Groups.Count == 0 && !w.Budget.Enabled)) issues.Add(Err(s, $"Waves[{i}]", "Нет точки появления"));
            foreach (var m in w.AllSpawnMarkers())
                if (map.FindMarker(m) == null)
                    issues.Add(Err(s, $"Waves[{i}]", $"Метка появления '{m}' не найдена на карте"));
            if (!string.IsNullOrEmpty(w.Route) && map.FindRoute(w.Route) == null && s.Route(w.Route) == null)
                issues.Add(Err(s, $"Waves[{i}].Route", $"Маршрут '{w.Route}' не найден на карте"));
            if (w.Start == WaveStart.AfterPrevious && i == 0) issues.Add(Warn(s, $"Waves[{i}]", "Первая волна «после предыдущей» начнётся сразу"));
        }
        // Every condition anywhere: objectives, waves, triggers, boss phases of the units used.
        IEnumerable<ConditionDef> Flatten(ConditionDef c) => new[] { c }.Concat(c.Children.SelectMany(Flatten));
        var conds = s.Victory.Concat(s.Defeat).Concat(s.Triggers.Select(t => t.Condition))
            .Concat(s.Waves.Where(w => w.Start == WaveStart.OnCondition).Select(w => w.StartCondition)).Concat(s.Waves.Where(w => w.CustomEnd).Select(w => w.EndCondition))
            .Concat(s.Objectives.SelectMany(o => new[] { o.Complete, o.Fail })).SelectMany(Flatten).ToList();
        foreach (var c in conds)
        {
            if (c.Type is ConditionType.ReachMarker or ConditionType.UnitInArea && map.FindMarker(c.Marker) == null)
                issues.Add(Err(s, "Conditions", $"Метка '{c.Marker}' не найдена на карте"));
            if ((c.Type == ConditionType.UnitDead || c.Type == ConditionType.UnitAlive) && !tags.Contains(c.UnitTag))
                issues.Add(Err(s, "Conditions", $"Нет юнита с меткой '{c.UnitTag}'"));
            if (c.Type is ConditionType.WaveSpawned or ConditionType.WaveCleared && (c.Wave < 0 || c.Wave >= s.Waves.Count))
                issues.Add(Err(s, "Conditions", $"Нет волны с индексом {c.Wave}"));
            if (c.Type is ConditionType.ObjectiveCompleted or ConditionType.ObjectiveFailed && !objIds.Contains(c.Objective))
                issues.Add(Err(s, "Conditions", $"Нет цели с ID '{c.Objective}'"));
            if (c.Type is ConditionType.All or ConditionType.Any or ConditionType.Not or ConditionType.InOrder && c.Children.Count == 0 && c.Type != ConditionType.All)
                issues.Add(Warn(s, "Conditions", $"Логическое условие «{c.Describe()}» без вложенных условий"));
        }
        foreach (var o in s.Objectives)
        {
            foreach (var d in o.DependsOn)
                if (!objIds.Contains(d)) issues.Add(Err(s, $"Objectives[{o.Id}].DependsOn", $"Нет цели с ID '{d}'"));
            if (o.DependsOn.Contains(o.Id)) issues.Add(Err(s, $"Objectives[{o.Id}].DependsOn", "Цель зависит сама от себя"));
            if (o.Kind is ObjectiveKind.Defend or ObjectiveKind.Capture && o.Cells.Count == 0 && map.FindMarker(o.Marker) == null)
                issues.Add(Err(s, $"Objectives[{o.Id}]", "Нужны гексы цели или метка карты"));
            if (o.Kind == ObjectiveKind.Escort && map.FindMarker(o.Marker) == null) issues.Add(Err(s, $"Objectives[{o.Id}]", "Нужна метка назначения"));
            if (o.Kind is ObjectiveKind.Escort or ObjectiveKind.Destroy && !tags.Contains(o.UnitTag))
                issues.Add(Err(s, $"Objectives[{o.Id}]", $"Нет юнита с меткой '{o.UnitTag}'"));
            foreach (var c in o.Cells)
                if (!map.Cells.Any(x => x.C == c)) issues.Add(Err(s, $"Objectives[{o.Id}].Cells", $"Клетка {c} вне карты"));
        }
        if (s.Victory.Count == 0 && s.Objectives.Count == 0) issues.Add(Warn(s, "Victory", "Нет условий победы"));
        if (s.Objectives.Count > 0 && s.Objectives.All(o => !o.Required)) issues.Add(Warn(s, "Objectives", "Нет обязательных целей — победа невозможна"));
    }

    /// <summary>Why a unit cannot start on a hex with the others (docs/DECISIONS_2.md §26), or null.</summary>
    private static string? StackProblem(ContentDatabase db, ScenarioDef s, List<UnitDef> here, UnitDef ud, string faction)
    {
        string layer = string.IsNullOrEmpty(ud.Layer) ? Layers.Ground : ud.Layer;
        var mates = here.Where(o => (string.IsNullOrEmpty(o.Layer) ? Layers.Ground : o.Layer) == layer).ToList();
        if (mates.Count == 0) return null;
        string f = string.IsNullOrEmpty(faction) ? ud.Faction : faction;
        switch (s.Limits.Stacking)
        {
            case StackingMode.ForbidAll:
                return "в сценарии совместное размещение запрещено";
            case StackingMode.AllowAll:
                return mates.Count + 1 > Math.Max(1, db.Rules.Limits.MaxPerHex) ? $"больше {db.Rules.Limits.MaxPerHex} на гексе" : null;
        }
        foreach (var o in mates)
        {
            if (db.RelationOf(f, o.Faction) == Relation.Enemy) return "враждебные существа не делят клетку";
            if (!StackingDef.Mutual(ud, o)) return $"«{ud.DisplayName}» и «{o.DisplayName}» не могут делить клетку (блок «Совместное размещение» типа юнита)";
        }
        int limit = mates.Append(ud).Min(o => o.Stacking.MaxPerHex);
        return mates.Count + 1 > Math.Max(1, limit) ? $"превышен предел {limit} на гексе" : null;
    }
}
