using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>Kind of an imported visual/sound resource (docs/DECISIONS_2.md §34).</summary>
public enum AssetKind
{
    [Ed("3D-модель (glTF/GLB)")] Model,
    [Ed("Текстура (PNG/JPEG/WebP)")] Texture,
    [Ed("Спрайт / billboard")] Sprite,
    [Ed("Иконка")] Icon,
    [Ed("Звук (OGG/WAV)")] Sound,
    [Ed("Материал")] Material,
}

/// <summary>Animation roles of a unit (TZ §3.5): model clips are mapped to them, the base figure animates them procedurally.</summary>
public enum AnimRole
{
    [Ed("Ожидание")] Idle,
    [Ed("Движение")] Move,
    [Ed("Атака")] Attack,
    [Ed("Навык")] Skill,
    [Ed("Получение урона")] Hit,
    [Ed("Блокирование")] Block,
    [Ed("Контроль (оглушение…)")] Control,
    [Ed("Смерть")] Death,
    [Ed("Появление")] Spawn,
}

public sealed class AnimationMapDef
{
    [Ed("Роль")] public AnimRole Role { get; set; } = AnimRole.Idle;
    [Ed("Клип модели", "Имя анимации в файле модели")] public string Clip { get; set; } = "";
    [Ed("Скорость"), EdRange(0.05, 10)] public double Speed { get; set; } = 1;
    [Ed("Повтор")] public bool Loop { get; set; }

    public override string ToString() => $"{Role} → {Clip}";
}

/// <summary>
/// An imported resource: a file inside the content pack (assets/…) with its checksum and import
/// settings. Purely presentational: the simulation never reads it, and a missing or damaged file
/// falls back to the base geometry / silence.
/// </summary>
public sealed class AssetDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Asset;

    [Ed("Вид", Group = "Файл")] public AssetKind Kind { get; set; } = AssetKind.Model;
    [Ed("Файл", "Относительный путь в пакете (assets/…); абсолютные пути и «..» запрещены", Group = "Файл"), EdShowIf(nameof(Kind), AssetKind.Model, AssetKind.Texture, AssetKind.Sprite, AssetKind.Icon, AssetKind.Sound)]
    public string Path { get; set; } = "";
    [Ed("SHA-256", "Контрольная сумма файла при импорте", Group = "Файл"), EdReadOnly] public string Sha256 { get; set; } = "";
    [Ed("Размер, байт", Group = "Файл"), EdReadOnly] public long Size { get; set; }

    // Model.
    [Ed("Масштаб", Group = "Модель"), EdRange(0.01, 100), EdShowIf(nameof(Kind), AssetKind.Model)] public double Scale { get; set; } = 1;
    [Ed("Смещение по высоте", Group = "Модель"), EdRange(-10, 10), EdShowIf(nameof(Kind), AssetKind.Model)] public double YOffset { get; set; }
    [Ed("Поворот, °", "Чтобы «лицо» модели смотрело по направлению юнита", Group = "Модель"), EdRange(-360, 360), EdShowIf(nameof(Kind), AssetKind.Model)]
    public double RotationY { get; set; }
    [Ed("Клипы в файле", Group = "Модель"), EdReadOnly, EdShowIf(nameof(Kind), AssetKind.Model)] public List<string> Clips { get; set; } = new();
    [Ed("Анимации", "Соответствие ролей клипам; роль без клипа анимируется процедурно", Group = "Модель"), EdShowIf(nameof(Kind), AssetKind.Model)]
    public List<AnimationMapDef> Animations { get; set; } = new();

    // Sprite.
    [Ed("Всегда к камере (billboard)", Group = "Спрайт"), EdShowIf(nameof(Kind), AssetKind.Sprite)] public bool Billboard { get; set; } = true;
    [Ed("Размер пикселя", Group = "Спрайт"), EdRange(0.0005, 1), EdShowIf(nameof(Kind), AssetKind.Sprite)] public double PixelSize { get; set; } = 0.01;

    // Sound.
    [Ed("Громкость, дБ", Group = "Звук"), EdRange(-60, 24), EdShowIf(nameof(Kind), AssetKind.Sound)] public double VolumeDb { get; set; }
    [Ed("Повтор", Group = "Звук"), EdShowIf(nameof(Kind), AssetKind.Sound)] public bool Loop { get; set; }

    // Material.
    [Ed("Цвет", Group = "Материал"), EdColor, EdShowIf(nameof(Kind), AssetKind.Material)] public string Albedo { get; set; } = "#c0c4cc";
    [Ed("Текстура", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)] public string Texture { get; set; } = "";
    [Ed("Металличность", Group = "Материал"), EdRange(0, 1), EdShowIf(nameof(Kind), AssetKind.Material)] public double Metallic { get; set; }
    [Ed("Шероховатость", Group = "Материал"), EdRange(0, 1), EdShowIf(nameof(Kind), AssetKind.Material)] public double Roughness { get; set; } = 0.6;
    [Ed("Свечение", Group = "Материал"), EdColor, EdShowIf(nameof(Kind), AssetKind.Material)] public string Emission { get; set; } = "";
    [Ed("Сила свечения", Group = "Материал"), EdRange(0, 16), EdShowIf(nameof(Kind), AssetKind.Material)] public double EmissionEnergy { get; set; } = 1;
    [Ed("Прозрачность", Group = "Материал"), EdRange(0, 0.95), EdShowIf(nameof(Kind), AssetKind.Material)] public double Transparency { get; set; }

    [JsonIgnore] public bool HasFile => Kind != AssetKind.Material;
}

