using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class LifecycleResultPaginationTests
    {
        private static List<string> MakeErrors(int count)
        {
            var list = new List<string>(count);
            for (int i = 1; i <= count; i++)
                list.Add("error CS" + i.ToString("D4") + ": fake error " + i);
            return list;
        }

        [Fact]
        public void Page2_Of120_Size50_Returns50Items_StartingAtIndex50()
        {
            var errors = MakeErrors(120);
            var result = BatchService.BuildResultPayload(errors, page: 2, pageSize: 50);

            var arr = (JArray)result["items"];
            var meta = (JObject)result["_meta"]["pagination"];

            Assert.Equal(50, arr.Count);
            Assert.Equal(120, meta["total"].ToObject<int>());
            Assert.Equal(2, meta["page"].ToObject<int>());
            Assert.Equal(50, meta["page_size"].ToObject<int>());
            Assert.True(meta["has_more"].ToObject<bool>());
            // First item on page 2 is index 50 (1-based: error 51)
            Assert.Contains("CS0051", arr[0].ToString());
        }

        [Fact]
        public void LastPage_Of120_Size50_HasMoreFalse()
        {
            var errors = MakeErrors(120);
            // Page 3: items 100-119 (20 items), page_size=50
            var result = BatchService.BuildResultPayload(errors, page: 3, pageSize: 50);

            var arr = (JArray)result["items"];
            var meta = (JObject)result["_meta"]["pagination"];

            Assert.Equal(20, arr.Count);
            Assert.Equal(120, meta["total"].ToObject<int>());
            Assert.Equal(3, meta["page"].ToObject<int>());
            Assert.False(meta["has_more"].ToObject<bool>());
        }

        [Fact]
        public void PageSize500_ClampsTo200()
        {
            var errors = MakeErrors(200);
            var result = BatchService.BuildResultPayload(errors, page: 1, pageSize: 500);

            var meta = (JObject)result["_meta"]["pagination"];
            Assert.Equal(200, meta["page_size"].ToObject<int>());
            // All 200 items fit on page 1 with clamped size 200
            var arr = (JArray)result["items"];
            Assert.Equal(200, arr.Count);
        }

        [Fact]
        public void Page0_ClampsTo1()
        {
            var errors = MakeErrors(120);
            var result = BatchService.BuildResultPayload(errors, page: 0, pageSize: 50);

            var meta = (JObject)result["_meta"]["pagination"];
            Assert.Equal(1, meta["page"].ToObject<int>());
            // Page 1 with size 50 → 50 items
            var arr = (JArray)result["items"];
            Assert.Equal(50, arr.Count);
        }

        [Fact]
        public void NullItems_ReturnsEmptyWithZeroTotal()
        {
            var result = BatchService.BuildResultPayload(null, page: 1, pageSize: 50);

            var arr = (JArray)result["items"];
            var meta = (JObject)result["_meta"]["pagination"];

            Assert.Empty(arr);
            Assert.Equal(0, meta["total"].ToObject<int>());
            Assert.False(meta["has_more"].ToObject<bool>());
        }
    }
}
