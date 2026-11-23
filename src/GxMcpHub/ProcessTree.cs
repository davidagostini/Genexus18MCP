using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GxMcpHub;

// Answers one question about the running processes: was this process started, directly or not, by that one? The Hub
// asks it to tell the Gateway it started (possibly through a wrapper script) from a process of another launcher that
// owns the same port.
internal static class ProcessTree
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int MaxDepth = 32;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // true: processId is ancestorProcessId itself or descends from it. false: ancestorProcessId is nowhere in the
    // recorded ancestry of processId, so it certainly does not. null: nothing may be concluded (the process list could
    // not be read, or the ancestry names ancestorProcessId but the creation instants do not fit, as with a reused
    // process id or a system clock that was set back), least of all that a process should be stopped.
    public static bool? IsSameOrDescendant(int ancestorProcessId, int processId)
    {
        if (ancestorProcessId == processId)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var parents = ReadParents();
        return parents is null ? null : Descends(ancestorProcessId, processId, parents, StartedUtc);
    }

    // The walk itself, over a given picture of the processes (who started whom, and when each one was created), so
    // that the self-tests can hand it one.
    internal static bool? Descends(
        int ancestorProcessId,
        int processId,
        IReadOnlyDictionary<int, int> parents,
        Func<int, (DateTime? Value, bool Gone)> startedUtc)
    {
        if (ancestorProcessId == processId)
        {
            return true;
        }

        // First by the recorded parent ids alone: without the ancestor in that chain there is nothing to date.
        var chain = new List<int> { processId };
        var reached = false;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var current = chain[^1];
            if (!parents.TryGetValue(current, out var parent) || parent == 0 || parent == current)
            {
                break;
            }

            chain.Add(parent);
            if (parent == ancestorProcessId)
            {
                reached = true;
                break;
            }
        }

        if (!reached)
        {
            return false;
        }

        // Windows reuses process ids, and the recorded parent id outlives the parent: a parent that no longer exists,
        // or that was created after its child, is not shown to be the parent. A process that cannot be dated (another
        // account) is passed through without that check.
        for (var index = 0; index < chain.Count - 1; index++)
        {
            var childStarted = startedUtc(chain[index]);
            var parentStarted = startedUtc(chain[index + 1]);
            if (parentStarted.Gone)
            {
                return null;
            }

            if (childStarted.Value is DateTime childTime && parentStarted.Value is DateTime parentTime &&
                parentTime > childTime)
            {
                return null;
            }
        }

        return true;
    }

    internal static Dictionary<int, int>? ReadParents()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var parents = new Dictionary<int, int>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            while (Process32Next(snapshot, ref entry));

            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static (DateTime? Value, bool Gone) StartedUtc(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return (process.StartTime.ToUniversalTime(), false);
        }
        catch (ArgumentException)
        {
            return (null, true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return (null, false);
        }
    }
}
