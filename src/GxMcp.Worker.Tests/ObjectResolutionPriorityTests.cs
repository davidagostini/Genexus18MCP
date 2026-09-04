using System;
using System.Collections.Generic;
using GxMcp.Worker.Models;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Regression (friction 2026-06-02): a bare name that matches both a Transaction
    // and its generated Table must resolve deterministically to the editable logic
    // object, not to whichever the index dictionary happened to enumerate first.
    // This nondeterminism was the root cause of genexus_inspect (which got the Table)
    // and genexus_analyze impact (which prefers the Transaction) silently resolving
    // DIFFERENT objects for the same name and appearing to contradict each other.
    public class ObjectResolutionPriorityTests
    {
        private static SearchIndex.IndexEntry E(string name, string type)
            => new SearchIndex.IndexEntry { Name = name, Type = type };

        [Fact]
        public void TransactionWinsOverTable_RegardlessOfOrder()
        {
            var a = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("Acao", "Table"), E("Acao", "Transaction") });
            var b = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("Acao", "Transaction"), E("Acao", "Table") });
            Assert.Equal("Transaction", a.Type);
            Assert.Equal("Transaction", b.Type);
        }

        [Fact]
        public void TableReturnedWhenItIsTheOnlyMatch()
        {
            var only = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("Acao", "Table") });
            Assert.Equal("Table", only.Type);
        }

        [Fact]
        public void FolderAndFileRankLastBehindLogic()
        {
            var pick = ObjectService.PrioritizeNameMatches(new List<SearchIndex.IndexEntry>
            {
                E("X", "Folder"), E("X", "Image"), E("X", "Procedure")
            });
            Assert.Equal("Procedure", pick.Type);
        }

        [Fact]
        public void DeterministicAcrossLogicTypes()
        {
            // Two logic types colliding (rare) → stable tiebreak by type name, not
            // by enumeration order, so repeated calls always agree.
            var a = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("X", "WebPanel"), E("X", "Procedure") });
            var b = ObjectService.PrioritizeNameMatches(
                new List<SearchIndex.IndexEntry> { E("X", "Procedure"), E("X", "WebPanel") });
            Assert.Equal(a.Type, b.Type);
        }

        [Theory]
        [InlineData("11111111-1111-1111-1111-111111111111:42")]
        [InlineData("EntityKey(11111111-1111-1111-1111-111111111111, 42)")]
        public void EntityKeyTextIsParsedIntoTypedIdentity(string raw)
        {
            Assert.True(ObjectService.TryParseEntityKey(raw, out var typeGuid, out var id));
            Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), typeGuid);
            Assert.Equal(42, id);
        }

        [Fact]
        public void IndexEntryPersistsAllNativeIdentityFields()
        {
            var index = new SearchIndex();
            index.Objects["Procedure:ReverseOrder"] = new SearchIndex.IndexEntry
            {
                Guid = "22222222-2222-2222-2222-222222222222",
                EntityKey = "EntityKey(11111111-1111-1111-1111-111111111111, 42)",
                EntityTypeGuid = "11111111-1111-1111-1111-111111111111",
                EntityId = 42,
                Name = "ReverseOrder",
                Type = "Procedure",
                Path = "Root Module/Operations/ReverseOrder"
            };

            var roundTripped = SearchIndex.FromJson(index.ToJson());
            var entry = roundTripped.Objects["Procedure:ReverseOrder"];
            Assert.Equal("22222222-2222-2222-2222-222222222222", entry.Guid);
            Assert.Equal("EntityKey(11111111-1111-1111-1111-111111111111, 42)", entry.EntityKey);
            Assert.Equal(42, entry.EntityId);
            Assert.Equal("Root Module/Operations/ReverseOrder", entry.Path);
        }
    }
}
