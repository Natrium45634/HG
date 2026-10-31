using System;
using System.IO;
using System.Text.Json;
using Godot;
using HexTactics.Core.Content;

namespace HexTactics.Game.App;

/// <summary>Per-user settings (not content): camera, interface, input conveniences.</summary>
public sealed class Settings
{
    [Ed("Наклон камеры, °", "Угол между взглядом и плоскостью стола (90 — строго сверху)"), EdRange(35, 89)]
    public double CameraTilt { get; set; } = 60;
    [Ed("Прокрутка у края экрана")] public bool EdgeScroll { get; set; } = true;
    [Ed("Скорость камеры"), EdRange(0.2, 5)] public double CameraSpeed { get; set; } = 1.0;
    [Ed("Меню действий по клику на юнита", "Прежний способ (часть 1); по умолчанию — стрелка взгляда и панель команд")] public bool ActionMenuOnClick { get; set; }
    [Ed("Добавлять команды в очередь по умолчанию", "Иначе клик заменяет очередь; Shift инвертирует")] public bool AppendByDefault { get; set; } = true;
    [Ed("Подробный журнал")] public bool DetailedLog { get; set; }
    [Ed("Всплывающие числа урона")] public bool FloatingNumbers { get; set; } = true;
    [Ed("Показывать зону досягаемости")] public bool ShowMoveRange { get; set; } = true;
    [Ed("Полный экран")] public bool Fullscreen { get; set; }
    [Ed("Масштаб интерфейса"), EdRange(0.75, 2)] public double UiScale { get; set; } = 1.0;
    [Ed("Тени")] public bool Shadows { get; set; } = true;
    [Ed("Затенение (SSAO)")] public bool Ssao { get; set; } = true;

    // Control (docs/DECISIONS_3.md §39, §40, §41, §50).
    [Ed("Автопауза", "Общий выключатель автопаузы при планировании", Group = "Управление")] public bool AutopauseEnabled { get; set; } = true;
    [Ed("Пауза при выборе юнита", Group = "Управление"), EdShowIf(nameof(AutopauseEnabled), true)] public bool PauseOnSelect { get; set; } = true;
    [Ed("Пауза при открытии меню навыков", Group = "Управление"), EdShowIf(nameof(AutopauseEnabled), true)] public bool PauseOnSkillMenu { get; set; } = true;
    [Ed("Пауза при выборе цели", "Режимы движения, атаки, прицела и жест маршрута", Group = "Управление"), EdShowIf(nameof(AutopauseEnabled), true)]
    public bool PauseOnTargeting { get; set; } = true;
    [Ed("Только в начале планирования", "Если игрок снял паузу во время планирования — не ставить её снова до нового выбора", Group = "Управление"),
     EdShowIf(nameof(AutopauseEnabled), true)]
    public bool PauseOnlyAtPlanningStart { get; set; }
    [Ed("Сохранять паузу после приказа", "Иначе автопауза снимается, как только приказ подтверждён", Group = "Управление")]
    public bool KeepPauseAfterOrder { get; set; } = true;
    [Ed("Жестовое управление", "Стрелка взгляда: короткий жест — поворот, длинный — маршрут. Выкл. — только «кнопка → клик»", Group = "Управление")]
    public bool Gestures { get; set; } = true;
    [Ed("Чувствительность поворота, px", "Сдвиг мыши, после которого жест начинает поворачивать", Group = "Управление"), EdRange(1, 40)]
    public double GestureDeadZone { get; set; } = 6;
    [Ed("Порог «поворот → маршрут», гексов", "Расстояние от юнита, после которого жест строит маршрут", Group = "Управление"), EdRange(0.6, 4)]
    public double RouteThreshold { get; set; } = 1.25;
    [Ed("Размер стрелки", Group = "Управление"), EdRange(0.5, 2.5)] public double ArrowSize { get; set; } = 1.0;
    [Ed("Скорость анимации интерфейса", Group = "Управление"), EdRange(0.25, 4)] public double UiAnimSpeed { get; set; } = 1.0;
    [Ed("Степень монохромности карты", "Приглушение карты при выборе точки или цели", Group = "Управление"), EdRange(0, 1)] public double DimStrength { get; set; } = 0.6;
    [Ed("Цвет допустимых гексов", Group = "Управление"), EdColor] public string HighlightColor { get; set; } = "#5fd16b";
    [Ed("Показывать стоимость маршрута", Group = "Управление")] public bool ShowRouteCost { get; set; } = true;
    [Ed("Показывать опасные участки", "Значки и штриховка: огонь/яд, обнаружение, автоатаки, препятствия…", Group = "Управление")]
    public bool ShowHazards { get; set; } = true;
    [Ed("Радиальное меню навыков", "Выкл. — меню списком", Group = "Управление")] public bool RadialMenu { get; set; } = true;
    [Ed("Подтверждать удар по своим", "Навык задевает союзников по правилам дружественного огня — спросить перед приказом (Alt — без вопроса)", Group = "Управление")]
    public bool ConfirmFriendlyFire { get; set; } = true;
    [Ed("Режим маршрута", Group = "Управление")] public RouteMode RouteMode { get; set; } = RouteMode.MinCost;
    [Ed("Веса своего маршрута", Group = "Управление"), EdShowIf(nameof(RouteMode), RouteMode.Custom)] public RouteWeights RouteWeights { get; set; } = new();
    [Ed("Упрощённые визуальные эффекты", Group = "Управление")] public bool SimplifiedEffects { get; set; }
    [Ed("Все связи блока", "Иначе цепь блока видна у выбранного и наведённого юнита (и в диагностике F3)", Group = "Управление")]
    public bool AllBlockLinks { get; set; }

    /// <summary>Favourite library objects ("Category:id"): a personal mark, not part of the content.</summary>
    [EdHidden] public System.Collections.Generic.List<string> Favorites { get; set; } = new();

    /// <summary>Settings file (user://settings.json; UI tests use a sandbox copy).</summary>
    public static string FileName { get; set; } = "user://settings.json";

    private static string PathOnDisk => ProjectSettings.GlobalizePath(FileName);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(PathOnDisk))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOnDisk), ContentJson.Options) ?? new Settings();
        }
        catch (Exception e)
        {
            GD.PushWarning("Настройки повреждены, используются значения по умолчанию: " + e.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
            File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, ContentJson.Options));
        }
        catch (Exception e)
        {
            GD.PushWarning("Не удалось сохранить настройки: " + e.Message);
        }
    }
}
