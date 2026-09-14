using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class SdkTimestampTests
    {
        public SdkTimestampTests()
        {
            SdkTimestamp.ResetDiagnosticsForTest();
        }

        [Fact]
        public void UtcValue_IsPreserved()
        {
            var value = new DateTime(2026, 9, 14, 15, 30, 0, DateTimeKind.Utc);

            var normalized = SdkTimestamp.Normalize(value);

            Assert.Equal(value, normalized);
            Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        }

        [Fact]
        public void LocalValue_IsConvertedToUtc()
        {
            var value = new DateTime(2026, 9, 14, 15, 30, 0, DateTimeKind.Local);

            var normalized = SdkTimestamp.Normalize(value);

            Assert.Equal(value.ToUniversalTime(), normalized);
            Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        }

        [Fact]
        public void UnspecifiedValue_UsesUtcTicksWithoutMachineOffset()
        {
            var value = new DateTime(2026, 9, 14, 15, 30, 0, DateTimeKind.Unspecified);

            var normalized = SdkTimestamp.Normalize(value);

            Assert.Equal(value.Ticks, normalized.Ticks);
            Assert.Equal(DateTimeKind.Utc, normalized.Kind);
            Assert.Equal("2026-09-14T15:30:00.0000000Z", SdkTimestamp.ToIsoUtc(value));
        }

        [Fact]
        public void Read_ConvertsAccessorFailuresToUnknown()
        {
            var value = SdkTimestamp.Read(() => throw new InvalidOperationException("SDK unavailable"));

            Assert.Equal(DateTime.MinValue, value);
        }

        [Fact]
        public void Diagnostics_ReportsObservedKindsAndExplicitPolicy()
        {
            SdkTimestamp.Normalize(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            SdkTimestamp.Normalize(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local));
            SdkTimestamp.Normalize(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));

            var diagnostics = SdkTimestamp.Diagnostics();

            Assert.True(diagnostics["observed"].ToObject<bool>());
            Assert.Equal(1, diagnostics["kinds"]["utc"].ToObject<long>());
            Assert.Equal(1, diagnostics["kinds"]["local"].ToObject<long>());
            Assert.Equal(1, diagnostics["kinds"]["unspecified"].ToObject<long>());
            Assert.Equal(SdkTimestamp.UnspecifiedPolicy, diagnostics["unspecifiedPolicy"].ToObject<string>());
        }
    }
}
