using System.Collections.Generic;
using System.Threading;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ThemeStyleAndValidationTests
    {
        [Fact]
        public void ValidateCss_rejects_unbalanced_braces()
        {
            Assert.Contains("unmatched opening", ThemeStyleEditHelper.ValidateCss(".button { color: red;"));
            Assert.Contains("unmatched closing", ThemeStyleEditHelper.ValidateCss(".button { color: red; }}"));
            Assert.Null(ThemeStyleEditHelper.ValidateCss(".button { content: \"}\"; }"));
        }

        [Fact]
        public void ValidateRequest_rejects_non_object_theme_properties()
        {
            string error = ThemeStyleEditHelper.ValidateRequest(
                new JObject
                {
                    ["className"] = "Button",
                    ["properties"] = new JArray("color")
                }.ToString());

            Assert.Equal("Style properties must be a JSON object.", error);
        }

        [Fact]
        public void HasErrors_accepts_sdk_error_severities_but_not_warnings()
        {
            Assert.True(WebFormPreSaveValidator.HasErrors(new[]
            {
                new WebFormPreSaveValidator.ValidationMessage { Severity = "Erro" }
            }));
            Assert.True(WebFormPreSaveValidator.HasErrors(new[]
            {
                new WebFormPreSaveValidator.ValidationMessage { Severity = "1" }
            }));
            Assert.False(WebFormPreSaveValidator.HasErrors(new[]
            {
                new WebFormPreSaveValidator.ValidationMessage { Severity = "Warning" }
            }));
        }

        [Fact]
        public void ObjectText_pre_cancel_returns_typed_cancelled_envelope_for_all_batch_actions()
        {
            var service = new ObjectTextService(null, null);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                foreach (string action in new[]
                {
                    "export_kb_to_text",
                    "import_text_to_kb",
                    "validate_kb_text_files",
                    "delete_kb_objects"
                })
                {
                    JObject response = JObject.Parse(service.Execute(action, null, null, cts.Token));
                    Assert.Equal("ok", response["status"]?.ToString());
                    Assert.Equal("Cancelled", response["code"]?.ToString());
                    Assert.True(response["result"]?["cancelled"]?.ToObject<bool>() ?? false);
                }
            }
        }

        [Fact]
        public void ObjectText_file_part_sanitization_does_not_allow_path_separators()
        {
            string safe = ObjectTextService.SanitizeFilePart("../Theme\\Styles");

            Assert.DoesNotContain("/", safe);
            Assert.DoesNotContain("\\", safe);
            Assert.NotEqual(".", safe);
            Assert.NotEqual("..", safe);
        }
    }
}
