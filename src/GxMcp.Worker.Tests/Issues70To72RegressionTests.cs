using System;
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;
using System.Reflection;

namespace GxMcp.Worker.Tests
{
    public class Issues70To72RegressionTests
    {
        [Fact]
        public void Issue70_WhitespaceInsensitiveEquals_IgnoresCase_ForProcedureSourceNormalization()
        {
            string requested = "for each Customer\n    where CustomerId = &CustomerId\n    Msg(\"Hello World\")\nendfor";
            string persisted = "For Each Customer\n    Where CustomerId = &CustomerId\n    Msg(\"Hello World\")\nEndfor";

            var method = typeof(WriteService).GetMethod("WhitespaceInsensitiveEquals", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            bool matches = (bool)method.Invoke(null, new object[] { persisted, requested });
            Assert.True(matches, "WhitespaceInsensitiveEquals should treat case-normalized procedure source as equivalent to avoid false WriteNotPersisted rollback.");
        }

        [Fact]
        public void Issue70_WhitespaceInsensitiveEquals_PreservesStringLiteralSpaces()
        {
            string requested = "Msg(\"Hello World\")";
            string modified = "Msg(\"HelloWorld\")";

            var method = typeof(WriteService).GetMethod("WhitespaceInsensitiveEquals", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            bool matches = (bool)method.Invoke(null, new object[] { modified, requested });
            Assert.False(matches, "WhitespaceInsensitiveEquals must NOT collapse spaces inside string literals.");
        }

        [Fact]
        public void Issue71_WhitespaceInsensitiveEquals_TreatsNormalizedXml_AsEquivalent()
        {
            string requested = "<PatternInstance defaultWidth=\"100\" defaultVisible=\"true\"><table name=\"Table1\"><tabularTab></tabularTab></table></PatternInstance>";
            string persisted = "<PatternInstance><table name=\"Table1\"><tabularTab/></table></PatternInstance>";

            var method = typeof(WriteService).GetMethod("WhitespaceInsensitiveEquals", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            bool matches = (bool)method.Invoke(null, new object[] { persisted, requested });
            Assert.True(matches, "WhitespaceInsensitiveEquals should treat XML with SDK-dropped default attributes as equivalent to avoid false WriteNotPersisted on PatternInstance.");
        }

        [Fact]
        public void Issue72_ReportLayoutHelper_TryParseColor_ParsesCommaSeparatedRGB()
        {
            var tryParseColor = typeof(ReportLayoutHelper).GetMethod("TryParseColor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(tryParseColor);

            object[] args = new object[] { "192, 0, 0", null };
            bool parsed = (bool)tryParseColor.Invoke(null, args);
            Assert.True(parsed);

            System.Drawing.Color color = (System.Drawing.Color)args[1];
            Assert.Equal(192, color.R);
            Assert.Equal(0, color.G);
            Assert.Equal(0, color.B);
        }

        [Fact]
        public void Issue72_ReportLayoutHelper_IsPropertyEquivalent_RecognizesEquivalentValues()
        {
            var isPropertyEquivalent = typeof(ReportLayoutHelper).GetMethod("IsPropertyEquivalent", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(isPropertyEquivalent);

            // Color equivalence (comma vs semicolon/pipe)
            bool colorEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "ForeColor", "ForeColor", "192, 0, 0", "192; 0; 0|" });
            Assert.True(colorEq, "Comma RGB '192, 0, 0' should be equivalent to normalized '192; 0; 0|'.");

            // Numeric geometry equivalence
            bool numEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "X", "X", "499", "499.0" });
            Assert.True(numEq, "Numeric X '499' should be equivalent to '499.0'.");

            // String properties must NOT use double parsing
            bool stringEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "Caption", "Caption", "1E2", "100" });
            Assert.False(stringEq, "String property 'Caption' must NOT treat '1E2' as equivalent to '100'.");

            // Case insensitive text for enums
            bool textEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "Alignment", "Alignment", "TopRight", "topright" });
            Assert.True(textEq, "Text alignment 'TopRight' should be equivalent to 'topright'.");
        }
    }
}
