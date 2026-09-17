using Newtonsoft.Json.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class TextBatchOptionsTests
    {
        [Fact]
        public void Parse_applies_operation_defaults_and_validates_windows()
        {
            Assert.True(TextBatchOptions.TryParse(
                new JObject(),
                defaultDryRun: false,
                defaultListOnly: false,
                defaultStopOnError: false,
                defaultIncludeChildren: true,
                defaultIncludeVisualParts: false,
                defaultForceSave: false,
                defaultRollbackOnFailure: true,
                out TextBatchOptions options,
                out string error));

            Assert.Null(error);
            Assert.False(options.DryRun);
            Assert.True(options.IncludeChildren);
            Assert.True(options.RollbackOnFailure);
            Assert.Equal(0, options.Skip);
            Assert.Equal(0, options.Limit);

            Assert.False(TextBatchOptions.TryParse(
                new JObject { ["skip"] = -1 },
                false, false, false, true, false, false, true,
                out _, out error));
            Assert.Contains("skip", error);
        }
    }
}
