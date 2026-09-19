using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class SharedWorkerConnectionTests
    {
        private static SharedWorkerIdentity Identity() => SharedWorkerIdentity.Create(
            @"C:\Worker\GxMcp.Worker.exe",
            @"C:\KBs\Shared",
            @"C:\GeneXus18",
            "native-sdk",
            "18");

        [Fact]
        public void AttachRequestCarriesIdentityAndGatewayProcessMetadata()
        {
            var frame = SharedWorkerConnection.BuildAttachRequest(
                Identity(), "client-a", 123, 456, "nonce-a");

            Assert.Equal("attach", frame["gxmcp"]?.ToString());
            Assert.Equal(SharedWorkerConnection.ProtocolVersion, frame.Value<int>("version"));
            Assert.Equal(Identity().Key, frame["key"]?.ToString());
            Assert.Equal("client-a", frame["clientId"]?.ToString());
            Assert.Equal(123, frame.Value<int>("gatewayPid"));
            Assert.Equal(456, frame.Value<long>("gatewayStartTimeUtcTicks"));
        }

        [Fact]
        public void AttachAckRequiresMatchingIdentityAndCompleteProcessIdentity()
        {
            var identity = Identity();
            string line = new JObject
            {
                ["gxmcp"] = "attach_ack",
                ["version"] = SharedWorkerConnection.ProtocolVersion,
                ["key"] = identity.Key,
                ["attachId"] = "attach-a",
                ["hostPid"] = 10,
                ["hostStartTimeUtcTicks"] = 11,
                ["workerPid"] = 12,
                ["workerStartTimeUtcTicks"] = 13,
                ["generation"] = 2,
                ["ready"] = true
            }.ToString(Newtonsoft.Json.Formatting.None);

            Assert.True(SharedWorkerConnection.TryParseAttachAck(line, identity, out var info, out var error));
            Assert.Empty(error);
            Assert.Equal("attach-a", info!.AttachId);
            Assert.Equal(12, info.WorkerPid);
            Assert.Equal(2, info.Generation);
            Assert.True(info.SdkReady);
        }

        [Fact]
        public void AttachAckRejectsDifferentIdentity()
        {
            var identity = Identity();
            string line = new JObject
            {
                ["gxmcp"] = "attach_ack",
                ["version"] = SharedWorkerConnection.ProtocolVersion,
                ["key"] = "wrong",
                ["attachId"] = "attach-a",
                ["hostPid"] = 10,
                ["hostStartTimeUtcTicks"] = 11,
                ["workerPid"] = 12,
                ["workerStartTimeUtcTicks"] = 13,
                ["generation"] = 2
            }.ToString(Newtonsoft.Json.Formatting.None);

            Assert.False(SharedWorkerConnection.TryParseAttachAck(line, identity, out _, out var error));
            Assert.Contains("identity", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ControlFrameDetectionDoesNotTreatJsonRpcAsHostControl()
        {
            Assert.True(SharedWorkerConnection.IsControlFrame(
                JObject.Parse("{\"gxmcp\":\"heartbeat_ack\"}"), "heartbeat_ack"));
            Assert.False(SharedWorkerConnection.IsControlFrame(
                JObject.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"ping\"}"), "heartbeat_ack"));
        }
    }
}
