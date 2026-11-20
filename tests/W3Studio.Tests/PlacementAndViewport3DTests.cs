using System;
using W3Studio.Models;
using W3Studio.Services;
using Xunit;

namespace W3Studio.Tests;

public class PlacementAndViewport3DTests
{
    [Fact]
    public void RollPlacementTransform_RespectsBuildingAngleAndDoodadRandomizationRanges()
    {
        var rng = new Random(42);

        // Buildings must keep default 270° even when randomAngle is checked
        var bldg = PlacementTransformHelper.RollPlacementTransform(
            isDoodad: false,
            isBuilding: true,
            randomAngle: true,
            randomScale: true,
            randomVariation: true,
            fixedVariation: 3,
            numVariations: 5,
            minScale: 0.7f,
            maxScale: 1.4f,
            rng: rng);

        Assert.Equal(PlacementTransformHelper.DefaultBuildingAngleRadians, bldg.AngleRadians, precision: 4);
        Assert.Equal(1.0f, bldg.Scale);
        Assert.Equal(0, bldg.Variation);

        // Doodads with randomAngle/Scale/Variation stay strictly inside minScale..maxScale and 0..numVar-1
        bool sawNonDefaultVar = false;
        for (int i = 0; i < 30; i++)
        {
            var d = PlacementTransformHelper.RollPlacementTransform(
                isDoodad: true,
                isBuilding: false,
                randomAngle: true,
                randomScale: true,
                randomVariation: true,
                fixedVariation: 0,
                numVariations: 6,
                minScale: 0.8f,
                maxScale: 1.25f,
                rng: rng);

            Assert.InRange(d.AngleRadians, 0f, (float)(Math.PI * 2.0));
            Assert.InRange(d.Scale, 0.8f, 1.25f);
            Assert.InRange(d.Variation, 0, 5);
            if (d.Variation > 0) sawNonDefaultVar = true;
        }
        Assert.True(sawNonDefaultVar);

        // Fixed variation clamps safely into [0, numVar - 1]
        var fixedD = PlacementTransformHelper.RollPlacementTransform(
            isDoodad: true,
            isBuilding: false,
            randomAngle: false,
            randomScale: false,
            randomVariation: false,
            fixedVariation: 9,
            numVariations: 4,
            minScale: 0.8f,
            maxScale: 1.2f,
            rng: rng);

        Assert.Equal(3, fixedD.Variation);
        Assert.Equal(1.0f, fixedD.Scale);
    }

    [Fact]
    public void ComputeCameraEyePosition_MatchesWc3StandardCameraGeometry()
    {
        var cam = new W3MapCamera
        {
            Name = "StandardWE",
            TargetX = 256f,
            TargetY = -128f,
            ZOffset = 40f,
            Rotation = 90f,       // Looking North -> Eye is South of Target (eyeY < targetY, eyeX == targetX)
            AngleOfAttack = 304f, // 34° from vertical -> elevated eye above target
            Distance = 1650f
        };

        var (ex, ey, ez) = PlacementTransformHelper.ComputeCameraEyePosition(cam, groundZ: 100f);

        Assert.InRange(Math.Abs(ex - cam.TargetX), 0f, 0.5f);
        Assert.True(ey < cam.TargetY, "Eye should be South of Target when Rotation = 90°.");
        Assert.True(ez > 140f, "Eye should be elevated above TargetZ + ZOffset.");
        float actualDist = MathF.Sqrt((ex - cam.TargetX) * (ex - cam.TargetX) + (ey - cam.TargetY) * (ey - cam.TargetY) + (ez - 140f) * (ez - 140f));
        Assert.InRange(actualDist, 1649f, 1651f);
    }

    [Fact]
    public void ComputeResizedRegionRect_SupportsAllEightHandlesAndEnforcesMinSize()
    {
        // Edge midpoint handles (n, s, e, w)
        var north = PlacementTransformHelper.ComputeResizedRegionRect(-100, -100, 100, 100, "n", 0, 250);
        Assert.Equal((-100f, -100f, 100f, 250f), north);

        var west = PlacementTransformHelper.ComputeResizedRegionRect(-100, -100, 100, 100, "w", -320, 0);
        Assert.Equal((-320f, -100f, 100f, 100f), west);

        // Corner handle (se)
        var se = PlacementTransformHelper.ComputeResizedRegionRect(-100, -100, 100, 100, "se", 220, -240);
        Assert.Equal((-100f, -240f, 220f, 100f), se);

        // Minimum size enforcement (dragging west past east clamps at right - minSize)
        var clamped = PlacementTransformHelper.ComputeResizedRegionRect(-100, -100, 100, 100, "w", 500, 0, minSize: 32f);
        Assert.Equal(68f, clamped.Left);
        Assert.Equal(100f, clamped.Right);
    }

    [Fact]
    public void ScaleAndHeightHelpers_ClampWithinSafeWc3Bounds()
    {
        Assert.Equal(0.1f, PlacementTransformHelper.ClampEntityScale(0.01f));
        Assert.Equal(20.0f, PlacementTransformHelper.ClampEntityScale(99f));
        Assert.Equal(1.5f, PlacementTransformHelper.ClampEntityScale(1.5f));

        Assert.Equal(116f, PlacementTransformHelper.NudgeHeight(100f, 16f));
        Assert.Equal(-2048f, PlacementTransformHelper.NudgeHeight(-2040f, -100f));
        Assert.Equal(4096f, PlacementTransformHelper.NudgeHeight(4090f, 50f));
    }
}

