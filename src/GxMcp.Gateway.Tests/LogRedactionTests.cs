using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    /// <summary>
    /// Plan 014 — the HTTP request-logging summary must never leak a credential
    /// value, even when nested one level deep in the tool arguments.
    /// </summary>
    public class LogRedactionTests
    {
        [Fact]
        public void RedactBodyForLog_MasksSensitiveKeys_KeepsPlainOnes()
        {
            var body = JObject.Parse(@"{
                ""method"": ""tools/call"",
                ""params"": {
                    ""arguments"": {
                        ""user"": ""alice"",
                        ""password"": ""hunter2"",
                        ""target"": ""Foo""
                    }
                }
            }");

            string result = Program.RedactBodyForLog(body);

            Assert.Contains("user=alice", result);
            Assert.Contains("target=Foo", result);
            Assert.Contains("password=***", result);
            Assert.DoesNotContain("hunter2", result);
        }

        [Fact]
        public void RedactBodyForLog_NestedObject_NeverLeaksValue()
        {
            var body = JObject.Parse(@"{
                ""method"": ""tools/call"",
                ""params"": {
                    ""arguments"": {
                        ""gamSession"": { ""pass"": ""x"" }
                    }
                }
            }");

            string result = Program.RedactBodyForLog(body);

            Assert.Contains("gamSession=***", result);
            Assert.DoesNotContain("\"x\"", result);
            Assert.DoesNotContain(">x<", result);
        }

        [Fact]
        public void RedactBodyForLog_NoArguments_ReturnsPlaceholder_NoThrow()
        {
            var body = JObject.Parse(@"{ ""method"": ""tools/list"" }");

            string result = Program.RedactBodyForLog(body);

            Assert.Equal("(no arguments)", result);
        }

        [Theory]
        [InlineData("token")]
        [InlineData("apikey")]
        [InlineData("secret")]
        public void RedactBodyForLog_MasksTokenApikeySecret(string keyName)
        {
            var body = new JObject
            {
                ["method"] = "tools/call",
                ["params"] = new JObject
                {
                    ["arguments"] = new JObject
                    {
                        [keyName] = "super-secret-value"
                    }
                }
            };

            string result = Program.RedactBodyForLog(body);

            Assert.Contains(keyName + "=***", result);
            Assert.DoesNotContain("super-secret-value", result);
        }

        [Fact]
        public void LogValue_RedactsQuotedStructuredCredentialValues()
        {
            const string input = "Exception: {\"password\":\"password-value\", \"token\": 'token-value', \"authorization\": \"Bearer auth-value\"}";

            string result = Program.LogValue(input);

            Assert.DoesNotContain("password-value", result);
            Assert.DoesNotContain("token-value", result);
            Assert.DoesNotContain("auth-value", result);
            Assert.Equal(3, CountOccurrences(result, "<redacted>"));
        }

        private static int CountOccurrences(string value, string fragment)
        {
            int count = 0;
            int offset = 0;
            while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += fragment.Length;
            }
            return count;
        }
    }
}
