using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GxMcpHub;

// What one pass over the inheritable handles of the Hub did. Path says how the handles were found: "nt" (the handle
// table of the process read in one call), "scan" (every handle value probed up to a fixed ceiling, when that call
// failed) or "none" (nothing was done: not Windows, or the pass failed altogether).
internal readonly record struct HandleSweep(string Path, int Cleared, int Probes, double ElapsedMilliseconds);

// The handle table of the process as the system reports it: how many handles it has, and which of them are inheritable.
internal readonly record struct HandleTable(int Total, List<IntPtr> Inheritable);

// The handles of the Hub that a process it starts could inherit. The standard handles are the MCP channel of whoever
// launched the Hub: the input it writes and the output and error it reads. But a launcher can hand the Hub more than
// those three (the pwsh of call.ps1 gives it new pipes for input and output, and its own original output pipe, the
// one a caller captures, goes along as a loose inheritable handle), and a loose handle keeps in the Hub the very
// number it had in the launcher. A process the Hub starts inherits every inheritable handle of the Hub, so a Gateway
// that outlives the Hub keeps all of them open, and whoever waits for the end of those channels waits for the
// Gateway. With a Gateway shared between Hubs that is every normal exit, not only a Hub killed by force.
//
// So the Hub stops passing on every inheritable handle it has, first of all in Main, before any process is started.
// The processes the Hub starts get pipes of their own for standard input, output and error
// (ProcessSupervisor.BuildStartInfo), and those are created later, at each Process.Start, so they lose nothing.
//
// This is done only in Main, never before each Process.Start: a second backend being started on another thread would
// have created, inside its Process.Start, the inheritable ends of the pipes of its child, and a pass in the middle
// would invalidate them, so the child would be born without channels.
//
// Nothing here may fail the Hub: a handle that is missing, invalid, a console, or not ours to change is left as it
// is, without a word.
internal static class StandardHandles
{
    internal const string PathNt = "nt";
    internal const string PathScan = "scan";
    internal const string PathNone = "none";

