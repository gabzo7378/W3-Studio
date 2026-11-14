using System;
using System.Collections.Generic;
using W3Studio.Models;

namespace W3Studio.Services
{
    public enum TerrainCliffTool
    {
        None = 0,
        DecreaseOne,
        SameLevel,
        IncreaseOne,
        Water,
        Ramp
    }

    public enum TerrainHeightTool
    {
        None = 0,
        Raise,
        Lower,
        Plateau,
        Noise,
        Smooth
    }

    public enum TerrainBrushShape
    {
        Circle = 0,
        Square = 1
    }

    public sealed class TerrainBrushOptions
    {
        public bool ApplyTexture { get; set; }
        public int TextureIndex { get; set; }
        public bool ApplyBlight { get; set; }

        public bool ApplyCliff { get; set; }
        public TerrainCliffTool CliffTool { get; set; } = TerrainCliffTool.None;
        public int CliffTileIndex { get; set; } = -1; // -1 = mantener cliff tileset existente

        public bool ApplyHeight { get; set; }
        public TerrainHeightTool HeightTool { get; set; } = TerrainHeightTool.None;

        public int BrushSize { get; set; } = 2; // 1, 2, 3, 5, 8
        public TerrainBrushShape Shape { get; set; } = TerrainBrushShape.Circle;
    }

    public sealed class TerrainStrokeState
    {
        public int AnchorLayer { get; set; } = 2;
        public ushort AnchorWaterRaw { get; set; } = 8448;
        public int AnchorWaterSurfaceLayer { get; set; } = 2;
        public bool AnchorIsWater { get; set; }
        public float AnchorWorldZ { get; set; }
        public bool HasAnchor { get; set; }
    }

    public sealed class TerrainCornerPatch
    {
        public int X { get; set; }
        public int Y { get; set; }
        public ushort GroundHeightRaw { get; set; }
        public ushort WaterRaw { get; set; }
        public byte FlagsAndTex { get; set; }
        public byte Variations { get; set; }
        public byte CliffAndLayer { get; set; }
    }

    public sealed class TerrainDabResult
    {
        public bool Modified { get; set; }
        public bool CliffsChanged { get; set; }
        public int MinX { get; set; }
        public int MaxX { get; set; }
        public int MinY { get; set; }
        public int MaxY { get; set; }
        public List<TerrainCornerPatch> Patches { get; } = new List<TerrainCornerPatch>();
    }

    public static class TerrainBrushService
    {
        private static readonly Random Rng = new Random();

        public static bool ApplyDab(
            MapProject project,
            float worldX,
            float worldY,
            TerrainBrushOptions opts,
            bool isStrokeStart,
            TerrainStrokeState state)
        {
            return ApplyDabDetailed(project, worldX, worldY, opts, isStrokeStart, state).Modified;
        }

