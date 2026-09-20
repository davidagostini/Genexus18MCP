using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdkTimestampNormalizerTests
    {
        [Fact]
        public void Unspecified_sdk_timestamp_is_preserved_as_utc_ticks()
        {
            var raw = new DateTime(2026, 9, 14, 12, 34, 56, DateTimeKind.Unspecified);

            var normalized = SdkTimestampNormalizer.NormalizeUtc(raw);

            Assert.Equal(raw.Ticks, normalized.Ticks);
            Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        }

        [Fact]
        public void Local_sdk_timestamp_is_converted_to_utc()
        {
            var raw = new DateTime(2026, 9, 14, 12, 34, 56, DateTimeKind.Local);

            var normalized = SdkTimestampNormalizer.NormalizeUtc(raw);

            Assert.Equal(raw.ToUniversalTime(), normalized);
            Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        }

        [Fact]
        public void MinValue_remains_unknown_and_utc_labelled()
        {
            var normalized = SdkTimestampNormalizer.NormalizeUtc(DateTime.MinValue);

            Assert.Equal(DateTime.MinValue.Ticks, normalized.Ticks);
            Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        }
    }
}
