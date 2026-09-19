using System;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    public static class ColorHelper
    {
        private static readonly Regex BracketRx = new Regex(@"\[(?<name>[^\[\]]+)\]", RegexOptions.Compiled);

        private static readonly Regex DotNetArgbRx = new Regex(
            @"^(?:A\s*=\s*(?<a>\d{1,3})\s*,\s*)?R\s*=\s*(?<r>\d{1,3})\s*,\s*G\s*=\s*(?<g>\d{1,3})\s*,\s*B\s*=\s*(?<b>\d{1,3})$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TokenRgbRx = new Regex(
            @"^\s*(?<r>\d{1,3})\s*[,;]\s*(?<g>\d{1,3})\s*[,;]\s*(?<b>\d{1,3})(?:\s*[,;]\s*(?<a>\d{1,3}))?\s*\|?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex CssRgbRx = new Regex(
            @"^rgba?\s*\(\s*(?<r>\d{1,3})\s*,\s*(?<g>\d{1,3})\s*,\s*(?<b>\d{1,3})(?:\s*,\s*(?<a>(?:\d+(?:\.\d+)?)|(?:\.\d+)))?\s*\)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HexColorRx = new Regex(
            @"^#?(?:0x)?([0-9a-fA-F]{3,8})$",
            RegexOptions.Compiled);

        public static bool IsColorAttributeName(string attributeName)
        {
            if (string.IsNullOrWhiteSpace(attributeName)) return false;
            return string.Equals(attributeName, "ForeColor", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(attributeName, "BackColor", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(attributeName, "BorderColor", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(attributeName, "RPT_FORECOLOR", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(attributeName, "RPT_BACKCOLOR", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(attributeName, "RPT_BORDERCOLOR", StringComparison.OrdinalIgnoreCase);
        }

        public static string ExtractColorLeafToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            string token = raw.Trim();
            if ((token.StartsWith("'", StringComparison.Ordinal) && token.EndsWith("'", StringComparison.Ordinal) && token.Length > 1) ||
                (token.StartsWith("\"", StringComparison.Ordinal) && token.EndsWith("\"", StringComparison.Ordinal) && token.Length > 1))
            {
                token = token.Substring(1, token.Length - 2).Trim();
            }

            var matches = BracketRx.Matches(token);
            if (matches.Count > 0)
            {
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    string candidate = matches[i].Groups["name"].Value.Trim();
                    if (!string.Equals(candidate, "Color", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return token;
        }

        public static bool TryParseColor(string raw, out Color color)
        {
            color = Color.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string token = ExtractColorLeafToken(raw);
            if (string.IsNullOrWhiteSpace(token)) return false;

            if (string.Equals(token, "Transparent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Empty", StringComparison.OrdinalIgnoreCase))
            {
                color = Color.Transparent;
                return true;
            }

            // 1. Check .NET Color.ToString() format: "A=255, R=144, G=238, B=144" or "R=144, G=238, B=144"
            var dotNetMatch = DotNetArgbRx.Match(token);
            if (dotNetMatch.Success)
            {
                int r = Math.Max(0, Math.Min(255, int.Parse(dotNetMatch.Groups["r"].Value, CultureInfo.InvariantCulture)));
                int g = Math.Max(0, Math.Min(255, int.Parse(dotNetMatch.Groups["g"].Value, CultureInfo.InvariantCulture)));
                int b = Math.Max(0, Math.Min(255, int.Parse(dotNetMatch.Groups["b"].Value, CultureInfo.InvariantCulture)));
                int a = 255;
                if (dotNetMatch.Groups["a"].Success && int.TryParse(dotNetMatch.Groups["a"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedA))
                {
                    a = Math.Max(0, Math.Min(255, parsedA));
                }
                color = Color.FromArgb(a, r, g, b);
                return true;
            }

            // 2. Check GeneXus RGB token ("200; 255; 200|" or "200, 255, 200")
            var tokenMatch = TokenRgbRx.Match(token);
            if (tokenMatch.Success)
            {
                int r = Math.Max(0, Math.Min(255, int.Parse(tokenMatch.Groups["r"].Value, CultureInfo.InvariantCulture)));
                int g = Math.Max(0, Math.Min(255, int.Parse(tokenMatch.Groups["g"].Value, CultureInfo.InvariantCulture)));
                int b = Math.Max(0, Math.Min(255, int.Parse(tokenMatch.Groups["b"].Value, CultureInfo.InvariantCulture)));
                int a = 255;
                if (tokenMatch.Groups["a"].Success && int.TryParse(tokenMatch.Groups["a"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedA))
                {
                    a = Math.Max(0, Math.Min(255, parsedA));
                }
                color = Color.FromArgb(a, r, g, b);
                return true;
            }

            // 3. Check CSS rgb(...) / rgba(...)
            var cssMatch = CssRgbRx.Match(token);
            if (cssMatch.Success)
            {
                int r = Math.Max(0, Math.Min(255, int.Parse(cssMatch.Groups["r"].Value, CultureInfo.InvariantCulture)));
                int g = Math.Max(0, Math.Min(255, int.Parse(cssMatch.Groups["g"].Value, CultureInfo.InvariantCulture)));
                int b = Math.Max(0, Math.Min(255, int.Parse(cssMatch.Groups["b"].Value, CultureInfo.InvariantCulture)));
                int a = 255;
                if (cssMatch.Groups["a"].Success)
                {
                    string aStr = cssMatch.Groups["a"].Value;
                    if (float.TryParse(aStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float aFloat))
                    {
                        a = aFloat <= 1.0f ? (int)Math.Round(aFloat * 255) : Math.Max(0, Math.Min(255, (int)aFloat));
                    }
                }
                color = Color.FromArgb(a, r, g, b);
                return true;
            }

            // 4. Check Hexadecimal (#RGB, #RGBA, #RRGGBB, #AARRGGBB)
            var hexMatch = HexColorRx.Match(token);
            if (hexMatch.Success)
            {
                string hex = hexMatch.Groups[1].Value;
                if (hex.Length == 3) // RGB
                {
                    int r = int.Parse(new string(hex[0], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int g = int.Parse(new string(hex[1], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int b = int.Parse(new string(hex[2], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    color = Color.FromArgb(255, r, g, b);
                    return true;
                }
                if (hex.Length == 4) // RGBA
                {
                    int r = int.Parse(new string(hex[0], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int g = int.Parse(new string(hex[1], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int b = int.Parse(new string(hex[2], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int a = int.Parse(new string(hex[3], 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    color = Color.FromArgb(a, r, g, b);
                    return true;
                }
                if (hex.Length == 6) // RRGGBB
                {
                    int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    color = Color.FromArgb(255, r, g, b);
                    return true;
                }
                if (hex.Length == 8) // AARRGGBB
                {
                    int a = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int r = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int g = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int b = int.Parse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    color = Color.FromArgb(a, r, g, b);
                    return true;
                }
            }

            // 5. Check Named Color
            var named = Color.FromName(token);
            if (named.IsKnownColor || named.IsSystemColor)
            {
                color = named;
                return true;
            }

            // 6. Check Signed / Unsigned ARGB integer
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
            {
                color = Color.FromArgb(intVal);
                return true;
            }

            return false;
        }

        public static string NormalizeColorToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            if (!TryParseColor(raw, out var color))
            {
                return raw.Trim();
            }

            if (color.IsEmpty || (color.A == 0 && (color.R == 0 && color.G == 0 && color.B == 0 || string.Equals(ExtractColorLeafToken(raw), "Transparent", StringComparison.OrdinalIgnoreCase))))
            {
                return "Transparent";
            }

            // GeneXus color editor interoperates well with this canonical RGB token form.
            return $"{color.R}; {color.G}; {color.B}|";
        }

        public static string NormalizeColorTokenForSdkWrite(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            if (!TryParseColor(raw, out var color))
            {
                return raw.Trim();
            }

            if (color.IsEmpty || (color.A == 0 && (color.R == 0 && color.G == 0 && color.B == 0 || string.Equals(ExtractColorLeafToken(raw), "Transparent", StringComparison.OrdinalIgnoreCase))))
            {
                return "Transparent";
            }

            // Preserve known named colors if user supplied a name without punctuation
            string token = ExtractColorLeafToken(raw);
            if (color.IsNamedColor && (color.IsKnownColor || color.IsSystemColor) &&
                !token.Contains(";") && !token.Contains(",") && !token.Contains("#") &&
                !token.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) &&
                !token.StartsWith("A=", StringComparison.OrdinalIgnoreCase) &&
                !token.StartsWith("R=", StringComparison.OrdinalIgnoreCase))
            {
                return color.Name;
            }

            return $"{color.R}; {color.G}; {color.B}|";
        }

        public static bool IsColorEquivalent(string color1, string color2)
        {
            if (color1 == null && color2 == null) return true;
            if (color1 == null || color2 == null) return false;
            if (string.Equals(color1.Trim(), color2.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

            if (TryParseColor(color1, out var c1) && TryParseColor(color2, out var c2))
            {
                return c1.ToArgb() == c2.ToArgb();
            }

            return false;
        }
    }
}
