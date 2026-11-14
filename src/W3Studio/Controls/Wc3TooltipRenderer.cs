using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace W3Studio.Controls;

public static class Wc3TooltipRenderer
{
    private static readonly SolidColorBrush DefaultWhiteBrush = new(Color.FromRgb(242, 242, 242));

    public static void RenderToTextBlock(TextBlock target, string? wc3Text)
    {
        target.Inlines.Clear();
        if (string.IsNullOrEmpty(wc3Text))
        {
            target.Inlines.Add(new Run("(Sin texto de tooltip)")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128))
            });
            return;
        }

        // Normalize Warcraft III line breaks
        var normalized = wc3Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("|n", "\n", StringComparison.OrdinalIgnoreCase);

        Brush currentBrush = DefaultWhiteBrush;
        var pos = 0;
        var len = normalized.Length;

        while (pos < len)
        {
            var nextPipe = normalized.IndexOf('|', pos);
            var nextNewline = normalized.IndexOf('\n', pos);

            int stopIdx;
            if (nextPipe == -1 && nextNewline == -1)
                stopIdx = len;
            else if (nextPipe == -1)
                stopIdx = nextNewline;
            else if (nextNewline == -1)
                stopIdx = nextPipe;
            else
                stopIdx = Math.Min(nextPipe, nextNewline);

            if (stopIdx > pos)
            {
                var chunk = normalized.Substring(pos, stopIdx - pos);
                target.Inlines.Add(new Run(chunk) { Foreground = currentBrush });
                pos = stopIdx;
            }

            if (pos >= len)
                break;

            if (normalized[pos] == '\n')
            {
                target.Inlines.Add(new LineBreak());
                pos++;
                continue;
            }

            // Handle pipe sequences: |cAARRGGBB or |r
            if (pos + 1 < len)
            {
                var tag = char.ToLowerInvariant(normalized[pos + 1]);
                if (tag == 'r')
                {
                    currentBrush = DefaultWhiteBrush;
                    pos += 2;
                    continue;
                }
                if (tag == 'c' && pos + 10 <= len)
                {
                    var hex = normalized.Substring(pos + 2, 8);
                    if (TryParseWc3HexColor(hex, out var parsedBrush))
                    {
                        currentBrush = parsedBrush;
                        pos += 10;
                        continue;
                    }
                }
            }

            // Literal '|'
            target.Inlines.Add(new Run("|") { Foreground = currentBrush });
            pos++;
        }
    }

    private static bool TryParseWc3HexColor(string hex8, out SolidColorBrush brush)
    {
        brush = DefaultWhiteBrush;
        if (hex8.Length != 8)
            return false;

        if (byte.TryParse(hex8.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(hex8.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(hex8.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            return true;
        }
        return false;
    }
}

