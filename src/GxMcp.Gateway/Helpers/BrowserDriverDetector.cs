using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GxMcp.Gateway.Helpers
{
    /// <summary>
    /// Friction-report 2026-05-22 item 70 — browser-driver fallback.
    ///
    /// <c>genexus_preview</c> and the <c>verify_in_browser</c> playbook want a
    /// CDP / automation driver they can call to render and screenshot WebPanels.
    /// The shipped target is <c>chrome-devtools-axi</c> (npm global). When that
    /// CLI is not on PATH, we silently fall back to Playwright
    /// (<c>npx playwright …</c>), which exposes a near-equivalent surface.
    ///
    /// This detector probes PATH once at gateway startup and caches the result
    /// for the life of the process. The cached <see cref="DetectionResult"/> is
    /// surfaced via <c>genexus_whoami.browserDriver</c> so the agent knows what
    /// it can drive without making a tool call to find out.
    /// </summary>
    public static class BrowserDriverDetector
    {
        public enum DriverKind
        {
            None = 0,
            ChromeDevtoolsAxi = 1,
            Playwright = 2
        }

        public sealed class DetectionResult
        {
            public DriverKind Kind { get; set; }
            public string? ResolvedPath { get; set; }   // absolute path of the executable (axi) or "npx" (Playwright)
            public string? Command { get; set; }        // the canonical command to invoke
            public string? Hint { get; set; }           // install hint when Kind == None
        }

        // Public so tests can inject a fake probe / PATH.
        public interface IPathProbe
        {
            string? Which(string command);     // returns absolute path or null
            bool FileExists(string path);
        }

        private sealed class DefaultProbe : IPathProbe
        {
            public string? Which(string command)
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathEnv)) return null;
                var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.PS1";
                var extensions = new List<string> { string.Empty };
                extensions.AddRange(pathext.Split(';').Where(e => !string.IsNullOrEmpty(e)));
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    foreach (var ext in extensions)
                    {
                        try
                        {
                            var candidate = Path.Combine(dir, command + ext);
                            if (File.Exists(candidate)) return candidate;
                        }
                        catch { /* invalid PATH entries can throw */ }
                    }
                }
                return null;
            }
            public bool FileExists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        private static DetectionResult? _cached;
        private static readonly object _lock = new object();

        /// <summary>
        /// Returns the cached detection result; performs the probe on the first call.
        /// Thread-safe.
        /// </summary>
        public static DetectionResult Detect()
        {
            if (_cached != null) return _cached;
            lock (_lock)
            {
                if (_cached != null) return _cached;
                _cached = Probe(new DefaultProbe());
                return _cached;
            }
        }

        /// <summary>Test-facing: reset cache so the next Detect() re-probes.</summary>
        public static void ResetForTests()
        {
            lock (_lock) { _cached = null; }
        }

        /// <summary>Test-facing: run a probe with a custom path resolver.</summary>
        public static DetectionResult Probe(IPathProbe probe)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));

            // 1) Prefer chrome-devtools-axi if present (canonical driver, faster cold-start).
            var axi = probe.Which("chrome-devtools-axi") ?? probe.Which("chrome-devtools-axi.cmd");
            if (!string.IsNullOrEmpty(axi))
            {
                return new DetectionResult
                {
                    Kind = DriverKind.ChromeDevtoolsAxi,
                    ResolvedPath = axi,
                    Command = "chrome-devtools-axi",
                    Hint = null
                };
            }

            // 2) Playwright fallback. We don't insist on a global install — `npx
            //    playwright` works as long as Node is on PATH, fetching the package
            //    on first use. We still require `npx` itself so we don't promise
            //    something that fails at the first call.
            var npx = probe.Which("npx") ?? probe.Which("npx.cmd");
            if (!string.IsNullOrEmpty(npx))
            {
                return new DetectionResult
                {
                    Kind = DriverKind.Playwright,
                    ResolvedPath = npx,
                    Command = "npx playwright",
                    Hint = null
                };
            }

            // 3) Neither available — return a structured error the caller can surface.
            return new DetectionResult
            {
                Kind = DriverKind.None,
                ResolvedPath = null,
                Command = null,
                Hint = "Install one of: `npm install -g chrome-devtools-axi` (preferred) or `npm install -g playwright` (fallback). Node.js must be on PATH."
            };
        }
    }
}
