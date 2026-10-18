using System.Collections.Generic;
using System.Diagnostics;

namespace HexTactics.Core.Sim;

/// <summary>
/// Lightweight per-subsystem timing (shown in the diagnostics overlay and printed by stress tests).
/// Uses Stopwatch timestamps; disabled profiling costs one branch per section.
/// </summary>
public sealed class SimProfiler
{
    private readonly Dictionary<string, long> _total = new();
    private readonly Dictionary<string, long> _window = new();
    private readonly Dictionary<string, double> _lastWindowMs = new();
    private int _windowTicks;

    public bool Enabled { get; set; } = true;
    public long Ticks { get; private set; }

    public long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public void End(string section, long start)
    {
        if (!Enabled) return;
        long d = Stopwatch.GetTimestamp() - start;
        _total[section] = (_total.TryGetValue(section, out var t) ? t : 0) + d;
        _window[section] = (_window.TryGetValue(section, out var w) ? w : 0) + d;
    }

    public void TickDone()
    {
        Ticks++;
        if (++_windowTicks < 20) return;
        foreach (var kv in _window) _lastWindowMs[kv.Key] = kv.Value * 1000.0 / Stopwatch.Frequency / _windowTicks;
        _window.Clear();
        _windowTicks = 0;
    }

    /// <summary>Average ms per tick per section over the last ~second of simulation.</summary>
    public IReadOnlyDictionary<string, double> RecentMsPerTick => _lastWindowMs;

    public IEnumerable<(string Section, double TotalMs, double MsPerTick)> Totals()
    {
        foreach (var kv in _total)
        {
            double ms = kv.Value * 1000.0 / Stopwatch.Frequency;
            yield return (kv.Key, ms, Ticks > 0 ? ms / Ticks : 0);
        }
    }

    public void Reset()
    {
        _total.Clear();
        _window.Clear();
        _lastWindowMs.Clear();
        Ticks = 0;
        _windowTicks = 0;
    }
}
