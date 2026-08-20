using System;
using System.IO;
using System.Linq;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class GeneratorReferenceServiceTests
    {
        [Fact]
        public void ParseReferences_MirrorsGxExternalReferenceOrder()
        {
            var references = GeneratorReferenceService.ParseReferences(
                "/debug /r:Security.Common.dll /r:\"Library One.dll\" /reference:Json.dll /r:Text.dll");

            Assert.Equal(new[] { "Security.Common.dll", "Library One.dll", "Json.dll", "Text.dll" }, references);
        }

        [Fact]
        public void DryRunAdd_ReportsOnlyRequestedReference_AndDoesNotApply()
        {
            var store = new FakeStore(Snapshot("/debug /r:Json.dll", "v1"));
            var service = new GeneratorReferenceService(store);
            JObject response = JObject.Parse(service.Run(AddArgs("dry_run_add")));

            Assert.Equal("ok", response["status"]?.ToString());
            Assert.False(response["result"]?["persisted"]?.Value<bool>());
            Assert.Equal(new[] { "Json.dll" }, response["result"]?["before"]?.ToObject<string[]>());
            Assert.Equal(new[] { "Json.dll", TestAssemblyName }, response["result"]?["after"]?.ToObject<string[]>());
            Assert.Empty(response["result"]?["unrelatedChanges"] as JArray);
            Assert.Empty(response["result"]?["implicitLifecycleActions"] as JArray);
            Assert.Equal(0, store.ApplyCalls);
        }

        [Fact]
        public void Add_PersistsRereads_AndSecondAddIsIdempotent()
        {
            var store = new FakeStore(Snapshot("/r:Json.dll", "v1"));
            var service = new GeneratorReferenceService(store);
            JObject first = JObject.Parse(service.Run(AddArgs("add", "v1")));
            JObject second = JObject.Parse(service.Run(AddArgs("add")));

            Assert.True(first["result"]?["persisted"]?.Value<bool>());
            Assert.True(first["result"]?["verified"]?.Value<bool>());
            Assert.Equal(1, first["result"]?["after"]?.ToObject<string[]>()
                .Count(x => string.Equals(x, TestAssemblyName, StringComparison.OrdinalIgnoreCase)));
            Assert.True(second["result"]?["idempotent"]?.Value<bool>());
            Assert.Equal(1, store.ApplyCalls);
        }

        [Fact]
        public void Add_RejectsStaleBaseVersionWithoutWriting()
        {
            var store = new FakeStore(Snapshot("/r:Json.dll", "v2"));
            var service = new GeneratorReferenceService(store);
            JObject response = JObject.Parse(service.Run(AddArgs("add", "v1")));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("VersionConflict", response["error"]?["code"]?.ToString());
            Assert.Equal(1, store.ApplyCalls);
            Assert.Equal("/r:Json.dll", store.Current.CompilerFlags);
        }

        [Fact]
        public void VerificationFailure_ReportsExactRestoration()
        {
            var store = new FakeStore(Snapshot("/r:Json.dll", "v1")) { FailAndRestore = true };
            var service = new GeneratorReferenceService(store);
            JObject response = JObject.Parse(service.Run(AddArgs("add", "v1")));

            Assert.Equal("error", response["status"]?.ToString());
            Assert.Equal("GeneratorReferenceNotPersisted", response["error"]?["code"]?.ToString());
            Assert.True(response["rollbackPerformed"]?.Value<bool>());
            Assert.True(response["stateRestoredExactly"]?.Value<bool>());
            Assert.False(response["partialPersistenceDetected"]?.Value<bool>());
            Assert.Empty(response["implicitLifecycleActions"] as JArray);
        }

        [Fact]
        public void RemoveReferenceTokens_PreservesUnrelatedFlagsExactly()
        {
            string flags = "/debug   /r:Json.dll /warnaserror+  /r:Text.dll";
            Assert.Equal("/debug   /r:Json.dll /warnaserror+", GeneratorReferenceService.RemoveReferenceTokens(flags, "Text.dll"));
        }

        private static readonly string TestAssemblyPath = typeof(GeneratorReferenceServiceTests).Assembly.Location;
        private static readonly string TestAssemblyName = Path.GetFileName(TestAssemblyPath);

        private static JObject AddArgs(string action, string baseVersion = null)
        {
            var args = new JObject
            {
                ["action"] = action,
                ["environment"] = ".Net Environment",
                ["generator"] = "Default (.NET)",
                ["assembly"] = TestAssemblyName,
                ["assemblyPath"] = TestAssemblyPath,
                ["rollbackOnFailure"] = true
            };
            if (baseVersion != null) args["baseVersion"] = baseVersion;
            return args;
        }

        private static GeneratorReferenceService.GeneratorConfigurationSnapshot Snapshot(string flags, string version)
        {
            var snapshot = new GeneratorReferenceService.GeneratorConfigurationSnapshot
            {
                EnvironmentName = ".Net Environment",
                GeneratorName = "Default (.NET)",
                TargetIdentity = "target",
                CompilerFlags = flags,
                VersionToken = version,
                KbLocation = Path.GetDirectoryName(TestAssemblyPath),
                TargetPath = Path.GetDirectoryName(TestAssemblyPath)
            };
            var target = new GeneratorReferenceService.GeneratorState
            {
                Identity = "target",
                PropertiesXml = "<Properties />"
            };
            target.Properties["CSHARP_COMPILER_FLAGS"] = flags;
            snapshot.Generators["target"] = target;
            return snapshot;
        }

        private sealed class FakeStore : GeneratorReferenceService.IGeneratorConfigurationStore
        {
            public GeneratorReferenceService.GeneratorConfigurationSnapshot Current { get; private set; }
            public int ApplyCalls { get; private set; }
            public bool FailAndRestore { get; set; }

            public FakeStore(GeneratorReferenceService.GeneratorConfigurationSnapshot current) => Current = current;

            public GeneratorReferenceService.GeneratorConfigurationSnapshot Read(string environment, string generator, bool reload) => Current;

            public GeneratorReferenceService.GeneratorMutationResult Apply(string environment, string generator,
                string baseVersion, string compilerFlags, bool rollbackOnFailure)
            {
                ApplyCalls++;
                GeneratorReferenceService.GeneratorConfigurationSnapshot before = Current;
                if (!string.Equals(before.VersionToken, baseVersion, StringComparison.Ordinal))
                    return new GeneratorReferenceService.GeneratorMutationResult { Before = before, After = before, VersionConflict = true };

                if (FailAndRestore)
                    return new GeneratorReferenceService.GeneratorMutationResult
                    {
                        Before = before,
                        After = before,
                        Committed = true,
                        Verified = false,
                        RollbackPerformed = true,
                        StateRestoredExactly = true,
                        Error = "Injected reread mismatch."
                    };

                Current = Snapshot(compilerFlags, "v2");
                return new GeneratorReferenceService.GeneratorMutationResult
                {
                    Before = before,
                    After = Current,
                    Committed = true,
                    Verified = true
                };
            }
        }
    }
}
