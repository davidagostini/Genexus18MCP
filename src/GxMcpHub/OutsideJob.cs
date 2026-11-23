using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace GxMcpHub;

// How the process this Hub started for a backend was started (gxhub_status backends[].gateway.startMode).
internal static class StartModes
{
    // The usual way: Process.Start from the Hub. The child is in the tree of the Hub and, if the client runs the Hub in
    // a job object, in that job.
    internal const string HubTree = "hub-tree";

    // By the detached launcher that the WMI service created: the child is in no job, and in no tree of the Hub.
    internal const string OutsideJob = "outside-job";

    // The launcher was tried and failed (WMI refused, no result in time, a process that does not match): the usual way
    // was used after all, with the limit it has.
    internal const string HubTreeFallback = "hub-tree-fallback";
}

// What GXMCP_HUB_START_OUTSIDE_JOB asks for.
internal enum OutsideJobPolicy
{
    // Leave the job only when a normal child would be inside one: the Hub outside any job starts as usual; the Hub in a
    // job asks, once, a probe child that starts the usual way whether it is in a job too.
    Auto,

    // Always through the launcher (for tests and for diagnosis).
    Always,

    // The start of the 0.3.0, without WMI.
    Never
}

internal static class OutsideJobPolicySetting
{
    internal const string Variable = "GXMCP_HUB_START_OUTSIDE_JOB";

    // auto (the default, also empty or anything else), always (1, true, on, yes), never (0, false, off, no).
    internal static OutsideJobPolicy Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "always" or "1" or "true" or "on" or "yes" => OutsideJobPolicy.Always,
        "never" or "0" or "false" or "off" or "no" => OutsideJobPolicy.Never,
        _ => OutsideJobPolicy.Auto
    };

    internal static string Name(OutsideJobPolicy policy) => policy switch
    {
        OutsideJobPolicy.Always => "always",
        OutsideJobPolicy.Never => "never",
        _ => "auto"
    };

    internal static OutsideJobPolicy FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(Variable));
}

