using System.Collections.Generic;
using GxMcp.Worker.Compatibility;
using GxMcp.Worker.Helpers;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DesignSystemCompatibilityTests
    {
        [Fact]
        public void OptionalSdkInvoker_InvokesAvailableMethodWithoutCompileTimeContract()
        {
            var outcome = OptionalSdkInvoker.InvokeNoArgs(new FakeDesignSystemHelper(), "GetClassesNames");

            Assert.True(outcome.Available);
            Assert.True(outcome.Succeeded);
            var classes = Assert.IsAssignableFrom<IEnumerable<string>>(outcome.Value);
            Assert.Contains("Button", classes);
        }

        [Fact]
        public void OptionalSdkInvoker_ReportsMissingMethodAsUnavailable()
        {
            var outcome = OptionalSdkInvoker.InvokeNoArgs(new FakeDesignSystemHelper(), "GetAllDSOsNames");

            Assert.False(outcome.Available);
            Assert.False(outcome.Succeeded);
            Assert.Null(outcome.Value);
        }

        [Fact]
        public void DesignSystemSourceParser_ExtractsTokensClassesImagesAndImportedDsos()
        {
            const string tokens = @"
tokens MainTokens {
    #colors {
        primary: #0066CC;
    }
}";
            const string styles = @"
styles MainStyles {
    @import BaseDesignSystem.tokens;
    .Hero {
        background-image: gx-image(Logo);
    }
    .Hero:hover {
        color: $colors.primary;
    }
}";

            var parsed = DesignSystemSourceParser.Parse(tokens, styles);

            Assert.Equal("#0066CC", parsed.TokenGroups["colors"]["primary"]?.ToString());
            Assert.Contains("Hero", parsed.Classes);
            Assert.Contains("Hero:hover", parsed.Classes);
            Assert.Contains("Logo", parsed.Images);
            Assert.Contains("BaseDesignSystem", parsed.ReferencedDSOs);
            Assert.Equal("complete", parsed.Completeness);
        }

        [Fact]
        public void DesignSystemSourceParser_DoesNotTreatExternalCssAsDsoReference()
        {
            var parsed = DesignSystemSourceParser.Parse(null, "styles MainStyles { @import 'theme.css'; }");

            Assert.Empty(parsed.ReferencedDSOs);
        }

        [Fact]
        public void DesignSystemSourceParser_HandlesNestedBracesAndComments()
        {
            const string styles = @"
styles MainStyles {
    /* .Ignored { color: red; } */
        .Card {
        content: ""{literal}"";
        asset: ""https://example.com/a"";
        background: url(data:image/svg+xml,{nested});
        color: #fff;
    }
}";

            var parsed = DesignSystemSourceParser.Parse(null, styles);

            Assert.Contains("Card", parsed.Classes);
            Assert.Equal("complete", parsed.Completeness);
            Assert.Empty(parsed.Warnings);
            var classes = DesignSystemSourceParser.ParseClasses(styles);
            Assert.Equal("\"https://example.com/a\"", classes["Card"]["asset"]?.ToString());
        }

        [Fact]
        public void DesignSystemSourceParser_ReportsPartialForUnbalancedSource()
        {
            var parsed = DesignSystemSourceParser.Parse(null, "styles MainStyles { .Card { color: red; ");

            Assert.Equal("partial", parsed.Completeness);
            Assert.NotEmpty(parsed.Warnings);
            Assert.Contains(parsed.UnparsedConstructs, item => item.Contains("Styles:unbalanced"));
        }

        private sealed class FakeDesignSystemHelper
        {
            public List<string> GetClassesNames()
            {
                return new List<string> { "Button" };
            }
        }
    }
}
