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
    [Ed("Меню действий по клику на юнита")] public bool ActionMenuOnClick { get; set; } = true;
    [Ed("Добавлять команды в очередь по умолчанию", "Иначе клик заменяет очередь; Shift инвертирует")] public bool AppendByDefault { get; set; } = true;
    [Ed("Подробный журнал")] public bool DetailedLog { get; set; }
    [Ed("Всплывающие числа урона")] public bool FloatingNumbers { get; set; } = true;
    [Ed("Показывать зону досягаемости")] public bool ShowMoveRange { get; set; } = true;
    [Ed("Полный экран")] public bool Fullscreen { get; set; }
    [Ed("Масштаб интерфейса"), EdRange(0.75, 2)] public double UiScale { get; set; } = 1.0;
    [Ed("Тени")] public bool Shadows { get; set; } = true;
    [Ed("Затенение (SSAO)")] public bool Ssao { get; set; } = true;

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
