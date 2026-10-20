using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

// Visual resources (docs/DECISIONS_3.md §47): limits, purposes, texture settings, PBR, attachment points,
// animation states and markers, hex effects, projectiles.

/// <summary>What a texture is for — decides its recommended and maximum size.</summary>
public enum TexturePurpose
{
    [Ed("Поверхность гекса")] HexSurface,
    [Ed("Материал окружения")] Environment,
    [Ed("Крупный атлас окружения")] Atlas,
    [Ed("Текстура персонажа")] Character,
    [Ed("Иконка навыка")] SkillIcon,
    [Ed("Иконка состояния")] StatusIcon,
    [Ed("Маска или карта шума")] Mask,
    [Ed("Прочее")] Other,
}

public sealed class TextureLimitDef
{
    [Ed("Назначение")] public TexturePurpose Purpose { get; set; }
    [Ed("Рекомендуемый минимум, пикс."), EdRange(1, 16384)] public int RecommendedMin { get; set; } = 512;
    [Ed("Рекомендуемый максимум, пикс."), EdRange(1, 16384)] public int RecommendedMax { get; set; } = 1024;
    [Ed("Максимум, пикс.", "Больше — отклонить или уменьшить при импорте"), EdRange(1, 16384)] public int Max { get; set; } = 4096;
    [Ed("Нужна прозрачность", "JPEG и изображения без альфа-канала — предупреждение")] public bool NeedsAlpha { get; set; }

    public override string ToString() => $"{Purpose}: {RecommendedMin}–{RecommendedMax}, макс. {Max}";
}

/// <summary>
/// Starting limits of resources (TZ §11–13, 15): stored with the rules, shown in the editor and on the requirements
/// page; changing them needs no code.
/// </summary>
public sealed class AssetLimitsDef
{
    [Ed("Текстуры по назначению")]
    public List<TextureLimitDef> Textures { get; set; } = new()
    {
        new() { Purpose = TexturePurpose.HexSurface, RecommendedMin = 512, RecommendedMax = 1024, Max = 4096 },
        new() { Purpose = TexturePurpose.Environment, RecommendedMin = 1024, RecommendedMax = 1024, Max = 4096 },
        new() { Purpose = TexturePurpose.Atlas, RecommendedMin = 2048, RecommendedMax = 2048, Max = 4096 },
        new() { Purpose = TexturePurpose.Character, RecommendedMin = 1024, RecommendedMax = 2048, Max = 4096 },
        new() { Purpose = TexturePurpose.SkillIcon, RecommendedMin = 128, RecommendedMax = 256, Max = 1024, NeedsAlpha = true },
        new() { Purpose = TexturePurpose.StatusIcon, RecommendedMin = 64, RecommendedMax = 128, Max = 512, NeedsAlpha = true },
        new() { Purpose = TexturePurpose.Mask, RecommendedMin = 256, RecommendedMax = 1024, Max = 2048 },
        new() { Purpose = TexturePurpose.Other, RecommendedMin = 64, RecommendedMax = 2048, Max = 4096 },
    };
    [Ed("Макс. размер файла изображения, МБ"), EdRange(1, 1024)] public double MaxImageMb { get; set; } = 64;
    [Ed("Макс. размер файла модели, МБ"), EdRange(1, 4096)] public double MaxModelMb { get; set; } = 100;
    [Ed("Предупреждение по размеру модели, МБ"), EdRange(1, 4096)] public double WarnModelMb { get; set; } = 25;
    [Ed("Предупреждение: треугольников персонажа"), EdRange(100, 100_000_000)] public int WarnTriangles { get; set; } = 100_000;
    [Ed("Предупреждение: костей скелета"), EdRange(1, 10_000)] public int WarnBones { get; set; } = 256;
    [Ed("Предупреждение: длительность анимации, с"), EdRange(1, 3600)] public double WarnAnimationSeconds { get; set; } = 60;
    [Ed("Макс. размер звука, МБ"), EdRange(1, 1024)] public double MaxSoundMb { get; set; } = 64;
    [Ed("Предупреждение: длительность звука, с"), EdRange(1, 3600)] public double WarnSoundSeconds { get; set; } = 15;
    [Ed("Предупреждение: суммарный объём ресурсов, МБ", "Для всего пакета"), EdRange(1, 100_000)] public double WarnTotalMb { get; set; } = 512;

