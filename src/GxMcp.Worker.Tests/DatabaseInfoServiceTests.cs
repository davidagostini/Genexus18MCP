using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DatabaseInfoServiceTests
    {
        public sealed class DataStoresPart
        {
            public System.Collections.Generic.List<object> DataStores { get; } = new System.Collections.Generic.List<object>();
        }

        public sealed class Model
        {
            public System.Collections.Generic.List<object> Parts { get; } = new System.Collections.Generic.List<object>();
            public System.Collections.Generic.List<object> DataStores { get; } = new System.Collections.Generic.List<object>();
        }

        public sealed class EnvironmentModel
        {
            public Model TargetModel { get; set; }
        }

        public sealed class FakeKb
        {
            public Model DesignModel { get; set; }
            public EnvironmentModel Environment { get; set; }
        }

        [Fact]
        public void ActiveEnvironmentDataStores_does_not_fall_back_to_design_model()
        {
            var designPart = new DataStoresPart();
            designPart.DataStores.Add("design-store");
            var targetPart = new DataStoresPart();
            targetPart.DataStores.Add("target-store");
            var target = new Model();
            target.Parts.Add(targetPart);
            var kb = new FakeKb
            {
                DesignModel = new Model(),
                Environment = new EnvironmentModel { TargetModel = target }
            };
            kb.DesignModel.Parts.Add(designPart);

            var stores = DatabaseInfoService.EnumerateActiveEnvironmentDataStores(kb);

            Assert.Single(stores);
            Assert.Equal("target-store", (string)stores[0]);
        }

        [Theory]
        [InlineData(1, "SqlServer")]
        [InlineData(2, "Db2")]
        [InlineData(3, "Informix")]
        [InlineData(4, "Oracle")]
        [InlineData(5, "MySQL")]
        [InlineData(6, "PostgreSQL")]
        [InlineData(7, "Oracle")]
        [InlineData(8, "Db2/AS400")]
        [InlineData(9, "Db2Universal")]
        [InlineData(10, "SAPHana")]
        [InlineData(11, "DynamoDB")]
        [InlineData(0, "Unknown")]
        [InlineData(99, "Unknown")]
        public void DbmsTypeLabel_MapsKnownCodes(int code, string expected)
        {
            Assert.Equal(expected, DatabaseInfoService.DbmsTypeLabel(code));
        }

        [Fact]
        public void GetInfo_WithoutKb_ReturnsStructuredError()
        {
            var svc = new DatabaseInfoService(null);
            string json = svc.GetInfo();
            var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
            Assert.Equal("error", obj["status"]?.ToString());
            Assert.Equal("KbNotOpen", obj["error"]?["code"]?.ToString());
        }
    }
}
