using System.Collections.Generic;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DesignSystemServiceTests
    {
        [Fact]
        public void ParseDsoTokens_ExtractsColorAndSpacingTokens()
        {
            string dsoTokens = @"
tokens MyDesignSystem {
    #colors {
        Primary: #007bff;
        Secondary: #6c757d;
        Success: #28a745;
        Danger: #dc3545;
    }
    #font-sizes {
        Small: 12px;
        Medium: 16px;
        Large: 24px;
    }
    #spacing {
        PaddingSmall: 8px;
        PaddingLarge: 24px;
    }
}";

            var tokens = DesignSystemService.ParseDsoTokens(dsoTokens);

            Assert.NotNull(tokens);
            Assert.True(tokens.ContainsKey("colors"));
            Assert.Equal("#007bff", tokens["colors"]["Primary"]?.ToString());
            Assert.Equal("#6c757d", tokens["colors"]["Secondary"]?.ToString());

            Assert.True(tokens.ContainsKey("font-sizes"));
            Assert.Equal("16px", tokens["font-sizes"]["Medium"]?.ToString());

            Assert.True(tokens.ContainsKey("spacing"));
            Assert.Equal("8px", tokens["spacing"]["PaddingSmall"]?.ToString());
        }

        [Fact]
        public void ParseDsoClasses_ExtractsClassDefinitionsAndRules()
        {
            string dsoStyles = @"
styles MyDesignSystem {
    .ButtonPrimary {
        background-color: $colors.Primary;
        color: #ffffff;
        font-size: $font-sizes.Medium;
        border-radius: 4px;
    }
    .ButtonPrimary:hover {
        background-color: #0056b3;
    }
    .CardContainer {
        padding: $spacing.PaddingLarge;
        border: 1px solid #e0e0e0;
    }
}";

            var classes = DesignSystemService.ParseDsoClasses(dsoStyles);

            Assert.NotNull(classes);
            Assert.Contains("ButtonPrimary", classes.Keys);
            Assert.Contains("ButtonPrimary:hover", classes.Keys);
            Assert.Contains("CardContainer", classes.Keys);

            var btn = classes["ButtonPrimary"];
            Assert.Equal("$colors.Primary", btn["background-color"]?.ToString());
            Assert.Equal("#ffffff", btn["color"]?.ToString());

            var btnHover = classes["ButtonPrimary:hover"];
            Assert.Equal("#0056b3", btnHover["background-color"]?.ToString());
        }

        [Fact]
        public void ValidateDso_ValidSource_ReturnsSuccess()
        {
            string combined = @"
tokens MyDesignSystem {
    #colors {
        Brand: #123456;
    }
}
styles MyDesignSystem {
    .BrandHeader {
        color: $colors.Brand;
    }
}";

            var result = DesignSystemService.ValidateDso(combined);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateDso_SourceWithCommentsContainingBraces_PassesValidation()
        {
            string combined = @"
tokens MyDesignSystem {
    /* { Note: Theme tokens begin here } */
    #colors {
        // { default color
        Brand: #123456;
    }
}
styles MyDesignSystem {
    .BrandHeader {
        color: $colors.Brand;
    }
}";

            var result = DesignSystemService.ValidateDso(combined);

            Assert.True(result.IsValid);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ValidateDso_MismatchedBrackets_ReturnsSyntaxError()
        {
            string broken = @"
tokens MyDesignSystem {
    #colors {
        Brand: #123456;
}
styles MyDesignSystem {
";

            var result = DesignSystemService.ValidateDso(broken);

            Assert.False(result.IsValid);
            Assert.NotEmpty(result.Errors);
            Assert.Contains(result.Errors, e => e.Contains("bracket") || e.Contains("brace"));
        }
    }
}