    public TextureLimitDef For(TexturePurpose p) => Textures.FirstOrDefault(t => t.Purpose == p) ?? Textures.FirstOrDefault(t => t.Purpose == TexturePurpose.Other) ?? new TextureLimitDef();
}

public enum TextureFilter
{
    [Ed("Сглаживание с mipmap")] LinearMipmap,
    [Ed("Сглаживание")] Linear,
    [Ed("Пиксельная (ближайший)")] Nearest,
}

public enum MaterialBlend
{
    [Ed("Обычное")] Mix,
    [Ed("Сложение (свечение)")] Add,
    [Ed("Умножение")] Multiply,
}

/// <summary>Attachment points of a model (TZ §12.4).</summary>
public enum AttachmentPoint
{
    [Ed("Оружие")] Weapon,
    [Ed("Снаряд")] Projectile,
    [Ed("Источник магии")] MagicSource,
    [Ed("Точка попадания")] Hit,
    [Ed("Эффект смерти")] Death,
    [Ed("Индикатор состояния")] Status,
    [Ed("Направление взгляда")] Facing,
}

public sealed class AttachmentDef
{
    [Ed("Точка")] public AttachmentPoint Point { get; set; } = AttachmentPoint.Weapon;
    [Ed("Узел модели", "Имя узла в файле; пусто — смещение от опоры")] public string Node { get; set; } = "";
    [Ed("Смещение X"), EdRange(-10, 10)] public double X { get; set; }
    [Ed("Смещение Y (вверх)"), EdRange(-10, 10)] public double Y { get; set; } = 0.5;
    [Ed("Смещение Z (вперёд)"), EdRange(-10, 10)] public double Z { get; set; }