internal sealed class LaunchSpecException : Exception
{
    public LaunchSpecException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

// What the Hub asks the detached launcher to start: the command, its arguments and directory, the environment (only
// what the profile declares, plus the profile revision; never the environment of the Hub, which can hold secrets of
// the client), the file the launcher answers in and the instant after which it must not start anything (the budget of the
// path outside the job: a launcher that the WMI service creates later than that finds the Hub gone to the usual start).
// It travels as base64 of a small JSON in the command line the WMI service is given; nothing is left on disk.
internal sealed record LaunchSpec(
    string File,
    string Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string ResultPath,
    DateTime? NotAfterUtc = null)
{
    internal const int Version = 1;

    // The encoded form must stay short: it is part of a command line, and anyone can read command lines.
    internal const int MaxEncodedLength = 8000;
    private const int MaxDecodedBytes = 64 * 1024;
    internal const string LaunchFolderName = "launch";
    internal const string ResultExtension = ".result";

    internal string Encode()
    {
        var environment = new JsonObject();
        foreach (var pair in Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        var json = new JsonObject
        {
            ["v"] = Version,
            ["file"] = File,
            ["args"] = Arguments,
            ["cwd"] = WorkingDirectory,
            ["env"] = environment,
            ["result"] = ResultPath
        };
        if (NotAfterUtc is DateTime notAfter)
        {
            json["notAfterUtc"] = notAfter.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    // The launcher answers only in a file named like this: <something>\GxMcpHub\launch\<name>.result.
    internal static bool IsValidResultPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            if (!string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(path), ResultExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var folder = Path.GetDirectoryName(path);
            var parent = folder is null ? null : Path.GetDirectoryName(folder);
            return folder is not null &&
                   parent is not null &&
                   string.Equals(Path.GetFileName(folder), LaunchFolderName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetFileName(parent), "GxMcpHub", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Reads and validates. The result path is checked first, on its own: with it the launcher can still say why it
    // refused the rest.
    internal static JsonObject ParseRoot(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is 0 or > MaxDecodedBytes)
            {
                throw new LaunchSpecException("invalid-spec");
            }

            return JsonNode.Parse(bytes) as JsonObject ?? throw new LaunchSpecException("invalid-spec");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new LaunchSpecException("invalid-spec");
        }
    }

    internal static string ReadResultPath(JsonObject root)
    {
        var path = Text(root["result"]);
        return IsValidResultPath(path) ? path! : throw new LaunchSpecException("invalid-spec");
    }

    internal static LaunchSpec Decode(string encoded)
    {
        var root = ParseRoot(encoded);
        var resultPath = ReadResultPath(root);
        return FromRoot(root, resultPath);
    }

    internal static LaunchSpec FromRoot(JsonObject root, string resultPath)
    {
        if (root["v"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != Version)
        {
            throw new LaunchSpecException("invalid-spec");
        }

        var file = Text(root["file"]);
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\0'))
        {
            throw new LaunchSpecException("invalid-spec");
        }

        var arguments = root["args"] is null ? string.Empty : Text(root["args"]);
        if (arguments is null || arguments.Contains('\0'))
        {
            throw new LaunchSpecException("invalid-spec");
        }

        string? workingDirectory = null;
        if (root["cwd"] is not null)
        {
            workingDirectory = Text(root["cwd"]);
            if (string.IsNullOrWhiteSpace(workingDirectory) || workingDirectory.Contains('\0'))
            {
                throw new LaunchSpecException("invalid-spec");
            }
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root["env"] is not null)
        {
            if (root["env"] is not JsonObject declared)
            {
                throw new LaunchSpecException("invalid-spec");
            }

            foreach (var pair in declared)
            {
                var value = Text(pair.Value);
                if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0') ||
                    value is null || value.Contains('\0'))
                {
                    throw new LaunchSpecException("invalid-spec");
                }

                environment[pair.Key] = value;
            }
        }

        // Optional (a specification without it has no deadline): the instant, in UTC, after which nothing is started.
        DateTime? notAfterUtc = null;
        if (root["notAfterUtc"] is not null)
        {
            var text = Text(root["notAfterUtc"]);
            if (text is null ||
                !DateTimeOffset.TryParse(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                throw new LaunchSpecException("invalid-spec");
            }

            notAfterUtc = parsed.UtcDateTime;
        }

        return new LaunchSpec(file, arguments, workingDirectory, environment, resultPath, notAfterUtc);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

// What the launcher says to the Hub, in the file named by the specification.
internal sealed record LaunchResult(int? ProcessId, DateTime? StartUtc, string? Error);

internal static class LaunchResultFile
{
    // Written whole beside the destination and moved over it, so the Hub never reads half of it.
    internal static void Write(string path, int? processId, DateTime? startUtc, string? error)
    {
        var json = new JsonObject
        {
            ["pid"] = processId,
            ["startUtc"] = startUtc?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["error"] = error
        };
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    // Null while the file is absent or unreadable (the Hub looks again). It is opened sharing everything: something that
    // scans the file just after it is moved into place (an antivirus) holds it for a moment, and must not make the
    // answer look absent.
    internal static LaunchResult? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string content;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            if (JsonNode.Parse(content) is not JsonObject root)
            {
                return null;
            }

            int? processId = root["pid"] is JsonValue pid && pid.TryGetValue<int>(out var number) ? number : null;
            DateTime? startUtc = null;
            if (root["startUtc"] is JsonValue start && start.TryGetValue<string>(out var text) &&
                DateTimeOffset.TryParse(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                startUtc = parsed.UtcDateTime;
            }

            var error = root["error"] is JsonValue code && code.TryGetValue<string>(out var message) ? message : null;
            return new LaunchResult(processId, startUtc, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

// The launcher: GxMcpHub.exe --launch-detached <specification>. The WMI service creates it outside every job; it
// starts the process of the backend with the environment of the WMI service (the default of the user) plus the one in
// the specification, tells the Hub which process it started and leaves. The process it started has no job, no
// channels of the Hub and, once the launcher is gone, no parent.
internal static class DetachedLauncher
{
    internal const string Switch = "--launch-detached";
    internal const int ExitInvalidSpec = 2;

    // The specification said not to start anything after an instant, and the instant has passed: the launcher answers
    // with LateCode and leaves with ExitLate, and starts nothing.
    internal const int ExitLate = 3;
    internal const string LateCode = "launcher-late";

    // A second parameter of the command line, which only the self-tests give (the Hub never writes it, and a
    // specification cannot carry it): the answer is written that many milliseconds after the process has been started, as
    // when something holds the disk. It lets a test hold the launcher between starting the process and answering.
    internal const string DelayAfterStartSwitch = "--self-test-delay-after-start";

    internal static int Run(string encoded, int delayAfterStartMilliseconds = 0)
    {
        LaunchSpec spec;
        string resultPath;
        try
        {
            var root = LaunchSpec.ParseRoot(encoded);
            resultPath = LaunchSpec.ReadResultPath(root);
            try
            {
                spec = LaunchSpec.FromRoot(root, resultPath);
            }
            catch (LaunchSpecException ex)
            {
                TryWriteError(resultPath, ex.Code);
                return 1;
            }
        }
        catch (LaunchSpecException)
        {
            // Without a valid place to answer there is nothing to write.
            return ExitInvalidSpec;
        }

        // The last thing before the process is started: the Hub gives up waiting for this launcher at this very instant
        // (plus a short grace) and starts the process the usual way. A launcher that the WMI service created after the
        // Hub gave up must not start a second one on the same port.
        if (spec.NotAfterUtc is DateTime notAfter && DateTime.UtcNow > notAfter)
        {
            TryWriteError(resultPath, LateCode);
            return ExitLate;
        }

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = spec.File,
                Arguments = spec.Arguments,
                WorkingDirectory = spec.WorkingDirectory ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var pair in spec.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }

            process = Process.Start(startInfo);
            if (process is null)
            {
                TryWriteError(resultPath, "start-failed");
                return 1;
            }

            if (delayAfterStartMilliseconds > 0)
            {
                Thread.Sleep(delayAfterStartMilliseconds);
            }

            try
            {
                LaunchResultFile.Write(resultPath, process.Id, process.StartTime.ToUniversalTime(), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                                           Win32Exception)
            {
                // The Hub will never learn of this process: it must not be left running, ownerless, where the Hub
                // starts another one.
                TryKill(process);
                return 1;
            }

            return 0;
        }
        catch (Win32Exception ex)
        {
            TryWriteError(resultPath, ex.NativeErrorCode switch
            {
                2 or 3 => "file-not-found",
                5 => "access-denied",
                _ => "start-failed"
            });
            return 1;
        }
        catch (Exception)
        {
            TryWriteError(resultPath, "start-failed");
            return 1;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryWriteError(string resultPath, string code)
    {
        try
        {
            LaunchResultFile.Write(resultPath, null, null, code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The Hub sees no answer and falls back.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }
}

// The probe: GxMcpHub.exe --job-probe. It is started the usual way by a Hub that is in a job and says whether it, a
// normal child of that Hub, is in a job too (a client whose job lets its children break away says no).
internal static class JobProbe
{
    internal const string Switch = "--job-probe";
    internal const string Prefix = "job-probe ";

    internal static int RunChild()
    {
        using var self = Process.GetCurrentProcess();
        var inJob = ProcessSupervisor.IsInJob(self);
        Console.Out.WriteLine(Prefix + (inJob is bool value ? (value ? "true" : "false") : "unknown"));
        Console.Out.Flush();
        return 0;
    }

    // true or false as the probe said; null for anything else (no answer, "unknown", noise).
    internal static bool? Parse(string? output)
    {
        foreach (var line in (output ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return line[Prefix.Length..] switch
                {
                    "true" => true,
                    "false" => false,
                    _ => null
                };
            }
        }

        return null;
    }
}

// Uncertain: the call failed after the service may have created the process (the failure came with, or after, the
// call that creates it): the answer file is waited for, like for a launcher that is slow.
internal readonly record struct WmiCreateOutcome(bool Ok, int ProcessId, string? Code, bool Uncertain = false);

// Win32_Process.Create through the WMI scripting objects, late bound (no package, no System.Management: the package
// of the Hub is five files). The WMI service creates the process under the token of the caller, in the session of the
// caller, and outside every job; it does not hand over the environment of the caller.
internal static class WmiProcessCreator
{
    // SW_HIDE for the window the process would open, and CREATE_NO_WINDOW (0x08000000) so that it opens none. The
    // service refuses CREATE_NO_WINDOW alone (return value 21, invalid parameter, measured) and accepts it with another
    // bit: CREATE_NEW_PROCESS_GROUP (0x00000200), harmless for a process that only starts another and leaves. Both
    // together, and each of them, keep a console process from showing a window on this machine, where the default
    // terminal is Windows Terminal; with neither, a Windows Terminal window opens for as long as the process lives.
    internal const ushort ShowWindowHidden = 0;
    internal const uint CreateFlagsNoWindow = 0x08000200;

    // Runs on the calling thread, which must be an STA one. Every COM object is kept in a local until the call has
    // returned and released at the end: chaining temporaries makes the scripting objects fail ("Unspecified error").
    internal static WmiCreateOutcome Create(
        string commandLine,
        string? currentDirectory,
        ushort? showWindow = ShowWindowHidden,
        uint? createFlags = CreateFlagsNoWindow,
        Action<IReadOnlyList<object>>? afterRelease = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WmiCreateOutcome(false, 0, "wmi-unavailable");
        }

        var objects = new List<object>();
        var invoked = false;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null)
            {
                return new WmiCreateOutcome(false, 0, "wmi-unavailable");
            }

            dynamic locator = Activator.CreateInstance(locatorType)!;
            objects.Add(locator);
            dynamic services = locator.ConnectServer(".", @"root\cimv2");
            objects.Add(services);
            dynamic processClass = services.Get("Win32_Process");
            objects.Add(processClass);
            dynamic methods = processClass.Methods_;
            objects.Add(methods);
            dynamic method = methods.Item("Create");
            objects.Add(method);
            dynamic definition = method.InParameters;
            objects.Add(definition);
            dynamic inParameters = definition.SpawnInstance_();
            objects.Add(inParameters);
            SetProperty(objects, inParameters, "CommandLine", commandLine);
            if (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                SetProperty(objects, inParameters, "CurrentDirectory", currentDirectory);
            }

            if (showWindow is not null || createFlags is not null)
            {
                dynamic startupClass = services.Get("Win32_ProcessStartup");
                objects.Add(startupClass);
                dynamic startup = startupClass.SpawnInstance_();
                objects.Add(startup);
                if (showWindow is ushort show)
                {
                    // Plain integers, as the scripting languages hand them over: WMI converts them to the type of the property.
                    SetProperty(objects, startup, "ShowWindow", (int)show);
                }

                if (createFlags is uint flags)
                {
                    SetProperty(objects, startup, "CreateFlags", flags);
                }

                SetProperty(objects, inParameters, "ProcessStartupInformation", startup);
            }

            // From here on the service may have created the process, whatever fails afterwards.
            invoked = true;
            dynamic outParameters = services.ExecMethod("Win32_Process", "Create", inParameters);
            objects.Add(outParameters);
            var returnValue = Convert.ToInt64(GetProperty(objects, outParameters, "ReturnValue"), System.Globalization.CultureInfo.InvariantCulture);
            if (returnValue != 0)
            {
                return new WmiCreateOutcome(false, 0, "wmi-refused-" + returnValue);
            }

            var processId = Convert.ToInt32(GetProperty(objects, outParameters, "ProcessId"), System.Globalization.CultureInfo.InvariantCulture);
            return new WmiCreateOutcome(true, processId, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException or
                                       Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or
                                       NotSupportedException or Win32Exception or FormatException or OverflowException)
        {
            Console.Error.WriteLine($"[hub] WMI recusou ou falhou ao criar o lançador: {ex.GetType().Name} (0x{ex.HResult:X8}).");
            return new WmiCreateOutcome(false, 0, "wmi-error", Uncertain: invoked);
        }
        finally
        {
            for (var index = objects.Count - 1; index >= 0; index--)
            {
                try
                {
                    if (Marshal.IsComObject(objects[index]))
                    {
                        Marshal.FinalReleaseComObject(objects[index]);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
                {
                    // Already released.
                }
            }

            // Only the self-tests give this: it shows them the objects after they were released.
            afterRelease?.Invoke(objects);
        }
    }

    // The properties of a WMI object are reached through its Properties_ collection, never by their own names: the
    // names of a class that WMI makes up at run time have identifiers that differ from one object to the next, and
    // the dynamic binder keeps what it learned from the first object (the first call works, every later one fails
    // with "Unspecified error"). The collection, Item and Value are fixed members of the scripting interfaces.
    private static void SetProperty(List<object> objects, dynamic wmiObject, string name, object value)
    {
        dynamic properties = wmiObject.Properties_;
        objects.Add(properties);
        dynamic property = properties.Item(name);
        objects.Add(property);
        property.Value = value;
    }

    private static object GetProperty(List<object> objects, dynamic wmiObject, string name)
    {
        dynamic properties = wmiObject.Properties_;
        objects.Add(properties);
        dynamic property = properties.Item(name);
        objects.Add(property);
        return (object)property.Value;
    }
}

// The few calls on a process handle that tell whether a process is the one that was expected and that stop it. Always
// through a handle: a process id can be handed to another process the moment the first one is gone, a handle cannot.
internal static class ProcessNative
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeHandle process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

    // ERROR_INVALID_PARAMETER is what OpenProcess says for a process id that no process has.
    internal const int ErrorInvalidParameter = 87;

    // A handle with the least that is needed (query, wait and stop), or null when the process is gone or cannot be
    // opened (another user, or protected); the error says which.
    internal static SafeProcessHandle? Open(int processId, out int error)
    {
        error = 0;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var raw = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, processId);
        if (raw == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        return new SafeProcessHandle(raw, ownsHandle: true);
    }

    // When the process was created, as the system says it, read from the handle.
    internal static DateTime? CreationUtc(SafeHandle handle)
    {
        try
        {
            return GetProcessTimes(handle, out var created, out _, out _, out _) && created > 0
                ? DateTime.FromFileTimeUtc(created)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ObjectDisposedException)
        {
            return null;
        }
    }

    // The file the process runs, as the system says it, read from the handle.
    internal static string? ImagePath(SafeHandle handle)
    {
        try
        {
            foreach (var capacity in new[] { 1024, 32768 })
            {
                var name = new StringBuilder(capacity);
                var size = (uint)capacity;
                if (QueryFullProcessImageNameW(handle, 0, name, ref size))
                {
                    return name.ToString();
                }

                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                {
                    return null;
                }
            }

            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    internal static bool HasExited(SafeHandle handle) => WaitForSingleObject(handle, 0) == 0;

    internal static bool WaitForExit(SafeHandle handle, uint milliseconds) => WaitForSingleObject(handle, milliseconds) == 0;

    internal static void Terminate(SafeHandle handle)
    {
        try
        {
            _ = TerminateProcess(handle, 1);
        }
        catch (ObjectDisposedException)
        {
            // Already released.
        }
    }

    // True when both name the same file, whatever the case, the slashes or the 8.3 form: the image of a process is what
    // the system reports, and a profile says its command however it was written.
    internal static bool SamePath(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(full, buffer, (uint)buffer.Capacity);
        if (length > buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetLongPathNameW(full, buffer, (uint)buffer.Capacity);
        }

        return length > 0 && length < buffer.Capacity ? buffer.ToString() : full;
    }
}

// What the Hub knows about the process the WMI service named as the launcher.
internal enum LauncherState
{
    // Opened and checked: held by a handle.
    Ours,

    // Not there any more: the launcher has left.
    Gone,

    // Could not be opened or read: it may well be running, and the Hub does not know.
    Unknown
}

// A process of this machine held by one handle, opened once. What is decided about it is decided through that handle.
internal sealed class OwnedProcess : IDisposable
{
    private readonly SafeProcessHandle _handle;

    private OwnedProcess(SafeProcessHandle handle, int id, DateTime createdUtc)
    {
        _handle = handle;
        Id = id;
        CreatedUtc = createdUtc;
    }

    internal int Id { get; }

    internal DateTime CreatedUtc { get; }

    // Only the self-tests set this: it makes Terminate do nothing, as for a process that something protects, to prove what
    // the Hub does when it cannot stop the launcher.
    internal static Func<bool>? TerminateSuppressedForTests { get; set; }

    // The process with this id, only if it was born at or after the instant and runs the given image. Null otherwise,
    // and nothing is ever done to a process that did not pass. The state says what a null means: Gone (no such process,
    // one older than the launch, or another program under that number: what was created is not there any more) or
    // Unknown (the process could not be opened or read: nothing is known about it, and it must not be taken as gone).
    internal static OwnedProcess? OpenIfOurs(
        int processId,
        DateTime bornNotBeforeUtc,
        string? expectedImage,
        out LauncherState state)
    {
        var handle = ProcessNative.Open(processId, out var error);
        if (handle is null)
        {
            state = error == ProcessNative.ErrorInvalidParameter ? LauncherState.Gone : LauncherState.Unknown;
            return null;
        }

        // A process that has exited can still be opened for as long as something holds a handle to it, and cannot be
        // read: it is the launcher that left.
        if (ProcessNative.HasExited(handle))
        {
            handle.Dispose();
            state = LauncherState.Gone;
            return null;
        }

        if (ProcessNative.CreationUtc(handle) is not DateTime born)
        {
            handle.Dispose();
            state = LauncherState.Unknown;
            return null;
        }

        if (born < bornNotBeforeUtc)
        {
            handle.Dispose();
            state = LauncherState.Gone;
            return null;
        }

        var image = ProcessNative.ImagePath(handle);
        if (image is null)
        {
            handle.Dispose();
            state = LauncherState.Unknown;
            return null;
        }

        if (!ProcessNative.SamePath(image, expectedImage))
        {
            handle.Dispose();
            state = LauncherState.Gone;
            return null;
        }

        state = LauncherState.Ours;
        return new OwnedProcess(handle, processId, born);
    }

    // A live process opened for the sweep of what a launcher left behind, only if it was born at or after the instant: a
    // process cannot be the child of one that was created after it, so a recorded parent id that fits only because the
    // number was reused does not pass.
    internal static OwnedProcess? OpenBornAfter(int processId, DateTime bornNotBeforeUtc)
    {
        var handle = ProcessNative.Open(processId, out _);
        if (handle is null)
        {
            return null;
        }

        if (ProcessNative.HasExited(handle) ||
            ProcessNative.CreationUtc(handle) is not DateTime born ||
            born < bornNotBeforeUtc)
        {
            handle.Dispose();
            return null;
        }

        return new OwnedProcess(handle, processId, born);
    }

    internal bool HasExited => ProcessNative.HasExited(_handle);

    internal bool WaitForExit(TimeSpan timeout) =>
        ProcessNative.WaitForExit(_handle, (uint)Math.Max(0, timeout.TotalMilliseconds));

    // Stops the process through the handle (never through its id). One that has already left is not touched.
    internal void Terminate()
    {
        if (TerminateSuppressedForTests?.Invoke() == true || HasExited)
        {
            return;
        }

        ProcessNative.Terminate(_handle);
    }

    public void Dispose() => _handle.Dispose();
}

// What the Hub does with a launcher it gives up on: the launcher is stopped through the handle it holds, its exit is
// waited for through that same handle, and then what it started is stopped, a process at a time, and what those started
// in turn. Descendants are the processes whose recorded parent is the launcher (its id cannot be given to another process
// while the handle is open) created after it, and in turn theirs, sweep after sweep until nobody new shows up. A Gateway
// that the launcher started and did not get to write down is not left behind to share the port with the one the Hub is
// about to start.
internal static class LauncherReaper
{
    internal const int MaxSweeps = 5;

    // True when the launcher has gone. The descendants are swept either way.
    internal static bool StopWithDescendants(
        OwnedProcess launcher,
        TimeSpan exitWait,
        TimeSpan pause,
        Func<IReadOnlyDictionary<int, int>?>? readParents = null)
    {
        launcher.Terminate();
        var exited = launcher.WaitForExit(exitWait);
        var read = readParents ?? (() => ProcessTree.ReadParents());
        var created = new Dictionary<int, DateTime> { [launcher.Id] = launcher.CreatedUtc };
        var held = new List<OwnedProcess>();
        try
        {
            for (var sweep = 0; sweep < MaxSweeps; sweep++)
            {
                var found = 0;
                if (read() is { } parents)
                {
                    // Within one picture of the processes, what a stopped process started is looked for until nothing is
                    // added; what starts after the picture is the next sweep's.
                    var added = true;
                    while (added)
                    {
                        added = false;
                        foreach (var (id, parent) in parents)
                        {
                            if (created.ContainsKey(id) || !created.TryGetValue(parent, out var parentCreated))
                            {
                                continue;
                            }

                            var child = OwnedProcess.OpenBornAfter(id, parentCreated);
                            if (child is null)
                            {
                                continue;
                            }

                            child.Terminate();
                            held.Add(child);
                            created[id] = child.CreatedUtc;
                            found++;
                            added = true;
                        }
                    }
                }

                if (found == 0)
                {
                    break;
                }

                Thread.Sleep(pause);
            }
        }
        finally
        {
            foreach (var process in held)
            {
                process.Dispose();
            }
        }

        return exited;
    }
}

// What the Hub needs to start a Gateway through the launcher, and the decision whether to.
internal sealed class OutsideJobStarter
{
    private readonly Func<bool?> _hubInJob;
    private readonly Func<CancellationToken, Task<bool?>> _probe;
    private readonly Func<string, string?, WmiCreateOutcome> _wmiCreate;
    private readonly Func<(string File, string Prefix)> _hubCommand;
    private readonly string _launchFolder;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool _probed;
    private bool? _probeResult;

    internal OutsideJobStarter(
        OutsideJobPolicy policy,
        Func<bool?>? hubInJob = null,
        Func<CancellationToken, Task<bool?>>? probe = null,
        Func<string, string?, WmiCreateOutcome>? wmiCreate = null,
        string? launchFolder = null,
        Func<(string File, string Prefix)>? hubCommand = null)
    {
        Policy = policy;
        _hubInJob = hubInJob ?? DefaultHubInJob;
        _hubCommand = hubCommand ?? DefaultHubCommand;
        _probe = probe ?? RunProbeChildAsync;
        _wmiCreate = wmiCreate ?? ((commandLine, directory) =>
            WmiProcessCreator.Create(commandLine, directory));
        _launchFolder = launchFolder ?? Path.Combine(Path.GetTempPath(), "GxMcpHub", LaunchSpec.LaunchFolderName);
    }

    internal OutsideJobPolicy Policy { get; }

    // The budget of the whole path outside the job (the WMI call, the answer of the launcher and the adoption), counted
    // from the call. The launcher is told the instant it ends (notAfterUtc) and starts nothing after it.
    internal TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(15);

    // How long past that instant the Hub still waits for the answer: what the launcher started just before it needs a
    // moment to be written down. Only after that is the usual start made.
    internal TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(1);

    // How long the answer is still looked for once the launcher has left without it being readable (a file that
    // something else still holds for a moment).
    internal TimeSpan LauncherExitGrace { get; set; } = TimeSpan.FromSeconds(1);

    // How long after a call that came back too late the answer file of that launcher is removed.
    internal TimeSpan LateAnswerCleanup { get; set; } = TimeSpan.FromSeconds(2);

    // How long the launcher that the Hub gives up on is given to leave once stopped, and the pause between the sweeps
    // for what it started. A launcher that does not leave in time is warned about (launcher-unkillable) and the usual
    // start goes on: availability first.
    internal TimeSpan LauncherExitWait { get; set; } = TimeSpan.FromSeconds(3);

    internal TimeSpan SweepPause { get; set; } = TimeSpan.FromMilliseconds(150);

    internal const string LauncherUnkillable = "launcher-unkillable";

    // The time the probe has (a child that only says whether it is in a job).
    internal TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    internal TimeSpan ResultPoll { get; set; } = TimeSpan.FromMilliseconds(50);

    // How far the creation instant of the process that the launcher names may be from the one it reports, and from the
    // window of the launch.
    internal TimeSpan StartTolerance { get; set; } = TimeSpan.FromSeconds(2);

    // Whether a child started the usual way would be in a job: false when the Hub itself is in none (nothing to
    // inherit), what the probe said when the Hub is in one, null while the probe has not answered.
    internal bool? ChildWouldBeInJob => _hubInJob() switch
    {
        false => false,
        true => _probeResult,
        _ => null
    };

    // Whether the next start goes through the launcher.
    internal async Task<bool> ShouldLeaveJobAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        switch (Policy)
        {
            case OutsideJobPolicy.Never:
                return false;
            case OutsideJobPolicy.Always:
                return true;
            default:
                if (_hubInJob() != true)
                {
                    return false;
                }

                return await ProbeOnceAsync(cancellationToken) == true;
        }
    }

    // The probe runs once per Hub process, whatever the number of backends. A probe that fails is not repeated: the
    // start goes the usual way.
    private async Task<bool?> ProbeOnceAsync(CancellationToken cancellationToken)
    {
        await _probeGate.WaitAsync(cancellationToken);
        try
        {
            if (!_probed)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(ProbeTimeout);
                try
                {
                    _probeResult = await _probe(budget.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _probeResult = null;
                }

                _probed = true;
            }

            return _probeResult;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    // Starts the backend through the launcher. On any failure the process is null and the failure is a short code
    // (no path): the caller starts the usual way, and before that nothing the launcher created is left behind.
    //
    // The whole path (the WMI call, the answer of the launcher and the adoption) has one budget, counted from the call.
    // The launcher is told the instant it ends (notAfterUtc) and starts nothing after it; the Hub waits for the answer up
    // to that instant plus a short grace, whether or not the WMI call has returned. So the usual start is never made
    // while a launcher that is still to come could start a second process on the same port.
    internal async Task<OutsideJobStart> TryStartAsync(
        string backendId,
        StartupConfig startup,
        string? profileRevision,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OutsideJobStart.Failed("not-windows");
        }

        // The environment of the Hub is never copied: only the one the profile declares and the profile revision.
        var environment = ProcessSupervisor.BuildStartupEnvironment(startup, profileRevision);
        var directory = string.IsNullOrWhiteSpace(startup.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startup.WorkingDirectory;
        var callUtc = DateTime.UtcNow;
        var notAfterUtc = callUtc + Budget;
        var deadlineUtc = notAfterUtc + Grace;
        string resultPath;
        string commandLine;
        try
        {
            Directory.CreateDirectory(_launchFolder);
            RemoveStale(_launchFolder);
            resultPath = Path.Combine(_launchFolder, Guid.NewGuid().ToString("N") + LaunchSpec.ResultExtension);
            var spec = new LaunchSpec(startup.Command, startup.Arguments, directory, environment, resultPath, notAfterUtc);
            var encoded = spec.Encode();
            if (encoded.Length > LaunchSpec.MaxEncodedLength)
            {
                return OutsideJobStart.Failed("spec-too-large");
            }

            var (file, prefix) = _hubCommand();
            commandLine = $"\"{file}\" {prefix}{DetachedLauncher.Switch} {encoded}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                       InvalidOperationException)
        {
            return OutsideJobStart.Failed("launch-folder");
        }

        var hubImage = _hubCommand().File;
        var adopted = new System.Runtime.CompilerServices.StrongBox<bool>();
        var wmi = StartWmiCall(backendId, commandLine, directory, resultPath, callUtc, deadlineUtc, adopted);
        OwnedProcess? launcher = null;
        try
        {
            LaunchResult? result = null;
            WmiCreateOutcome? created = null;
            DateTime? launcherGoneUtc = null;
            var launcherState = LauncherState.Unknown;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (created is null && wmi.IsCompleted)
                {
                    created = wmi.Result;
                    if (created.Value.Ok)
                    {
                        // Once, right after the call returns: the launcher is held by a handle that is checked (born in this
                        // launch, running the executable of the Hub) and everything done to it is done through that handle.
                        // Null when it has already left (it takes a few hundredths of a second) or is not what it should be.
                        launcher = OwnedProcess.OpenIfOurs(
                            created.Value.ProcessId,
                            callUtc - StartTolerance,
                            hubImage,
                            out launcherState);
                    }
                    else if (!created.Value.Uncertain)
                    {
                        // The service says it created nothing: no launcher is to come.
                        return OutsideJobStart.Failed(created.Value.Code ?? "wmi-error");
                    }
                }

                result = LaunchResultFile.TryRead(resultPath);
                if (result is not null)
                {
                    break;
                }

                var now = DateTime.UtcNow;
                if (now >= deadlineUtc)
                {
                    break;
                }

                // Gone only when it is known to be: a launcher that could not be opened is waited for like one that is
                // slow, up to the end of the budget.
                if (created is { Ok: true } && launcherGoneUtc is null &&
                    (launcherState == LauncherState.Gone || launcher is { HasExited: true }))
                {
                    launcherGoneUtc = now;
                }

                if (launcherGoneUtc is DateTime gone && now >= gone + LauncherExitGrace)
                {
                    // The launcher left, and its answer has not become readable for a while: it will not.
                    result = LaunchResultFile.TryRead(resultPath);
                    break;
                }

                await Task.Delay(ResultPoll, cancellationToken);
            }

            result ??= LaunchResultFile.TryRead(resultPath);
            if (result is null)
            {
                // Nobody answered. The launcher is stopped through its handle, and so is what it started and did not get
                // to write down (a launcher that passed the deadline check just before the instant can be held for longer
                // than the grace between starting the process and answering), so that nothing is left to share the port
                // with the one the Hub starts next. One that is created later refuses to start anything: it is told the
                // instant.
                StopLauncher(backendId, launcher, created is { Ok: true } && launcherState == LauncherState.Unknown);
                return OutsideJobStart.Failed(
                    created is null ? "wmi-timeout" : created.Value.Ok ? "no-result" : created.Value.Code ?? "wmi-error");
            }

            if (result.Error is not null)
            {
                return OutsideJobStart.Failed(LauncherReason(result.Error));
            }

            if (result.ProcessId is not int processId)
            {
                return OutsideJobStart.Failed("invalid-result");
            }

            if (result.StartUtc is not DateTime reportedStart)
            {
                // An id without the instant is not enough to adopt a process. If it is, all the same, what the launcher
                // started, it is stopped: left alone, it would share the port with the one the Hub is about to start.
                StopIfStartedByTheLauncher(processId, callUtc, startup.Command);
                return OutsideJobStart.Failed("invalid-result");
            }

            var (process, failure) = InspectGateway(processId, callUtc, startup.Command, reportedStart);
            if (process is null)
            {
                return OutsideJobStart.Failed(failure ?? "process-gone");
            }

            adopted.Value = true;
            return OutsideJobStart.Started(process);
        }
        catch (OperationCanceledException)
        {
            StopLauncher(backendId, launcher, false);
            throw;
        }
        finally
        {
            launcher?.Dispose();
            TryDelete(resultPath);
        }
    }

    // The launcher the Hub gives up on is stopped with what it started (see LauncherReaper). Without a handle (it could
    // not be opened, so nothing is known of it) there is nothing the Hub can stop, and it says so.
    private void StopLauncher(string backendId, OwnedProcess? launcher, bool unreachable)
    {
        var stopped = launcher is not null &&
                      LauncherReaper.StopWithDescendants(launcher, LauncherExitWait, SweepPause);
        if (!stopped && (launcher is not null || unreachable))
        {
            Console.Error.WriteLine(
                $"[{backendId}] aviso: o lançador não pôde ser encerrado ({LauncherUnkillable}); o início segue pelo caminho comum.");
        }
    }

    // The code the launcher wrote, as a reason to fall back: launcher-<code>. The late refusal already says it.
    internal static string LauncherReason(string? error)
    {
        var code = SafeCode(error);
        return code.StartsWith("launcher-", StringComparison.Ordinal) ? code : "launcher-" + code;
    }

    // A code the launcher wrote, kept to what a short code holds. It ends up on stderr and in the status, and the file
    // it comes from can be written by anything running as the same user: no path, space or line break gets through.
    internal static string SafeCode(string? code)
    {
        var clean = new StringBuilder();
        foreach (var character in (code ?? string.Empty).ToLowerInvariant().Take(40))
        {
            clean.Append(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? character : '-');
        }

        return clean.Length == 0 ? "unknown" : clean.ToString();
    }

    // The process the launcher says it started, checked through ONE handle opened once: born in this launch (and at the
    // instant the launcher said, when it said), and, for a command given by the full path of an executable, running that
    // very image. The process that passes comes back bound to that handle (what the Hub does with it afterwards, watching
    // it and stopping it, uses the same handle). What does not pass is not touched: it is not the Hub's.
    private (Process? Process, string? Failure) InspectGateway(
        int processId,
        DateTime callUtc,
        string command,
        DateTime? reportedStart)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return (null, "process-gone");
        }

        try
        {
            var handle = process.SafeHandle;
            if (ProcessNative.CreationUtc(handle) is not DateTime born)
            {
                process.Dispose();
                return (null, "start-time-unreadable");
            }

            if (born < callUtc - StartTolerance ||
                born > DateTime.UtcNow + StartTolerance ||
                (reportedStart is DateTime reported && Math.Abs((born - reported).TotalSeconds) > StartTolerance.TotalSeconds))
            {
                process.Dispose();
                return (null, "start-time-mismatch");
            }

            if (CommandNamesItsImage(command) && !ProcessNative.SamePath(ProcessNative.ImagePath(handle), command))
            {
                process.Dispose();
                return (null, "image-mismatch");
            }

            return (process, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            process.Dispose();
            return (null, "start-time-unreadable");
        }
    }

    // A command given by the full path of an executable is the image of the process it starts. A name that is searched for
    // (powershell.exe) or a script another program runs (a .cmd) is not: for those only the instant is checked.
    internal static bool CommandNamesItsImage(string command) =>
        Path.IsPathFullyQualified(command) &&
        Path.GetExtension(command) is { } extension &&
        (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".com", StringComparison.OrdinalIgnoreCase));

    // The process the launcher named without telling when it was born: stopped if, checked the same way, it is what the
    // launcher started (born in this launch, the right image), and left alone if not.
    private void StopIfStartedByTheLauncher(int processId, DateTime callUtc, string command)
    {
        var (process, _) = InspectGateway(processId, callUtc, command, reportedStart: null);
        if (process is null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or
                                       AggregateException)
        {
            // Gone already, or not reachable: nothing to stop.
        }
        finally
        {
            process.Dispose();
        }
    }

    // The WMI call on a thread of its own: the scripting objects like an STA one, and a service that hangs must not hang
    // the Hub. The call has no time of its own: the whole path has its budget (see TryStartAsync). A call that returns
    // after the Hub gave up and created a launcher anyway gets that launcher stopped (it is told the instant and starts
    // nothing, but nothing of it is left either), and the answer it may still write is removed.
    private Task<WmiCreateOutcome> StartWmiCall(
        string backendId,
        string commandLine,
        string? directory,
        string resultPath,
        DateTime callUtc,
        DateTime deadlineUtc,
        System.Runtime.CompilerServices.StrongBox<bool> adopted)
    {
        var completion = new TaskCompletionSource<WmiCreateOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(_wmiCreate(commandLine, directory));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[hub] a criação do lançador falhou: {ex.GetType().Name}.");
                completion.TrySetResult(new WmiCreateOutcome(false, 0, "wmi-error"));
            }
        })
        {
            IsBackground = true,
            Name = "gxhub-wmi"
        };
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        _ = completion.Task.ContinueWith(
            async task =>
            {
                // Only for an attempt the Hub gave up on: what an adopted launcher started is the Hub's own.
                if (DateTime.UtcNow < deadlineUtc || adopted.Value || !task.IsCompletedSuccessfully || !task.Result.Ok)
                {
                    return;
                }

                using (var late = OwnedProcess.OpenIfOurs(
                           task.Result.ProcessId,
                           callUtc - StartTolerance,
                           _hubCommand().File,
                           out var lateState))
                {
                    StopLauncher(backendId, late, late is null && lateState == LauncherState.Unknown);
                }

                await Task.Delay(LateAnswerCleanup);
                TryDelete(resultPath);
            },
            TaskScheduler.Default);
        return completion.Task;
    }

    private static void RemoveStale(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if ((file.EndsWith(LaunchSpec.ResultExtension, StringComparison.OrdinalIgnoreCase) ||
                     file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) &&
                    File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - TimeSpan.FromHours(1))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool? DefaultHubInJob()
    {
        using var self = Process.GetCurrentProcess();
        return ProcessSupervisor.IsInJob(self);
    }

    // The executable that runs this Hub: the apphost, or "dotnet <dll>" when it was started that way (the self-tests).
    internal static (string File, string Prefix) DefaultHubCommand()
    {
        var processPath = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Executável do Hub indisponível.");
        var assemblyPath = typeof(OutsideJobStarter).Assembly.Location;
        return Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrEmpty(assemblyPath)
            ? (processPath, $"\"{assemblyPath}\" ")
            : (processPath, string.Empty);
    }

    // The probe child: started the usual way, so that it inherits whatever job the Hub has.
    private async Task<bool?> RunProbeChildAsync(CancellationToken cancellationToken)
    {
        var (file, prefix) = _hubCommand();
        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            Arguments = prefix + JobProbe.Switch,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        try
        {
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return JobProbe.Parse(output);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        }
    }
}

internal readonly record struct OutsideJobStart(Process? Process, string? Failure)
{
    internal static OutsideJobStart Started(Process process) => new(process, null);

    internal static OutsideJobStart Failed(string code) => new(null, code);
}
