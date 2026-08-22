using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // Granular semantic-cache invalidation: RemoveByTarget drops only cached entries
    // whose args JSON references the mutated object, keeping unrelated warm reads.
    public class SemanticCacheGranularInvalidationTests
    {
        private static string Key(string kb, string tool, string argsJson)
            => $"{kb}|{tool}:{argsJson}";

        [Fact]
        public void RemoveByTarget_DropsOnlyEntriesReferencingTarget()
        {
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            store.Set(Key("kb1", "genexus_read", "{\"targets\":[\"Cliente\"],\"part\":\"Source\"}"), new JObject());
            store.Set(Key("kb1", "genexus_inspect", "{\"name\":\"Cliente\"}"), new JObject());
            store.Set(Key("kb1", "genexus_read", "{\"targets\":[\"Proveedor\"],\"part\":\"Source\"}"), new JObject());
            store.Set(Key("kb2", "genexus_read", "{\"targets\":[\"Cliente\"],\"part\":\"Source\"}"), new JObject());

            int removed = store.RemoveByTarget("kb1", "Cliente");

            Assert.Equal(2, removed);
            Assert.False(store.TryGet(Key("kb1", "genexus_read", "{\"targets\":[\"Cliente\"],\"part\":\"Source\"}"), out _));
            Assert.False(store.TryGet(Key("kb1", "genexus_inspect", "{\"name\":\"Cliente\"}"), out _));
            // unrelated object read survives
            Assert.True(store.TryGet(Key("kb1", "genexus_read", "{\"targets\":[\"Proveedor\"],\"part\":\"Source\"}"), out _));
            // other KB's entry for the same name survives (scoped invalidation)
            Assert.True(store.TryGet(Key("kb2", "genexus_read", "{\"targets\":[\"Cliente\"],\"part\":\"Source\"}"), out _));
        }

        [Fact]
        public void RemoveByTarget_WordBoundary_NoPartialNameMatches()
        {
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            store.Set(Key("kb1", "genexus_read", "{\"targets\":[\"ClienteId\"]}"), new JObject());

            int removed = store.RemoveByTarget("kb1", "Cliente");

            Assert.Equal(0, removed);
            Assert.True(store.TryGet(Key("kb1", "genexus_read", "{\"targets\":[\"ClienteId\"]}"), out _));
        }

        [Fact]
        public void RemoveByTarget_EmptyTarget_RemovesNothing()
        {
            var store = new SemanticCacheStore(64, TimeSpan.FromMinutes(30));
            store.Set(Key("kb1", "genexus_read", "{\"targets\":[\"Cliente\"]}"), new JObject());

            Assert.Equal(0, store.RemoveByTarget("kb1", ""));
            Assert.Equal(0, store.RemoveByTarget("kb1", null!));
        }

        [Theory]
        [InlineData("genexus_rename_across_kb")]
        [InlineData("genexus_kb_import")]
        [InlineData("genexus_import_object")]
        public void ExtractMutationTarget_KbWideTools_ReturnNull(string tool)
        {
            Assert.Null(Program.ExtractMutationTarget(tool, new JObject { ["name"] = "X" }));
        }

        [Fact]
        public void ExtractMutationTarget_SingleObjectShapes_Extracted()
        {
            Assert.Equal("TrnA", Program.ExtractMutationTarget("genexus_edit", new JObject { ["target"] = "TrnA" }));
            Assert.Equal("TrnB", Program.ExtractMutationTarget("genexus_delete_object", new JObject { ["name"] = "TrnB" }));
            Assert.Equal("TrnC",
                Program.ExtractMutationTarget("genexus_write", new JObject { ["targets"] = new JArray("TrnC") }));
        }

        [Fact]
        public void ExtractMutationTarget_MultiTargetArray_FallsBackToFullClear()
        {
            Assert.Null(Program.ExtractMutationTarget(
                "genexus_write", new JObject { ["targets"] = new JArray("A", "B") }));
        }
    }
}
