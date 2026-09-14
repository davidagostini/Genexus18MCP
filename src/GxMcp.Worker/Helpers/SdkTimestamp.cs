using System;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Single boundary for timestamps read from the GeneXus SDK.
    ///
    /// COM interop can return DateTime values with Utc, Local, or Unspecified
    /// kind. Utc and Local have unambiguous semantics. For Unspecified we keep
    /// the original ticks and label them UTC: that is the contract already used
    /// by the index HWM and watcher, and avoids silently applying the machine's
    /// local offset to a value whose zone was not supplied by the SDK.
    /// </summary>
    internal static class SdkTimestamp
    {
        private static long _utcCount;
        private static long _localCount;
        private static long _unspecifiedCount;

        public const string UnspecifiedPolicy = "utc-ticks";

        public static DateTime Read(Func<DateTime> read)
        {
            try { return Normalize(read()); }
            catch { return DateTime.MinValue; }
        }

        public static DateTime Normalize(DateTime value)
        {
            if (value == DateTime.MinValue) return value;

            switch (value.Kind)
            {
                case DateTimeKind.Utc:
                    Interlocked.Increment(ref _utcCount);
                    return value;
                case DateTimeKind.Local:
                    Interlocked.Increment(ref _localCount);
                    return value.ToUniversalTime();
                default:
                    Interlocked.Increment(ref _unspecifiedCount);
                    return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            }
        }

        public static string ToIsoUtc(DateTime value)
        {
            if (value == DateTime.MinValue) return null;
            return Normalize(value).ToString("o", CultureInfo.InvariantCulture);
        }

        public static JObject Diagnostics()
        {
            long utc = Interlocked.Read(ref _utcCount);
            long local = Interlocked.Read(ref _localCount);
            long unspecified = Interlocked.Read(ref _unspecifiedCount);
            return new JObject
            {
                ["observed"] = utc + local + unspecified > 0,
                ["kinds"] = new JObject
                {
                    ["utc"] = utc,
                    ["local"] = local,
                    ["unspecified"] = unspecified
                },
                ["unspecifiedPolicy"] = UnspecifiedPolicy
            };
        }

        internal static void ResetDiagnosticsForTest()
        {
            Interlocked.Exchange(ref _utcCount, 0);
            Interlocked.Exchange(ref _localCount, 0);
            Interlocked.Exchange(ref _unspecifiedCount, 0);
        }
    }
}
