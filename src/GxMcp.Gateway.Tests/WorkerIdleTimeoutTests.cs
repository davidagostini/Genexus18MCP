using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // The worker's cold start is ~90s (intrinsic GxServiceManager activation), so idle
    // reaping trades a warm worker for a 90s re-pay on the next call. These pin the
    // resolved idle window: a 30-minute default, a working "disable" path (0), and no
    // silent floor forcing 0 up to 1 minute (the old Math.Max(1,…) bug).
    public class WorkerIdleTimeoutTests
    {
        private static WorkerProcess Make(int? idleMinutes)
        {
            var config = new Configuration { Server = new ServerConfig() };
            if (idleMinutes.HasValue) config.Server.WorkerIdleTimeoutMinutes = idleMinutes.Value;
            return new WorkerProcess(config, new KbHandle("test", "C:\\fake\\path"));
        }

        [Fact]
        public void Default_IsThirtyMinutes()
        {
            // ServerConfig's own default...
            Assert.Equal(30, new ServerConfig().WorkerIdleTimeoutMinutes);
            // ...and the resolved window with an explicit default Server.
            Assert.Equal(TimeSpan.FromMinutes(30), Make(null).IdleTimeoutForTest);
        }

        [Fact]
        public void NullServer_ResolvesToThirtyMinutes()
        {
            // Server is null by default; the ctor fallback must still yield 30, not 0/crash.
            var worker = new WorkerProcess(new Configuration(), new KbHandle("test", "C:\\fake\\path"));
            Assert.Equal(TimeSpan.FromMinutes(30), worker.IdleTimeoutForTest);
        }

        [Fact]
        public void Zero_DisablesIdleReaping()
        {
            Assert.Equal(TimeSpan.Zero, Make(0).IdleTimeoutForTest);
        }

        [Fact]
        public void Negative_DisablesIdleReaping()
        {
            Assert.Equal(TimeSpan.Zero, Make(-5).IdleTimeoutForTest);
        }

        [Fact]
        public void PositiveValue_IsHonoredExactly_NoOneMinuteFloor()
        {
            // Regression: the old Math.Max(1, …) floor is gone; a small positive value
            // must pass through unchanged (and 0 must NOT become 1).
            Assert.Equal(TimeSpan.FromMinutes(2), Make(2).IdleTimeoutForTest);
        }
    }
}
