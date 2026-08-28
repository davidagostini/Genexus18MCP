using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class NavigationDomainModelTests
    {
        [Fact]
        public void NavigationReport_RoundTripsJson_PreservesLevelsAndFilters()
        {
            var report = new NavigationReport
            {
                TargetName = "PInvoiceProcess",
                Status = "OK"
            };

            var level = new NavigationLevel
            {
                Number = 1,
                Type = "For Each",
                Line = 15,
                BaseTable = "Invoice",
                BaseTableDescription = "Customer Invoices",
                Index = "UINVOICE",
                IsOptimized = true,
                Order = new List<string> { "InvoiceDate", "InvoiceId" }
            };

            level.Filters.Add(new NavigationFilter
            {
                Element = "Equality",
                Attribute = "CustomerId",
                Op = "=",
                Value = "&CustomerId",
                Expression = "CustomerId = &CustomerId"
            });

            report.Levels.Add(level);
            report.Warnings.Add("Warning: slow index scan");

            var json = report.ToJson();
            var restored = NavigationReport.FromJson(json.ToString());

            Assert.Equal("PInvoiceProcess", restored.TargetName);
            Assert.Equal("OK", restored.Status);
            Assert.Single(restored.Levels);
            Assert.Equal(1, restored.Levels[0].Number);
            Assert.Equal("Invoice", restored.Levels[0].BaseTable);
            Assert.Equal("UINVOICE", restored.Levels[0].Index);
            Assert.True(restored.Levels[0].IsOptimized);
            Assert.Equal(2, restored.Levels[0].Order.Count);
            Assert.Single(restored.Levels[0].Filters);
            Assert.Equal("CustomerId", restored.Levels[0].Filters[0].Attribute);
            Assert.Equal("=", restored.Levels[0].Filters[0].Op);
            Assert.Equal("&CustomerId", restored.Levels[0].Filters[0].Value);
            Assert.Single(restored.Warnings);
        }

        [Fact]
        public void NavigationReport_GenerateSql_ProducesSelectWhereAndOrderBy()
        {
            var report = new NavigationReport
            {
                TargetName = "POrdersReport",
                Status = "OK"
            };

            var level = new NavigationLevel
            {
                Number = 1,
                BaseTable = "Orders",
                Index = "IORDERS1",
                Order = new List<string> { "OrderDate", "OrderId" }
            };

            level.Filters.Add(new NavigationFilter
            {
                Attribute = "OrderStatus",
                Op = "=",
                Value = "&Status"
            });

            level.Filters.Add(new NavigationFilter
            {
                Attribute = "OrderAmount",
                Op = ">=",
                Value = "&MinAmount"
            });

            report.Levels.Add(level);

            var sqlResult = report.GenerateSql();
            var queries = sqlResult["queries"] as JArray;

            Assert.NotNull(queries);
            Assert.Single(queries);

            var query = queries[0] as JObject;
            Assert.Equal(1, (int)query["level"]);
            Assert.Equal("Orders", (string)query["baseTable"]);
            Assert.Equal("IORDERS1", (string)query["indexUsed"]);

            string sql = (string)query["sql"];
            Assert.Contains("SELECT * FROM Orders", sql);
            Assert.Contains("WHERE OrderStatus = :Status AND OrderAmount >= :MinAmount", sql);
            Assert.Contains("ORDER BY OrderDate, OrderId", sql);

            var parms = query["parametersExpected"] as JArray;
            Assert.NotNull(parms);
            Assert.Contains("Status", parms.Values<string>());
            Assert.Contains("MinAmount", parms.Values<string>());
        }

        [Fact]
        public void NavigationReport_GenerateSql_WithLevelFilter_GeneratesOnlyTargetLevel()
        {
            var report = new NavigationReport { TargetName = "MultiLevelProc" };

            report.Levels.Add(new NavigationLevel
            {
                Number = 1,
                BaseTable = "Header"
            });

            report.Levels.Add(new NavigationLevel
            {
                Number = 2,
                BaseTable = "Lines"
            });

            var sqlResult = report.GenerateSql(levelNumber: 2);
            var queries = sqlResult["queries"] as JArray;

            Assert.NotNull(queries);
            Assert.Single(queries);
            Assert.Equal(2, (int)queries[0]["level"]);
            Assert.Equal("Lines", (string)queries[0]["baseTable"]);
        }

        [Fact]
        public void NavigationReport_GenerateSql_WithoutBaseTable_EmitsWarning()
        {
            var report = new NavigationReport { TargetName = "NoBaseTableProc" };
            report.Levels.Add(new NavigationLevel
            {
                Number = 1,
                BaseTable = null
            });

            var sqlResult = report.GenerateSql();
            var queries = sqlResult["queries"] as JArray;
            var warnings = sqlResult["warnings"] as JArray;

            Assert.Empty(queries);
            Assert.Single(warnings);
            Assert.Contains("no base table", (string)warnings[0]);
        }

        [Fact]
        public void NavigationReport_Status_ReflectsNoNavigationBlocksWhenEmpty()
        {
            var report = new NavigationReport { TargetName = "EmptyProc" };
            var json = report.ToJson();

            Assert.Equal("NoNavigationBlocks", (string)json["status"]);
            Assert.NotNull(json["hint"]);
        }

        [Fact]
        public void NavigationReport_Error_SerializesErrorEnvelope()
        {
            var report = NavigationReport.Error("MissingObj", "Navigation report not found.");
            var json = report.ToJson();

            Assert.Equal("Error", (string)json["status"]);
            Assert.Equal("Navigation report not found.", (string)json["message"]);
        }

        [Fact]
        public void NavigationSqlService_DirectDelegation_WorksWithoutSdk()
        {
            var navService = new NavigationService(kbService: null);
            var sqlService = new NavigationSqlService(navService, kbService: null, objectService: null);

            var resultJson = sqlService.Generate("NonExistentObject");
            var result = JObject.Parse(resultJson);

            Assert.Equal("Error", (string)result["status"]);
        }
    }
}
