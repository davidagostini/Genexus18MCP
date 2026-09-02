using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Tests
{
    public class MutationEngineTests
    {
        [Fact]
        public void MutationEngine_PreflightGuard_RejectsLiteralLineBreaks()
        {
            var engine = new MutationEngine();
            var req = new MutationRequest
            {
                Target = "TestProc",
                Part = "Source",
                Content = "// comment\\r\\nMsg('hello');",
                Mode = MutationMode.Xml
            };

            var res = engine.Execute(req);

            Assert.False(res.Success);
            Assert.Equal("LiteralLineBreaksDetected", res.ErrorCode);
            Assert.Contains("literal line break", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MutationEngine_DryRun_ReturnsPlanWithoutPersisting()
        {
            var engine = new MutationEngine();
            var req = new MutationRequest
            {
                Target = "CustomerProc",
                Part = "Source",
                Content = "Msg('Hello World');",
                Mode = MutationMode.Xml,
                DryRun = true
            };

            var res = engine.Execute(req);

            Assert.True(res.Success);
            Assert.NotNull(res.Plan);
            Assert.Equal(1, res.Plan["totalObjects"]?.ToObject<int>());
            var mutations = res.Plan["mutations"] as JArray;
            Assert.NotNull(mutations);
            Assert.Single(mutations);
            Assert.Equal("CustomerProc", mutations[0]["target"]?.ToString());
            Assert.Equal("Source", mutations[0]["part"]?.ToString());
        }

        [Fact]
        public void MutationEngine_OptimisticConcurrency_RejectsVersionMismatch()
        {
            var engine = new MutationEngine();
            var req = new MutationRequest
            {
                Target = "CustomerProc",
                Part = "Source",
                Content = "Msg('Updated');",
                Mode = MutationMode.Xml,
                ExpectedVersion = "v1.0",
                CurrentVersionResolver = (target, part) => "v2.0"
            };

            var res = engine.Execute(req);

            Assert.False(res.Success);
            Assert.Equal("ConcurrencyConflict", res.ErrorCode);
            Assert.Contains("expected version", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MutationEngine_MultiObjectUnitOfWork_ExecutesLifoRollbackOnFailure()
        {
            var applied = new List<string>();
            var rolledBack = new List<string>();

            var mockWriter = new DelegateSdkObjectWriter(
                write: (target, args) =>
                {
                    if (target == "FailObj")
                    {
                        return new JObject { ["status"] = "Error", ["message"] = "Simulated disk failure" }.ToString();
                    }
                    applied.Add(target);
                    return new JObject { ["status"] = "Success" }.ToString();
                },
                rollback: (target, args) =>
                {
                    rolledBack.Add(target);
                    return new JObject { ["status"] = "Success" }.ToString();
                }
            );

            var engine = new MutationEngine(mockWriter);
            var req = new MutationRequest
            {
                Targets = new JArray
                {
                    new JObject { ["target"] = "Obj1", ["part"] = "Source", ["content"] = "Code1" },
                    new JObject { ["target"] = "Obj2", ["part"] = "Source", ["content"] = "Code2" },
                    new JObject { ["target"] = "FailObj", ["part"] = "Source", ["content"] = "CodeFail" }
                },
                RollbackOnFailure = true
            };

            var res = engine.Execute(req);

            Assert.False(res.Success);
            Assert.True(res.RolledBack);
            Assert.Contains("FailObj", res.ErrorMessage);
            // Verify LIFO order of rollback: Obj2 rolled back before Obj1
            Assert.Equal(2, rolledBack.Count);
            Assert.Equal("Obj2", rolledBack[0]);
            Assert.Equal("Obj1", rolledBack[1]);
        }

        private class DelegateSdkObjectWriter : ISdkObjectWriter
        {
            private readonly Func<string, JObject, string> _write;
            private readonly Func<string, JObject, string> _rollback;

            public DelegateSdkObjectWriter(Func<string, JObject, string> write, Func<string, JObject, string> rollback = null)
            {
                _write = write;
                _rollback = rollback ?? write;
            }

            public string WriteObject(string target, JObject args)
            {
                bool isRollback = args["isRollback"]?.ToObject<bool?>() ?? false;
                return isRollback ? _rollback(target, args) : _write(target, args);
            }

            public string ApplySemanticOps(JObject args) => new JObject { ["status"] = "Success" }.ToString();
            public string ApplyJsonPatch(JObject args) => new JObject { ["status"] = "Success" }.ToString();
            public string BulkWrite(JObject args) => new JObject { ["status"] = "Success" }.ToString();
            public string ReadObjectSource(string target, string part) => "OriginalSource";
        }
    }
}
