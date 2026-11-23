using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GxMcpHub;

internal enum ListenerLookupState
{
    Found,
    NotFound,
    NotInspectable
}

// Who is listening on a backend endpoint, as the operating system reports it. A process is identified by PID and
// creation instant together, because Windows reuses PIDs.
internal sealed record ListenerLookup(ListenerLookupState State, int? ProcessId, DateTime? StartedUtc)
{
    public static readonly ListenerLookup NotFound = new(ListenerLookupState.NotFound, null, null);

    public static ListenerLookup NotInspectable(int? processId = null) =>
        new(ListenerLookupState.NotInspectable, processId, null);

    public static ListenerLookup Found(int processId, DateTime startedUtc) =>
        new(ListenerLookupState.Found, processId, startedUtc);
}

// Reads the system TCP listener table (no MCP traffic, no connection to the port) to learn which process owns the
// loopback port of a backend. The Hub uses it to notice that a Gateway was replaced and to date a Gateway it did not
// start against the last edit of the profile.
internal static class ListenerInspector
{
    private const int AddressFamilyIPv4 = 2;
    private const int AddressFamilyIPv6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorNotSupported = 50;
    private const int IPv4RowSize = 24;
    private const int IPv6RowSize = 56;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily,
        int tableClass,
        uint reserved);

    private sealed record ListenerRow(int ProcessId, bool IsIPv6, bool IsLoopback, bool IsAnyAddress);

    public static ListenerLookup Find(Uri endpoint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ListenerLookup.NotInspectable();
        }

        List<ListenerRow> rows;
        try
        {
            rows = ReadRows(AddressFamilyIPv4, endpoint.Port);
            rows.AddRange(ReadRows(AddressFamilyIPv6, endpoint.Port));
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException
                                       or EntryPointNotFoundException)
        {
            return ListenerLookup.NotInspectable();
        }

        var owners = SelectOwners(rows, endpoint.HostNameType);
        return owners.Count switch
        {
            0 => ListenerLookup.NotFound,
            1 => Describe(owners[0]),
            // Two different processes own the port on addresses that both serve the endpoint: the table alone
            // cannot say which one answers.
            _ => ListenerLookup.NotInspectable()
        };
    }

    private static List<int> SelectOwners(List<ListenerRow> rows, UriHostNameType hostType)
    {
        var candidates = rows.Where(row => row.IsLoopback || row.IsAnyAddress).ToList();
        // An explicit IP address is looked up in its own family only: a socket bound to ::1 never answers 127.0.0.1,
        // and the other way round. A host name (localhost) may resolve to either, so both families count.
        if (hostType == UriHostNameType.IPv4)
        {
            candidates = candidates.Where(row => !row.IsIPv6).ToList();
        }
        else if (hostType == UriHostNameType.IPv6)
        {
            candidates = candidates.Where(row => row.IsIPv6).ToList();
        }

        var owners = candidates.Select(row => row.ProcessId).Distinct().ToList();
        if (owners.Count > 1)
        {
            // A socket bound to the loopback address itself wins over one bound to every address.
            var specific = candidates.Where(row => row.IsLoopback).Select(row => row.ProcessId).Distinct().ToList();
            if (specific.Count == 1)
            {
                return specific;
            }
        }

        return owners;
    }

    private static ListenerLookup Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return ListenerLookup.Found(processId, process.StartTime.ToUniversalTime());
        }
        catch (ArgumentException)
        {
            // The process exited between the table read and the inspection.
            return ListenerLookup.NotFound;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Another account or an elevated process: the PID is known, its creation instant is not.
            return ListenerLookup.NotInspectable(processId);
        }
    }

    private static List<ListenerRow> ReadRows(int addressFamily, int port)
    {
        var rows = new List<ListenerRow>();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (size <= 0)
            {
                size = 4096;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
                if (result == 0)
                {
                    Parse(buffer, size, addressFamily, port, rows);
                    return rows;
                }

                if (result == ErrorNotSupported && addressFamily == AddressFamilyIPv6)
                {
                    return rows;
                }

                if (result != ErrorInsufficientBuffer)
                {
                    throw new InvalidOperationException($"GetExtendedTcpTable falhou com o código {result}.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException("GetExtendedTcpTable não estabilizou o tamanho da tabela.");
    }

    private static void Parse(IntPtr buffer, int size, int addressFamily, int port, List<ListenerRow> rows)
    {
        if (size < sizeof(int))
        {
            return;
        }

        var count = Marshal.ReadInt32(buffer);
        var rowSize = addressFamily == AddressFamilyIPv6 ? IPv6RowSize : IPv4RowSize;
        for (var index = 0; index < count; index++)
        {
            var offset = sizeof(int) + (index * rowSize);
            if (offset + rowSize > size)
            {
                return;
            }

            var row = IntPtr.Add(buffer, offset);
            if (addressFamily == AddressFamilyIPv6)
            {
                // MIB_TCP6ROW_OWNER_PID: address[16], scope, port (network order), remote address[16], remote scope,
                // remote port, state, owning PID.
                var rowPort = (Marshal.ReadByte(row, 20) << 8) | Marshal.ReadByte(row, 21);
                if (rowPort != port)
                {
                    continue;
                }

                var address = new byte[16];
                Marshal.Copy(row, address, 0, 16);
                var isAny = address.All(value => value == 0);
                var isLoopback = (address.Take(15).All(value => value == 0) && address[15] == 1) ||
                                 (address.Take(10).All(value => value == 0) &&
                                  address[10] == 0xFF && address[11] == 0xFF && address[12] == 127);
                rows.Add(new ListenerRow(Marshal.ReadInt32(row, 52), true, isLoopback, isAny));
            }
            else
            {
                // MIB_TCPROW_OWNER_PID: state, local address, local port (network order), remote address,
                // remote port, owning PID.
                var rowPort = (Marshal.ReadByte(row, 8) << 8) | Marshal.ReadByte(row, 9);
                if (rowPort != port)
                {
                    continue;
                }

                var first = Marshal.ReadByte(row, 4);
                var isAny = Marshal.ReadInt32(row, 4) == 0;
                rows.Add(new ListenerRow(Marshal.ReadInt32(row, 20), false, first == 127, isAny));
            }
        }
    }
}

// Whether the Gateway behind a backend runs with the profile as it is now, and what that answer rests on.
internal static class GatewayProfileEvidence
{
    public const string FromLoadedRevision = "started-by-this-hub-from-loaded-revision";
    public const string FromOlderRevision = "started-by-this-hub-from-older-revision";
    public const string StartedAfterEdit = "started-after-last-profile-edit";
    public const string StartedBeforeEdit = "started-before-last-profile-edit";
    public const string ListenerNotFound = "listener-not-found";
    public const string ListenerNotInspectable = "listener-not-inspectable";

    public static (bool? Applied, string Evidence) Evaluate(
        ListenerLookup listener,
        bool startedByThisHub,
        string? startedFromProfileRevision,
        string loadedRevision,
        string? onDiskRevision,
        DateTime? profileModifiedUtc)
    {
        if (startedByThisHub && !string.IsNullOrWhiteSpace(startedFromProfileRevision))
        {
            // A Gateway this Hub started: the Hub knows the revision it handed over. It is applied only while that
            // revision is the loaded one and the file on disk has not moved past it (an edit waiting for
            // gxhub_refresh is not applied).
            var applied = string.Equals(startedFromProfileRevision, loadedRevision, StringComparison.Ordinal) &&
                          (onDiskRevision is null ||
                           string.Equals(startedFromProfileRevision, onDiskRevision, StringComparison.Ordinal));
            return (applied, applied ? FromLoadedRevision : FromOlderRevision);
        }

        switch (listener.State)
        {
            case ListenerLookupState.NotFound:
                return (null, ListenerNotFound);
            case ListenerLookupState.Found when listener.StartedUtc is DateTime startedUtc &&
                                                profileModifiedUtc is DateTime modifiedUtc:
                // A Gateway another launcher started (another Hub, the promotion, a client): the only evidence is
                // time. A process created after the last write of the profile was started from it; one created
                // before, or in the same instant, predates the edit.
                return startedUtc > modifiedUtc
                    ? (true, StartedAfterEdit)
                    : (false, StartedBeforeEdit);
            default:
                return (null, ListenerNotInspectable);
        }
    }
}
