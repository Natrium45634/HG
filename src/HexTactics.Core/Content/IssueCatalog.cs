using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace HexTactics.Core.Content;

/// <summary>
/// The code and the fix of a problem of the content (ТЗ ч. 6, T31; docs/DECISIONS_6.md §107): every problem has a stable code
/// («E-REF-01» for the known kinds, «E-SKIL-3F2A» — the kind of the message — for the rest), its place (object and field) and how
/// to fix it; some are fixed by one button — a reference to nothing is cleared, a pinned version goes to the current one, a number
/// is brought into its range, a few checks of part 6 take their safe value.
/// </summary>
public static class IssueCatalog
{
    private sealed record Rule(Regex Pattern, string Code, Func<Match, ContentIssue, string> Fix, Fixer? Auto = null);

    /// <summary>An automatic fix: false — not possible here (the text says why).</summary>
    private delegate bool Fixer(ContentDatabase db, Def def, ContentIssue issue, Match m, out string done);

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Rule[] Rules =
    {
        new(R(@"^Ссылка на несуществующий объект (.+?) '(.+)'$"), "REF-01",
            (m, i) => $"Выберите в поле «{Field(i)}» существующий объект ({m.Groups[1].Value}) или создайте «{m.Groups[2].Value}». Автоисправление очищает ссылку.",
            FixReference),
        new(R(@"^Не указана ссылка \((.+)\)$"), "REF-02", (m, i) => $"Выберите объект ({m.Groups[1].Value}) в поле «{Field(i)}»."),
        new(R(@"^Закреплённая версия v(\d+) объекта (.+?) '(.+)' не найдена"), "REF-03",
            (m, i) => $"Версии v{m.Groups[1].Value} нет в истории: выберите текущую версию «{m.Groups[3].Value}». Автоисправление снимает закрепление.", FixReference),
        new(R(@"^Значение (.+) вне диапазона \[(.+); (.+)\]$"), "RNG-01",
            (m, i) => $"Поставьте в поле «{Field(i)}» значение от {m.Groups[2].Value} до {m.Groups[3].Value}. Автоисправление приводит его к ближайшей границе.", FixRange),
        new(R(@"^Значение (.+) вне диапазона характеристики \[(.+); (.+)\]$"), "RNG-02",
            (m, i) => $"Характеристика должна быть от {m.Groups[2].Value} до {m.Groups[3].Value} — измените её на вкладке «Характеристики» или пределы характеристики."),
        new(R(@"^Некорректное число$"), "NUM-01", (_, i) => $"Введите в поле «{Field(i)}» конечное число."),
        new(R(@"^Недопустимые символы в ID"), "ID-01", (_, _) => "Переименуйте объект кнопкой «Переименовать»: латиница, цифры, «_», «.», «-»; ссылки обновятся."),
        new(R(@"ни на кого не действует \(фильтр пуст\)"), "EFF-01", (_, _) => "Отметьте в фильтре эффекта хотя бы одну группу: враги, союзники, нейтралы или сам."),
        new(R(@"^Призыв несуществующего юнита '(.*)'$"), "SUM-02", (m, _) => $"Выберите призываемого юнита или описание из «Призывов» (поле «Призыв»); «{m.Groups[1].Value}» нет."),
        new(R(@"^Нет описания призыва '(.+)'$"), "SUM-03", (m, _) => $"Создайте описание «{m.Groups[1].Value}» в разделе «Призывы» или выберите существующее."),
        new(R(@"^Призыв: «По окончании» — нужно действие"), "SUM-01",
            (_, _) => "Выберите в «По окончании» действие: исчезает, гибнет, нейтрализуется, независимый, передаётся. Автоисправление ставит «Исчезает».", FixSummonDespawn),
        new(R(@"^Призыв: Расстановка «заданные смещения» без смещений"), "SUM-04", (_, _) => "Добавьте смещения или выберите другую расстановку (например, «Соседние гексы»)."),
        new(R(@"^Призыв: .*без ограничения и без лимитов"), "SUM-05", (_, _) => "Задайте время жизни или хотя бы один лимит живых («у призывателя», «у стороны», «в сценарии»)."),
        new(R(@"^Призыв: .*неуничтожаем"), "SUM-06", (_, _) => "Ограничьте время жизни или выберите уничтожаемого юнита — иначе бой может не закончиться."),
        new(R(@"^Шкала: "), "SCL-01", (_, i) => $"Откройте шкалу в разделе «Шкалы» и исправьте «{Field(i)}»: график и прогноз покажут результат."),
        new(R(@"^Кампания: |^Награда: "), "CMP-01", (_, i) => $"Откройте раздел «Кампании» / «Наборы наград» и исправьте «{Field(i)}»; проверка сверху формы обновится сразу."),
    };