    // The fallback probes every handle value up to here. Handle values are four times a table index, and an inherited
    // handle keeps the value it had in the launcher, so 0x40000 reaches a launcher with 65,536 handles open (the
    // launchers of a Hub have a few thousand at most). A probe that finds nothing costs about 0.22 µs, so the whole
    // fallback costs about 15 ms; 0x100000 would cost about 60 ms.
    internal const int ScanCeiling = 0x40000;

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint ObjInherit = 0x00000002;
    private const int ProcessHandleInformation = 51;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int InitialTableBytes = 0x10000;
    private const int MaxTableBytes = 0x4000000;
    private const int MaxTableAttempts = 8;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process,
        int informationClass,
        IntPtr information,
        int length,
        out int returnLength);

    // What the last pass did, for gxhub_status: how many handles lost their inheritance and how they were found.
    // The setter is internal for the self-tests, which show that the status reports whatever the last pass was.
    internal static HandleSweep LastSweep { get; set; } = new(PathNone, 0, 0, 0);

    private static int _passes;

    // How many passes over the handles this process made. The Hub makes one, in Main; the self-tests check that
    // starting a process does not make another (see the comment above on why).
    internal static int PassCount => Volatile.Read(ref _passes);

    // Called first of all in Main. The sweep is a seam for the self-tests, which show that the three channels lose their
    // inheritance whatever happens to it.
    public static void StopInheriting(Func<HandleSweep>? sweepAll = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var watch = Stopwatch.StartNew();
            var cleared = 0;
            // The three channels of the launcher first: whatever happens to the pass below, these are never passed on.
            foreach (var which in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
            {
                if (StopInheriting(GetStdHandle(which)))
                {
                    cleared++;
                }
            }

            var sweep = (sweepAll ?? (() => StopInheritingAll()))();
            LastSweep = sweep with
            {
                Cleared = sweep.Cleared + cleared,
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
            };
        }
        catch (Exception)
        {
            // Never the Hub's problem.
        }
    }

    // True when the handle was valid and is not inheritable any more. Never throws.
    internal static bool StopInheriting(IntPtr handle)
    {
        // No handle (a Hub started without standard channels) or INVALID_HANDLE_VALUE.
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            return SetHandleInformation(handle, HandleFlagInherit, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return false;
        }
    }

    // Takes the inheritance away from every inheritable handle of the process. The handle table of the process is
    // read in one call; if that fails, whatever the reason, every handle value is probed up to the ceiling. The
    // delegates are seams for the self-tests; the defaults are the system calls.
    internal static HandleSweep StopInheritingAll(
        bool forceScan = false,
        int ceiling = ScanCeiling,
        Func<HandleTable?>? readTable = null,
        Func<IntPtr, uint?>? getFlags = null,
        Func<IntPtr, bool>? clear = null)
    {
        Interlocked.Increment(ref _passes);
        var watch = Stopwatch.StartNew();
        clear ??= StopInheriting;

        if (!forceScan)
        {
            HandleTable? table = null;
            try
            {
                table = (readTable ?? ReadHandleTable)();
            }
            catch (Exception)
            {
                // The table could not be read: the fallback below takes over.
            }

            if (table is HandleTable read)
            {
                var cleared = 0;
                foreach (var handle in read.Inheritable)
                {
                    if (TryClear(clear, handle))
                    {
                        cleared++;
                    }
                }

                return new HandleSweep(PathNt, cleared, read.Total, watch.Elapsed.TotalMilliseconds);
            }
        }

        getFlags ??= ReadFlags;
        var scanned = 0;
        var clearedByScan = 0;
        for (long value = 4; value <= ceiling; value += 4)
        {
            scanned++;
            var handle = new IntPtr(value);
            try
            {
                if (getFlags(handle) is uint flags && (flags & HandleFlagInherit) != 0 && TryClear(clear, handle))
                {
                    clearedByScan++;
                }
            }
            catch (Exception)
            {
                // One probe failing does not stop the others.
            }
        }

        return new HandleSweep(PathScan, clearedByScan, scanned, watch.Elapsed.TotalMilliseconds);
    }

    private static bool TryClear(Func<IntPtr, bool> clear, IntPtr handle)
    {
        try
        {
            return clear(handle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static uint? ReadFlags(IntPtr handle) => GetHandleInformation(handle, out var flags) ? flags : null;

    // The handle table of this process, from NtQueryInformationProcess(ProcessHandleInformation, Windows 8 or later).
    // Null for any failure, and for a table without handles, which no process has.
    private static HandleTable? ReadHandleTable()
    {
        var size = InitialTableBytes;
        for (var attempt = 0; attempt < MaxTableAttempts; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(GetCurrentProcess(), ProcessHandleInformation, buffer, size, out var needed);
                if (status == 0)
                {
                    return ParseTable(buffer, size);
                }

                if (status != StatusInfoLengthMismatch)
                {
                    return null;
                }

                // Too small: the handles are counted again on the next call, and may have grown meanwhile.
                size = Math.Min(MaxTableBytes, Math.Max(needed, size) + size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    // PROCESS_HANDLE_SNAPSHOT_INFORMATION: NumberOfHandles and a reserved value, then one entry per handle:
    // HandleValue, HandleCount, PointerCount (pointer sized), GrantedAccess, ObjectTypeIndex, HandleAttributes, reserved.
    private static HandleTable? ParseTable(IntPtr buffer, int size)
    {
        var pointer = IntPtr.Size;
        var header = 2 * pointer;
        var entry = (3 * pointer) + 16;
        var count = Marshal.ReadIntPtr(buffer, 0).ToInt64();
        if (count <= 0 || header + (count * entry) > size)
        {
            return null;
        }

        var inheritable = new List<IntPtr>();
        for (var index = 0; index < count; index++)
        {
            var at = header + (index * entry);
            var attributes = unchecked((uint)Marshal.ReadInt32(buffer, at + (3 * pointer) + 8));
            if ((attributes & ObjInherit) != 0)
            {
                inheritable.Add(Marshal.ReadIntPtr(buffer, at));
            }
        }

        return new HandleTable((int)count, inheritable);
    }
}
