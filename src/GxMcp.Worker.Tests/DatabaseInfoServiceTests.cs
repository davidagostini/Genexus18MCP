using GxMcp.Worker.Services;
using Xunit;
using System.Reflection;

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
            public string Name { get; set; }
            public Model TargetModel { get; set; }
        }

        public sealed class PropertyBag
        {
            private readonly System.Collections.Generic.Dictionary<string, object> _values
                = new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);

            public void Set(string name, object value) => _values[name] = value;
            public object GetPropertyValue(string name) => _values.TryGetValue(name, out var value) ? value : null;
        }

        public sealed class DataStore
        {
            public string Name { get; set; }
            public int Dbms { get; set; }
            public bool IsDefault { get; set; }
            public PropertyBag Properties { get; } = new PropertyBag();
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

        [Fact]
        public void DefaultDataStoreInfo_UsesActiveTargetAndPostgresProvider()
        {
            var designPart = new DataStoresPart();
            designPart.DataStores.Add(new DataStore { Name = "DesignOracle", Dbms = 4, IsDefault = true });
            var targetPart = new DataStoresPart();
            var targetStore = new DataStore { Name = "TargetPostgres", Dbms = 6, IsDefault = true };
            targetStore.Properties.Set("ADONET_DRIVER", "Npgsql");
            targetStore.Properties.Set("CS_SERVER", "localhost");
            targetStore.Properties.Set("CS_SCHEMA", "public");
            targetPart.DataStores.Add(targetStore);

            var target = new Model();
            target.Parts.Add(targetPart);
            var kb = new FakeKb
            {
                DesignModel = new Model(),
                Environment = new EnvironmentModel { TargetModel = target }
            };
            kb.DesignModel.Parts.Add(designPart);

            var info = DatabaseInfoService.GetDefaultDataStoreInfo(kb);

            Assert.Equal("TargetPostgres", info["name"]?.ToString());
            Assert.Equal("postgres", info["dialect"]?.ToString());
            Assert.Equal("PostgreSQL", info["type"]?.ToString());
            Assert.Equal("Npgsql", info["provider"]?.ToString());

            var describe = typeof(KbService).GetMethod("DescribeActiveDataStore", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(describe);
            string description = (string)describe.Invoke(null, new object[] { kb });
            Assert.Contains("TargetPostgres", description);
            Assert.Contains("type=postgres", description);
            Assert.DoesNotContain("DesignOracle", description);
        }

        [Fact]
        public void DefaultDataStoreInfo_RecognizesModernPostgresCodeWithoutProvider()
        {
            var targetPart = new DataStoresPart();
            var targetStore = new DataStore { Name = "TargetPostgres", Dbms = 0, IsDefault = true };
            targetStore.Properties.Set("DBMS", 15);
            targetPart.DataStores.Add(targetStore);
            var target = new Model();
            target.Parts.Add(targetPart);
            var kb = new FakeKb
            {
                DesignModel = new Model(),
                Environment = new EnvironmentModel { TargetModel = target }
            };

            var info = DatabaseInfoService.GetDefaultDataStoreInfo(kb);

            Assert.Equal("postgres", info["dialect"]?.ToString());
            Assert.Equal("PostgreSQL", info["type"]?.ToString());
            Assert.Equal(string.Empty, info["provider"]?.ToString());
            Assert.Equal(15, info["dbmsCode"]?.Value<int>());
        }

        [Fact]
        public void GetInfo_UsesActiveEnvironmentTargetInsteadOfDesignModel()
        {
            var designPart = new DataStoresPart();
            designPart.DataStores.Add(new DataStore { Name = "DesignOracle", Dbms = 4, IsDefault = true });
            var targetPart = new DataStoresPart();
            targetPart.DataStores.Add(new DataStore { Name = "TargetPostgres", Dbms = 6, IsDefault = true });
            var target = new Model();
            target.Parts.Add(targetPart);
            var kb = new FakeKb
            {
                DesignModel = new Model(),
                Environment = new EnvironmentModel { Name = "TargetPostgres", TargetModel = target }
            };
            kb.DesignModel.Parts.Add(designPart);

            var kbService = new KbService(new IndexCacheService());
            typeof(KbService).GetField("_kb", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(kbService, kb);
            var payload = Newtonsoft.Json.Linq.JObject.Parse(new DatabaseInfoService(kbService).GetInfo());
            var result = payload["result"] as Newtonsoft.Json.Linq.JObject;

            Assert.Equal("TargetPostgres", result["environment"]?.ToString());
            Assert.Equal("resolved", result["environmentState"]?.ToString());
            Assert.Single((Newtonsoft.Json.Linq.JArray)result["datastores"]);
            Assert.Equal("TargetPostgres", result["datastores"][0]["name"]?.ToString());
            Assert.Equal("postgres", result["datastores"][0]["dialect"]?.ToString());
        }

        [Theory]
        [InlineData(1, "SqlServer")]
        [InlineData(2, "Db2")]
        [InlineData(3, "Informix")]
        [InlineData(4, "Oracle")]
        [InlineData(5, "MySQL")]
        [InlineData(6, "PostgreSQL")]
        [InlineData(15, "PostgreSQL")]
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
