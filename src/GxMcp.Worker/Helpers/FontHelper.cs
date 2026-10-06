using System;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GxMcp.Worker.Helpers
{
    /// <summary>Parses, composes and verifies report SDK fonts without silent family substitution.</summary>
    public static class FontHelper
    {
        private static readonly Regex ToStringRx = new Regex(@"^\s*\[Font:\s*(?<body>.*)\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsFontAttributeName(string name)
            => string.Equals(name, "Font", StringComparison.OrdinalIgnoreCase);

        public static bool IsFontProperty(string name)
            => IsFontAttributeName(name) || string.Equals(name, "FontName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "FontSize", StringComparison.OrdinalIgnoreCase);

        public sealed class FontSpec
        {
            public string Name;
            public float Size;
            public GraphicsUnit Unit = GraphicsUnit.Point;
            public FontStyle Style;
            public bool StyleSpecified;
            public bool SizeSpecified = true;
            public bool UnitSpecified;
            public byte GdiCharSet = 1;
            public bool GdiCharSetSpecified;
            public bool GdiVerticalFont;
            public bool GdiVerticalFontSpecified;
        }

        private static bool IsInstalledFamily(string name)
        {
            try
            {
                using (var family = new FontFamily(name))
                    return string.Equals(name, family.Name, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }

        public static bool TryParse(string raw, out FontSpec spec)
        {
            spec = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var m = ToStringRx.Match(raw);
            if (m.Success) return TryParseToString(m.Groups["body"].Value, out spec);

            string requested = raw.Split(',')[0].Trim();
            if (!IsInstalledFamily(requested)) return false;
            try
            {
                // GeneXus registers a different converter; use the stock invariant converter directly.
                using (var font = new FontConverter().ConvertFromInvariantString(raw.Trim()) as Font)
                {
                    if (font == null || !string.Equals(requested, font.Name, StringComparison.OrdinalIgnoreCase)) return false;
                    spec = new FontSpec
                    {
                        Name = font.Name, Size = font.Size, Unit = font.Unit, Style = font.Style,
                        StyleSpecified = raw.IndexOf("style=", StringComparison.OrdinalIgnoreCase) >= 0,
                        SizeSpecified = raw.IndexOf(',') >= 0,
                        UnitSpecified = raw.IndexOf(',') >= 0
                    };
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        private static bool TryParseToString(string body, out FontSpec spec)
        {
            spec = null;
            var s = new FontSpec();
            bool hasName = false, hasSize = false;
            foreach (var part in body.Split(','))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) return false; // Do not silently drop a component of a style or malformed field.
                string key = part.Substring(0, eq).Trim();
                string val = part.Substring(eq + 1).Trim();
                switch (key.ToLowerInvariant())
                {
                    case "name": s.Name = val; hasName = val.Length > 0; break;
                    case "size":
                        if (!float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out s.Size)) return false;
                        hasSize = true;
                        break;
                    case "units":
                        if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int u)
                            || !Enum.IsDefined(typeof(GraphicsUnit), u)) return false;
                        s.Unit = (GraphicsUnit)u;
                        s.UnitSpecified = true;
                        break;
                    case "style":
                        if (!Enum.TryParse(val.Replace("|", ","), true, out FontStyle st)
                            || (st & ~(FontStyle.Bold | FontStyle.Italic | FontStyle.Underline | FontStyle.Strikeout)) != 0) return false;
                        s.Style = st;
                        s.StyleSpecified = true;
                        break;
                    case "gdicharset":
                        if (!byte.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out s.GdiCharSet)) return false;
                        s.GdiCharSetSpecified = true;
                        break;
                    case "gdiverticalfont":
                        if (!bool.TryParse(val, out s.GdiVerticalFont)) return false;
                        s.GdiVerticalFontSpecified = true;
                        break;
                }
            }
            if (!hasName || !hasSize || float.IsNaN(s.Size) || float.IsInfinity(s.Size)
                || s.Size <= 0 || !IsInstalledFamily(s.Name)) return false;
            spec = s;
            return true;
        }

        public static Font ToFont(FontSpec spec, Font current = null)
            => new Font(spec.Name, spec.SizeSpecified ? spec.Size : current?.Size ?? 8f,
                spec.StyleSpecified ? spec.Style : current?.Style ?? FontStyle.Regular,
                spec.UnitSpecified ? spec.Unit : current?.Unit ?? GraphicsUnit.Point,
                spec.GdiCharSetSpecified ? spec.GdiCharSet : current?.GdiCharSet ?? 1,
                spec.GdiVerticalFontSpecified ? spec.GdiVerticalFont : current?.GdiVerticalFont ?? false);

        public static Font Compose(Font current, string propertyName, string raw)
        {
            if (!IsFontProperty(propertyName) || string.IsNullOrWhiteSpace(raw)) return null;
            if (string.Equals(propertyName, "FontSize", StringComparison.OrdinalIgnoreCase))
            {
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float size)
                    || float.IsNaN(size) || float.IsInfinity(size) || size <= 0) return null;
                return ToFont(new FontSpec { Name = current?.Name ?? "MS Sans Serif", Size = size }, current);
            }
            if (string.Equals(propertyName, "FontName", StringComparison.OrdinalIgnoreCase) && !IsInstalledFamily(raw.Trim())) return null;
            return TryParse(raw, out var spec) ? ToFont(spec, current) : null;
        }

        public static string Project(Font font)
            => "[Font: Name=" + font.Name + ", Size=" + font.Size.ToString(CultureInfo.InvariantCulture)
                + ", Units=" + (int)font.Unit + ", Style=" + font.Style.ToString().Replace(", ", "|")
                + ", GdiCharSet=" + font.GdiCharSet + ", GdiVerticalFont=" + font.GdiVerticalFont + "]";

        /// <summary>Directional verification: every requested component must be present in the read-back.</summary>
        public static bool AreEquivalent(string expected, string actual)
        {
            if (!TryParse(expected, out var x) || !TryParse(actual, out var y)) return false;
            if (!string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (x.SizeSpecified && (!y.SizeSpecified || Math.Abs(x.Size - y.Size) > 0.01f)) return false;
            if (x.UnitSpecified && (!y.UnitSpecified || x.Unit != y.Unit)) return false;
            if (x.StyleSpecified && (!y.StyleSpecified || x.Style != y.Style)) return false;
            if (x.GdiCharSetSpecified && (!y.GdiCharSetSpecified || x.GdiCharSet != y.GdiCharSet)) return false;
            return !x.GdiVerticalFontSpecified || (y.GdiVerticalFontSpecified && x.GdiVerticalFont == y.GdiVerticalFont);
        }
    }
}
