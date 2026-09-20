using System.Linq;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class StylePayloadSafetyTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("css")]
        [InlineData("source")]
        public void Large_inline_image_is_rejected_before_sdk_validation_or_mutation(string wrapper)
        {
            string css = "styles Probe { .Probe { background-image: url(\"data:image/png;base64,"
                + new string('A', 940000) + "\"); } }";
            string request = wrapper == null ? css : new JObject { [wrapper] = css }.ToString();
            var part = new RecordingDesignStylesPart();

            Assert.Contains("Image object", ThemeStyleEditHelper.ValidateRequest(request));
            Assert.False(ThemeStyleEditHelper.TryApply(part, request, out _, out string error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal(0, part.ValidationCalls);
            Assert.Equal(0, part.SourceWrites);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("css")]
        [InlineData("class")]
        public void Theme_import_is_not_invoked_for_oversized_inline_images(string wrapper)
        {
            string css = ".Probe { background: url('data:image/png;base64," + new string('A', 20000) + "'); }";
            var structured = new JObject { ["css"] = css };
            if (wrapper == "class") structured["className"] = "Probe";
            string request = wrapper == null ? css : structured.ToString();
            var part = new RecordingThemeStylesPart();

            Assert.False(ThemeStyleEditHelper.TryApply(part, request, out _, out string error));
            Assert.Contains("Image object", error);
            Assert.Equal(0, part.ImportCalls);
        }

        [Fact]
        public void Small_inline_image_reaches_sdk()
        {
            var part = new RecordingDesignStylesPart();
            string css = ".Probe { background: url(\"data:image/png;base64," + new string('A', 1000) + "\"); }";
            Assert.True(ThemeStyleEditHelper.TryApply(part, css, out _, out _));
            Assert.Equal(1, part.ValidationCalls);
            Assert.Equal(1, part.SourceWrites);
        }

        [Fact]
        public void Many_short_inline_images_do_not_accumulate_toward_limit()
        {
            string css = string.Concat(Enumerable.Repeat(".Probe{background:url(data:image/svg+xml,%3Csvg/%3E);}", 50000));
            Assert.Null(ThemeStyleEditHelper.ValidateCss(css));
        }

        [Fact]
        public void Unquoted_custom_property_does_not_consume_following_declarations()
        {
            string css = ":root{--icon:data:image/svg+xml,%3Csvg/%3E;"
                + string.Concat(Enumerable.Repeat("--other:1;", 200)) + "}";
            Assert.Null(ThemeStyleEditHelper.ValidateCss(css));
            Assert.Null(ThemeStyleEditHelper.ValidateCss(":root{--metadata:" + new string('A', 2000) + ";}"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Structured_non_css_properties_are_not_scanned_as_source(bool withCss)
        {
            var request = new JObject
            {
                ["className"] = "Probe",
                ["properties"] = new JObject { ["Description"] = "data:" + new string('A', 2000) }
            };
            if (withCss) request["css"] = ".Probe{}";
            Assert.Null(ThemeStyleEditHelper.ValidateRequest(request.ToString()));
        }

        [Theory]
        [InlineData("Styles")]
        [InlineData("StyleSheet")]
        [InlineData("ThemeStyles")]
        public void Validation_and_direct_batch_reject_before_accessing_the_kb(string partName)
        {
            string css = ".Probe{background:url(data:image/png;base64," + new string('A', 20000) + ");}";
            var validation = JObject.Parse(new ValidationService(null).ValidateCode("Probe", partName, css));
            Assert.Equal("SyntaxError", validation["error"]?["code"]?.ToString());
            Assert.Contains("Image object", validation["error"]?["message"]?.ToString());

            var batch = JObject.Parse(new BatchService(null, null, null, null).BatchEdit("Probe", new JArray
            {
                new JObject { ["part"] = "Rules", ["content"] = "" },
                new JObject { ["part"] = partName, ["content"] = css }
            }));
            Assert.Equal("ThemeStyleValidationFailed", batch["error"]?["code"]?.ToString());
            Assert.Contains("Image object", batch["error"]?["message"]?.ToString());
        }

        [Theory]
        [InlineData("\"")]
        [InlineData("'")]
        [InlineData("")]
        public void Inline_data_uri_limit_is_case_insensitive_and_counts_the_uri_only(string quote)
        {
            const string prefix = "DATA:image/png;base64,";
            string uri = prefix + new string('A', 1024 - prefix.Length);
            Assert.Null(ThemeStyleEditHelper.ValidateCss(".Probe { background: url(" + quote + uri + quote + "); }"));
            string error = ThemeStyleEditHelper.ValidateCss(".Probe { background: url(" + quote + uri + "A" + quote + "); }");
            Assert.Contains("Image object", error);
        }

        [Fact]
        public void Large_stylesheet_with_short_declarations_still_reaches_sdk()
        {
            string css = string.Concat(Enumerable.Repeat(".Probe { color: red; }\n", 50000));
            var part = new RecordingDesignStylesPart();

            Assert.True(ThemeStyleEditHelper.TryApply(part, css, out _, out string error));
            Assert.Null(error);
            Assert.Equal(1, part.ValidationCalls);
            Assert.Equal(1, part.SourceWrites);
        }

        public class RecordingDesignStylesPart
        {
            public int ValidationCalls { get; private set; }
            public int SourceWrites { get; private set; }
            public string Source { get => string.Empty; set => SourceWrites++; }
            public bool ValidateNewSource(string source) { ValidationCalls++; return true; }
        }

        public class RecordingThemeStylesPart
        {
            public int ImportCalls { get; private set; }
            public object GetStyle(string name) => this;
            public void ImportCss(string css) { ImportCalls++; }
        }
    }
}
