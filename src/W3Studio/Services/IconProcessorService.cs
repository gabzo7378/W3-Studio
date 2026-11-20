using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace W3Studio.Services;

public enum IconBorderStyle
{
    Btn,      // Botón Activo clásico (borde metálico 3D)
    Pas,      // Pasivo (viñeta oscura difuminada)
    Atc,      // Autocast (borde activo + esquinas doradas)
    Upg,      // Upgrade / Mejora (marco dorado/bronce)
    Inf,      // Info Card (borde fino metálico)
    None      // Sin marco adicional (64x64 completo)
}

public enum IconColorFrameStyle
{
    SolidBox,
    GlowingBox,
    CornerBrackets
}

public sealed class IconEditOptions
{
    public IconBorderStyle BorderStyle { get; set; } = IconBorderStyle.Btn;
    public bool UseColorFrame { get; set; }
    public Color FrameColor { get; set; } = Color.FromRgb(255, 210, 40);
    public int FrameThickness { get; set; } = 3;
    public IconColorFrameStyle ColorFrameStyle { get; set; } = IconColorFrameStyle.SolidBox;

    public int ZoomPercent { get; set; } = 100; // 75..140
    public int CropMargin { get; set; } = 0;    // 0 o 5 para recortar borde WC3 anterior
    public int Brightness { get; set; } // -80..+80
    public int Contrast { get; set; }   // -50..+50
    public bool FlipHorizontal { get; set; }
    public bool UseTint { get; set; }
    public Color TintColor { get; set; } = Colors.White;
    public int TintStrength { get; set; } = 30; // 0..100
}

public static class IconProcessorService
{
    public const int IconSize = 64;

    public static byte[]? ExtractBgraPixels(BitmapSource? src, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (src == null) return null;

        var converted = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        width = converted.PixelWidth;
        height = converted.PixelHeight;
        if (width <= 0 || height <= 0) return null;

        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    public static BitmapSource? LoadExternalImage(string filePath, BlpIconService? blpService = null)
    {
        if (!File.Exists(filePath)) return null;
        var data = File.ReadAllBytes(filePath);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".blp" or ".tga")
        {
            return BlpIconService.DecodeBlpOrTga(data);
        }

        using var ms = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    /// <summary>
    /// Compone el icono final 64x64 BGRA aplicando zoom, ajustes de color, marco WC3 y recuadro de color opcional.
    /// </summary>
    public static byte[] ComposeIcon64(byte[] srcBgra, int srcW, int srcH, IconEditOptions opts)
    {
        var dst = new byte[IconSize * IconSize * 4];

        // Área visible interna según estilo (para que el marco no tape los detalles principales cuando zoom = 100%)
        float zoom = Math.Clamp(opts.ZoomPercent / 100f, 0.5f, 2.0f);
        float contrastFactor = MathF.Pow((100f + Math.Clamp(opts.Contrast, -80, 80)) / 100f, 2f);
        float brightOffset = opts.Brightness * 2.0f;
        float tintAmt = opts.UseTint ? Math.Clamp(opts.TintStrength / 100f, 0f, 1f) : 0f;

        float marginRatio = Math.Clamp(opts.CropMargin / 64f, 0f, 0.25f);
        float srcStartX = srcW * marginRatio;
        float srcSpanX = Math.Max(1f, srcW * (1f - 2f * marginRatio));
        float srcStartY = srcH * marginRatio;
        float srcSpanY = Math.Max(1f, srcH * (1f - 2f * marginRatio));

        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                float u = (x + 0.5f - IconSize * 0.5f) / zoom + IconSize * 0.5f;
                float v = (y + 0.5f - IconSize * 0.5f) / zoom + IconSize * 0.5f;
                if (opts.FlipHorizontal)
                    u = IconSize - u;

                float sx = srcStartX + (u / IconSize) * srcSpanX - 0.5f;
                float sy = srcStartY + (v / IconSize) * srcSpanY - 0.5f;

                SampleBilinearClamp(srcBgra, srcW, srcH, sx, sy, out float b, out float g, out float r, out float a);

                // Ajustes Brillo / Contraste
                r = ((r - 128f) * contrastFactor + 128f) + brightOffset;
                g = ((g - 128f) * contrastFactor + 128f) + brightOffset;
                b = ((b - 128f) * contrastFactor + 128f) + brightOffset;

                if (tintAmt > 0f)
                {
                    r = r * (1f - tintAmt) + (r * (opts.TintColor.R / 255f)) * tintAmt;
                    g = g * (1f - tintAmt) + (g * (opts.TintColor.G / 255f)) * tintAmt;
                    b = b * (1f - tintAmt) + (b * (opts.TintColor.B / 255f)) * tintAmt;
                }

                int di = (y * IconSize + x) * 4;
                dst[di] = (byte)Math.Clamp(b, 0f, 255f);
                dst[di + 1] = (byte)Math.Clamp(g, 0f, 255f);
                dst[di + 2] = (byte)Math.Clamp(r, 0f, 255f);
                dst[di + 3] = (byte)Math.Clamp(a, 0f, 255f);
            }
        }