    public override string ToString() => $"{Point}: {(string.IsNullOrEmpty(Node) ? $"({X:0.##}, {Y:0.##}, {Z:0.##})" : Node)}";
}

/// <summary>Game markers inside an animation clip (TZ §13.4).</summary>
public enum AnimMarker
{
    [Ed("Начало удара")] StrikeStart,
    [Ed("Проверка попадания")] HitCheck,
    [Ed("Выпуск снаряда")] ProjectileRelease,
    [Ed("Создание области поражения")] AreaCreate,
    [Ed("Появление эффекта")] EffectAppear,
    [Ed("Окончание каста")] CastEnd,
    [Ed("Завершение движения")] MoveEnd,
    [Ed("Начало защиты")] DefenseStart,
    [Ed("Конец защиты")] DefenseEnd,
}

public sealed class AnimMarkerDef
{
    [Ed("Маркер")] public AnimMarker Kind { get; set; } = AnimMarker.HitCheck;
    [Ed("Время в клипе, с"), EdRange(0, 600)] public double Time { get; set; } = 0.3;

    public override string ToString() => $"{Kind} @ {Time:0.##} с";
}

public enum AnimEndRule
{
    [Ed("Вернуться в ожидание")] ReturnIdle,
    [Ed("Держать последний кадр")] HoldLast,
    [Ed("Повторять")] Loop,
}

public enum VfxType
{
    [Ed("Частицы")] Particles,
    [Ed("Статичная текстура")] Texture,
    [Ed("Анимированная текстура")] AnimatedTexture,
    [Ed("Модель")] Model,
    [Ed("Проекция на поверхность (decal)")] Decal,
    [Ed("Свечение / объём")] Glow,
    [Ed("Линия")] Line,
    [Ed("Конус")] Cone,
    [Ed("Контур области")] AreaOutline,
    [Ed("Комбинация слоёв")] Composite,
}

public enum VfxOverlay
{
    [Ed("Добавлять поверх")] Add,
    [Ed("Заменять прежний")] Replace,
    [Ed("Не дублировать")] NoDuplicate,
}

public enum ProjectileTrajectory
{
    [Ed("Прямая")] Straight,
    [Ed("Дуга")] Arc,
    [Ed("Мгновенно (луч)")] Instant,
}

public enum ProjectileVanish
{
    [Ed("При попадании")] OnHit,
    [Ed("В точке цели")] AtTarget,
    [Ed("Растворяется")] Fade,
}

/// <summary>
/// The look of the projectile of a skill stage (TZ §17.3). Display only: the stage computes the hit; the flight time
/// is the visual part of the stage's delay, never the reason of the damage.
/// </summary>
public sealed class ProjectileDef
{
    [Ed("Есть снаряд")] public bool Enabled { get; set; }
    [Ed("Модель", "Ресурс «Модель»; пусто — светящаяся капля цвета навыка"), EdRef(DefCategory.Asset), EdShowIf(nameof(Enabled), true)] public string Model { get; set; } = "";
    [Ed("Траектория"), EdShowIf(nameof(Enabled), true)] public ProjectileTrajectory Trajectory { get; set; } = ProjectileTrajectory.Straight;
    [Ed("Скорость, гексов/с"), EdRange(0.5, 100), EdShowIf(nameof(Enabled), true)] public double Speed { get; set; } = 12;
    [Ed("Вращение, об/с"), EdRange(0, 20), EdShowIf(nameof(Enabled), true)] public double Spin { get; set; }
    [Ed("Цвет следа", "Пусто — без следа"), EdColor, EdShowIf(nameof(Enabled), true)] public string Trail { get; set; } = "";
    [Ed("Эффект запуска"), EdRef(DefCategory.Vfx), EdShowIf(nameof(Enabled), true)] public string LaunchVfx { get; set; } = "";
    [Ed("Эффект попадания"), EdRef(DefCategory.Vfx), EdShowIf(nameof(Enabled), true)] public string HitVfx { get; set; } = "";
    [Ed("Эффект промаха"), EdRef(DefCategory.Vfx), EdShowIf(nameof(Enabled), true)] public string MissVfx { get; set; } = "";
    [Ed("Эффект на гексе"), EdRef(DefCategory.Vfx), EdShowIf(nameof(Enabled), true)] public string CellVfx { get; set; } = "";
    [Ed("Звук полёта"), EdRef(DefCategory.Asset), EdShowIf(nameof(Enabled), true)] public string Sound { get; set; } = "";
    [Ed("Исчезновение"), EdShowIf(nameof(Enabled), true)] public ProjectileVanish Vanish { get; set; } = ProjectileVanish.OnHit;
    [Ed("Огибает препятствия", "Только вид: дуга над стенами; урон всё равно решает линия видимости этапа"), EdShowIf(nameof(Enabled), true)] public bool OverObstacles { get; set; } = true;
}

/// <summary>A visual variant of a terrain (chosen by the cell's coordinates — deterministic, never changes the rules).</summary>
public sealed class TerrainVariantDef
{
    [Ed("Текстура верха"), EdRef(DefCategory.Asset)] public string TopTexture { get; set; } = "";
    [Ed("Материал верха"), EdRef(DefCategory.Asset)] public string TopMaterial { get; set; } = "";
    [Ed("Оттенок", "Пусто — без изменения"), EdColor] public string Tint { get; set; } = "";
    [Ed("Вес", "Чаще выбирается вариант с большим весом"), EdRange(0, 100)] public double Weight { get; set; } = 1;

    public override string ToString() => $"{(string.IsNullOrEmpty(TopTexture) ? TopMaterial : TopTexture)} ×{Weight:0.#}";
}
