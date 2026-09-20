using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Single canonical classifier for worker startup SDK diagnostics. Owns the
    /// fatal/informational taxonomy and code extraction so consumers cannot diverge.
    /// Unknown "GXMCP_SDK_*" codes fail closed as fatal (never reported as compatible).
    /// </summary>
    public static class SdkDiagnosticClassifier
    {
        /// <summary>Reported when no GXMCP_SDK_* code appears in the diagnostic.</summary>
        public const string NonSdkCode = "WORKER_STARTUP_FAILED";

        /// <summary>Reported when a GXMCP_SDK_* code appears but is not in any known set.</summary>
        public const string UnknownSdkCode = "GXMCP_SDK_UNKNOWN";

        private static readonly HashSet<string> FatalCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "GXMCP_SDK_PATH_MISSING",
            "GXMCP_SDK_VERSION_UNDETECTED",
            "GXMCP_SDK_VERSION_MISMATCH",
            "GXMCP_SDK_CATALOG_MISSING",
            "GXMCP_SDK_CATALOG_INVALID",
            "GXMCP_SDK_MANIFEST_MISSING",
            "GXMCP_SDK_MANIFEST_INVALID",
            "GXMCP_SDK_ANCHOR_MISSING",
            "GXMCP_SDK_ASSEMBLY_MISSING",
            UnknownSdkCode
        };

        private static readonly HashSet<string> InformationalCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "GXMCP_SDK_COMPATIBLE",
            "GXMCP_SDK_LEGACY_COMPATIBLE",
            "GXMCP_SDK_FINGERPRINT_DRIFT"
        };

        /// <summary>
        /// Extracts every GXMCP_SDK_ token that appears in the diagnostic, in order,
        /// scanning by position so tokens embedded in single-line payloads are found.
        /// </summary>
        public static List<string> ExtractCodes(string? diagnostic)
        {
            var codes = new List<string>();
            if (string.IsNullOrWhiteSpace(diagnostic)) return codes;
            int searchFrom = 0;
            while (searchFrom < diagnostic.Length)
            {
                int start = diagnostic.IndexOf("GXMCP_SDK_", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                int end = start;
                while (end < diagnostic.Length
                    && (char.IsLetterOrDigit(diagnostic[end]) || diagnostic[end] == '_'))
                    end++;
                codes.Add(diagnostic.Substring(start, end - start));
                searchFrom = Math.Max(end, start + 1);
            }
            return codes;
        }

        /// <summary>
        /// The representative code for reporting: the first fatal code when any is
        /// present (a diagnostic can carry COMPATIBLE before a later fatal code),
        /// otherwise the first code, otherwise the non-SDK fallback.
        /// </summary>
        public static string ClassifyCode(string? diagnostic)
        {
            var codes = ExtractCodes(diagnostic);
            if (codes.Count == 0) return NonSdkCode;
            string? fatal = codes.FirstOrDefault(IsFatalCode);
            return fatal ?? codes[0];
        }

        public static bool IsFatalCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            return FatalCodes.Contains(code)
                || (code.StartsWith("GXMCP_SDK_", StringComparison.OrdinalIgnoreCase)
                    && !InformationalCodes.Contains(code));
        }

        public static bool IsInformationalCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            return InformationalCodes.Contains(code);
        }

        /// <summary>True when every SDK code in the diagnostic is informational.</summary>
        public static bool IsInformationalDiagnostic(string? diagnostic)
        {
            var codes = ExtractCodes(diagnostic);
            return codes.Count > 0 && codes.All(IsInformationalCode);
        }

        /// <summary>True when any SDK code in the diagnostic is fatal (fail closed).</summary>
        public static bool IsFatalDiagnostic(string? diagnostic)
            => ExtractCodes(diagnostic).Any(IsFatalCode);
    }
}
