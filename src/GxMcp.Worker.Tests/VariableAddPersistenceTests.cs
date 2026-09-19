using System.Collections.Generic;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // issue #59 — per-item post-save verification on the add-variable path: EVERY added
    // variable (not just Domain-bound ones) must be present in the persisted Variables
    // part. MissingVariableNames decides which requested names the re-read lost.
    public class VariableAddPersistenceTests
    {
        [Fact]
        public void MissingVariableNames_EmptyText_UnverifiableReturnsEmpty()
        {
            // Empty persisted text means the re-read couldn't produce a verifiable part —
            // the caller treats it as unverifiable and must NOT falsely accuse the write.
            var missing = WriteService.MissingVariableNames("", new List<string> { "Foo", "Bar" });
            Assert.Empty(missing);
        }

        [Fact]
        public void MissingVariableNames_AllPresent_ReturnsEmpty()
        {
            string text = "&Foo : Numeric(8.0)\n&Bar : Character(20)\n&Baz : Date";
            var missing = WriteService.MissingVariableNames(text, new List<string> { "Foo", "Bar" });
            Assert.Empty(missing);
        }

        [Fact]
        public void MissingVariableNames_OneDropped_ReportsIt()
        {
            // The silent-drop symptom: Bar never landed in the persisted part.
            string text = "&Foo : Numeric(8.0)";
            var missing = WriteService.MissingVariableNames(text, new List<string> { "Foo", "Bar" });
            Assert.Single(missing);
            Assert.Equal("Bar", missing[0]);
        }

        [Fact]
        public void MissingVariableNames_CaseInsensitive()
        {
            string text = "&foo : Numeric(8.0)";
            var missing = WriteService.MissingVariableNames(text, new List<string> { "Foo" });
            Assert.Empty(missing);
        }

        [Fact]
        public void MissingVariableNames_AmperandPrefixHandled()
        {
            // Callers may pass the raw name with or without the leading &.
            string text = "&Foo : Numeric(8.0)";
            var missing = WriteService.MissingVariableNames(text, new List<string> { "&Foo" });
            Assert.Empty(missing);
        }

        [Fact]
        public void MissingVariableNames_DoesNotMatchNamePrefix()
        {
            // &FooBar must not satisfy a request for &Foo.
            string text = "&FooBar : Numeric(8.0)";
            var missing = WriteService.MissingVariableNames(text, new List<string> { "Foo" });
            Assert.Single(missing);
            Assert.Equal("Foo", missing[0]);
        }

        [Fact]
        public void MissingVariableNames_NullExpected_ReturnsEmpty()
        {
            Assert.Empty(WriteService.MissingVariableNames("&Foo : Numeric", null));
        }

        [Fact]
        public void MissingVariableNames_IgnoresBlankNames()
        {
            var missing = WriteService.MissingVariableNames("&Foo : Numeric", new List<string> { "Foo", "  " });
            Assert.Empty(missing);
        }
    }
}