        public static TerrainDabResult ApplyDabDetailed(
            MapProject project,
            float worldX,
            float worldY,
            TerrainBrushOptions opts,
            bool isStrokeStart,
            TerrainStrokeState state)
        {
            var result = new TerrainDabResult();
            if (project?.Terrain?.RawW3eBytes == null || project.Terrain.RawW3eBytes.Length < 44)
                return result;

            var t = project.Terrain;
            int w = t.GridWidth;
            int h = t.GridHeight;
            if (w <= 1 || h <= 1)
                return result;

            int headerLen = 37 + 4 * t.GroundTiles.Count + 4 * t.CliffTiles.Count;
            if (t.RawW3eBytes.Length < headerLen + w * h * 7)
                return result;

            bool anyTool =
                opts.ApplyTexture ||
                opts.ApplyBlight ||
                (opts.ApplyCliff && opts.CliffTool != TerrainCliffTool.None) ||
                (opts.ApplyHeight && opts.HeightTool != TerrainHeightTool.None);

            if (!anyTool)
                return result;

            byte[] raw = t.RawW3eBytes;
            float fcx = (worldX - t.OffsetX) / 128f;
            float fcy = (worldY - t.OffsetY) / 128f;
            int centerCx = Math.Clamp((int)MathF.Round(fcx), 0, w - 1);
            int centerCy = Math.Clamp((int)MathF.Round(fcy), 0, h - 1);
            int centerOff = headerLen + (centerCy * w + centerCx) * 7;

            if (isStrokeStart || !state.HasAnchor)
            {
                state.AnchorLayer = raw[centerOff + 6] & 0x0F;
                state.AnchorWaterRaw = BitConverter.ToUInt16(raw, centerOff + 2);
                state.AnchorIsWater = (raw[centerOff + 4] & 0x40) != 0;
                state.AnchorWorldZ = GetCornerWorldZ(raw, centerOff);

                // Detectar plano de agua continuo si el ancla o sus vecinos cercanos ya tienen agua
                if (TryFindNearbyWaterPlane(raw, w, h, headerLen, centerCx, centerCy, out ushort nearbyWaterRaw, out int nearbySurfaceLayer))
                {
                    state.AnchorWaterRaw = nearbyWaterRaw;
                    state.AnchorWaterSurfaceLayer = nearbySurfaceLayer;
                }
                else
                {
                    int surfLayer = Math.Clamp(state.AnchorLayer, 1, 14);
                    state.AnchorWaterSurfaceLayer = surfLayer;
                    state.AnchorWaterRaw = ComputeWaterRawForLayer(surfLayer, state.AnchorWaterRaw);
                }

                state.HasAnchor = true;
            }

            // Radio en celdas: WC3 Size 1 -> ~0.75 celdas, Size 2 -> 1.5, Size 3 -> 2.5, Size 5 -> 4.0, Size 8 -> 6.5
            float radius = opts.BrushSize switch
            {
                <= 1 => 0.75f,
                2 => 1.55f,
                3 => 2.55f,
                <= 5 => 4.05f,
                _ => Math.Max(5.5f, opts.BrushSize * 0.8f)
            };

            int minX = Math.Max(0, (int)MathF.Floor(fcx - radius - 1));
            int maxX = Math.Min(w - 1, (int)MathF.Ceiling(fcx + radius + 1));
            int minY = Math.Max(0, (int)MathF.Floor(fcy - radius - 1));
            int maxY = Math.Min(h - 1, (int)MathF.Ceiling(fcy + radius + 1));

            // Snapshot previo para comparar cambios reales (altura/capa/flags) y calcular Smooth
            int capMinX = Math.Max(0, minX - 3);
            int capMaxX = Math.Min(w - 1, maxX + 3);
            int capMinY = Math.Max(0, minY - 3);
            int capMaxY = Math.Min(h - 1, maxY + 3);
            int capW = capMaxX - capMinX + 1;
            int capH = capMaxY - capMinY + 1;

            byte[] beforeBytes = new byte[capW * capH * 7];
            float[,]? snapshotZ = (opts.ApplyHeight && opts.HeightTool == TerrainHeightTool.Smooth)
                ? new float[capW, capH]
                : null;

            for (int sy = capMinY; sy <= capMaxY; sy++)
            {
                for (int sx = capMinX; sx <= capMaxX; sx++)
                {
                    int sOff = headerLen + (sy * w + sx) * 7;
                    int localOff = ((sy - capMinY) * capW + (sx - capMinX)) * 7;
                    Buffer.BlockCopy(raw, sOff, beforeBytes, localOff, 7);
                    if (snapshotZ != null)
                        snapshotZ[sx - capMinX, sy - capMinY] = GetCornerWorldZ(raw, sOff);
                }
            }

            // Si es herramienta Agua y tocamos un cuerpo de agua existente durante el trazo, sincronizar plano de agua
            if (opts.ApplyCliff && opts.CliffTool == TerrainCliffTool.Water &&
                TryFindWaterPlaneInBrush(raw, w, h, headerLen, minX, maxX, minY, maxY, fcx, fcy, radius, opts.Shape, out ushort brushWaterRaw, out int brushWaterSurface))
            {
                state.AnchorWaterRaw = brushWaterRaw;
                state.AnchorWaterSurfaceLayer = brushWaterSurface;
            }

            bool applyCliffTool = opts.ApplyCliff && opts.CliffTool != TerrainCliffTool.None;
            bool needLayerTopologyPass = applyCliffTool &&
                (opts.CliffTool == TerrainCliffTool.IncreaseOne ||
                 opts.CliffTool == TerrainCliffTool.DecreaseOne ||
                 opts.CliffTool == TerrainCliffTool.SameLevel ||
                 opts.CliffTool == TerrainCliffTool.Water);

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    float dist = GetBrushDistance(cx - fcx, cy - fcy, opts.Shape);
                    if (dist > radius)
                        continue;

                    float falloff = Math.Clamp(1.0f - (dist / Math.Max(radius, 0.5f)), 0.05f, 1.0f);
                    float smoothFalloff = falloff * falloff * (3f - 2f * falloff);

                    int idx = cy * w + cx;
                    int off = headerLen + idx * 7;

                    ushort gHeightRaw = BitConverter.ToUInt16(raw, off);
                    ushort waterRaw = BitConverter.ToUInt16(raw, off + 2);
                    byte flagsAndTex = raw[off + 4];
                    byte cliffAndLayer = raw[off + 6];

                    int groundTex = flagsAndTex & 0x0F;
                    byte flags = (byte)(flagsAndTex & 0xF0);
                    int cliffTex = (cliffAndLayer >> 4) & 0x0F;
                    int layer = cliffAndLayer & 0x0F;

                    // 1. Apply Texture / Blight
                    if (opts.ApplyTexture)
                    {
                        groundTex = Math.Clamp(opts.TextureIndex, 0, Math.Max(0, t.GroundTiles.Count - 1));
                        if (opts.ApplyBlight)
                            flags |= 0x20;
                        else
                            flags &= unchecked((byte)~0x20);
                    }
                    else if (opts.ApplyBlight)
                    {
                        flags |= 0x20;
                    }

                    // 2. Apply Cliff / Water (Ramp se procesa en pase dedicado tras estabilizar capas)
                    if (applyCliffTool && opts.CliffTool != TerrainCliffTool.Ramp)
                    {
                        if (opts.CliffTileIndex >= 0 && opts.CliffTileIndex < t.CliffTiles.Count)
                            cliffTex = opts.CliffTileIndex;

                        switch (opts.CliffTool)
                        {
                            case TerrainCliffTool.SameLevel:
                                layer = Math.Clamp(state.AnchorLayer, 0, 14);
                                flags &= unchecked((byte)~0x10);
                                if (!state.AnchorIsWater)
                                    flags &= unchecked((byte)~0x40);
                                else
                                {
                                    waterRaw = MergeWaterHeightPreservingEdge(waterRaw, state.AnchorWaterRaw);
                                    flags |= 0x40;
                                }
                                break;

                            case TerrainCliffTool.IncreaseOne:
                                layer = isStrokeStart || !state.HasAnchor
                                    ? Math.Clamp(state.AnchorLayer + 1, 0, 14)
                                    : Math.Clamp(Math.Max(layer, state.AnchorLayer + 1), 0, 14);
                                flags &= unchecked((byte)~0x10);
                                float raisedZ = ((gHeightRaw - 8192f) / 4f) + ((layer - 2) * 128f);
                                float existingWaterZ = (((waterRaw & 0x3FFF) - 8192f) / 4f) - 89.6f;
                                if ((flags & 0x40) != 0 && raisedZ >= existingWaterZ)
                                    flags &= unchecked((byte)~0x40);
                                break;

                            case TerrainCliffTool.DecreaseOne:
                                int decTarget = Math.Clamp(state.AnchorLayer - 1, 0, 14);
                                layer = Math.Min(layer, decTarget);
                                flags &= unchecked((byte)~0x10);
                                break;

                            case TerrainCliffTool.Water:
                                int surfLayer = Math.Clamp(state.AnchorWaterSurfaceLayer, 1, 14);
                                int shallowBed = Math.Max(0, surfLayer - 1);
                                int deepBed = Math.Max(0, surfLayer - 2);
                                // Agua profunda (-2 capas) al iniciar trazo sobre agua existente; si viene desde tierra, agua poco profunda (-1)
                                int targetWaterLayer = state.AnchorIsWater ? deepBed : shallowBed;
                                if (layer > targetWaterLayer)
                                    layer = targetWaterLayer;
                                waterRaw = MergeWaterHeightPreservingEdge(waterRaw, state.AnchorWaterRaw);
                                flags |= 0x40;
                                flags &= unchecked((byte)~0x10);
                                break;
                        }
                    }

                    // 3. Apply Height (Raise, Lower, Plateau, Noise, Smooth)
                    if (opts.ApplyHeight && opts.HeightTool != TerrainHeightTool.None)
                    {
                        float currentZ = ((gHeightRaw - 8192) / 4f) + ((layer - 2) * 128f);
                        float newZ = currentZ;
                        const float stepAmplitude = 26f;

                        switch (opts.HeightTool)
                        {
                            case TerrainHeightTool.Raise:
                                newZ = currentZ + stepAmplitude * Math.Max(0.25f, smoothFalloff);
                                break;

                            case TerrainHeightTool.Lower:
                                newZ = currentZ - stepAmplitude * Math.Max(0.25f, smoothFalloff);
                                break;

                            case TerrainHeightTool.Plateau:
                                newZ = currentZ + (state.AnchorWorldZ - currentZ) * Math.Clamp(0.45f + 0.55f * smoothFalloff, 0.35f, 1.0f);
                                break;

                            case TerrainHeightTool.Noise:
                                float rnd = (float)(Rng.NextDouble() * 2.0 - 1.0);
                                newZ = currentZ + rnd * 22f * Math.Max(0.3f, smoothFalloff);
                                break;

                            case TerrainHeightTool.Smooth:
                                if (snapshotZ != null)
                                {
                                    float sum = 0f;
                                    int count = 0;
                                    for (int ny = Math.Max(capMinY, cy - 1); ny <= Math.Min(capMaxY, cy + 1); ny++)
                                    {
                                        for (int nx = Math.Max(capMinX, cx - 1); nx <= Math.Min(capMaxX, cx + 1); nx++)
                                        {
                                            sum += snapshotZ[nx - capMinX, ny - capMinY];
                                            count++;
                                        }
                                    }
                                    if (count > 0)
                                    {
                                        float avgZ = sum / count;
                                        newZ = currentZ + (avgZ - currentZ) * Math.Clamp(0.55f * smoothFalloff + 0.2f, 0.2f, 0.85f);
                                    }
                                }
                                break;
                        }

                        float relZ = newZ - ((layer - 2) * 128f);
                        int newRawH = (int)MathF.Round(relZ * 4f + 8192f);
                        gHeightRaw = (ushort)Math.Clamp(newRawH, 0, 16383);
                    }

                    raw[off] = (byte)(gHeightRaw & 0xFF);
                    raw[off + 1] = (byte)((gHeightRaw >> 8) & 0xFF);
                    raw[off + 2] = (byte)(waterRaw & 0xFF);
                    raw[off + 3] = (byte)((waterRaw >> 8) & 0xFF);
                    raw[off + 4] = (byte)((flags & 0xF0) | (groundTex & 0x0F));
                    raw[off + 6] = (byte)(((cliffTex & 0x0F) << 4) | (layer & 0x0F));
                }
            }

