using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Services
{
    public class ProcessExecutionSpec
    {
        public string ExecutablePath { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);
    }

    public class ProcessExecutionResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public bool Cancelled { get; set; }
        public List<string> OutputLines { get; set; } = new List<string>();
        public List<string> ErrorLines { get; set; } = new List<string>();
    }

    public interface IProcessExecutor
    {
        ProcessExecutionResult Execute(ProcessExecutionSpec spec, Action<string> onOutputLine = null);
        int KillProcessTree(int rootPid);
    }
}
