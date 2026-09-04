using System;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class OperationClassifierTests
    {
        [Theory]
        [InlineData("genexus_query")]
        [InlineData("genexus_list_objects")]
        [InlineData("genexus_read")]
        [InlineData("genexus_inspect")]
        [InlineData("genexus_analyze")]
        [InlineData("genexus_whoami")]
        [InlineData("genexus_doctor")]
        [InlineData("genexus_navigation")]
        [InlineData("genexus_search_source")]
        [InlineData("genexus_security")]
        public void PureReadTools_ClassifiedAsReadOnly(string toolName)
        {
            Assert.True(OperationClassifier.IsReadOnly(toolName, new JObject()));
        }

        [Theory]
        [InlineData("get_visual")]
        [InlineData("get_indexes")]
        [InlineData("get_logic")]
        [InlineData("check_subtypes")]
        public void GenexusStructure_ReadActions_ClassifiedAsReadOnly(string action)
        {
            Assert.True(OperationClassifier.IsReadOnly("genexus_structure", new JObject { ["action"] = action }));
        }

        [Theory]
        [InlineData("update_visual")]
        [InlineData("create_index")]
        [InlineData("drop_index")]
        [InlineData("set_attribute")]
        [InlineData("remove_attribute")]
        [InlineData("set_level")]
        [InlineData("set_domain")]
        [InlineData("update_group")]
        [InlineData("move_attribute")]
        public void GenexusStructure_MutatingActions_ClassifiedAsNotReadOnly(string action)
        {
            Assert.False(OperationClassifier.IsReadOnly("genexus_structure", new JObject { ["action"] = action }));
        }

        [Fact]
        public void GenexusDoc_Classifications()
        {
            // health is pure read
            Assert.True(OperationClassifier.IsReadOnly("genexus_doc", new JObject { ["action"] = "health" }));

            // wiki and visualize write files to disk
            Assert.False(OperationClassifier.IsReadOnly("genexus_doc", new JObject { ["action"] = "wiki" }));
            Assert.False(OperationClassifier.IsReadOnly("genexus_doc", new JObject { ["action"] = "visualize" }));
        }

        [Fact]
        public void GenexusRecipe_Classifications()
        {
            // list, describe, suggest_macro are reads
            Assert.True(OperationClassifier.IsReadOnly("genexus_recipe", new JObject { ["action"] = "list" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_recipe", new JObject { ["action"] = "describe" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_recipe", new JObject { ["action"] = "suggest_macro" }));

            // crystallize writes JSON file to disk
            Assert.False(OperationClassifier.IsReadOnly("genexus_recipe", new JObject { ["action"] = "crystallize" }));
        }

        [Fact]
        public void GenexusKb_Classifications()
        {
            // list, list_environments, get_environment are reads
            Assert.True(OperationClassifier.IsReadOnly("genexus_kb", new JObject { ["action"] = "list" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_kb", new JObject { ["action"] = "list_environments" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_kb", new JObject { ["action"] = "get_environment" }));

            // set_environment, open, close, set_default mutate state
            Assert.False(OperationClassifier.IsReadOnly("genexus_kb", new JObject { ["action"] = "set_environment" }));
            Assert.False(OperationClassifier.IsReadOnly("genexus_kb", new JObject { ["action"] = "open" }));
            Assert.False(OperationClassifier.IsReadOnly("genexus_kb", new JObject { ["action"] = "close" }));
            Assert.False(OperationClassifier.IsReadOnly("genexus_kb", new JObject { ["action"] = "set_default" }));
        }

        [Fact]
        public void GenexusTelemetry_Classifications()
        {
            Assert.True(OperationClassifier.IsReadOnly("genexus_telemetry", new JObject { ["action"] = "logs" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_telemetry", new JObject { ["action"] = "metrics" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_telemetry", new JObject { ["action"] = "status" }));

            // friction_append writes to disk
            Assert.False(OperationClassifier.IsReadOnly("genexus_telemetry", new JObject { ["action"] = "friction_append" }));
        }

        [Fact]
        public void GenexusVersioning_Classifications()
        {
            Assert.True(OperationClassifier.IsReadOnly("genexus_versioning", new JObject { ["action"] = "history_list" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_versioning", new JObject { ["action"] = "history_get" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_versioning", new JObject { ["action"] = "time_travel" }));
            Assert.True(OperationClassifier.IsReadOnly("genexus_versioning", new JObject { ["action"] = "blame" }));

            // history_save, history_restore, undo mutate
            Assert.False(OperationClassifier.IsReadOnly("genexus_versioning", new JObject { ["action"] = "history_save" }));
            Assert.False(OperationClassifier.IsReadOnly("genexus_versioning", new JObject { ["action"] = "history_restore" }));
            Assert.False(OperationClassifier.IsReadOnly("genexus_versioning", new JObject { ["action"] = "undo" }));
        }
    }
}