            // 4. Restricciones de bordes de acantilado WE 1.27b (delta <= 2 niveles entre vecinos) y limpieza de rampas huérfanas
            if (needLayerTopologyPass)
            {
                bool pullingUp = opts.CliffTool == TerrainCliffTool.IncreaseOne ||
                                 (opts.CliffTool == TerrainCliffTool.SameLevel && state.AnchorLayer >= 2);
                EnforceCliffConstraints(raw, w, h, headerLen, capMinX, capMaxX, capMinY, capMaxY, opts.CliffTileIndex, t.CliffTiles.Count, pullingUp);
                CleanupOrphanRamps(raw, w, h, headerLen, capMinX, capMaxX, capMinY, capMaxY);
            }

            // 5. Cálculo exacto de Rampas (0x10 únicamente en transiciones válidas de exactamente 1 capa)
            if (applyCliffTool && opts.CliffTool == TerrainCliffTool.Ramp)
            {
                ApplyExactRamps(raw, w, h, headerLen, minX, maxX, minY, maxY, fcx, fcy, radius, opts.Shape, opts.CliffTileIndex, t.CliffTiles.Count);
            }

            // 6. Detectar deltas exactos respecto al snapshot y sincronizar arrays en memoria + Patches para el visor 3D
            int dirtyMinX = int.MaxValue;
            int dirtyMaxX = int.MinValue;
            int dirtyMinY = int.MaxValue;
            int dirtyMaxY = int.MinValue;

