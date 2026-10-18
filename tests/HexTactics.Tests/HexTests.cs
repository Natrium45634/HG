using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using Xunit;

namespace HexTactics.Tests;

public class HexTests
{
    [Fact]
    public void Neighbors_AreAtDistanceOne_AndDistinct()
    {
        var c = new HexCoord(3, -2);
        var ns = c.Neighbors().ToList();
        Assert.Equal(6, ns.Distinct().Count());
        Assert.All(ns, n => Assert.Equal(1, HexCoord.Distance(c, n)));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 0, 3, 0, 3)]
    [InlineData(0, 0, 2, -3, 3)]
    [InlineData(-2, 1, 3, -1, 5)]
    [InlineData(1, 1, -1, -1, 4)]
    public void Distance_Cube(int q1, int r1, int q2, int r2, int expected)
    {
        Assert.Equal(expected, HexCoord.Distance(new HexCoord(q1, r1), new HexCoord(q2, r2)));
        Assert.Equal(expected, HexCoord.Distance(new HexCoord(q2, r2), new HexCoord(q1, r1)));
    }

    [Fact]
    public void Directions_OrderIsCounterClockwise()
    {
        Assert.Equal(new HexCoord(1, 0), HexDirection.E.Offset());
        Assert.Equal(new HexCoord(1, -1), HexDirection.NE.Offset());
        Assert.Equal(new HexCoord(0, -1), HexDirection.NW.Offset());
        Assert.Equal(new HexCoord(-1, 0), HexDirection.W.Offset());
        Assert.Equal(new HexCoord(-1, 1), HexDirection.SW.Offset());
        Assert.Equal(new HexCoord(0, 1), HexDirection.SE.Offset());
        // In the board plane (y = south) NE must be up-right: x > 0, y < 0.
        var (x, y) = HexLayout.ToPlane(HexDirection.NE.Offset());
        Assert.True(x > 0 && y < 0);
        Assert.Equal(60, HexLayout.AngleBetween(HexCoord.Zero, HexDirection.NE.Offset()), 6);
    }

    [Fact]
    public void Rotate_OneStepMapsDirectionToNext_AndSixStepsIsIdentity()
    {
        for (int d = 0; d < 6; d++)
        {
            var dir = (HexDirection)d;
            Assert.Equal(dir.Rotate(1).Offset(), dir.Offset().Rotate(1));
            Assert.Equal(dir.Rotate(-1).Offset(), dir.Offset().Rotate(-1));
        }
        var p = new HexCoord(3, -1);
        Assert.Equal(p, p.Rotate(6));
        Assert.Equal(p, p.Rotate(2).Rotate(4));
        Assert.Equal(p.Length, p.Rotate(1).Length);
        var center = new HexCoord(2, 2);
        Assert.Equal(center + HexDirection.NE.Offset() * 2, (center + HexDirection.E.Offset() * 2).RotateAround(center, 1));
    }

    [Fact]
    public void Ring_And_Spiral_Sizes()
    {
        Assert.Single(HexCoord.Ring(HexCoord.Zero, 0));
        for (int r = 1; r <= 6; r++)
        {
            var ring = HexCoord.Ring(new HexCoord(1, 2), r);
            Assert.Equal(6 * r, ring.Count);
            Assert.Equal(6 * r, ring.Distinct().Count());
            Assert.All(ring, c => Assert.Equal(r, HexCoord.Distance(c, new HexCoord(1, 2))));
            Assert.Equal(1 + 3 * r * (r + 1), HexCoord.Spiral(HexCoord.Zero, r).Count);
        }
    }

    [Fact]
    public void Line_IsContinuous_AndHasDistancePlusOneCells()
    {
        var a = new HexCoord(-3, 1);
        var b = new HexCoord(4, -2);
        var line = HexCoord.Line(a, b);
        Assert.Equal(HexCoord.Distance(a, b) + 1, line.Count);
        Assert.Equal(a, line[0]);
        Assert.Equal(b, line[^1]);
        for (int i = 1; i < line.Count; i++) Assert.Equal(1, HexCoord.Distance(line[i - 1], line[i]));
    }

    [Fact]
    public void Layout_RoundTrip_ScreenToHex()
    {
        foreach (var c in HexCoord.Spiral(HexCoord.Zero, 8))
        {
            var (x, y) = HexLayout.ToPlane(c, 1.3);
            Assert.Equal(c, HexLayout.FromPlane(x, y, 1.3));
            // Points well inside the hex (70% towards each corner) still map to the hex.
            for (int i = 0; i < 6; i++)
            {
                var (cx, cy) = HexLayout.Corner(c, i, 1.3);
                Assert.Equal(c, HexLayout.FromPlane(x + (cx - x) * 0.7, y + (cy - y) * 0.7, 1.3));
            }
        }
    }

    [Fact]
    public void DirectionTo_Neighbors_AndWedgeTies()
    {
        for (int d = 0; d < 6; d++)
            Assert.Equal((HexDirection)d, HexCoord.Zero.DirectionTo(((HexDirection)d).Offset() * 3));
        // (2,-1) lies exactly between E and NE.
        var w = HexCoord.Zero.WedgesTo(new HexCoord(2, -1));
        Assert.Equal(new List<HexDirection> { HexDirection.E, HexDirection.NE }, w);
        Assert.Single(HexCoord.Zero.WedgesTo(new HexCoord(3, -1)));
    }

    [Fact]
    public void SignedSteps_ShortestRotation()
    {
        Assert.Equal(1, HexDirectionExt.SignedSteps(HexDirection.E, HexDirection.NE));
        Assert.Equal(-1, HexDirectionExt.SignedSteps(HexDirection.E, HexDirection.SE));
        Assert.Equal(3, HexDirectionExt.SignedSteps(HexDirection.E, HexDirection.W));
        Assert.Equal(-2, HexDirectionExt.SignedSteps(HexDirection.NE, HexDirection.SW.Rotate(1)));
        Assert.Equal(2, HexDirectionExt.StepsBetween(HexDirection.SE, HexDirection.NE));
    }

    [Theory]
    [InlineData(HexDirection.E, 1, 0, AttackSector.Front)]
    [InlineData(HexDirection.E, 1, -1, AttackSector.NearFlank)]
    [InlineData(HexDirection.E, 0, 1, AttackSector.NearFlank)]
    [InlineData(HexDirection.E, 0, -1, AttackSector.FarFlank)]
    [InlineData(HexDirection.E, -1, 1, AttackSector.FarFlank)]
    [InlineData(HexDirection.E, -1, 0, AttackSector.Rear)]
    [InlineData(HexDirection.E, -3, 0, AttackSector.Rear)]
    [InlineData(HexDirection.E, 2, -1, AttackSector.Front)] // boundary E/NE → more frontal
    [InlineData(HexDirection.W, 1, 0, AttackSector.Rear)]
    [InlineData(HexDirection.NE, 1, -1, AttackSector.Front)]
    [InlineData(HexDirection.NE, -1, 0, AttackSector.FarFlank)]
    [InlineData(HexDirection.SW, 1, -1, AttackSector.Rear)]
    public void AttackSectors_RelativeToFacing(HexDirection facing, int aq, int ar, AttackSector expected)
    {
        Assert.Equal(expected, CombatSystem.SectorOf(HexCoord.Zero, facing, new HexCoord(aq, ar)));
    }
}
