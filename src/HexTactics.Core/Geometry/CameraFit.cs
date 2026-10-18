using System;
using System.Collections.Generic;
using System.Numerics;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Geometry;

/// <summary>Screen area covered by interface panels (pixels from each edge).</summary>
public readonly record struct ScreenInsets(float Left, float Top, float Right, float Bottom)
{
    /// <summary>
    /// Insets of the battle HUD at a resolution: top bar, action bar and the side panels
    /// (unit panel / queue panel). Mirrors the HUD layout (docs/DECISIONS_2.md §19).
    /// </summary>
    public static ScreenInsets BattleHud(float width, float height)
    {
        float side = Math.Clamp(width * 0.18f, 0, 350);
        return new ScreenInsets(side, 56, side, Math.Min(170, height * 0.2f));
    }
}

public readonly record struct FitResult(float Distance, Vector3 Pivot, float HexPixelWidth, bool Fits);

/// <summary>
/// "Fit map to screen" for the table camera (tilt above the table plane, yaw, vertical FOV).
/// Pure math, shared by the game and the tests: finds the camera distance and pivot so that
/// every given world point projects into the free area of the screen.
/// World axes as in the game: X east, Y up, Z south.
/// </summary>
public static class CameraFit
{
    public static Vector3 CameraOffset(float tiltDeg, float yawDeg, float distance)
    {
        float tilt = MathF.PI / 180f * Math.Clamp(tiltDeg, 20f, 89.5f);
        float yaw = MathF.PI / 180f * yawDeg;
        return new Vector3(MathF.Sin(yaw) * MathF.Cos(tilt), MathF.Sin(tilt), MathF.Cos(yaw) * MathF.Cos(tilt)) * distance;
    }

    /// <summary>Projects a world point to pixel coordinates (origin top-left). Returns false behind the camera.</summary>
    public static bool Project(Vector3 p, Vector3 pivot, float distance, float tiltDeg, float yawDeg, float fovDeg, float width, float height, out Vector2 px)
    {
        var cam = pivot + CameraOffset(tiltDeg, yawDeg, distance);
        var f = Vector3.Normalize(pivot - cam);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var u = Vector3.Cross(r, f);
        var v = p - cam;
        float z = Vector3.Dot(v, f);
        px = default;
        if (z <= 1e-4f) return false;
        float t = MathF.Tan(MathF.PI / 180f * fovDeg * 0.5f);
        float aspect = width / height;
        float xn = Vector3.Dot(v, r) / (z * t * aspect);
        float yn = Vector3.Dot(v, u) / (z * t);
        px = new Vector2((xn + 1f) * 0.5f * width, (1f - yn) * 0.5f * height);
        return true;
    }

    /// <summary>World points of a board: every hex corner at its top and bottom height.</summary>
    public static List<Vector3> BoardPoints(IEnumerable<(HexCoord Coord, float Top, float Bottom)> cells, float hexSize = 1f)
    {
        var pts = new List<Vector3>();
        foreach (var (c, top, bottom) in cells)
            for (int i = 0; i < 6; i++)
            {
                var (x, z) = HexLayout.Corner(c, i, hexSize);
                pts.Add(new Vector3((float)x, top, (float)z));
                pts.Add(new Vector3((float)x, bottom, (float)z));
            }
        return pts;
    }

    /// <summary>Flat rectangle of columns × rows (odd-r) — the "standard zoom" reference board.</summary>
    public static List<Vector3> RectanglePoints(int columns, int rows, float hexSize = 1f)
    {
        var cells = new List<(HexCoord, float, float)>();
        for (int row = 0; row < rows; row++)
        for (int col = 0; col < columns; col++)
            cells.Add((HexLayout.FromOffset(col, row), 0f, -0.45f));
        return BoardPoints(cells, hexSize);
    }