            for (int sy = capMinY; sy <= capMaxY; sy++)
            {
                for (int sx = capMinX; sx <= capMaxX; sx++)
                {
                    int off = headerLen + (sy * w + sx) * 7;
                    int localOff = ((sy - capMinY) * capW + (sx - capMinX)) * 7;

                    bool cornerChanged = false;
                    for (int b = 0; b < 7; b++)
                    {
                        if (raw[off + b] != beforeBytes[localOff + b])
                        {
                            cornerChanged = true;
                            break;
                        }
                    }

                    if (!cornerChanged)
                        continue;

                    result.Modified = true;
                    dirtyMinX = Math.Min(dirtyMinX, sx);
                    dirtyMaxX = Math.Max(dirtyMaxX, sx);
                    dirtyMinY = Math.Min(dirtyMinY, sy);
                    dirtyMaxY = Math.Max(dirtyMaxY, sy);

                    byte oldFlags = (byte)(beforeBytes[localOff + 4] & 0xF0);
                    byte oldCliffLayer = beforeBytes[localOff + 6];
                    byte newFlags = (byte)(raw[off + 4] & 0xF0);
                    byte newCliffLayer = raw[off + 6];
                    ushort oldWaterRaw = BitConverter.ToUInt16(beforeBytes, localOff + 2);
                    ushort newWaterRaw = BitConverter.ToUInt16(raw, off + 2);

                    // Solo es necesario reconstruir geometría de acantilados/agua si cambió layer, cliffTex, ramp, water o nivel de agua
                    if ((oldCliffLayer != newCliffLayer) ||
                        ((oldFlags & 0x50) != (newFlags & 0x50)) ||
                        (oldWaterRaw != newWaterRaw && (newFlags & 0x40) != 0))
                    {
                        result.CliffsChanged = true;
                    }

                    ushort gHeightRaw = BitConverter.ToUInt16(raw, off);
                    byte flagsAndTex = raw[off + 4];
                    int groundTex = flagsAndTex & 0x0F;
                    int layer = newCliffLayer & 0x0F;
                    float realHeight = ((gHeightRaw - 8192f) / 4f) + ((layer - 2) * 128f);
                    float parsedWaterHeight = (((newWaterRaw & 0x3FFF) - 8192f) / 4f) - 89.6f;
                    bool hasWater = (newFlags & 0x40) != 0;
                    float waterDepth = hasWater ? (parsedWaterHeight - realHeight) : -64f;

                    if (t.TextureIndices != null && sx < t.TextureIndices.GetLength(0) && sy < t.TextureIndices.GetLength(1))
                        t.TextureIndices[sx, sy] = (byte)groundTex;
                    if (t.CliffLevels != null && sx < t.CliffLevels.GetLength(0) && sy < t.CliffLevels.GetLength(1))
                        t.CliffLevels[sx, sy] = (byte)layer;
                    if (t.WaterFlags != null && sx < t.WaterFlags.GetLength(0) && sy < t.WaterFlags.GetLength(1))
                        t.WaterFlags[sx, sy] = hasWater && waterDepth > 0f;
                    if (t.WaterDepths != null && sx < t.WaterDepths.GetLength(0) && sy < t.WaterDepths.GetLength(1))
                        t.WaterDepths[sx, sy] = waterDepth;
                    if (t.RampFlags != null && sx < t.RampFlags.GetLength(0) && sy < t.RampFlags.GetLength(1))
                        t.RampFlags[sx, sy] = (newFlags & 0x10) != 0;
                    if (t.GroundHeights != null && sx < t.GroundHeights.GetLength(0) && sy < t.GroundHeights.GetLength(1))
                        t.GroundHeights[sx, sy] = realHeight;

                    result.Patches.Add(new TerrainCornerPatch
                    {
                        X = sx,
                        Y = sy,
                        GroundHeightRaw = gHeightRaw,
                        WaterRaw = newWaterRaw,
                        FlagsAndTex = flagsAndTex,
                        Variations = raw[off + 5],
                        CliffAndLayer = newCliffLayer
                    });
                }
            }

