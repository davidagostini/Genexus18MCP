using GxMcp.Gateway;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // A variable batch that fails reports what happened to each item (`outcomes`) and the counts at the
    // top level of the envelope, beside `saved`. The terse error projection keeps an allowlist of fields,
    // so without these two the caller reads only "No variables were added because one or more batch items
    // failed validation" and cannot tell which item to fix.
    public class VariableBatchTerseErrorTests
    {
        private static JObject Trim(string json) => McpRouter.TrimErrorEnvelope(JObject.Parse(json), verbose: false);

        // The envelope WriteService.AddVariables returns when PopulateVariablesInto reports a failed item:
        // McpResponse.Err puts `extra` at the top level, then WrapWithPersistedState adds its read-back fields.
        private const string FailedBatch = """
            {
              "status": "error",
              "target": "SampleProcedure",
              "error": {
                "code": "VariableBatchValidationFailed",
                "message": "No variables were added because one or more batch items failed validation."
              },
              "outcomes": [
                { "name": "CustomerId", "itemStatus": "Exists" },
                { "name": "NewRecord", "status": "NotAdded" },
                { "name": "Broken", "status": "Failed", "reason": "UnknownType", "suggestion": "Character(40)" }
              ],
              "saved": false,
              "counts": { "added": 0, "existed": 1, "failed": 1 },
              "persistedHash": "5d41402abc4b2a76b9719d911017c592",
              "persistedSnippet": "&CustomerId",
              "source": "&CustomerId\n",
              "postSaveVerification": { "reReadConfirmed": true, "versionToken": "639:abc" },
              "implicitLifecycleActions": []
            }
            """;

        // The same shape, produced after the save, when a variable did not survive the reload.
        private const string NotPersistedBatch = """
            {
              "status": "error",
              "target": "SampleProcedure",
              "error": {
                "code": "VariableNotPersisted",
                "message": "One or more variables did not survive the save/reload cycle."
              },
              "counts": { "added": 1, "existed": 0, "failed": 1 },
              "outcomes": [
                { "name": "Kept", "status": "Added", "persisted": true },
                { "name": "Lost", "status": "NotPersisted", "reason": "The variable was not found after the save." }
              ],
              "saved": false
            }
            """;

        [Fact]
        public void TrimErrorEnvelope_KeepsWhichItemOfAFailedVariableBatchFailedAndWhy()
        {
            var input = JObject.Parse(FailedBatch);

            var trimmed = Trim(FailedBatch);

            Assert.Equal("VariableBatchValidationFailed", (string?)trimmed["code"]);
            foreach (var name in new[] { "outcomes", "counts", "saved" })
                Assert.True(JToken.DeepEquals(input[name], trimmed[name]), name);
            // The failed item keeps the reason and the suggestion the caller corrects it with.
            var failed = Assert.IsType<JObject>(((JArray)trimmed["outcomes"]!)[2]);
            Assert.Equal("UnknownType", (string?)failed["reason"]);
            Assert.Equal("Character(40)", (string?)failed["suggestion"]);
            // Still terse: the echoed source is not part of the projection.
            Assert.Null(trimmed["source"]);
        }

        [Fact]
        public void TrimErrorEnvelope_KeepsTheOutcomesOfAVariableBatchThatDidNotPersist()
        {
            var input = JObject.Parse(NotPersistedBatch);

            var trimmed = Trim(NotPersistedBatch);

            foreach (var name in new[] { "outcomes", "counts", "saved" })
                Assert.True(JToken.DeepEquals(input[name], trimmed[name]), name);
        }
    }
}
