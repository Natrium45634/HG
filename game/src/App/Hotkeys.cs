using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace HexTactics.Game.App;

/// <summary>
/// The keys of the battle the player may reassign (ТЗ ч. 6, 11.7; docs/DECISIONS_6.md §107). A binding is a text such as «M»,
/// «Ctrl+0», «Space»; an action may have several. Esc (one step back) and the camera keys stay fixed. Reassigned actions are
/// kept in the settings; the rest use their defaults, so a new action added later gets its default key.
/// </summary>
public static class Hotkeys
{
    public sealed record Action(string Id, string Label, string Group, string[] Defaults);

    public static readonly Action[] All = BuildAll();

    private static Action[] BuildAll()
    {
        var list = new List<Action>
        {
            new("pause", "Пауза / продолжить", "Время", new[] { "Space" }),
            new("slower", "Медленнее", "Время", new[] { "Z", "Minus" }),
            new("faster", "Быстрее", "Время", new[] { "X", "Equal" }),
            new("step", "Один тик (на паузе)", "Время", new[] { "Period" }),
            new("move", "Движение", "Приказы", new[] { "M" }),
            new("turn", "Поворот", "Приказы", new[] { "T" }),
            new("attack", "Атака", "Приказы", new[] { "F" }),
            new("defend", "Защитная стойка", "Приказы", new[] { "B" }),
            new("wait", "Ожидание", "Приказы", new[] { "H" }),
            new("skill_menu", "Меню навыков у курсора", "Приказы", new[] { "R" }),
            new("retreat", "Отозвать бойца", "Приказы", new[] { "O" }),
            new("undo_point", "Убрать последнюю точку", "Приказы", new[] { "Backspace" }),
            new("clear_orders", "Отменить ожидающие приказы (Shift — все)", "Приказы", new[] { "Delete" }),
        };
        for (int i = 1; i <= 9; i++) list.Add(new($"skill{i}", $"Навык {i}", "Навыки", new[] { i.ToString() }));
        list.AddRange(new Action[]
        {
            new("cycle", "Следующий юнит (Shift — предыдущий)", "Выбор и камера", new[] { "Tab" }),
            new("focus", "К выбранному юниту", "Выбор и камера", new[] { "Home" }),
            new("fit", "Вписать карту в экран", "Выбор и камера", new[] { "V" }),
            new("reset_zoom", "Стандартный масштаб", "Выбор и камера", new[] { "Ctrl+0", "Ctrl+Kp 0" }),
            new("info", "Характеристики", "Окна", new[] { "I" }),
            new("log", "Журнал", "Окна", new[] { "L" }),
            new("help", "Справка", "Окна", new[] { "F1" }),
            new("diag", "Диагностика", "Окна", new[] { "F3" }),
        });
        return list.ToArray();
    }

    public static Action? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    private static Settings S => Main.Instance.Settings;

    /// <summary>The bindings of an action now: the reassigned ones, else the defaults.</summary>
    public static List<string> Of(string id, Settings? s = null)
    {
        s ??= S;
        if (s.KeyBindings.TryGetValue(id, out var own))
            return own.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return Find(id)?.Defaults.ToList() ?? new List<string>();
    }

    /// <summary>Reassigns an action; a binding used by another action is taken from it (no two actions share a key). Returns who lost it.</summary>
    public static List<string> Assign(string id, string binding, Settings? s = null)
    {
        s ??= S;
        var lost = new List<string>();
        foreach (var other in All.Where(a => a.Id != id))
        {
            var keys = Of(other.Id, s);
            if (!keys.Remove(binding)) continue;
            s.KeyBindings[other.Id] = string.Join(", ", keys);
            lost.Add(other.Label);
        }
        s.KeyBindings[id] = binding;
        return lost;
    }

    public static void Reset(string id, Settings? s = null) => (s ?? S).KeyBindings.Remove(id);

    public static void ResetAll(Settings? s = null) => (s ?? S).KeyBindings.Clear();

    /// <summary>Keys bound to more than one action (a hand-edited settings file).</summary>
    public static List<(string Key, string A, string B)> Conflicts(Settings? s = null)
    {
        s ??= S;
        var seen = new Dictionary<string, string>();
        var list = new List<(string, string, string)>();
        foreach (var a in All)
            foreach (var k in Of(a.Id, s))
            {
                if (seen.TryGetValue(k, out var first)) list.Add((k, first, a.Label));
                else seen[k] = a.Label;
            }
        return list;
    }

    /// <summary>Whether a key event is one of the bindings of an action. Ctrl and Alt must match; Shift is free unless the binding names it.</summary>
    public static bool Is(InputEventKey k, string id) => Of(id).Any(b => Matches(k, b));

    public static bool Matches(InputEventKey k, string binding)
    {
        var parts = binding.Split('+');
        var key = parts[^1].Trim();
        var mods = parts.Take(parts.Length - 1).Select(p => p.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mods.Contains("Ctrl") != k.CtrlPressed || mods.Contains("Alt") != k.AltPressed) return false;
        if (mods.Contains("Shift") && !k.ShiftPressed) return false;
        var code = OS.FindKeycodeFromString(key);
        return code != Key.None && (k.Keycode == code || k.Keycode == Key.None && k.PhysicalKeycode == code);
    }

    /// <summary>The binding text of a key event (the dialog's capture): modifiers, then the key.</summary>
    public static string FromEvent(InputEventKey k)
    {
        var code = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        var name = OS.GetKeycodeString(code);
        return (k.CtrlPressed ? "Ctrl+" : "") + (k.AltPressed ? "Alt+" : "") + (k.ShiftPressed ? "Shift+" : "") + name;
    }

    /// <summary>Whether a key may be bound: modifiers alone and Esc may not.</summary>
    public static bool Bindable(InputEventKey k)
    {
        var code = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        return code is not (Key.None or Key.Escape or Key.Ctrl or Key.Alt or Key.Shift or Key.Meta);
    }

    private static readonly Dictionary<string, string> Pretty = new()
    {
        ["Space"] = "Пробел", ["Minus"] = "−", ["Equal"] = "+", ["Period"] = ".", ["Kp 0"] = "Num 0",
    };

    /// <summary>A binding as the interface shows it.</summary>
    public static string Show(string binding) =>
        string.Join("+", binding.Split('+').Select(p => Pretty.TryGetValue(p.Trim(), out var r) ? r : p.Trim()));

    /// <summary>The keys of an action as the interface shows them («Z/−»); "" — unbound.</summary>
    public static string Label(string id) => string.Join("/", Of(id).Select(Show));
}
