using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class IdeConcurrencyDetectorTests : IDisposable
    {
        public IdeConcurrencyDetectorTests()
        {
            IdeConcurrencyDetector.ResetProviders();
        }

        public void Dispose()
        {
            IdeConcurrencyDetector.ResetProviders();
        }

        [Fact]
        public void Check_WhenNoIdeProcessRunning_ReturnsNoWarning()
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>();

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");

            Assert.False(status.IsIdeRunning);
            Assert.False(status.IsTargetKbOpen);
            Assert.False(status.IsTargetObjectOpen);
            Assert.False(status.HasWarning);
            Assert.Null(status.WarningCode);
            Assert.Null(status.ToWarningObject("Customer", "SalesKB"));
        }

        [Fact]
        public void Check_WhenIdeRunningOnDifferentKb_ReturnsNoWarning()
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 1234,
                    ProcessName = "GeneXus",
                    MainWindowTitle = "GeneXus 18 - InventoryKB",
                    MainWindowHandle = new IntPtr(0x1000)
                },
                new IdeProcessInfo
                {
                    Id = 5678,
                    ProcessName = "GeneXus",
                    MainWindowTitle = "GeneXus 18 - HrKB",
                    MainWindowHandle = new IntPtr(0x2000)
                }
            };

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");

            Assert.True(status.IsIdeRunning);
            Assert.False(status.IsTargetKbOpen);
            Assert.False(status.IsTargetObjectOpen);
            Assert.False(status.HasWarning);
            Assert.Null(status.WarningCode);
        }

        [Fact]
        public void Check_WhenIdeRunningOnSameKb_TargetNotOpen_ReturnsNoticeWarning()
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 4242,
                    ProcessName = "GeneXus",
                    MainWindowTitle = "GeneXus 18 - SalesKB",
                    MainWindowHandle = new IntPtr(0x1000)
                }
            };
            IdeConcurrencyDetector.ChildWindowTextProvider = _ => new[] { "KB Explorer", "Properties", "Output" };

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");

            Assert.True(status.IsIdeRunning);
            Assert.True(status.IsTargetKbOpen);
            Assert.False(status.IsTargetObjectOpen);
            Assert.True(status.HasWarning);
            Assert.Equal(GotchaCodes.GotchaIdeActiveOnKb, status.WarningCode);
            Assert.Contains("CONCURRENCY NOTICE", status.WarningMessage);
            Assert.Contains("4242", status.WarningMessage);

            var warnJson = status.ToWarningObject("Customer", "SalesKB");
            Assert.NotNull(warnJson);
            Assert.Equal(GotchaCodes.GotchaIdeActiveOnKb, warnJson["code"]?.ToString());
            Assert.Equal("genexus://kb/tool-help/gotchas/GotchaIdeActiveOnKb", warnJson["docUrl"]?.ToString());
            Assert.False(warnJson["isTargetObjectOpen"]?.Value<bool>());
            Assert.True(warnJson["isTargetKbOpen"]?.Value<bool>());
        }

        [Fact]
        public void Check_WhenTargetOpenInMainWindowTitle_ReturnsCriticalWarning()
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 7777,
                    ProcessName = "GeneXus",
                    MainWindowTitle = "GeneXus 18 - SalesKB - [Customer]",
                    MainWindowHandle = new IntPtr(0x1000)
                }
            };

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");

            Assert.True(status.IsIdeRunning);
            Assert.True(status.IsTargetKbOpen);
            Assert.True(status.IsTargetObjectOpen);
            Assert.True(status.HasWarning);
            Assert.Equal(GotchaCodes.GotchaIdeObjectOpenInEditor, status.WarningCode);
            Assert.Contains("CRITICAL CONCURRENCY HAZARD", status.WarningMessage);
            Assert.Contains("7777", status.WarningMessage);

            var warnJson = status.ToWarningObject("Customer", "SalesKB");
            Assert.NotNull(warnJson);
            Assert.Equal(GotchaCodes.GotchaIdeObjectOpenInEditor, warnJson["code"]?.ToString());
            Assert.Equal("genexus://kb/tool-help/gotchas/GotchaIdeObjectOpenInEditor", warnJson["docUrl"]?.ToString());
            Assert.True(warnJson["isTargetObjectOpen"]?.Value<bool>());
            Assert.True(warnJson["isTargetKbOpen"]?.Value<bool>());
            Assert.Equal("Customer", warnJson["target"]?.ToString());
        }

        [Fact]
        public void Check_WhenTargetOpenInChildTab_ReturnsCriticalWarning()
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 8888,
                    ProcessName = "GeneXus",
                    MainWindowTitle = "GeneXus 18 - SalesKB",
                    MainWindowHandle = new IntPtr(0x1000)
                }
            };
            IdeConcurrencyDetector.ChildWindowTextProvider = _ => new[]
            {
                "KB Explorer",
                "Invoice [Transaction]",
                "Customer (Transaction)*",
                "Properties"
            };

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");

            Assert.True(status.IsIdeRunning);
            Assert.True(status.IsTargetKbOpen);
            Assert.True(status.IsTargetObjectOpen);
            Assert.Equal(GotchaCodes.GotchaIdeObjectOpenInEditor, status.WarningCode);
            Assert.Equal("Customer (Transaction)*", status.MatchedWindowTitle);
        }

        [Theory]
        [InlineData("Customer")]
        [InlineData("Customer (Transaction)")]
        [InlineData("Customer [Web Panel]")]
        [InlineData("Customer*")]
        [InlineData("Customer: Events")]
        [InlineData("GeneXus - [Customer]")]
        [InlineData("GeneXus - (Customer)")]
        [InlineData("GeneXus - Customer - SalesKB")]
        [InlineData("GeneXus 18 - Customer")]
        public void Check_TitleMatching_RecognizesValidTargetPatterns(string windowTitle)
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 9999,
                    MainWindowTitle = "GeneXus - SalesKB",
                    MainWindowHandle = new IntPtr(0x1000)
                }
            };
            IdeConcurrencyDetector.ChildWindowTextProvider = _ => new[] { windowTitle };

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");

            Assert.True(status.IsTargetObjectOpen);
            Assert.Equal(GotchaCodes.GotchaIdeObjectOpenInEditor, status.WarningCode);
        }

        [Theory]
        [InlineData("CustomerOrder")]
        [InlineData("NewCustomerForm")]
        [InlineData("CustomCustomer")]
        [InlineData("UnrelatedTab")]
        public void Check_TitleMatching_RejectsPartialSubstrings(string windowTitle)
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 9999,
                    MainWindowTitle = "GeneXus - SalesKB",
                    MainWindowHandle = new IntPtr(0x1000)
                }
            };
            IdeConcurrencyDetector.ChildWindowTextProvider = _ => new[] { windowTitle };

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");

            Assert.False(status.IsTargetObjectOpen);
            Assert.Equal(GotchaCodes.GotchaIdeActiveOnKb, status.WarningCode);
        }

        [Fact]
        public void TickleIde_InvokesPostMessageDelegateForEachIdeWindow()
        {
            var tickledHandles = new List<IntPtr>();
            var handle1 = new IntPtr(0x1234);
            var handle2 = new IntPtr(0x5678);

            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo { Id = 1, MainWindowTitle = "GeneXus - SalesKB", MainWindowHandle = handle1 },
                new IdeProcessInfo { Id = 2, MainWindowTitle = "GeneXus - OtherKB", MainWindowHandle = handle2 }
            };
            IdeConcurrencyDetector.PostMessageAction = hwnd => tickledHandles.Add(hwnd);

            var status = IdeConcurrencyDetector.Check("C:\\KBs\\SalesKB", "SalesKB", "Customer");
            status.TickleIde();

            Assert.Contains(handle1, tickledHandles);
            Assert.Contains(handle2, tickledHandles);
        }

        [Fact]
        public void AttachWarning_AddsWarningToEmptyOrExistingWarningsArray()
        {
            var warningObj = new JObject
            {
                ["code"] = GotchaCodes.GotchaIdeObjectOpenInEditor,
                ["message"] = "Object is open"
            };

            // Case 1: Raw JSON with no warnings field
            string raw1 = "{\"status\":\"ok\"}";
            string attached1 = WriteService.AttachWarning(raw1, warningObj);
            var parsed1 = JObject.Parse(attached1);
            var arr1 = parsed1["warnings"] as JArray;
            Assert.NotNull(arr1);
            Assert.Single(arr1);
            Assert.Equal(GotchaCodes.GotchaIdeObjectOpenInEditor, arr1[0]["code"]?.ToString());

            // Case 2: Raw JSON with existing different warning
            string raw2 = "{\"status\":\"ok\",\"warnings\":[{\"code\":\"LintKbCharsetLossy\"}]}";
            string attached2 = WriteService.AttachWarning(raw2, warningObj);
            var parsed2 = JObject.Parse(attached2);
            var arr2 = parsed2["warnings"] as JArray;
            Assert.NotNull(arr2);
            Assert.Equal(2, arr2.Count);

            // Case 3: Raw JSON already has the same warning code (deduplication)
            string raw3 = "{\"status\":\"ok\",\"warnings\":[{\"code\":\"GotchaIdeObjectOpenInEditor\"}]}";
            string attached3 = WriteService.AttachWarning(raw3, warningObj);
            var parsed3 = JObject.Parse(attached3);
            var arr3 = parsed3["warnings"] as JArray;
            Assert.NotNull(arr3);
            Assert.Single(arr3);
        }

        private static WriteService BuildIsolatedWriteService()
        {
            var indexCache = new IndexCacheService();
            var build = new BuildService();
            var kb = new KbService(indexCache);
            kb.SetBuildService(build);
            build.SetKbService(kb);
            indexCache.SetBuildService(build);
            var obj = new ObjectService(kb, build);
            return new WriteService(obj);
        }

        [Fact]
        public void WriteObject_FailIfOpenPolicy_RejectsEditWhenObjectIsOpenInIde()
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 5555,
                    MainWindowTitle = "GeneXus - [Customer]",
                    MainWindowHandle = new IntPtr(0x1000)
                }
            };

            var ws = BuildIsolatedWriteService();
            var args = new JObject
            {
                ["part"] = "Source",
                ["content"] = "parm();",
                ["concurrencyPolicy"] = "fail_if_open"
            };

            string response = ws.WriteObject("Customer", args);
            var parsed = JObject.Parse(response);

            Assert.Equal("error", parsed["status"]?.ToString());
            Assert.Equal("IdeObjectOpen", parsed["error"]?["code"]?.ToString());
            Assert.Contains("CRITICAL CONCURRENCY HAZARD", parsed["error"]?["message"]?.ToString());
            Assert.Contains("5555", parsed["error"]?["message"]?.ToString());
        }

        [Fact]
        public void BulkWrite_FailIfOpenPolicy_RejectsBatchWhenTargetIsOpenInIde()
        {
            IdeConcurrencyDetector.ProcessProvider = () => new List<IdeProcessInfo>
            {
                new IdeProcessInfo
                {
                    Id = 6666,
                    MainWindowTitle = "GeneXus - [Invoice]",
                    MainWindowHandle = new IntPtr(0x1000)
                }
            };

            var ws = BuildIsolatedWriteService();
            var args = new JObject
            {
                ["concurrencyPolicy"] = "fail_if_open",
                ["targets"] = new JArray
                {
                    new JObject { ["name"] = "Customer", ["content"] = "code1" },
                    new JObject { ["name"] = "Invoice", ["content"] = "code2" }
                }
            };

            string response = ws.BulkWrite(args);
            var parsed = JObject.Parse(response);

            Assert.Equal("error", parsed["status"]?.ToString());
            Assert.Equal("IdeObjectOpen", parsed["error"]?["code"]?.ToString());
            Assert.Equal("Invoice", parsed["target"]?.ToString());
        }
    }
}
