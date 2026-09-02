using System;
using System.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class AnalysisEngineTests
    {
        [Fact]
        public void GeneXusSignatureParser_ParsesParametersAndDirections()
        {
            string rules = @"
                // Calculation procedure rules
                Order(InvoiceDate);
                parm(in:&ClientId, in:&StartDate, out:&TotalBilled, inout:&ProcessingStatus);
            ";

            var sig = GeneXusSignatureParser.Parse(rules);

            Assert.Equal("parm(in:&ClientId, in:&StartDate, out:&TotalBilled, inout:&ProcessingStatus);", sig.ParmRule);
            Assert.Equal(4, sig.Parameters.Count);

            Assert.Equal("ClientId", sig.Parameters[0].Name);
            Assert.Equal("in", sig.Parameters[0].Accessor);

            Assert.Equal("StartDate", sig.Parameters[1].Name);
            Assert.Equal("in", sig.Parameters[1].Accessor);

            Assert.Equal("TotalBilled", sig.Parameters[2].Name);
            Assert.Equal("out", sig.Parameters[2].Accessor);

            Assert.Equal("ProcessingStatus", sig.Parameters[3].Name);
            Assert.Equal("inout", sig.Parameters[3].Accessor);
        }

        [Fact]
        public void GeneXusSignatureParser_ExtractsOutgoingCalls()
        {
            string source = @"
                Event Start
                    Call(InitializeBatch, &BatchId)
                    &Rate = udp(GetCurrencyRate, 'USD', Today())
                    Submit(AsyncExportService, &BatchId)
                EndEvent
            ";

            var sig = GeneXusSignatureParser.Parse(null, source);

            Assert.Equal(3, sig.OutgoingCalls.Count);
            Assert.Contains("InitializeBatch", sig.OutgoingCalls);
            Assert.Contains("GetCurrencyRate", sig.OutgoingCalls);
            Assert.Contains("AsyncExportService", sig.OutgoingCalls);
        }

        private class MockAnalysisModeHandler : IAnalysisModeHandler
        {
            public string Mode => "test_mode";
            public string Handle(AnalysisContext context)
            {
                return McpResponse.Ok(code: "TestModeSuccess", target: context.Target);
            }
        }

        [Fact]
        public void AnalysisEngine_DispatchesRegisteredMode()
        {
            var engine = new AnalysisEngine();
            engine.RegisterHandler(new MockAnalysisModeHandler());

            Assert.True(engine.SupportsMode("test_mode"));
            Assert.True(engine.SupportsMode("TEST_MODE")); // case-insensitive

            string result = engine.Execute("test_mode", new AnalysisContext { Target = "InvoiceProc" });
            var json = JObject.Parse(result);

            Assert.Equal("ok", json["status"]?.ToString());
            Assert.Equal("InvoiceProc", json["target"]?.ToString());
        }

        [Fact]
        public void AnalysisEngine_RejectsUnknownModeWithHint()
        {
            var engine = new AnalysisEngine();
            engine.RegisterHandler(new MockAnalysisModeHandler());

            string result = engine.Execute("nonexistent_mode", new AnalysisContext());
            var json = JObject.Parse(result);

            Assert.Equal("error", json["status"]?.ToString());
            Assert.Equal("UnknownAnalysisMode", json["error"]?["code"]?.ToString());
            Assert.Contains("test_mode", json["error"]?["hint"]?.ToString());
        }
    }
}
