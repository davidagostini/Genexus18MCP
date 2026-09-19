using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace GxMcp.Gateway
{
    /// <summary>
    /// Physical compatibility identity for a shared Worker host. A KB path alone
    /// is not sufficient: different SDK majors, drivers, installations, or
    /// checkouts must never attach to the same SDK process.
    /// </summary>
    internal sealed class SharedWorkerIdentity
    {
        public string WorkerExecutable { get; init; } = string.Empty;
        public string KbPath { get; init; } = string.Empty;
        public string InstallationPath { get; init; } = string.Empty;
        public string Driver { get; init; } = string.Empty;
        public string Major { get; init; } = string.Empty;
        public string Key { get; init; } = string.Empty;

        internal static SharedWorkerIdentity Create(
            string? workerExecutable,
            string? kbPath,
            string? installationPath,
            string? driver,
            string? major)
        {
            string executable = NormalizePath(workerExecutable);
            string kb = NormalizePath(kbPath);
            string installation = NormalizePath(installationPath);
            string normalizedDriver = NormalizeToken(driver);
            string normalizedMajor = NormalizeToken(major);
            string material = string.Join("\n", executable, kb, installation, normalizedDriver, normalizedMajor);

            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
            var key = new StringBuilder(digest.Length * 2);
            foreach (byte value in digest)
                key.Append(value.ToString("x2"));

            return new SharedWorkerIdentity
            {
                WorkerExecutable = executable,
                KbPath = kb,
                InstallationPath = installation,
                Driver = normalizedDriver,
                Major = normalizedMajor,
                Key = key.ToString()
            };
        }

        internal static string NormalizePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                return Path.GetFullPath(value)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToLowerInvariant();
            }
            catch
            {
                return value.Trim()
                    .TrimEnd('\\', '/')
                    .ToLowerInvariant();
            }
        }

        internal static string NormalizeToken(string? value) =>
            (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    internal sealed class SharedWorkerRecord
    {
        public SharedWorkerIdentity Identity { get; set; } = new SharedWorkerIdentity();
        public int HostPid { get; set; }
        public long HostStartTimeUtcTicks { get; set; }
        public int WorkerPid { get; set; }
        public long WorkerStartTimeUtcTicks { get; set; }
        public string PipeName { get; set; } = string.Empty;
        public long Generation { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    internal sealed class SharedWorkerRegistryReservation : IDisposable
    {
        private Mutex? _mutex;
        private bool _held;

        internal SharedWorkerRegistryReservation(Mutex mutex)
        {
            _mutex = mutex;
            _held = true;
        }

        public void Dispose()
        {
            if (!_held) return;
            _held = false;
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
        }
    }

    internal static class SharedWorkerRegistry
    {
        private static readonly object Gate = new object();
        private static readonly string RegistryRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GxMcp", "shared-workers");

        internal static string GetRecordPath(SharedWorkerIdentity identity) =>
            Path.Combine(RegistryRoot, identity.Key + ".json");

        internal static string MutexName(SharedWorkerIdentity identity) =>
            "Local\\GxMcp.SharedWorker." + identity.Key;

        internal static string PipeName(SharedWorkerIdentity identity) =>
            "GxMcpShared_" + identity.Key;

        internal static SharedWorkerRegistryReservation? TryReserve(
            SharedWorkerIdentity identity,
            TimeSpan timeout,
            out SharedWorkerRecord? existing)
        {
            existing = null;
            Directory.CreateDirectory(RegistryRoot);
            var mutex = new Mutex(false, MutexName(identity));
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    mutex.Dispose();
                    return null;
                }

                string path = GetRecordPath(identity);
                existing = ReadRecord(path);
                if (IsRecordLive(existing, identity))
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                    return null;
                }

                ReapStaleRecord(existing, identity);
                TryDelete(path);
                return new SharedWorkerRegistryReservation(mutex);
            }
            catch
            {
                if (acquired) { try { mutex.ReleaseMutex(); } catch { } }
                mutex.Dispose();
                throw;
            }
        }

        internal static bool IsRecordLive(SharedWorkerRecord? record, SharedWorkerIdentity identity)
        {
            if (!IsRecordShapeValid(record, identity)) return false;
            try
            {
                using var host = Process.GetProcessById(record!.HostPid);
                using var worker = Process.GetProcessById(record.WorkerPid);
                return IsSameProcess(host, record.HostPid, record.HostStartTimeUtcTicks)
                    && IsSameProcess(worker, record.WorkerPid, record.WorkerStartTimeUtcTicks);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsSameProcess(Process process, int expectedPid, long expectedStartTimeUtcTicks)
        {
            if (process == null || expectedPid <= 0 || expectedStartTimeUtcTicks <= 0) return false;
            try
            {
                return process.Id == expectedPid
                    && !process.HasExited
                    && process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsRecordShapeValid(SharedWorkerRecord? record, SharedWorkerIdentity identity)
        {
            return record != null
                && string.Equals(record.Identity?.Key, identity.Key, StringComparison.Ordinal)
                && record.HostPid > 0
                && record.HostStartTimeUtcTicks > 0
                && record.WorkerPid > 0
                && record.WorkerStartTimeUtcTicks > 0
                && !string.IsNullOrWhiteSpace(record.PipeName)
                && record.Generation > 0;
        }

        internal static SharedWorkerRecord? ReadRecord(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonConvert.DeserializeObject<SharedWorkerRecord>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        internal static void WriteRecord(string path, SharedWorkerRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            lock (Gate)
            {
                string temp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, JsonConvert.SerializeObject(record, Formatting.None));
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
        }

        private static void ReapStaleRecord(SharedWorkerRecord? record, SharedWorkerIdentity identity)
        {
            if (!IsRecordShapeValid(record, identity)) return;
            TryKillExact(record!.WorkerPid, record.WorkerStartTimeUtcTicks);
            TryKillExact(record.HostPid, record.HostStartTimeUtcTicks);
        }

        private static void TryKillExact(int pid, long startTimeUtcTicks)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!IsSameProcess(process, pid, startTimeUtcTicks)) return;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
