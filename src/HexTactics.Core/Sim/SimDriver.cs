using System;
using System.Collections.Generic;

namespace HexTactics.Core.Sim;

/// <summary>
/// Converts real frame time into fixed simulation ticks: accumulator += delta × speed;
/// whole ticks are executed. Every tick runs completely (nothing is skipped at high speed).
/// If a frame needs more than MaxTicksPerFrame ticks, the excess time is dropped (the game
/// slows down instead of skipping events). Pause = speed 0: no ticks, UI stays live.
/// </summary>
public sealed class SimDriver
{
    private double _accumulator;

    public SimDriver(Simulation sim)
    {
        Sim = sim;
        Speeds = new List<double>(sim.Rules.Time.SpeedPresets.Count > 0 ? sim.Rules.Time.SpeedPresets : new List<double> { 1 });
        SpeedIndex = Math.Clamp(sim.Rules.Time.DefaultSpeedIndex, 0, Speeds.Count - 1);
        Paused = sim.Rules.Time.StartPaused;
    }

    public Simulation Sim { get; }
    public List<double> Speeds { get; }
    public int SpeedIndex { get; private set; }
    public bool Paused { get; set; }
    public double Speed => Paused ? 0 : Speeds[SpeedIndex];
    public long DroppedTicks { get; private set; }
    public int LastFrameTicks { get; private set; }
    /// <summary>Interpolation factor between the last tick and the next one (0..1) for smooth rendering.</summary>
    public double Alpha => Math.Clamp(_accumulator / Sim.Dt, 0, 1);

    public event Action<string>? Autopaused;

    /// <summary>
    /// «Небезопасный тест» (ТЗ ч. 5, 26.4): an exception inside a tick pauses the battle with its text instead of
    /// breaking the application. Off — exceptions go up as before.
    /// </summary>
    public bool CatchErrors { get; set; }
    /// <summary>The last caught exception (with <see cref="CatchErrors"/>); null — none.</summary>
    public string? Error { get; private set; }
    public event Action<string>? Failed;

    private bool SafeStep()
    {
        if (!CatchErrors)
        {
            Sim.Step();
            return true;
        }
        try
        {
            Sim.Step();
            return true;
        }
        catch (Exception e)
        {
            Error = $"тик {Sim.Tick}: {e.GetType().Name}: {e.Message}";
            Paused = true;
            _accumulator = 0;
            Sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, "Небезопасный тест: ошибка симуляции — " + Error);
            Failed?.Invoke(Error);
            return false;
        }
    }

    public void SetSpeedIndex(int i) => SpeedIndex = Math.Clamp(i, 0, Speeds.Count - 1);

    public void Faster() => SetSpeedIndex(SpeedIndex + 1);
    public void Slower() => SetSpeedIndex(SpeedIndex - 1);

    /// <summary>Advances by real time. Returns the number of ticks executed.</summary>
    public int Advance(double realDelta)
    {
        LastFrameTicks = 0;
        if (Paused || realDelta <= 0) return 0;
        if (Sim.Outcome != BattleOutcome.None && !Sim.Scenario.ContinueAfterOutcome) return 0;
        _accumulator += realDelta * Speeds[SpeedIndex];
        int max = Math.Max(1, Sim.Rules.Time.MaxTicksPerFrame);
        int n = 0;
        while (_accumulator >= Sim.Dt && n < max)
        {
            _accumulator -= Sim.Dt;
            if (!SafeStep()) return n;
            n++;
            var reason = Sim.ConsumeAutopause();
            if (reason != null)
            {
                Paused = true;
                _accumulator = 0;
                Autopaused?.Invoke(reason);
                break;
            }
        }
        if (_accumulator >= Sim.Dt)
        {
            long drop = (long)(_accumulator / Sim.Dt);
            DroppedTicks += drop;
            _accumulator -= drop * Sim.Dt;
        }
        LastFrameTicks = n;
        return n;
    }

    /// <summary>Executes exactly one tick (step-by-step debugging while paused).</summary>
    public void StepOnce()
    {
        if (!SafeStep()) return;
        var reason = Sim.ConsumeAutopause();
        if (reason != null) Autopaused?.Invoke(reason);
    }
}