    private static (Vector2 Min, Vector2 Max, bool Ok) ProjectedBounds(List<Vector3> pts, Vector3 pivot, float d, float tilt, float yaw, float fov, float w, float h)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var p in pts)
        {
            if (!Project(p, pivot, d, tilt, yaw, fov, w, h, out var px)) return (min, max, false);
            min = Vector2.Min(min, px);
            max = Vector2.Max(max, px);
        }
        return (min, max, true);
    }

    /// <summary>
    /// Smallest distance at which all points fit into the free area (viewport minus insets),
    /// with the pivot shifted so that the projected board is centred in that area.
    /// </summary>
    public static FitResult Fit(List<Vector3> points, float width, float height, ScreenInsets insets, float tiltDeg, float yawDeg, float fovDeg,
        float minDistance = 2f, float maxDistance = 400f, float margin = 12f)
    {
        if (points.Count == 0) return new FitResult(minDistance * 3, Vector3.Zero, 0, true);
        var center = Vector3.Zero;
        float minY = float.MaxValue;
        foreach (var p in points)
        {
            center += p;
            minY = Math.Min(minY, p.Y);
        }
        center /= points.Count;
        var pivot = new Vector3(center.X, 0, center.Z);

        float freeL = insets.Left + margin, freeT = insets.Top + margin;
        float freeR = width - insets.Right - margin, freeB = height - insets.Bottom - margin;
        if (freeR - freeL < 50 || freeB - freeT < 50)
        {
            freeL = margin;
            freeT = margin;
            freeR = width - margin;
            freeB = height - margin;
        }
        var freeCenter = new Vector2((freeL + freeR) * 0.5f, (freeT + freeB) * 0.5f);

        bool FitsAt(float d, Vector3 pv)
        {
            var (mn, mx, ok) = ProjectedBounds(points, pv, d, tiltDeg, yawDeg, fovDeg, width, height);
            return ok && mn.X >= freeL && mn.Y >= freeT && mx.X <= freeR && mx.Y <= freeB;
        }

        // Centre the projection in the free area (a few fixed-point iterations), then bisect the distance.
        float dist = maxDistance * 0.25f;
        for (int pass = 0; pass < 3; pass++)
        {
            for (int it = 0; it < 6; it++)
            {
                var (mn, mx, ok) = ProjectedBounds(points, pivot, dist, tiltDeg, yawDeg, fovDeg, width, height);
                if (!ok) break;
                var shift = (mn + mx) * 0.5f - freeCenter;
                if (shift.LengthSquared() < 0.25f) break;
                // Screen shift → world shift on the table plane along camera right / forward-on-plane.
                float yaw = MathF.PI / 180f * yawDeg;
                var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
                var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
                float worldPerPx = dist * 2f * MathF.Tan(MathF.PI / 180f * fovDeg * 0.5f) / height;
                float tiltS = MathF.Sin(MathF.PI / 180f * Math.Clamp(tiltDeg, 20f, 89.5f));
                pivot += right * (shift.X * worldPerPx) - fwd * (shift.Y * worldPerPx / tiltS);
            }
            float lo = minDistance, hi = maxDistance;
            if (!FitsAt(hi, pivot)) return new FitResult(hi, pivot, HexWidthPx(pivot, hi, tiltDeg, yawDeg, fovDeg, width, height), false);
            for (int i = 0; i < 40; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (FitsAt(mid, pivot)) hi = mid;
                else lo = mid;
            }
            dist = hi;
        }
        return new FitResult(dist, pivot, HexWidthPx(pivot, dist, tiltDeg, yawDeg, fovDeg, width, height), FitsAt(dist, pivot));
    }

    /// <summary>On-screen width (pixels) of a hex at the pivot (flat-to-flat, perpendicular to the view).</summary>
    public static float HexWidthPx(Vector3 pivot, float distance, float tiltDeg, float yawDeg, float fovDeg, float width, float height, float hexSize = 1f)
    {
        float yaw = MathF.PI / 180f * yawDeg;
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        float half = hexSize * MathF.Sqrt(3f) * 0.5f;
        Project(pivot - right * half, pivot, distance, tiltDeg, yawDeg, fovDeg, width, height, out var a);
        Project(pivot + right * half, pivot, distance, tiltDeg, yawDeg, fovDeg, width, height, out var b);
        return Vector2.Distance(a, b);
    }
}