    private static Match? Find(ContentIssue i, out Rule? rule)
    {
        foreach (var r in Rules)
        {
            var m = r.Pattern.Match(i.Message);
            if (!m.Success) continue;
            rule = r;
            return m;
        }
        rule = null;
        return null;
    }

    private static string Prefix(IssueSeverity s) => s switch { IssueSeverity.Error => "E", IssueSeverity.Warning => "W", _ => "I" };

    /// <summary>The stable code of a problem.</summary>
    public static string CodeOf(ContentIssue i)
    {
        if (Find(i, out var rule) != null) return $"{Prefix(i.Severity)}-{rule!.Code}";
        // The kind of the message: its text without quoted names and numbers.
        var kind = Regex.Replace(Regex.Replace(i.Message, "«[^»]*»|'[^']*'|\"[^\"]*\"", "_"), @"[-+]?\d+([.,]\d+)?", "#");
        string cat = Category(i) is { } c ? c.ToString().ToUpperInvariant()[..Math.Min(4, c.ToString().Length)] : "GEN";
        return $"{Prefix(i.Severity)}-{cat}-{(StableHash(kind) & 0xFFFF):X4}";
    }

    /// <summary>How to fix it: a known kind says exactly; the rest — by the words of the message, always with the place.</summary>
    public static string FixOf(ContentIssue i)
    {
        var m = Find(i, out var rule);
        if (m != null) return rule!.Fix(m, i);
        var msg = i.Message.ToLowerInvariant();
        string where = Field(i) != "" ? $"поле «{Field(i)}»" : "объект";
        if (msg.Contains("нет ") || msg.Contains("не найден") || msg.Contains("несуществ")) return $"Выберите существующий объект в {where} или создайте недостающий.";
        if (msg.Contains("больше") || msg.Contains("меньше") || msg.Contains("должн")) return $"Измените {where}, чтобы условие выполнялось.";
        if (msg.Contains("пуст") || msg.Contains("не выбран") || msg.Contains("не указан")) return $"Заполните {where}.";
        if (msg.Contains("повтор") || msg.Contains("дублик") || msg.Contains("уже ")) return $"Сделайте значения уникальными ({where}).";
        if (msg.Contains("недостижим") || msg.Contains("заперт")) return "Откройте карту и проверьте проходимость: путь, препятствия, переходы.";
        return $"Откройте «{i.Source}» и исправьте {where}; повторная проверка покажет, ушла ли проблема.";
    }

    public static bool CanAutoFix(ContentIssue i) => Find(i, out var rule) != null && rule!.Auto != null;

    /// <summary>Fixes a problem in the database (the draft); false — nothing changed.</summary>
    public static bool TryFix(ContentDatabase db, ContentIssue i, out string done)
    {
        done = "";
        var m = Find(i, out var rule);
        if (m == null || rule!.Auto == null) return false;
        var def = DefOf(db, i);
        if (def == null)
        {
            done = "объект не найден";
            return false;
        }
        return rule.Auto(db, def, i, m, out done);
    }

    // ------------------------------------------------------------------ fixes

    private static bool FixReference(ContentDatabase db, Def def, ContentIssue i, Match m, out string done)
    {
        string result = "";
        bool changed = false;
        ReferenceWalker.WalkDef(def, (owner, path, cat, value, set, _) =>
        {
            if (changed || path != i.Path || set == null) return;
            var (bare, pin) = ContentRef.Parse(value);
            string to = pin != null && db.Exists(cat, bare) ? bare : "";
            set(to);
            result = to == "" ? $"ссылка «{value}» очищена" : $"закрепление снято: «{to}»";
            changed = true;
        });
        done = changed ? result : "поле ссылки не найдено";
        return changed;
    }

