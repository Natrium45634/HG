using System;

namespace HexTactics.Core.Sim;

/// <summary>
/// Deterministic RNG (xoshiro256**, seeded via SplitMix64). Implementation is fixed in code,
/// so results do not depend on the .NET version. Only the simulation draws from it.
/// </summary>
public sealed class SimRng
{
    private ulong _s0, _s1, _s2, _s3;

    public SimRng(ulong seed)
    {
        Seed = seed;
        ulong x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
        if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 1;
    }

    public ulong Seed { get; }

    /// <summary>Number of draws so far (diagnostics / determinism checks).</summary>
    public long Draws { get; private set; }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextULong()
    {
        Draws++;
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform int in [min, maxExclusive).</summary>
    public int NextInt(int min, int maxExclusive)
    {
        if (maxExclusive <= min) return min;
        ulong range = (ulong)(maxExclusive - min);
        return min + (int)(NextULong() % range);
    }

    /// <summary>True with probability p (clamped to [0, 1]). Always consumes one draw.</summary>
    public bool Chance(double p)
    {
        double u = NextDouble();
        return u < Math.Clamp(p, 0.0, 1.0);
    }

    public (ulong, ulong, ulong, ulong) State => (_s0, _s1, _s2, _s3);
}
