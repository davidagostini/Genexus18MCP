using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Lightweight process and window descriptor for an IDE instance.
    /// </summary>
    public class IdeProcessInfo
    {
        public int Id { get; set; }
        public string ProcessName { get; set; }
        public string MainWindowTitle { get; set; }
        public IntPtr MainWindowHandle { get; set; }
    }

    /// <summary>
    /// Result of probing running GeneXus IDE instances for concurrency hazards.
    /// </summary>
    public class IdeConcurrencyStatus
    {
        public bool IsIdeRunning { get; set; }
        public List<int> IdePids { get; set; } = new List<int>();
        public bool IsTargetKbOpen { get; set; }
        public bool IsTargetObjectOpen { get; set; }
        public string MatchedWindowTitle { get; set; }
        public string WarningCode { get; set; }
        public string WarningMessage { get; set; }
        public Action<IntPtr> TickleAction { get; set; }
        public List<IntPtr> IdeWindowHandles { get; set; } = new List<IntPtr>();

        public bool HasWarning => !string.IsNullOrEmpty(WarningCode);

        public void TickleIde()
        {
            if (TickleAction == null || IdeWindowHandles == null) return;
            foreach (var hwnd in IdeWindowHandles)
            {
                if (hwnd != IntPtr.Zero)
                {
                    try { TickleAction(hwnd); }
                    catch (Exception ex) { Logger.Debug("[IDE-CONCURRENCY] Tickle window failed: " + ex.Message); }
                }
            }
        }

        public JObject ToWarningObject(string targetObjectName, string kbName)
        {
            if (!HasWarning) return null;

            var obj = new JObject
            {
                ["code"] = WarningCode,
                ["docUrl"] = GotchaCodes.DocUrlFor(WarningCode),
                ["message"] = WarningMessage,
                ["idePids"] = new JArray(IdePids),
                ["isTargetObjectOpen"] = IsTargetObjectOpen,
                ["isTargetKbOpen"] = IsTargetKbOpen
            };

            if (!string.IsNullOrEmpty(targetObjectName)) obj["target"] = targetObjectName;
            if (!string.IsNullOrEmpty(kbName)) obj["kbName"] = kbName;
            if (!string.IsNullOrEmpty(MatchedWindowTitle)) obj["matchedWindow"] = MatchedWindowTitle;

            return obj;
        }
    }

    /// <summary>
    /// Detects running GeneXus IDE instances and inspects whether the active Knowledge Base
    /// or specific target object is open in an editor tab.
    ///
    /// Addresses issue #128: prevents silent Last-Write-Wins overwrites when a developer
    /// keeps an object open in GeneXus.exe while MCP edits it out-of-process.
    /// </summary>
    public static class IdeConcurrencyDetector
    {
        // Win32 delegate and P/Invoke declarations for child window enumeration
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumChildWindows(IntPtr window, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_NULL = 0x0000;

        // Delegates allowing 100% testability without live GeneXus.exe processes
        internal static Func<IEnumerable<IdeProcessInfo>> ProcessProvider { get; set; } = DefaultProcessProvider;
        internal static Func<IntPtr, IEnumerable<string>> ChildWindowTextProvider { get; set; } = DefaultChildWindowTextProvider;
        internal static Action<IntPtr> PostMessageAction { get; set; } = DefaultPostMessageAction;

        private static IEnumerable<IdeProcessInfo> DefaultProcessProvider()
        {
            var results = new List<IdeProcessInfo>();
            try
            {
                var procs = Process.GetProcessesByName("GeneXus");
                foreach (var p in procs)
                {
                    try
                    {
                        results.Add(new IdeProcessInfo
                        {
                            Id = p.Id,
                            ProcessName = p.ProcessName,
                            MainWindowTitle = p.MainWindowTitle,
                            MainWindowHandle = p.MainWindowHandle
                        });
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("[IDE-CONCURRENCY] Process lookup failed: " + ex.Message);
            }
            return results;
        }

        private static IEnumerable<string> DefaultChildWindowTextProvider(IntPtr hwnd)
        {
            var texts = new List<string>();
            if (hwnd == IntPtr.Zero) return texts;

            try
            {
                EnumChildWindows(hwnd, (childHwnd, lParam) =>
                {
                    var sb = new StringBuilder(512);
                    int len = GetWindowText(childHwnd, sb, 512);
                    if (len > 0)
                    {
                        string t = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(t)) texts.Add(t);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Debug("[IDE-CONCURRENCY] EnumChildWindows failed: " + ex.Message);
            }

            return texts;
        }

        private static void DefaultPostMessageAction(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                // Send benign WM_NULL (0x0000) to nudge the message pump and trigger Application.Idle
                PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Debug("[IDE-CONCURRENCY] PostMessage WM_NULL failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Resets any test delegates back to standard OS Win32 providers.
        /// </summary>
        public static void ResetProviders()
        {
            ProcessProvider = DefaultProcessProvider;
            ChildWindowTextProvider = DefaultChildWindowTextProvider;
            PostMessageAction = DefaultPostMessageAction;
        }

        /// <summary>
        /// Probes running GeneXus IDE processes for concurrency hazards with the given KB and target object.
        /// </summary>
        public static IdeConcurrencyStatus Check(string kbPath, string kbName, string targetObjectName)
        {
            var status = new IdeConcurrencyStatus
            {
                TickleAction = PostMessageAction
            };

            var processes = ProcessProvider?.Invoke()?.ToList() ?? new List<IdeProcessInfo>();
            if (processes.Count == 0)
            {
                return status;
            }

            status.IsIdeRunning = true;
            foreach (var p in processes)
            {
                status.IdePids.Add(p.Id);
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    status.IdeWindowHandles.Add(p.MainWindowHandle);
                }
            }

            string cleanKbName = (kbName ?? string.Empty).Trim();
            string folderName = string.Empty;
            if (!string.IsNullOrWhiteSpace(kbPath))
            {
                try { folderName = Path.GetFileName(kbPath.TrimEnd('\\', '/')); }
                catch { }
            }

            IdeProcessInfo matchedProcess = null;

            // Step 1: Check if any GeneXus process has this KB open
            foreach (var proc in processes)
            {
                string title = proc.MainWindowTitle ?? string.Empty;
                bool kbMatch = false;

                if (!string.IsNullOrEmpty(cleanKbName) && title.IndexOf(cleanKbName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    kbMatch = true;
                }
                else if (!string.IsNullOrEmpty(folderName) && title.IndexOf(folderName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    kbMatch = true;
                }

                if (kbMatch)
                {
                    status.IsTargetKbOpen = true;
                    matchedProcess = proc;
                    break;
                }
            }

            // If only 1 GeneXus process is running and no other KB is indicated, assume it's target KB
            if (!status.IsTargetKbOpen && processes.Count == 1)
            {
                status.IsTargetKbOpen = true;
                matchedProcess = processes[0];
            }

            if (!status.IsTargetKbOpen)
            {
                // GeneXus is running, but apparently for an unrelated KB.
                return status;
            }

            // Step 2: Check if target object is open in an IDE tab
            string cleanTarget = (targetObjectName ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(cleanTarget) && matchedProcess != null)
            {
                // Check MainWindowTitle first (often contains active document name, e.g. "GeneXus - KB - [Target]")
                string mainTitle = matchedProcess.MainWindowTitle ?? string.Empty;
                if (IsTitleMatchingTarget(mainTitle, cleanTarget))
                {
                    status.IsTargetObjectOpen = true;
                    status.MatchedWindowTitle = mainTitle;
                }

                // If not found in main window title, inspect child window/tab texts
                if (!status.IsTargetObjectOpen && matchedProcess.MainWindowHandle != IntPtr.Zero)
                {
                    var childTexts = ChildWindowTextProvider?.Invoke(matchedProcess.MainWindowHandle) ?? Enumerable.Empty<string>();
                    foreach (var text in childTexts)
                    {
                        if (IsTitleMatchingTarget(text, cleanTarget))
                        {
                            status.IsTargetObjectOpen = true;
                            status.MatchedWindowTitle = text;
                            break;
                        }
                    }
                }
            }

            // Step 3: Format warning details
            int primaryPid = matchedProcess != null ? matchedProcess.Id : status.IdePids.FirstOrDefault();
            if (status.IsTargetObjectOpen)
            {
                status.WarningCode = GotchaCodes.GotchaIdeObjectOpenInEditor;
                string winInfo = !string.IsNullOrEmpty(status.MatchedWindowTitle) ? $" (Window: '{status.MatchedWindowTitle}')" : string.Empty;
                status.WarningMessage = $"CRITICAL CONCURRENCY HAZARD: Object '{cleanTarget}' is currently open in an active editor tab in GeneXus IDE (PID {primaryPid}{winInfo}). Saving in GeneXus IDE will silently overwrite your changes (Last-Write-Wins). Close the tab in the IDE without saving or reload it.";
            }
            else
            {
                status.WarningCode = GotchaCodes.GotchaIdeActiveOnKb;
                string kbLabel = !string.IsNullOrEmpty(cleanKbName) ? cleanKbName : (!string.IsNullOrEmpty(folderName) ? folderName : "active KB");
                status.WarningMessage = $"CONCURRENCY NOTICE: GeneXus IDE is actively open on Knowledge Base '{kbLabel}' (PID {primaryPid}). If '{cleanTarget}' is open in any editor tab, reload or close it without saving to avoid overwriting changes.";
            }

            return status;
        }

        private static bool IsTitleMatchingTarget(string title, string target)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(target)) return false;

            // Exact match
            if (string.Equals(title, target, StringComparison.OrdinalIgnoreCase)) return true;

            // Prefix match with delimiter (e.g. "Customer (Transaction)", "Customer [Web Panel]", "Customer: Events")
            if (title.StartsWith(target + " ", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith(target + " [", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith(target + " (", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith(target + ":", StringComparison.OrdinalIgnoreCase) ||
                title.StartsWith(target + "*", StringComparison.OrdinalIgnoreCase)) // modified indicator
            {
                return true;
            }

            // Enclosed in brackets / parentheses (e.g. "[Customer]", "(Customer)")
            if (title.IndexOf($"[{target}]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf($"({target})", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf($" - {target} - ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.EndsWith($" - {target}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
