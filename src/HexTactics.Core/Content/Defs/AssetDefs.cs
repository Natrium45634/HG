using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>Kind of an imported visual/sound resource (docs/DECISIONS_2.md §34).</summary>
public enum AssetKind
{
    [Ed("3D-модель (glTF/GLB, FBX, OBJ)")] Model,
    [Ed("Текстура (PNG/JPEG/WebP/TGA/HDR/EXR)")] Texture,
    [Ed("Спрайт / billboard")] Sprite,
    [Ed("Иконка")] Icon,
    [Ed("Звук (OGG/WAV/MP3)")] Sound,
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
    // Part 3 (docs/DECISIONS_3.md §47).
    [Ed("Поворот")] Turn,
    [Ed("Уклонение")] Dodge,
    [Ed("Полёт")] Fly,
    [Ed("Подземное движение")] Burrow,
    [Ed("Взаимодействие")] Interact,
    [Ed("Победа / эмоция")] Victory,
}

public sealed class AnimationMapDef : ContentPart
{
    [Ed("Роль")] public AnimRole Role { get; set; } = AnimRole.Idle;
    [Ed("Клип модели", "Имя анимации в файле модели")] public string Clip { get; set; } = "";
    [Ed("Скорость"), EdRange(0.05, 10)] public double Speed { get; set; } = 1;
    [Ed("Повтор")] public bool Loop { get; set; }
    // Part 3 (docs/DECISIONS_3.md §47): variants, transitions, markers.
    [Ed("Варианты", "Другие клипы этой роли; выбирается случайно (генератор интерфейса, не боя)")] public List<string> Variants { get; set; } = new();
    [Ed("Приоритет", "Роль с большим приоритетом прерывает меньшую (смерть > контроль > атака…)"), EdRange(0, 100)] public int Priority { get; set; }
    [Ed("Прерываемая", "Другая роль может прервать клип до конца")] public bool Interruptible { get; set; } = true;
    [Ed("Смешивание, с", "Плавный переход из предыдущего клипа"), EdRange(0, 2)] public double Blend { get; set; } = 0.15;
    [Ed("По окончании")] public AnimEndRule End { get; set; } = AnimEndRule.ReturnIdle;
    [Ed("Маркеры", "Игровые моменты в клипе: проверка попадания, выпуск снаряда…; клип подстраивается под момент симуляции")]
    public List<AnimMarkerDef> Markers { get; set; } = new();

    public override string ToString() => $"{Role} → {Clip}" + (Variants.Count > 0 ? $" (+{Variants.Count})" : "") + (Markers.Count > 0 ? $" · маркеров {Markers.Count}" : "");
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
    // Part 3 (docs/DECISIONS_3.md §47): description of the resource.
    [Ed("Исходное имя файла", Group = "Файл"), EdReadOnly] public string SourceFile { get; set; } = "";
    [Ed("Дата импорта", Group = "Файл"), EdReadOnly] public string ImportedAt { get; set; } = "";
    [Ed("Заметки автора", Group = "Файл"), EdMultiline] public string Notes { get; set; } = "";
    // Part 5 (ТЗ 7.5; docs/DECISIONS_5.md §89): where the file comes from and on what terms — optional text, never checked.
    [Ed("Лицензия", "Необязательно: автор, источник и условия использования (CC0, CC-BY 4.0 — автор…, собственная работа)", Group = "Файл"), EdMultiline, JsonOmitEmpty]
    public string License { get; set; } = "";
    [Ed("Анализ файла", "Размеры, треугольники, кости, анимации — при импорте", Group = "Файл"), EdReadOnly] public string Analysis { get; set; } = "";
    [Ed("Ширина, пикс.", Group = "Файл"), EdReadOnly] public int Width { get; set; }
    [Ed("Высота, пикс.", Group = "Файл"), EdReadOnly] public int Height { get; set; }
    [Ed("Треугольников", Group = "Файл"), EdReadOnly] public int Triangles { get; set; }
    [Ed("Костей", Group = "Файл"), EdReadOnly] public int Bones { get; set; }

