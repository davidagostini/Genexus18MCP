using System;
using System.Globalization;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Converts SDK lifecycle timestamps to the one representation used by the
    /// index, watcher, delta baseline and JSON projections.
    ///
    /// GeneXus SDK timestamps can cross the COM boundary as DateTimeKind.Unspecified.
    /// That value carries the SDK's wall-clock ticks, not the worker machine's local
    /// time zone. Treating it as local would shift the delta boundary on non-UTC hosts.
    /// </summary>
    public static class SdkTimestampNormalizer
    {
        public static DateTime NormalizeUtc(DateTime value)
        {
            if (value == DateTime.MinValue)
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

            if (value.Kind == DateTimeKind.Utc)
                return value;

            if (value.Kind == DateTimeKind.Local)
            {
                try { return value.ToUniversalTime(); }
                catch { return DateTime.SpecifyKind(value, DateTimeKind.Utc); }
            }

            // Unspecified is the SDK/COM representation. Preserve the exact ticks
            // and attach the UTC label instead of applying the host's local offset.
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        public static bool TryParseUtc(string text, out DateTime value)
        {
            value = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (!DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces
                        | DateTimeStyles.AssumeUniversal
                        | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                return false;
            }

            value = parsed.UtcDateTime;
            return true;
        }
    }
}
