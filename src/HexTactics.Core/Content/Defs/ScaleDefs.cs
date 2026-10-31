using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Scales: sanity, morale, stability… (ТЗ ч. 6, 18; docs/DECISIONS_6.md §104). A number of a unit that changes on events and
// acts only through what exists: its bands put ordinary statuses on, its curves give ordinary modifiers of characteristics.

/// <summary>The bounds of a scale (ТЗ 18.2).</summary>
public enum ScaleLimitMode
{
    [Ed("Ограничить (Clamp)", "Значение не выходит за мин. и макс.")] Clamp,
    [Ed("Мягкие границы (SoftCap)", "За мин./макс. — до жёстких пределов, прирост падает по кривой")] SoftCap,
    [Ed("Переход границ (Overflow)", "За мин./макс. свободно — до жёстких пределов")] Overflow,
}

/// <summary>What changes a scale (ТЗ 18.3) — the events the battle already has.</summary>
public enum ScaleEvent
{
    [Ed("Нанёс урон")] DamageDealt,
    [Ed("Получил урон")] DamageTaken,
    [Ed("Попал")] Hit,
    [Ed("Промахнулся")] Missed,
    [Ed("Критический удар (нанёс)")] Crit,
    [Ed("Погиб союзник", "В радиусе; 0 — где угодно")] AllyDied,
    [Ed("Погиб враг", "В радиусе; 0 — где угодно")] EnemyDied,
    [Ed("Погиб лидер", "Союзник с тегом-фильтром (пусто — leader)")] LeaderDied,
    [Ed("Потеряна база или сооружение своей стороны")] BaseLost,
    [Ed("Вылечен")] Healed,
    [Ed("Убил цель")] Killed,
    [Ed("Окончена волна")] WaveEnded,
    [Ed("Применил навык", "Фильтр — ID навыка")] SkillUsed,
    [Ed("Получил состояние", "Фильтр — ID состояния")] StatusReceived,
    [Ed("Рядом с тегом или фракцией", "Каждые N с, пока в радиусе есть такой юнит")] Near,
    [Ed("Каждые N с")] Timer,
    [Ed("Босс сменил фазу")] BossPhase,
    [Ed("Действие сценария или навыка", "«Изменить шкалу» триггера, графа или операции навыка")] Script,
}