public enum VfxShape
{
    [Ed("Вспышка частиц")] Burst,
    [Ed("Расходящееся кольцо")] Ring,
    [Ed("Столб")] Pillar,
    [Ed("Искры вверх")] Sparks,
    [Ed("Дым / пыль")] Smoke,
}

/// <summary>A visual effect preset: particles + light + sound, played at a cell (docs/DECISIONS_2.md §34).</summary>
public sealed class VfxDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Vfx;

    [Ed("Форма")] public VfxShape Shape { get; set; } = VfxShape.Burst;
    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#ffb040";
    [Ed("Второй цвет", "Цвет к концу жизни частиц; пусто — прозрачный основной"), EdColor] public string Color2 { get; set; } = "";
    [Ed("Длительность, с"), EdRange(0.05, 10)] public double Duration { get; set; } = 0.6;
    [Ed("Размер", "В радиусах гекса"), EdRange(0.05, 10)] public double Size { get; set; } = 0.6;
    [Ed("Частиц"), EdRange(0, 500)] public int Count { get; set; } = 24;
    [Ed("Скорость частиц"), EdRange(0, 20)] public double Speed { get; set; } = 2;
    [Ed("Вспышка света")] public bool Light { get; set; } = true;
    [Ed("Сила света"), EdRange(0, 16), EdShowIf(nameof(Light), true)] public double LightEnergy { get; set; } = 2;
    [Ed("Спрайт частиц", "Необязательная текстура"), EdRef(DefCategory.Asset)] public string Sprite { get; set; } = "";
    [Ed("Звук"), EdRef(DefCategory.Asset)] public string Sound { get; set; } = "";
}

/// <summary>Effects of environment events (TZ §3.5): appearing effects, traps, destruction, terrain change, fall…</summary>
public sealed class EnvironmentVisualsDef
{
    [Ed("Появление эффекта клетки", "Если у эффекта клетки не задан свой"), EdRef(DefCategory.Vfx)] public string EffectAppear { get; set; } = "";
    [Ed("Срабатывание ловушки"), EdRef(DefCategory.Vfx)] public string TrapTriggered { get; set; } = "";
    [Ed("Разрушение препятствия"), EdRef(DefCategory.Vfx)] public string ObstacleDestroyed { get; set; } = "";
    [Ed("Изменение местности"), EdRef(DefCategory.Vfx)] public string TerrainChanged { get; set; } = "";
    [Ed("Падение в яму"), EdRef(DefCategory.Vfx)] public string Fall { get; set; } = "";
    [Ed("Появление юнита"), EdRef(DefCategory.Vfx)] public string UnitSpawn { get; set; } = "";
    [Ed("Гибель юнита"), EdRef(DefCategory.Vfx)] public string UnitDeath { get; set; } = "";
    [Ed("Попадание (урон)"), EdRef(DefCategory.Vfx)] public string Hit { get; set; } = "";
    [Ed("Звук попадания"), EdRef(DefCategory.Asset)] public string HitSound { get; set; } = "";
}
