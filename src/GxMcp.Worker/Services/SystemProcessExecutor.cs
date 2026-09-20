using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Deep boundary executing external OS processes (e.g. MSBuild, compilers),
    /// capturing streams, and enforcing clean process tree termination on Windows.
    /// </summary>
    public class SystemProcessExecutor : IProcessExecutor
    {
        public ProcessExecutionResult Execute(ProcessExecutionSpec spec, Action<string> onOutputLine = null)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));

            var result = new ProcessExecutionResult();
            var psi = new ProcessStartInfo
            {
                FileName = spec.ExecutablePath,
                Arguments = spec.Arguments,
                WorkingDirectory = string.IsNullOrEmpty(spec.WorkingDirectory) ? Directory.GetCurrentDirectory() : spec.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (spec.EnvironmentVariables != null)
            {
                foreach (var kvp in spec.EnvironmentVariables)
                {
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;
                }
            }

            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (result.OutputLines)
                        {
                            result.OutputLines.Add(e.Data);
                        }
                        onOutputLine?.Invoke(e.Data);
                    }
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (result.ErrorLines)
                        {
                            result.ErrorLines.Add(e.Data);
                        }
                    }
                };

                try
                {
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    bool exited = process.WaitForExit((int)spec.Timeout.TotalMilliseconds);
                    if (!exited)
                    {
                        result.TimedOut = true;
                        KillProcessTree(process.Id);
                    }
                    else
                    {
                        result.ExitCode = process.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorLines.Add($"Failed to execute '{spec.ExecutablePath}': {ex.Message}");
                    result.ExitCode = -1;
                }
            }

            return result;
        }

        public int KillProcessTree(int rootPid)
        {
            if (rootPid <= 0) return 0;
            int killed = 0;
            try
            {
                using (var proc = Process.GetProcessById(rootPid))
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        killed++;
                    }
                }
            }
            catch { }
            return killed;
        }
    }
}