    // Texture (docs/DECISIONS_3.md §47).
    [Ed("Назначение", "Определяет рекомендуемый и максимальный размер", Group = "Текстура"), EdShowIf(nameof(Kind), AssetKind.Texture, AssetKind.Sprite, AssetKind.Icon)]
    public TexturePurpose Purpose { get; set; } = TexturePurpose.Other;
    [Ed("Фильтрация", Group = "Текстура"), EdShowIf(nameof(Kind), AssetKind.Texture, AssetKind.Sprite, AssetKind.Icon)] public TextureFilter Filter { get; set; } = TextureFilter.LinearMipmap;
    [Ed("Повторение", Group = "Текстура"), EdShowIf(nameof(Kind), AssetKind.Texture)] public bool Repeat { get; set; } = true;
    [Ed("Поворот, °", Group = "Текстура"), EdRange(-360, 360), EdShowIf(nameof(Kind), AssetKind.Texture, AssetKind.Material)] public double UvRotation { get; set; }
    [Ed("Масштаб", Group = "Текстура"), EdRange(0.01, 100), EdShowIf(nameof(Kind), AssetKind.Texture, AssetKind.Material)] public double UvScale { get; set; } = 1;
    [Ed("Смещение U", Group = "Текстура"), EdRange(-10, 10), EdShowIf(nameof(Kind), AssetKind.Texture, AssetKind.Material)] public double UvOffsetU { get; set; }
    [Ed("Смещение V", Group = "Текстура"), EdRange(-10, 10), EdShowIf(nameof(Kind), AssetKind.Texture, AssetKind.Material)] public double UvOffsetV { get; set; }
    [Ed("Есть альфа-канал", Group = "Текстура"), EdReadOnly, EdShowIf(nameof(Kind), AssetKind.Texture, AssetKind.Sprite, AssetKind.Icon)] public bool HasAlpha { get; set; }

    // Model.
    [Ed("Масштаб", Group = "Модель"), EdRange(0.01, 100), EdShowIf(nameof(Kind), AssetKind.Model)] public double Scale { get; set; } = 1;
    [Ed("Смещение по высоте", Group = "Модель"), EdRange(-10, 10), EdShowIf(nameof(Kind), AssetKind.Model)] public double YOffset { get; set; }
    [Ed("Поворот, °", "Чтобы «лицо» модели смотрело по направлению юнита", Group = "Модель"), EdRange(-360, 360), EdShowIf(nameof(Kind), AssetKind.Model)]
    public double RotationY { get; set; }
    [Ed("Клипы в файле", Group = "Модель"), EdReadOnly, EdShowIf(nameof(Kind), AssetKind.Model)] public List<string> Clips { get; set; } = new();
    [Ed("Анимации", "Соответствие ролей клипам; роль без клипа анимируется процедурно", Group = "Модель"), EdShowIf(nameof(Kind), AssetKind.Model)]
    public List<AnimationMapDef> Animations { get; set; } = new();
    [Ed("Длительности клипов, с", Group = "Модель"), EdReadOnly, EdShowIf(nameof(Kind), AssetKind.Model)] public List<double> ClipLengths { get; set; } = new();
    [Ed("Точки привязки", "Оружие, снаряд, источник магии, попадание, смерть, индикатор состояния, взгляд", Group = "Модель"), EdShowIf(nameof(Kind), AssetKind.Model)]
    public List<AttachmentDef> Attachments { get; set; } = new();
    [Ed("Упрощённая модель", "Для «упрощённых эффектов»", Group = "Модель"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Model)] public string Simplified { get; set; } = "";
    [Ed("Резервная модель", "Если эта не загрузилась; потом — базовая фишка", Group = "Модель"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Model)] public string Fallback { get; set; } = "";

    // Sprite.
    [Ed("Всегда к камере (billboard)", Group = "Спрайт"), EdShowIf(nameof(Kind), AssetKind.Sprite)] public bool Billboard { get; set; } = true;
    [Ed("Размер пикселя", Group = "Спрайт"), EdRange(0.0005, 1), EdShowIf(nameof(Kind), AssetKind.Sprite)] public double PixelSize { get; set; } = 0.01;

    // Sound.
    [Ed("Громкость, дБ", Group = "Звук"), EdRange(-60, 24), EdShowIf(nameof(Kind), AssetKind.Sound)] public double VolumeDb { get; set; }
    [Ed("Повтор", Group = "Звук"), EdShowIf(nameof(Kind), AssetKind.Sound)] public bool Loop { get; set; }
    [Ed("Пространственный", "Звучит из точки на поле (громче рядом с камерой)", Group = "Звук"), EdShowIf(nameof(Kind), AssetKind.Sound)] public bool Spatial { get; set; } = true;
    [Ed("Радиус слышимости, гексов", Group = "Звук"), EdRange(1, 200), EdShowIf(nameof(Spatial), true)] public double Radius { get; set; } = 20;
    [Ed("Варианты", "Другие звуки; при воспроизведении выбирается случайный", Group = "Звук"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Sound)]
    public List<string> Variants { get; set; } = new();
    [Ed("Длительность, с", Group = "Звук"), EdReadOnly, EdShowIf(nameof(Kind), AssetKind.Sound)] public double Duration { get; set; }