/// <summary>A rule of change: on an event, by how much, when, how often (ТЗ 18.3).</summary>
public sealed class ScaleRuleDef : ContentPart
{
    [Ed("Название", "Источник в журнале")] public string Name { get; set; } = "";
    [Ed("Включено")] public bool Enabled { get; set; } = true;
    [Ed("Событие")] public ScaleEvent Event { get; set; } = ScaleEvent.AllyDied;
    [Ed("Изменение", "Знак — вверх или вниз")] public double Amount { get; set; } = -10;
    [Ed("В % диапазона", "Изменение — доля от (макс. − мин.)")] public bool Percent { get; set; }
    [Ed("Радиус, гексов", "Гибель, «рядом»; 0 — где угодно"), EdRange(0, 100)] public int Radius { get; set; }
    [Ed("Фильтр", "Тег, категория или шаблон другого юнита (для «рядом» — и фракция); ID навыка или состояния")] public string Filter { get; set; } = "";
    [Ed("Условие", "Условие владельца шкалы; пусто — всегда")] public UnitConditionDef? Condition { get; set; }
    [Ed("Один раз за бой")] public bool Once { get; set; }
    [Ed("Каждые, с", "Для «каждые N с» и «рядом»"), EdRange(0, 3600)] public double Interval { get; set; } = 1;
    [Ed("Перезарядка, с", "Не чаще раза в N с"), EdRange(0, 3600)] public double Cooldown { get; set; }
    [Ed("Приоритет", "В один тик сначала больший"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Предел за событие", "Наибольшее изменение по модулю; 0 — без предела"), EdRange(0, 100000)] public double MaxPerEvent { get; set; }
    [Ed("Складывать в тике", "Несколько событий одного тика складываются; иначе — одно")] public bool Stack { get; set; } = true;

    public string Describe() => $"{EdUtil.EnumLabel(Event)}: {Amount:+0.##;-0.##}{(Percent ? " %" : "")}" + (Filter != "" ? $" [{Filter}]" : "") + (Once ? ", один раз" : "");
    public override string ToString() => Name != "" ? Name : Describe();
}

/// <summary>A point of a curve.</summary>
public sealed class ScaleCurvePointDef : ContentPart
{
    [Ed("Значение шкалы")] public double X { get; set; }
    [Ed("Результат")] public double Y { get; set; }
    public override string ToString() => $"({X:0.##}; {Y:0.##})";
}

public enum ScaleCurveOp
{
    [Ed("Прибавка")] Add,
    [Ed("Множитель", "Доля: 0.2 = +20 %")] Multiply,
    [Ed("Замена", "Характеристика становится значением кривой")] Override,
}

/// <summary>(B) A curve «шкала → характеристика» (ТЗ 18.4): carried out as an ordinary modifier of the characteristic.</summary>
public sealed class ScaleCurveDef : ContentPart
{
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Включена")] public bool Enabled { get; set; } = true;
    [Ed("Характеристика"), EdRef(DefCategory.Stat, AllowEmpty = false)] public string Stat { get; set; } = "";
    [Ed("Как")] public ScaleCurveOp Op { get; set; } = ScaleCurveOp.Multiply;
    [Ed("Точки", "Ломаная по значениям шкалы; за крайними точками — значение крайней")] public List<ScaleCurvePointDef> Points { get; set; } = new();

    /// <summary>The value of the curve (piecewise linear; flat beyond the ends; 0 without points).</summary>
    public double Eval(double x)
    {
        if (Points.Count == 0 || double.IsNaN(x)) return 0;
        var pts = Points.OrderBy(p => p.X).ToList();
        if (x <= pts[0].X) return pts[0].Y;
        if (x >= pts[^1].X) return pts[^1].Y;
        for (int i = 1; i < pts.Count; i++)
            if (x <= pts[i].X)
            {
                double w = pts[i].X - pts[i - 1].X;
                return w <= 0 ? pts[i].Y : pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * (x - pts[i - 1].X) / w;
            }
        return pts[^1].Y;
    }

    public override string ToString() => Name != "" ? Name : $"{Stat}: {EdUtil.EnumLabel(Op)}";
}

/// <summary>
/// (A) A band of the scale (ТЗ 18.4–18.5): while the value is in it, an ordinary status is on — panic, rage, numbness… — with a
/// chance, a re-check, a cooldown and a hysteresis (no restart every tick below a threshold).
/// </summary>
public sealed class ScaleBandDef : ContentPart
{
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Включён")] public bool Enabled { get; set; } = true;
    [Ed("От")] public double From { get; set; } = -200;
    [Ed("До")] public double To { get; set; } = -100;
    [Ed("Гистерезис", "Выход — только когда значение ушло за диапазон на столько"), EdRange(0, 100000)] public double Hysteresis { get; set; } = 10;
    [Ed("Состояние", "Обычное состояние: паника, ярость, оцепенение…"), EdRef(DefCategory.Status, AllowEmpty = false)] public string Status { get; set; } = "";
    [Ed("Шанс", "Проверка ГСЧ боя при входе (1 — всегда)"), EdRange(0, 1)] public double Chance { get; set; } = 1;
    [Ed("Перепроверка, с", "Не вышло или состояние кончилось — новая попытка через N с, пока в диапазоне; 0 — только после выхода"), EdRange(0, 3600)]
    public double Recheck { get; set; }
    [Ed("Длительность, с", "0 — пока в диапазоне"), EdRange(0, 3600)] public double Duration { get; set; }
    [Ed("Перезарядка, с", "После конца состояния — не раньше"), EdRange(0, 3600)] public double Cooldown { get; set; } = 5;

    public override string ToString() => (Name != "" ? Name : Status) + $" [{From:0.##}; {To:0.##}]";
}

/// <summary>
/// A scale (ТЗ 18; §104): «Боевой дух», «Санити», «Стабильность»… — a profile of the library units refer to. Bounds and their
/// mode, the start, the recovery, the rules of change, the bands and the curves.
/// </summary>
public sealed class ScaleProfileDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Scale;

    [Ed("Короткое имя", "На шкале юнита: SAN, ДУХ…")] public string Short { get; set; } = "SAN";
    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#b07cff";
    [Ed("Минимум", Group = "Границы")] public double Min { get; set; } = -200;
    [Ed("Максимум", Group = "Границы")] public double Max { get; set; } = 200;
    [Ed("Режим границ", Group = "Границы")] public ScaleLimitMode Mode { get; set; } = ScaleLimitMode.Clamp;
    [Ed("Жёсткий минимум", "SoftCap и Overflow: ниже нельзя", Group = "Границы"), EdShowIf(nameof(Mode), ScaleLimitMode.SoftCap, ScaleLimitMode.Overflow)]
    public double HardMin { get; set; } = -400;
    [Ed("Жёсткий максимум", "SoftCap и Overflow: выше нельзя", Group = "Границы"), EdShowIf(nameof(Mode), ScaleLimitMode.SoftCap, ScaleLimitMode.Overflow)]
    public double HardMax { get; set; } = 400;
    [Ed("Прирост за мягкой границей", "Доля прироста (Y) по тому, как далеко за границей (X: 0 — у границы, 1 — у жёсткого предела)", Group = "Границы"),
     EdShowIf(nameof(Mode), ScaleLimitMode.SoftCap)]
    public List<ScaleCurvePointDef> SoftCurve { get; set; } = new() { new() { X = 0, Y = 1 }, new() { X = 1, Y = 0.1 } };
    [Ed("Начальное значение", Group = "Значения")] public double Start { get; set; }
    [Ed("Базовое значение", "Отметка «норма» на шкале", Group = "Значения")] public double Base { get; set; }
    [Ed("Значение при появлении", "Призванные и появившиеся в бою", Group = "Значения")] public double SpawnValue { get; set; }
    [Ed("Цель восстановления", Group = "Значения")] public double RecoverTarget { get; set; }
    [Ed("Восстановление в секунду", "К цели, в игровом времени; 0 — без естественного восстановления", Group = "Значения"), EdRange(0, 10000)]
    public double Recovery { get; set; } = 1;
    [Ed("Не получают изменений", "Теги юнитов с иммунитетом: значение видно, события его не меняют", Group = "Значения")]
    public List<string> ImmuneTags { get; set; } = new();
    [Ed("Правила изменения")] public List<ScaleRuleDef> Rules { get; set; } = new();
    [Ed("Диапазоны → состояния")] public List<ScaleBandDef> Bands { get; set; } = new();
    [Ed("Кривые → характеристики")] public List<ScaleCurveDef> Curves { get; set; } = new();

    /// <summary>The lowest and the highest value the mode allows.</summary>
    [JsonIgnore] public double Lowest => Mode == ScaleLimitMode.Clamp ? Min : HardMin;
    [JsonIgnore] public double Highest => Mode == ScaleLimitMode.Clamp ? Max : HardMax;
}

public enum ScaleUseMode
{
    [Ed("Действует")] Active,
    [Ed("Иммунитет", "Значение видно, события его не меняют")] Immune,
}

/// <summary>A scale of a unit template (ТЗ 18.6): the profile, the mode, its own start.</summary>
public sealed class ScaleUseDef : ContentPart
{
    [Ed("Шкала"), EdRef(DefCategory.Scale, AllowEmpty = false)] public string Profile { get; set; } = "";
    [Ed("Режим")] public ScaleUseMode Mode { get; set; } = ScaleUseMode.Active;
    [Ed("Своё начальное значение")] public bool OwnStart { get; set; }
    [Ed("Начальное значение"), EdShowIf(nameof(OwnStart), true)] public double Start { get; set; }

    public override string ToString() => Profile + (Mode == ScaleUseMode.Immune ? " (иммунитет)" : "");
}
