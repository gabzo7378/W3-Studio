using System;
using W3Studio.Models;

namespace W3Studio.Services;

/// <summary>
/// Pure WC3 1.27b 3D placement, region 8-handle resizing, and camera frustum math helpers.
/// </summary>
public static class PlacementTransformHelper
{
    public const float DefaultBuildingAngleRadians = (float)(270.0 * Math.PI / 180.0);
    public const float MinEntityScale = 0.1f;
    public const float MaxEntityScale = 20.0f;

    public static float ClampEntityScale(float scale) => Math.Clamp(scale, MinEntityScale, MaxEntityScale);

    public static float NudgeHeight(float currentZ, float deltaZ) => Math.Clamp(currentZ + deltaZ, -2048f, 4096f);

    public static (float AngleRadians, float Scale, int Variation) RollPlacementTransform(
        bool isDoodad,
        bool isBuilding,
        bool randomAngle,
        bool randomScale,
        bool randomVariation,
        int fixedVariation,
        int numVariations,
        float minScale,
        float maxScale,
        Random rng)
    {
        float angle = (!isBuilding && randomAngle)
            ? (float)(rng.NextDouble() * Math.PI * 2.0)
            : DefaultBuildingAngleRadians;

        float lo = Math.Clamp(Math.Min(minScale, maxScale), MinEntityScale, MaxEntityScale);
        float hi = Math.Clamp(Math.Max(minScale, maxScale), lo, MaxEntityScale);
        float scale = (isDoodad && randomScale && hi > lo)
            ? (float)(lo + rng.NextDouble() * (hi - lo))
            : 1.0f;

        int safeNumVar = Math.Max(1, numVariations);
        int variation = isDoodad
            ? (randomVariation ? rng.Next(0, safeNumVar) : Math.Clamp(fixedVariation, 0, safeNumVar - 1))
            : 0;

        return (angle, scale, variation);
    }

    /// <summary>
    /// Computes the 3D world eye position of a Warcraft III W3MapCamera given terrain ground height at its target.
    /// Matches War3MapViewer spherical camera conventions (rotation 90° = looking North, AoA 304° = standard WE pitch).
    /// </summary>
    public static (float EyeX, float EyeY, float EyeZ) ComputeCameraEyePosition(W3MapCamera cam, float groundZ)
    {
        float dist = Math.Clamp(cam.Distance > 1f ? cam.Distance : 1650f, 100f, 20000f);
        float theta = (float)((cam.Rotation - 90.0) * Math.PI / 180.0);
        float aoa = cam.AngleOfAttack;
        if (aoa < 180f) aoa += 360f;
        float phi = Math.Clamp((float)((aoa - 270.0) * Math.PI / 180.0), 0.08f, (float)(Math.PI * 0.5 - 0.04));

        float tz = groundZ + cam.ZOffset;
        float eyeX = cam.TargetX + dist * MathF.Sin(phi) * MathF.Sin(theta);
        float eyeY = cam.TargetY - dist * MathF.Sin(phi) * MathF.Cos(theta);
        float eyeZ = tz + dist * MathF.Cos(phi);
        return (eyeX, eyeY, eyeZ);
    }

    /// <summary>
    /// Resizes an axis-aligned region rectangle from any of the 8 handles (nw, n, ne, e, se, s, sw, w)
    /// while enforcing a minimum width/height.
    /// </summary>
    public static (float Left, float Bottom, float Right, float Top) ComputeResizedRegionRect(
        float origL,
        float origB,
        float origR,
        float origT,
        string handle,
        float wx,
        float wy,
        float minSize = 32f)
    {
        float l = Math.Min(origL, origR);
        float r = Math.Max(origL, origR);
        float b = Math.Min(origB, origT);
        float t = Math.Max(origB, origT);

        string h = (handle ?? string.Empty).Trim().ToLowerInvariant();
        if (h.Contains('w')) l = Math.Min(r - minSize, wx);
        if (h.Contains('e')) r = Math.Max(l + minSize, wx);
        if (h.Contains('s')) b = Math.Min(t - minSize, wy);
        if (h.Contains('n')) t = Math.Max(b + minSize, wy);

        return (l, b, r, t);
    }
}