    // Material.
    [Ed("Цвет", Group = "Материал"), EdColor, EdShowIf(nameof(Kind), AssetKind.Material)] public string Albedo { get; set; } = "#c0c4cc";
    [Ed("Текстура", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)] public string Texture { get; set; } = "";
    [Ed("Металличность", Group = "Материал"), EdRange(0, 1), EdShowIf(nameof(Kind), AssetKind.Material)] public double Metallic { get; set; }
    [Ed("Шероховатость", Group = "Материал"), EdRange(0, 1), EdShowIf(nameof(Kind), AssetKind.Material)] public double Roughness { get; set; } = 0.6;
    [Ed("Свечение", Group = "Материал"), EdColor, EdShowIf(nameof(Kind), AssetKind.Material)] public string Emission { get; set; } = "";
    [Ed("Сила свечения", Group = "Материал"), EdRange(0, 16), EdShowIf(nameof(Kind), AssetKind.Material)] public double EmissionEnergy { get; set; } = 1;
    [Ed("Прозрачность", Group = "Материал"), EdRange(0, 0.95), EdShowIf(nameof(Kind), AssetKind.Material)] public double Transparency { get; set; }
    // PBR maps (docs/DECISIONS_3.md §47): what the renderer cannot use is reported, never silently ignored.
    [Ed("Карта нормалей", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)] public string NormalMap { get; set; } = "";
    [Ed("Карта шероховатости", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)] public string RoughnessMap { get; set; } = "";
    [Ed("Карта металличности", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)] public string MetallicMap { get; set; } = "";
    [Ed("Карта свечения", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)] public string EmissionMap { get; set; } = "";
    [Ed("Маска деталей", "Грязь, трещины, повреждения: где виден детальный слой", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)]
    public string DetailMask { get; set; } = "";
    [Ed("Текстура деталей", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)] public string DetailTexture { get; set; } = "";
    [Ed("Режим смешивания", Group = "Материал"), EdShowIf(nameof(Kind), AssetKind.Material)] public MaterialBlend Blend { get; set; } = MaterialBlend.Mix;
    [Ed("Дальность детализации", "Дальше этого расстояния камеры детальный слой и нормали не рисуются (0 — всегда)", Group = "Материал"), EdRange(0, 1000),
     EdShowIf(nameof(Kind), AssetKind.Material)]
    public double LodDistance { get; set; }
    [Ed("Упрощённый материал", "Для слабых систем («упрощённые эффекты»)", Group = "Материал"), EdRef(DefCategory.Asset), EdShowIf(nameof(Kind), AssetKind.Material)]
    public string SimplifiedMaterial { get; set; } = "";

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

    // Part 3 (docs/DECISIONS_3.md §47): kinds of hex effects and their parameters.
    [Ed("Тип эффекта")] public VfxType Type { get; set; } = VfxType.Particles;
    [Ed("Текстура", "Для текстуры, анимации, проекции, линии, конуса"), EdRef(DefCategory.Asset),
     EdShowIf(nameof(Type), VfxType.Texture, VfxType.AnimatedTexture, VfxType.Decal, VfxType.Line, VfxType.Cone, VfxType.Glow)]
    public string Texture { get; set; } = "";
    [Ed("Кадров по горизонтали"), EdRange(1, 64), EdShowIf(nameof(Type), VfxType.AnimatedTexture)] public int FramesX { get; set; } = 4;
    [Ed("Кадров по вертикали"), EdRange(1, 64), EdShowIf(nameof(Type), VfxType.AnimatedTexture)] public int FramesY { get; set; } = 1;
    [Ed("Кадров в секунду"), EdRange(1, 120), EdShowIf(nameof(Type), VfxType.AnimatedTexture)] public double Fps { get; set; } = 12;
    [Ed("Модель"), EdRef(DefCategory.Asset), EdShowIf(nameof(Type), VfxType.Model)] public string Model { get; set; } = "";
    [Ed("Слои", "Эффекты, показываемые вместе"), EdRef(DefCategory.Vfx), EdShowIf(nameof(Type), VfxType.Composite)] public List<string> Layers { get; set; } = new();
    [Ed("Появление, с"), EdRange(0, 10)] public double FadeIn { get; set; }
    [Ed("Затухание, с"), EdRange(0, 10)] public double FadeOut { get; set; } = 0.2;
    [Ed("Циклический", "Повторяется, пока длится причина (эффект клетки, состояние)")] public bool Loop { get; set; }
    [Ed("Интенсивность"), EdRange(0, 10)] public double Intensity { get; set; } = 1;
    [Ed("Высота над поверхностью"), EdRange(-2, 10)] public double Height { get; set; } = 0.02;
    [Ed("Поворот, °"), EdRange(-360, 360)] public double Orientation { get; set; }
    [Ed("Непрозрачность"), EdRange(0, 1)] public double Opacity { get; set; } = 1;
    [Ed("Порядок отображения", "Больше — поверх"), EdRange(-100, 100)] public int Order { get; set; }
    [Ed("Наложение", "Если на гексе уже есть такой эффект")] public VfxOverlay Overlay { get; set; } = VfxOverlay.Add;
    [Ed("Упрощённый вариант", "Для «упрощённых эффектов»"), EdRef(DefCategory.Vfx)] public string Simplified { get; set; } = "";

    [Ed("Форма частиц"), EdShowIf(nameof(Type), VfxType.Particles)] public VfxShape Shape { get; set; } = VfxShape.Burst;
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
public sealed class EnvironmentVisualsDef : ContentPart
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
