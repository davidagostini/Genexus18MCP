using System;
using System.Collections.Generic;
using Xunit;
using GxMcp.Worker.Services.Structure;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Tests
{
    public class SchemaMutationEngineTests
    {
        private class TestSchemaObject
        {
            public string Name { get; set; }
            public List<string> Attributes { get; set; } = new List<string>();
            public int Version { get; set; } = 1;

            public TestSchemaObject Clone()
            {
                return new TestSchemaObject
                {
                    Name = Name,
                    Attributes = new List<string>(Attributes),
                    Version = Version
                };
            }
        }

        [Fact]
        public void SchemaMutationEngine_DryRun_ComputesSchemaDiffWithoutMutating()
        {
            var engine = new SchemaMutationEngine();
            var target = new TestSchemaObject
            {
                Name = "Customer",
                Attributes = new List<string> { "CustomerId", "CustomerName" }
            };

            var options = new SchemaMutationOptions { DryRun = true };
            var result = engine.Execute(
                target,
                t => t.Clone(),
                (snapshot, current) =>
                {
                    current.Attributes.Add("CustomerEmail");
                    return new SchemaMutationOutcome
                    {
                        Success = true,
                        Diff = new JObject
                        {
                            ["added"] = new JArray("CustomerEmail")
                        }
                    };
                },
                options
            );

            Assert.True(result.Success);
            Assert.True(result.IsDryRun);
            Assert.NotNull(result.Diff);
            Assert.Equal("CustomerEmail", result.Diff["added"]?[0]?.ToString());
            // Target itself was not mutated because it was dry-run
            Assert.DoesNotContain("CustomerEmail", target.Attributes);
        }

        [Fact]
        public void SchemaMutationEngine_Failure_RollsBackToLosslessSnapshot()
        {
            var engine = new SchemaMutationEngine();
            var target = new TestSchemaObject
            {
                Name = "Invoice",
                Attributes = new List<string> { "InvoiceId", "InvoiceDate" }
            };

            var options = new SchemaMutationOptions { DryRun = false };
            var result = engine.Execute(
                target,
                t => t.Clone(),
                (snapshot, current) =>
                {
                    current.Attributes.Add("CorruptedField");
                    // Simulate an unexpected failure in secondary index or database constraint
                    throw new InvalidOperationException("Foreign key schema violation");
                },
                options,
                restoreAction: (current, snapshot) =>
                {
                    current.Attributes = new List<string>(snapshot.Attributes);
                }
            );

            Assert.False(result.Success);
            Assert.Contains("Foreign key schema violation", result.ErrorMessage);
            // Must have rolled back to original state
            Assert.DoesNotContain("CorruptedField", target.Attributes);
            Assert.Equal(2, target.Attributes.Count);
        }

        [Fact]
        public void SchemaMutationEngine_OptimisticLock_RejectsStaleExpectedVersion()
        {
            var engine = new SchemaMutationEngine();
            var target = new TestSchemaObject
            {
                Name = "Product",
                Version = 5
            };

            var options = new SchemaMutationOptions
            {
                ExpectedVersion = "v4" // Token mismatch
            };

            var result = engine.Execute(
                target,
                t => t.Clone(),
                (snapshot, current) => new SchemaMutationOutcome { Success = true },
                options,
                currentVersionResolver: t => $"v{t.Version}"
            );

            Assert.False(result.Success);
            Assert.Equal("VersionConflict", result.ErrorCode);
        }
    }
}
