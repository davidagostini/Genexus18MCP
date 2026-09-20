using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Deterministic property/fuzz coverage for protocol-boundary invariants.
    /// A fixed seed keeps failures reproducible without adding a test-only
    /// property-testing dependency to the production solution.
    /// </summary>
    public sealed class PropertyFuzzContractTests
    {
        [Fact]
        public void IdempotencyKeyValidator_RejectsEveryGeneratedInvalidKey()
        {
            var random = new Random(0x47A11);
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";
            const string invalidChars = " /\\:;,.\"'\r\n\0\u00E9\u2603";

            for (int i = 0; i < 2000; i++)
            {
                int length = random.Next(0, 140);
                var chars = new char[length];
                for (int j = 0; j < chars.Length; j++)
                    chars[j] = validChars[random.Next(validChars.Length)];

                string candidate = new string(chars);
                bool shouldBeValid = candidate.Length >= 1 && candidate.Length <= 128;
                if (shouldBeValid)
                    IdempotencyMiddleware.ValidateKey(candidate);
                else
                    Assert.Throws<UsageException>(() => IdempotencyMiddleware.ValidateKey(candidate));

                if (candidate.Length > 0 && candidate.Length <= 128)
                {
                    int position = random.Next(candidate.Length);
                    char invalid = invalidChars[random.Next(invalidChars.Length)];
                    string invalidCandidate = candidate.Substring(0, position) + invalid + candidate.Substring(position + 1);
                    Assert.Throws<UsageException>(() => IdempotencyMiddleware.ValidateKey(invalidCandidate));
                }
            }
        }

        [Fact]
        public async Task CanonicalMutationKey_IsInvariantToArgumentPropertyOrder()
        {
            var random = new Random(0xC0FFEE);
            string[] names = { "name", "part", "content", "mode", "validate" };

            for (int iteration = 0; iteration < 100; iteration++)
            {
                var values = new Dictionary<string, JToken>
                {
                    ["name"] = "Object" + random.Next(10000),
                    ["part"] = "Source",
                    ["content"] = "line " + random.Next(10000),
                    ["mode"] = "patch",
                    ["validate"] = "strict"
                };
                string[] order = names.OrderBy(_ => random.Next()).ToArray();
                var firstArgs = new JObject { ["idempotencyKey"] = "property-order-" + iteration };
                var secondArgs = new JObject { ["idempotencyKey"] = "property-order-" + iteration };
                foreach (string name in names) firstArgs[name] = values[name].DeepClone();
                foreach (string name in order) secondArgs[name] = values[name].DeepClone();

                int calls = 0;
                var middleware = new IdempotencyMiddleware(new IdempotencyCache(15, 1000), "property-fuzz-kb");
                Task<JObject> Inner(JObject request)
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(new JObject { ["isError"] = false, ["iteration"] = iteration });
                }

                await middleware.Invoke(new JObject { ["name"] = "genexus_edit", ["arguments"] = firstArgs }, Inner);
                await middleware.Invoke(new JObject { ["name"] = "genexus_edit", ["arguments"] = secondArgs }, Inner);
                Assert.Equal(1, calls);
            }
        }

        [Fact]
        public async Task ConcurrentSameKey_PropertyHoldsSingleExecution()
        {
            for (int iteration = 0; iteration < 50; iteration++)
            {
                int calls = 0;
                var cache = new IdempotencyCache(15, 1000);
                var tasks = Enumerable.Range(0, 8).Select(_ => cache.GetOrCompute(
                    "concurrency-fuzz-kb", "genexus_edit", "key-" + iteration, "hash-" + iteration,
                    async () =>
                    {
                        Interlocked.Increment(ref calls);
                        await Task.Delay(1).ConfigureAwait(false);
                        return new JObject { ["isError"] = false, ["iteration"] = iteration };
                    })).ToArray();

                await Task.WhenAll(tasks);
                Assert.Equal(1, calls);
                Assert.All(tasks, task => Assert.False((bool)task.Result["isError"]!));
            }
        }
    }
}