        // Aplicar recuadro de color antes o dentro del borde según estilo
        int boxInset = opts.BorderStyle switch
        {
            IconBorderStyle.Btn or IconBorderStyle.Atc or IconBorderStyle.Upg => 5,
            IconBorderStyle.Inf => 3,
            _ => 1
        };

        if (opts.UseColorFrame)
        {
            ApplyColorFrame(dst, opts.FrameColor, Math.Clamp(opts.FrameThickness, 1, 8), opts.ColorFrameStyle, boxInset);
        }

        ApplyWc3Border(dst, opts.BorderStyle);

        return dst;
    }

    /// <summary>
    /// Genera el DISBTN / DISPASBTN correspondiente oscureciendo y aplicando la viñeta oscura clásica de WC3.
    /// </summary>
    public static byte[] CreateDisabledVariant64(byte[] composedBgra, IconBorderStyle style)
    {
        var dis = new byte[composedBgra.Length];
        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                int i = (y * IconSize + x) * 4;
                float b = composedBgra[i];
                float g = composedBgra[i + 1];
                float r = composedBgra[i + 2];
                byte a = composedBgra[i + 3];

                // Leve desaturación + oscurecimiento al ~44%
                float lum = 0.299f * r + 0.587f * g + 0.114f * b;
                r = (r * 0.72f + lum * 0.28f) * 0.44f;
                g = (g * 0.72f + lum * 0.28f) * 0.44f;
                b = (b * 0.72f + lum * 0.28f) * 0.44f;

                int edgeDist = Math.Min(Math.Min(x, IconSize - 1 - x), Math.Min(y, IconSize - 1 - y));
                if (edgeDist < 6)
                {
                    // Sustituir el bisel brillante por el clásico marco negro deshabilitado difuminado
                    float t = edgeDist / 6f;
                    float edgeShade = t * t * 0.85f;
                    r *= edgeShade;
                    g *= edgeShade;
                    b *= edgeShade;
                }

                dis[i] = (byte)Math.Clamp(b, 0f, 255f);
                dis[i + 1] = (byte)Math.Clamp(g, 0f, 255f);
                dis[i + 2] = (byte)Math.Clamp(r, 0f, 255f);
                dis[i + 3] = a;
            }
        }
        return dis;
    }

    private static void ApplyColorFrame(byte[] bgra, Color color, int thickness, IconColorFrameStyle style, int inset)
    {
        int min = inset;
        int max = IconSize - 1 - inset;
        int cornerLen = 14 + thickness * 2;

        for (int y = min; y <= max; y++)
        {
            for (int x = min; x <= max; x++)
            {
                int dEdge = Math.Min(Math.Min(x - min, max - x), Math.Min(y - min, max - y));
                if (dEdge >= thickness + (style == IconColorFrameStyle.GlowingBox ? 4 : 0))
                    continue;

                if (style == IconColorFrameStyle.CornerBrackets)
                {
                    bool inCornerX = (x - min) < cornerLen || (max - x) < cornerLen;
                    bool inCornerY = (y - min) < cornerLen || (max - y) < cornerLen;
                    if (!(inCornerX && inCornerY) || dEdge >= thickness)
                        continue;
                }

                float alpha = 1f;
                if (style == IconColorFrameStyle.GlowingBox && dEdge >= thickness)
                {
                    alpha = 1f - ((dEdge - thickness + 1f) / 5f);
                    alpha = Math.Clamp(alpha * 0.75f, 0f, 1f);
                }

                int i = (y * IconSize + x) * 4;
                if (alpha >= 0.999f)
                {
                    bgra[i] = color.B;
                    bgra[i + 1] = color.G;
                    bgra[i + 2] = color.R;
                    bgra[i + 3] = 255;
                }
                else
                {
                    bgra[i] = (byte)(bgra[i] * (1f - alpha) + color.B * alpha);
                    bgra[i + 1] = (byte)(bgra[i + 1] * (1f - alpha) + color.G * alpha);
                    bgra[i + 2] = (byte)(bgra[i + 2] * (1f - alpha) + color.R * alpha);
                }
            }
        }
    }

    private static void ApplyWc3Border(byte[] bgra, IconBorderStyle style)
    {
        if (style == IconBorderStyle.None)
            return;

        if (style == IconBorderStyle.Pas)
        {
            // Viñeta oscura clásica de icono Pasivo (PASBTN)
            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    int edge = Math.Min(Math.Min(x, IconSize - 1 - x), Math.Min(y, IconSize - 1 - y));
                    if (edge >= 8) continue;

                    float fade = edge == 0 ? 0.0f : MathF.Pow(edge / 8f, 1.35f);
                    int i = (y * IconSize + x) * 4;
                    bgra[i] = (byte)(bgra[i] * fade);
                    bgra[i + 1] = (byte)(bgra[i + 1] * fade);
                    bgra[i + 2] = (byte)(bgra[i + 2] * fade);
                    bgra[i + 3] = 255;
                }
            }
            return;
        }

        if (style == IconBorderStyle.Inf)
        {
            // InfoCard: borde fino de 3px acero oscuro + bisel 1px
            for (int y = 0; y < IconSize; y++)
            {
                for (int x = 0; x < IconSize; x++)
                {
                    int edge = Math.Min(Math.Min(x, IconSize - 1 - x), Math.Min(y, IconSize - 1 - y));
                    if (edge >= 3) continue;
                    bool isTopLeft = x == edge || y == edge;
                    byte v = edge switch
                    {
                        0 => (byte)16,
                        1 => isTopLeft ? (byte)138 : (byte)58,
                        _ => (byte)28
                    };
                    SetRgbOpaque(bgra, x, y, v, v, (byte)Math.Min(255, v + 8));
                }
            }
            return;
        }

        // BTN / ATC / UPG: marco biselado 4px con esquinas redondeadas oscuras fiel al Command Button de Warcraft III
        bool isGold = style == IconBorderStyle.Upg;
        bool isAutocast = style == IconBorderStyle.Atc;

        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                int dx = Math.Min(x, IconSize - 1 - x);
                int dy = Math.Min(y, IconSize - 1 - y);
                int edge = Math.Min(dx, dy);
                if (edge > 4) continue;

                // Esquinas redondeadas oscuras clásicas de los iconos BTN de Warcraft III
                int cornerSum = dx + dy;
                if (cornerSum <= 2 || edge == 0)
                {
                    SetRgbOpaque(bgra, x, y, 8, 8, 10);
                    continue;
                }

                if (edge == 4 || cornerSum == 5)
                {
                    // Surco interno oscuro de 1px que separa el bisel de la ilustración
                    int i = (y * IconSize + x) * 4;
                    bgra[i] = (byte)(bgra[i] * 0.32f);
                    bgra[i + 1] = (byte)(bgra[i + 1] * 0.32f);
                    bgra[i + 2] = (byte)(bgra[i + 2] * 0.32f);
                    continue;
                }

                bool topOrLeft = y <= 3 || x <= 3;
                bool bottomOrRight = y >= IconSize - 4 || x >= IconSize - 4;
                bool litSide = (y < IconSize - 1 - x) ? topOrLeft : !bottomOrRight;

                if (isGold)
                {
                    byte r = litSide ? (byte)(edge == 1 ? 215 : edge == 2 ? 242 : 185) : (byte)(edge == 3 ? 98 : 138);
                    byte g = litSide ? (byte)(edge == 1 ? 172 : edge == 2 ? 202 : 145) : (byte)(edge == 3 ? 68 : 96);
                    byte b = litSide ? (byte)(edge == 1 ? 72 : edge == 2 ? 95 : 54) : (byte)(edge == 3 ? 22 : 36);
                    SetRgbOpaque(bgra, x, y, r, g, b);
                }
                else
                {
                    // Bisel metálico gris piedra de Warcraft III (cresta luminosa en edge==2)
                    byte baseVal = litSide
                        ? (edge == 1 ? (byte)150 : edge == 2 ? (byte)198 : (byte)134)
                        : (edge == 3 ? (byte)58 : edge == 2 ? (byte)96 : (byte)76);
                    SetRgbOpaque(bgra, x, y, baseVal, (byte)(baseVal + 2), (byte)Math.Min(255, baseVal + 6));
                }

                if (isAutocast)
                {
                    // Esquineros dorados/ámbar característicos del marco ATC de Warcraft III
                    bool nearCorner = (x < 16 || x >= IconSize - 16) && (y < 16 || y >= IconSize - 16);
                    if (nearCorner || edge == 2)
                    {
                        float blend = nearCorner ? 0.85f : 0.45f;
                        int i = (y * IconSize + x) * 4;
                        bgra[i] = (byte)(bgra[i] * (1f - blend) + 32 * blend);     // B
                        bgra[i + 1] = (byte)(bgra[i + 1] * (1f - blend) + 195 * blend); // G
                        bgra[i + 2] = (byte)(bgra[i + 2] * (1f - blend) + 255 * blend); // R
                    }
                }
            }
        }
    }

    private static void SetRgbOpaque(byte[] bgra, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * IconSize + x) * 4;
        bgra[i] = b;
        bgra[i + 1] = g;
        bgra[i + 2] = r;
        bgra[i + 3] = 255;
    }

    private static void SampleBilinearClamp(byte[] src, int w, int h, float x, float y, out float b, out float g, out float r, out float a)
    {
        x = Math.Clamp(x, 0f, w - 1f);
        y = Math.Clamp(y, 0f, h - 1f);
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int x1 = Math.Min(w - 1, x0 + 1);
        int y1 = Math.Min(h - 1, y0 + 1);
        float fx = x - x0;
        float fy = y - y0;

        int i00 = (y0 * w + x0) * 4;
        int i10 = (y0 * w + x1) * 4;
        int i01 = (y1 * w + x0) * 4;
        int i11 = (y1 * w + x1) * 4;

        float w00 = (1f - fx) * (1f - fy);
        float w10 = fx * (1f - fy);
        float w01 = (1f - fx) * fy;
        float w11 = fx * fy;

        b = src[i00] * w00 + src[i10] * w10 + src[i01] * w01 + src[i11] * w11;
        g = src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11;
        r = src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11;
        a = src[i00 + 3] * w00 + src[i10 + 3] * w10 + src[i01 + 3] * w01 + src[i11 + 3] * w11;
    }

    public static BitmapSource PixelsToBitmapSource(byte[] bgra, int width = IconSize, int height = IconSize)
    {
        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        wb.Freeze();
        return wb;
    }

    /// <summary>
    /// Codifica un icono BGRA 64x64 a formato BLP1 Paletted (256 colores + Alpha 8-bit + 7 mipmaps 64..1),
    /// 100% compatible con Warcraft III 1.27b, mdx-m3-viewer y BlpIconService.
    /// </summary>
    public static byte[] EncodeBlp1Paletted(byte[] bgra64, int width = IconSize, int height = IconSize, bool includeMipmaps = true)
    {
        var mips = new List<(int w, int h, byte[] bgra)>();
        mips.Add((width, height, bgra64));

        int curW = width;
        int curH = height;
        byte[] cur = bgra64;
        while (includeMipmaps && (curW > 1 || curH > 1))
        {
            int nextW = Math.Max(1, curW / 2);
            int nextH = Math.Max(1, curH / 2);
            var next = Downsample2x(cur, curW, curH, nextW, nextH);
            mips.Add((nextW, nextH, next));
            curW = nextW;
            curH = nextH;
            cur = next;
        }

        // Construir paleta óptima de 256 colores con Median Cut usando el mip0
        var palette = BuildMedianCutPalette(bgra64, width * height);
        var lut = new short[32768];
        Array.Fill(lut, (short)-1);

        const int headerSize = 156;
        const int paletteSize = 1024;
        int dataOffset = headerSize + paletteSize;

        var mipOffsets = new uint[16];
        var mipSizes = new uint[16];
        var mipPayloads = new List<byte[]>();

        for (int m = 0; m < mips.Count && m < 16; m++)
        {
            var (mw, mh, mData) = mips[m];
            int pxCount = mw * mh;
            var payload = new byte[pxCount * 2]; // [indices (pxCount)] + [alpha (pxCount)]
            for (int i = 0; i < pxCount; i++)
            {
                int si = i * 4;
                byte b = mData[si];
                byte g = mData[si + 1];
                byte r = mData[si + 2];
                int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                short cached = lut[key];
                if (cached < 0)
                {
                    cached = FindNearestPaletteIndex(palette, b, g, r);
                    lut[key] = cached;
                }
                payload[i] = (byte)cached;
                payload[pxCount + i] = mData[si + 3];
            }

            mipOffsets[m] = (uint)dataOffset;
            mipSizes[m] = (uint)payload.Length;
            mipPayloads.Add(payload);
            dataOffset += payload.Length;
        }

        using var ms = new MemoryStream(dataOffset);
        using var bw = new BinaryWriter(ms);

        // Header BLP1 (156 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'L');
        bw.Write((byte)'P');
        bw.Write((byte)'1');
        bw.Write((uint)1); // Compression = 1 (Paletted)
        bw.Write((uint)8); // AlphaBits = 8
        bw.Write((uint)width);
        bw.Write((uint)height);
        bw.Write((uint)5); // Extra = 5 (Alpha list)
        bw.Write((uint)(mips.Count > 1 ? 1 : 0)); // HasMipmaps
        for (int i = 0; i < 16; i++) bw.Write(mipOffsets[i]);
        for (int i = 0; i < 16; i++) bw.Write(mipSizes[i]);

        // Paleta BGRA (256 * 4 = 1024 bytes)
        bw.Write(palette);

        // Mipmaps
        foreach (var p in mipPayloads)
            bw.Write(p);

        return ms.ToArray();
    }

    private static byte[] Downsample2x(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int sy0 = Math.Min(sh - 1, y * 2);
            int sy1 = Math.Min(sh - 1, sy0 + 1);
            for (int x = 0; x < dw; x++)
            {
                int sx0 = Math.Min(sw - 1, x * 2);
                int sx1 = Math.Min(sw - 1, sx0 + 1);

                int i00 = (sy0 * sw + sx0) * 4;
                int i10 = (sy0 * sw + sx1) * 4;
                int i01 = (sy1 * sw + sx0) * 4;
                int i11 = (sy1 * sw + sx1) * 4;

                int di = (y * dw + x) * 4;
                for (int c = 0; c < 4; c++)
                    dst[di + c] = (byte)((src[i00 + c] + src[i10 + c] + src[i01 + c] + src[i11 + c] + 2) >> 2);
            }
        }
        return dst;
    }

    private static byte[] BuildMedianCutPalette(byte[] bgra, int pixelCount)
    {
        int step = Math.Max(1, pixelCount / 16384);
        var buckets = new List<List<(byte b, byte g, byte r)>>
        {
            new List<(byte b, byte g, byte r)>(Math.Min(pixelCount, 16384))
        };
        for (int i = 0; i < pixelCount; i += step)
        {
            int idx = i * 4;
            buckets[0].Add((bgra[idx], bgra[idx + 1], bgra[idx + 2]));
        }

        while (buckets.Count < 256)
        {
            int bestIdx = -1;
            int bestRange = -1;
            int bestChan = 0;

            for (int i = 0; i < buckets.Count; i++)
            {
                var list = buckets[i];
                if (list.Count < 2) continue;
                byte minB = 255, maxB = 0, minG = 255, maxG = 0, minR = 255, maxR = 0;
                foreach (var p in list)
                {
                    if (p.b < minB) minB = p.b; if (p.b > maxB) maxB = p.b;
                    if (p.g < minG) minG = p.g; if (p.g > maxG) maxG = p.g;
                    if (p.r < minR) minR = p.r; if (p.r > maxR) maxR = p.r;
                }
                int rb = maxB - minB;
                int rg = maxG - minG;
                int rr = maxR - minR;
                int maxChanRange = Math.Max(rr, Math.Max(rg, rb));
                if (maxChanRange > bestRange)
                {
                    bestRange = maxChanRange;
                    bestIdx = i;
                    bestChan = rr >= rg && rr >= rb ? 2 : (rg >= rb ? 1 : 0);
                }
            }

            if (bestIdx < 0 || bestRange <= 0)
                break;

            var toSplit = buckets[bestIdx];
            toSplit.Sort((a, b) => bestChan switch
            {
                0 => a.b.CompareTo(b.b),
                1 => a.g.CompareTo(b.g),
                _ => a.r.CompareTo(b.r)
            });

            int mid = toSplit.Count / 2;
            var left = toSplit.GetRange(0, mid);
            var right = toSplit.GetRange(mid, toSplit.Count - mid);
            buckets[bestIdx] = left;
            buckets.Add(right);
        }

        var palette = new byte[256 * 4];
        for (int i = 0; i < buckets.Count && i < 256; i++)
        {
            var list = buckets[i];
            if (list.Count == 0) continue;
            long sb = 0, sg = 0, sr = 0;
            foreach (var p in list) { sb += p.b; sg += p.g; sr += p.r; }
            int pi = i * 4;
            palette[pi] = (byte)(sb / list.Count);
            palette[pi + 1] = (byte)(sg / list.Count);
            palette[pi + 2] = (byte)(sr / list.Count);
            palette[pi + 3] = 0;
        }
        return palette;
    }

    private static byte FindNearestPaletteIndex(byte[] palette, byte b, byte g, byte r)
    {
        int bestIdx = 0;
        int bestDist = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            int pi = i * 4;
            int db = b - palette[pi];
            int dg = g - palette[pi + 1];
            int dr = r - palette[pi + 2];
            int dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
                if (dist == 0) break;
            }
        }
        return (byte)bestIdx;
    }

    public static void SaveAsPng(string filePath, byte[] bgra64, int width = IconSize, int height = IconSize)
    {
        var bmp = PixelsToBitmapSource(bgra64, width, height);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }
}
