using System;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ObjectReaderTests
    {
        [Fact]
        public void ObjectReader_RejectsNullOrEmptyTarget()
        {
            var reader = new ObjectReader(null);
            string result = reader.Read(new ObjectReadRequest { Target = "" });

            var json = JObject.Parse(result);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("MissingTarget", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ObjectReader_RejectsNullRequest()
        {
            var reader = new ObjectReader(null);
            string result = reader.Read(null);

            var json = JObject.Parse(result);
            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("InvalidRequest", json["error"]?["code"]?.ToString());
        }

        [Fact]
        public void ObjectReader_Invalidate_RemovesCachedKeys()
        {
            var reader = new ObjectReader(null);
            reader.Invalidate("CustomerTransaction", "Rules");

            bool cached = reader.TryGetCached("CustomerTransaction", "Rules", null, null, "mcp", out _);
            Assert.False(cached);
        }
    }
}