    private static bool FixRange(ContentDatabase db, Def def, ContentIssue i, Match m, out string done)
    {
        done = "поле не найдено";
        if (!Resolve(def, i.Path, out var target, out var prop) || prop == null) return false;
        var range = EdUtil.Attr<EdRangeAttribute>(prop);
        if (range == null) return false;
        object? v = prop.GetValue(target);
        double n = v switch { int x => x, double x => x, float x => x, long x => x, _ => double.NaN };
        double fixedValue = double.IsNaN(n) ? range.Min : Math.Clamp(n, range.Min, range.Max);
        object boxed = prop.PropertyType == typeof(int) ? (int)Math.Round(fixedValue) : prop.PropertyType == typeof(long) ? (long)Math.Round(fixedValue)
            : prop.PropertyType == typeof(float) ? (float)fixedValue : fixedValue;
        prop.SetValue(target, boxed);
        done = $"«{prop.Name}»: {n.ToString(CultureInfo.InvariantCulture)} → {fixedValue.ToString(CultureInfo.InvariantCulture)}";
        return true;
    }

    private static bool FixSummonDespawn(ContentDatabase db, Def def, ContentIssue i, Match m, out string done)
    {
        done = "не призыв";
        if (def is not SummonDef s) return false;
        s.Despawn = SummonEnd.Vanish;
        done = "«По окончании» — «Исчезает»";
        return true;
    }

    // ------------------------------------------------------------------ places

    /// <summary>The category of the object of a problem («Skill:fireball» → Skill).</summary>
    public static DefCategory? Category(ContentIssue i)
    {
        int colon = i.Source.IndexOf(':');
        return colon > 0 && Enum.TryParse<DefCategory>(i.Source[..colon], out var c) ? c : null;
    }

    public static Def? DefOf(ContentDatabase db, ContentIssue i)
    {
        int colon = i.Source.IndexOf(':');
        if (colon < 0 || Category(i) is not { } cat) return null;
        return db.Table(cat).Get(i.Source[(colon + 1)..]);
    }

    /// <summary>The field of a problem without the ID of its object.</summary>
    public static string Field(ContentIssue i)
    {
        int colon = i.Source.IndexOf(':');
        string id = colon >= 0 ? i.Source[(colon + 1)..] : "";
        return id != "" && i.Path.StartsWith(id + ".", StringComparison.Ordinal) ? i.Path[(id.Length + 1)..] : i.Path;
    }

    /// <summary>The object and the property a path names («Stages[0].Effects[1].Amount», with or without the ID in front).</summary>
    public static bool Resolve(Def def, string path, out object target, out PropertyInfo? prop)
    {
        target = def;
        prop = null;
        var parts = path.Split('.').ToList();
        if (parts.Count > 0 && parts[0] == def.Id) parts.RemoveAt(0);
        for (int k = 0; k < parts.Count; k++)
        {
            var seg = parts[k];
            int bracket = seg.IndexOf('[');
            string name = bracket >= 0 ? seg[..bracket] : seg;
            var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return false;
            if (k == parts.Count - 1 && bracket < 0)
            {
                prop = p;
                return true;
            }
            var next = p.GetValue(target);
            if (bracket >= 0)
            {
                if (next is not IList list || !int.TryParse(seg[(bracket + 1)..seg.IndexOf(']')], out int idx) || idx < 0 || idx >= list.Count) return false;
                next = list[idx];
            }
            if (next == null) return false;
            target = next;
        }
        return false;
    }

    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s)
        {
            h ^= c;
            h *= 16777619;
        }
        return h;
    }

    /// <summary>One line for logs and exports: code, place, message, fix.</summary>
    public static string Describe(ContentIssue i) => $"[{CodeOf(i)}] {i.Source}{(i.Path != "" ? " → " + Field(i) : "")}: {i.Message}. Исправление: {FixOf(i)}";
}