            if (result.Modified)
            {
                result.MinX = dirtyMinX;
                result.MaxX = dirtyMaxX;
                result.MinY = dirtyMinY;
                result.MaxY = dirtyMaxY;
                project.HasTerrainEdits = true;
                if (project.RawMpqFiles != null)
                    project.RawMpqFiles["war3map.w3e"] = raw;
            }

            return result;
        }

        private static float GetBrushDistance(float dx, float dy, TerrainBrushShape shape)
        {
            return shape == TerrainBrushShape.Square
                ? MathF.Max(MathF.Abs(dx), MathF.Abs(dy))
                : MathF.Sqrt(dx * dx + dy * dy);
        }

        private static ushort ComputeWaterRawForLayer(int surfaceLayer, ushort existingWaterRaw)
        {
            float waterHeightZ = ((surfaceLayer - 2) * 128f) - 25.6f;
            int newWaterRaw = (int)MathF.Round((waterHeightZ + 89.6f) * 4f + 8192f);
            ushort edgeFlag = (ushort)(existingWaterRaw & 0xC000);
            return (ushort)(Math.Clamp(newWaterRaw, 0, 16383) | edgeFlag);
        }

        private static ushort MergeWaterHeightPreservingEdge(ushort existingWaterRaw, ushort targetWaterRaw)
        {
            ushort edgeFlag = (ushort)(existingWaterRaw & 0xC000);
            return (ushort)((targetWaterRaw & 0x3FFF) | edgeFlag);
        }

        private static bool TryFindNearbyWaterPlane(
            byte[] raw, int w, int h, int headerLen, int cx, int cy,
            out ushort waterRaw, out int surfaceLayer)
        {
            for (int r = 0; r <= 1; r++)
            {
                for (int ny = Math.Max(0, cy - r); ny <= Math.Min(h - 1, cy + r); ny++)
                {
                    for (int nx = Math.Max(0, cx - r); nx <= Math.Min(w - 1, cx + r); nx++)
                    {
                        int nOff = headerLen + (ny * w + nx) * 7;
                        if ((raw[nOff + 4] & 0x40) != 0)
                        {
                            waterRaw = BitConverter.ToUInt16(raw, nOff + 2);
                            float waterZ = (((waterRaw & 0x3FFF) - 8192f) / 4f) - 89.6f;
                            surfaceLayer = Math.Clamp((int)MathF.Round((waterZ + 25.6f) / 128f) + 2, 1, 14);
                            return true;
                        }
                    }
                }
            }

            waterRaw = 8448;
            surfaceLayer = 2;
            return false;
        }

        private static bool TryFindWaterPlaneInBrush(
            byte[] raw, int w, int h, int headerLen,
            int minX, int maxX, int minY, int maxY,
            float fcx, float fcy, float radius, TerrainBrushShape shape,
            out ushort waterRaw, out int surfaceLayer)
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    if (GetBrushDistance(cx - fcx, cy - fcy, shape) > radius + 1.0f)
                        continue;

                    int off = headerLen + (cy * w + cx) * 7;
                    if ((raw[off + 4] & 0x40) != 0)
                    {
                        waterRaw = BitConverter.ToUInt16(raw, off + 2);
                        float waterZ = (((waterRaw & 0x3FFF) - 8192f) / 4f) - 89.6f;
                        surfaceLayer = Math.Clamp((int)MathF.Round((waterZ + 25.6f) / 128f) + 2, 1, 14);
                        return true;
                    }
                }
            }

            waterRaw = 8448;
            surfaceLayer = 2;
            return false;
        }

        private static void EnforceCliffConstraints(
            byte[] raw, int w, int h, int headerLen,
            int minX, int maxX, int minY, int maxY,
            int requestedCliffTex, int cliffTileCount, bool pullingUp)
        {
            // World Editor solo dispone de modelos A/B/C (desnivel máx. 2 capas entre esquinas adyacentes).
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                for (int cy = minY; cy <= maxY; cy++)
                {
                    for (int cx = minX; cx <= maxX; cx++)
                    {
                        int off = headerLen + (cy * w + cx) * 7;
                        int curLayer = raw[off + 6] & 0x0F;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = cx + dx;
                                int ny = cy + dy;
                                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;

                                int nOff = headerLen + (ny * w + nx) * 7;
                                int nLayer = raw[nOff + 6] & 0x0F;

                                if (curLayer - nLayer > 2)
                                {
                                    int adjusted = pullingUp ? Math.Clamp(curLayer - 2, 0, 14) : nLayer;
                                    if (!pullingUp)
                                    {
                                        raw[off + 6] = (byte)((raw[off + 6] & 0xF0) | Math.Clamp(nLayer + 2, 0, 14));
                                        curLayer = raw[off + 6] & 0x0F;
                                    }
                                    else if (adjusted != nLayer)
                                    {
                                        int cTex = (requestedCliffTex >= 0 && requestedCliffTex < cliffTileCount)
                                            ? requestedCliffTex
                                            : ((raw[nOff + 6] >> 4) & 0x0F);
                                        raw[nOff + 6] = (byte)(((cTex & 0x0F) << 4) | (adjusted & 0x0F));
                                        raw[nOff + 4] &= unchecked((byte)~0x10);
                                        float nRealZ = GetCornerWorldZ(raw, nOff);
                                        ushort nWaterRaw = BitConverter.ToUInt16(raw, nOff + 2);
                                        float nWaterZ = (((nWaterRaw & 0x3FFF) - 8192f) / 4f) - 89.6f;
                                        if ((raw[nOff + 4] & 0x40) != 0 && nRealZ >= nWaterZ)
                                            raw[nOff + 4] &= unchecked((byte)~0x40);
                                    }
                                    changed = true;
                                }
                                else if (nLayer - curLayer > 2 && !pullingUp)
                                {
                                    int adjusted = Math.Clamp(curLayer + 2, 0, 14);
                                    if (adjusted != nLayer)
                                    {
                                        int cTex = (requestedCliffTex >= 0 && requestedCliffTex < cliffTileCount)
                                            ? requestedCliffTex
                                            : ((raw[nOff + 6] >> 4) & 0x0F);
                                        raw[nOff + 6] = (byte)(((cTex & 0x0F) << 4) | (adjusted & 0x0F));
                                        raw[nOff + 4] &= unchecked((byte)~0x10);
                                        changed = true;
                                    }
                                }
                            }
                        }
                    }
                }
                if (!changed) break;
            }
        }

        private static void CleanupOrphanRamps(
            byte[] raw, int w, int h, int headerLen,
            int minX, int maxX, int minY, int maxY)
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    int off = headerLen + (cy * w + cx) * 7;
                    if ((raw[off + 4] & 0x10) == 0)
                        continue;

                    if (!HasAdjacentSingleLayerStep(raw, w, h, headerLen, cx, cy))
                        raw[off + 4] &= unchecked((byte)~0x10);
                }
            }
        }

        private static void ApplyExactRamps(
            byte[] raw, int w, int h, int headerLen,
            int minX, int maxX, int minY, int maxY,
            float fcx, float fcy, float radius, TerrainBrushShape shape,
            int requestedCliffTex, int cliffTileCount)
        {
            var rampBridgeTargets = new List<(int x, int y)>();

            for (int cy = minY; cy <= maxY; cy++)
            {
                for (int cx = minX; cx <= maxX; cx++)
                {
                    if (GetBrushDistance(cx - fcx, cy - fcy, shape) > radius)
                        continue;

                    if (!HasAdjacentSingleLayerStep(raw, w, h, headerLen, cx, cy))
                        continue;

                    rampBridgeTargets.Add((cx, cy));

                    int off = headerLen + (cy * w + cx) * 7;
                    int curLayer = raw[off + 6] & 0x0F;

                    // Completar el puente de rampa en ambos lados del escalón de 1 capa
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = cx + dx;
                            int ny = cy + dy;
                            if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                            if (GetBrushDistance(nx - fcx, ny - fcy, shape) > radius + 0.85f) continue;

                            int nOff = headerLen + (ny * w + nx) * 7;
                            int nLayer = raw[nOff + 6] & 0x0F;
                            if (Math.Abs(curLayer - nLayer) == 1)
                                rampBridgeTargets.Add((nx, ny));
                        }
                    }
                }
            }

            foreach (var (rx, ry) in rampBridgeTargets)
            {
                int rOff = headerLen + (ry * w + rx) * 7;
                raw[rOff + 4] |= 0x10;
                if (requestedCliffTex >= 0 && requestedCliffTex < cliffTileCount)
                {
                    int layer = raw[rOff + 6] & 0x0F;
                    raw[rOff + 6] = (byte)(((requestedCliffTex & 0x0F) << 4) | layer);
                }
            }
        }

        private static bool HasAdjacentSingleLayerStep(byte[] raw, int w, int h, int headerLen, int cx, int cy)
        {
            int off = headerLen + (cy * w + cx) * 7;
            int curLayer = raw[off + 6] & 0x0F;
            bool hasStepOne = false;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = cx + dx;
                    int ny = cy + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;

                    int nLayer = raw[headerLen + (ny * w + nx) * 7 + 6] & 0x0F;
                    int delta = Math.Abs(curLayer - nLayer);
                    if (delta == 1)
                        hasStepOne = true;
                    else if (delta > 2)
                        return false;
                }
            }

            return hasStepOne;
        }

        private static float GetCornerWorldZ(byte[] raw, int off)
        {
            ushort gHeightRaw = BitConverter.ToUInt16(raw, off);
            int layer = raw[off + 6] & 0x0F;
            return ((gHeightRaw - 8192) / 4f) + ((layer - 2) * 128f);
        }

        public static string GetTilesetDisplayName(string tileCode)
        {
            return tileCode switch
            {
                "Ldrt" => LocalizationService.L("Tierra", "Dirt"),
                "Ldro" => LocalizationService.L("Tierra Áspera", "Rough Dirt"),
                "Ldrg" => LocalizationService.L("Tierra con Hierba", "Grassy Dirt"),
                "Lrok" => LocalizationService.L("Roca", "Rock"),
                "Lgrs" => LocalizationService.L("Césped", "Grass"),
                "Lgrd" => LocalizationService.L("Césped Oscuro", "Dark Grass"),
                "Adrt" => LocalizationService.L("Tierra de Vallefresno", "Ashenvale Dirt"),
                "Adrd" => LocalizationService.L("Tierra Áspera de Vallefresno", "Ashenvale Rough Dirt"),
                "Agrs" => LocalizationService.L("Césped de Vallefresno", "Ashenvale Grass"),
                "Alvd" => LocalizationService.L("Hojas", "Leaves"),
                "Bdrt" => LocalizationService.L("Tierra de Los Baldíos", "Barrens Dirt"),
                "Bdsr" => LocalizationService.L("Desierto", "Desert"),
                "Bgrs" => LocalizationService.L("Césped de Los Baldíos", "Barrens Grass"),
                "Fdrt" => LocalizationService.L("Tierra de Frondavil", "Felwood Dirt"),
                "Fgrs" => LocalizationService.L("Césped de Frondavil", "Felwood Grass"),
                "Ndrt" => LocalizationService.L("Tierra de Rasganorte", "Northrend Dirt"),
                "Nsnw" => LocalizationService.L("Nieve", "Snow"),
                "Ngrs" => LocalizationService.L("Césped de Rasganorte", "Northrend Grass"),
                "Vdrt" => LocalizationService.L("Tierra de Aldea", "Village Dirt"),
                "Vgrs" => LocalizationService.L("Césped de Aldea", "Village Grass"),
                "Vcbb" => LocalizationService.L("Camino Empedrado", "Cobble Path"),
                "Ydrt" => LocalizationService.L("Tierra de Ciudad", "City Dirt"),
                "Ysqd" => LocalizationService.L("Baldosas Cuadradas", "Square Tiles"),
                "Ygsb" => LocalizationService.L("Mármol Negro", "Black Marble"),
                "Idrt" => LocalizationService.L("Tierra de Corona de Hielo", "Icecrown Dirt"),
                "Isnw" => LocalizationService.L("Nieve de Corona de Hielo", "Icecrown Snow"),
                "Iice" => LocalizationService.L("Hielo Oscuro", "Dark Ice"),
                _ => tileCode
            };
        }

        public static string GetTilesetSwatchHex(string tileCode)
        {
            if (string.IsNullOrEmpty(tileCode))
                return "#6B7B4A";
            char k = tileCode[0];
            string lower = tileCode.ToLowerInvariant();
            if (lower.Contains("snw") || lower.Contains("ice")) return "#DCEAF4";
            if (lower.Contains("grs") || lower.Contains("grd") || lower.Contains("vin")) return "#4C7D36";
            if (lower.Contains("rok") || lower.Contains("st") || lower.Contains("sq") || lower.Contains("cb") || lower.Contains("mb")) return "#7B7D82";
            if (lower.Contains("snd") || lower.Contains("dsr")) return "#C7AA6E";
            if (lower.Contains("lav") || lower.Contains("flm")) return "#A63A1F";
            return k switch
            {
                'N' or 'I' => "#94A8B3",
                'B' => "#9E7D54",
                'F' or 'O' => "#5D6E48",
                'D' => "#4A4954",
                _ => "#7D5E3C"
            };
        }
    }
}
