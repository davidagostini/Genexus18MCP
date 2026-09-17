using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    internal static class TextTreePath
    {
        internal static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        }

        internal static bool TryReadNonNegativeOption(JObject args, string name, out int value, out string error)
        {
            value = 0;
            error = null;
            JToken token = args?[name];
            if (token == null || token.Type == JTokenType.Null) return true;
            try { value = token.ToObject<int>(); }
            catch
            {
                error = name + " must be an integer greater than or equal to zero.";
                return false;
            }
            if (value < 0)
            {
                error = name + " must be greater than or equal to zero.";
                return false;
            }
            return true;
        }

        internal static string BuildObjectTextFileName(int ordinal, string type, string name)
        {
            return ordinal.ToString("D5") + "_" + SanitizeFilePart(type) + "__" + SanitizeFilePart(name) + ".gxtext";
        }

        internal static string SanitizeFilePart(string value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "unnamed" : value.Trim();
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var builder = new System.Text.StringBuilder(source.Length);
            foreach (char c in source)
                builder.Append(invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c);
            string result = builder.ToString().Trim().TrimEnd('.');
            if (result.Length == 0) result = "unnamed";
            return result.Length > 120 ? result.Substring(0, 120) : result;
        }

        internal static bool TryResolveUnderRoot(string root, string relative, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
                if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
                fullPath = candidate;
                return true;
            }
            catch { return false; }
        }
    }
}
