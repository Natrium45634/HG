using System;
using System.Collections.Generic;

namespace HexTactics.Core.Hex;

/// <summary>
/// Axial hex coordinate (pointy-top). Cube coordinate S = -Q - R.
/// See docs/DECISIONS.md §1–2.
/// </summary>
public readonly struct HexCoord : IEquatable<HexCoord>, IComparable<HexCoord>
{
    public readonly int Q;
    public readonly int R;

    public HexCoord(int q, int r)
    {
        Q = q;
        R = r;
    }

    public int S => -Q - R;

    public static readonly HexCoord Zero = new(0, 0);

    public static HexCoord operator +(HexCoord a, HexCoord b) => new(a.Q + b.Q, a.R + b.R);
    public static HexCoord operator -(HexCoord a, HexCoord b) => new(a.Q - b.Q, a.R - b.R);
    public static HexCoord operator *(HexCoord a, int k) => new(a.Q * k, a.R * k);
    public static bool operator ==(HexCoord a, HexCoord b) => a.Q == b.Q && a.R == b.R;
    public static bool operator !=(HexCoord a, HexCoord b) => !(a == b);

    public bool Equals(HexCoord other) => Q == other.Q && R == other.R;
    public override bool Equals(object? obj) => obj is HexCoord h && Equals(h);
    public override int GetHashCode() => unchecked((Q * 73856093) ^ (R * 19349663));

    public int CompareTo(HexCoord other)
    {
        int c = R.CompareTo(other.R);
        return c != 0 ? c : Q.CompareTo(other.Q);
    }

    public override string ToString() => $"({Q},{R})";

    public HexCoord Neighbor(HexDirection dir) => this + dir.Offset();

    public IEnumerable<HexCoord> Neighbors()
    {
        for (int i = 0; i < 6; i++) yield return this + ((HexDirection)i).Offset();
    }

    public int Length => (Math.Abs(Q) + Math.Abs(R) + Math.Abs(S)) / 2;

    public static int Distance(HexCoord a, HexCoord b) => (a - b).Length;

    public int DistanceTo(HexCoord other) => Distance(this, other);

    /// <summary>Rotate around origin by 60° steps; positive = counter-clockwise (direction index +1).</summary>
    public HexCoord Rotate(int steps)
    {
        steps = ((steps % 6) + 6) % 6;
        int q = Q, r = R, s = S;
        for (int i = 0; i < steps; i++)
        {
            // CCW: (q, r, s) -> (-s, -q, -r)
            int nq = -s, nr = -q, ns = -r;
            q = nq; r = nr; s = ns;
        }
        return new HexCoord(q, r);
    }

    public HexCoord RotateAround(HexCoord center, int steps) => (this - center).Rotate(steps) + center;

    /// <summary>Round fractional cube coordinates to the nearest hex.</summary>
    public static HexCoord Round(double fq, double fr)
    {
        double fs = -fq - fr;
        double rq = Math.Round(fq, MidpointRounding.AwayFromZero);
        double rr = Math.Round(fr, MidpointRounding.AwayFromZero);
        double rs = Math.Round(fs, MidpointRounding.AwayFromZero);
        double dq = Math.Abs(rq - fq), dr = Math.Abs(rr - fr), ds = Math.Abs(rs - fs);
        if (dq > dr && dq > ds) rq = -rr - rs;
        else if (dr > ds) rr = -rq - rs;
        return new HexCoord((int)rq, (int)rr);
    }

    /// <summary>Hex line from a to b inclusive (cube lerp with epsilon nudge).</summary>
    public static List<HexCoord> Line(HexCoord a, HexCoord b)
    {
        int n = Distance(a, b);
        var result = new List<HexCoord>(n + 1);
        if (n == 0)
        {
            result.Add(a);
            return result;
        }
        const double eps = 1e-6;
        double aq = a.Q + eps, ar = a.R + eps;
        double bq = b.Q + eps, br = b.R + eps;
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            result.Add(Round(aq + (bq - aq) * t, ar + (br - ar) * t));
        }
        return result;
    }

    /// <summary>Ring of the given radius around center (radius 0 = the center only).</summary>
    public static List<HexCoord> Ring(HexCoord center, int radius)
    {
        var result = new List<HexCoord>();
        if (radius <= 0)
        {
            result.Add(center);
            return result;
        }
        // Start at SW * radius and walk around: directions E, NE, NW, W, SW, SE.
        var h = center + HexDirection.SW.Offset() * radius;
        for (int side = 0; side < 6; side++)
        {
            var dir = (HexDirection)side;
            for (int j = 0; j < radius; j++)
            {
                result.Add(h);
                h = h.Neighbor(dir);
            }
        }
        return result;
    }

    /// <summary>All hexes within radius (spiral order: center, ring 1, ring 2...).</summary>
    public static List<HexCoord> Spiral(HexCoord center, int radius)
    {
        var result = new List<HexCoord> { center };
        for (int k = 1; k <= radius; k++) result.AddRange(Ring(center, k));
        return result;
    }

    /// <summary>
    /// Integer dot product in cube space. The cube plane is isometric (up to scale)
    /// to the world plane, so this ranks directions by angle exactly, without floats.
    /// </summary>
    public static int CubeDot(HexCoord a, HexCoord b) => a.Q * b.Q + a.R * b.R + a.S * b.S;

    /// <summary>
    /// Direction from this hex towards target, snapped to the closest of 6 directions.
    /// On an exact tie (target on a wedge boundary) the lower direction index wins.
    /// Returns null if target == this.
    /// </summary>
    public HexDirection? DirectionTo(HexCoord target)
    {
        if (target == this) return null;
        var d = target - this;
        int best = 0, bestDot = int.MinValue;
        for (int i = 0; i < 6; i++)
        {
            int dot = CubeDot(d, ((HexDirection)i).Offset());
            if (dot > bestDot) { bestDot = dot; best = i; }
        }
        return (HexDirection)best;
    }

    /// <summary>
    /// Directions whose 60° wedge contains the vector to target: one direction, or two
    /// if the target lies exactly on a wedge boundary. Empty if target == this.
    /// </summary>
    public List<HexDirection> WedgesTo(HexCoord target)
    {
        var result = new List<HexDirection>(2);
        if (target == this) return result;
        var d = target - this;
        int bestDot = int.MinValue;
        for (int i = 0; i < 6; i++)
        {
            int dot = CubeDot(d, ((HexDirection)i).Offset());
            if (dot > bestDot) { bestDot = dot; result.Clear(); result.Add((HexDirection)i); }
            else if (dot == bestDot) result.Add((HexDirection)i);
        }
        return result;
    }
}
