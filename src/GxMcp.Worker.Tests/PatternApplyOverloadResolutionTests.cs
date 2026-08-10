using System;
using System.IO;
using System.Xml.Linq;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternApplyOverloadResolutionTests
    {
        private sealed class FakeModel { }
        private sealed class FakeParent { }
        private enum FakeSettingsView { NativeMobile, Web }

        private static class U16LikePackageInterface
        {
            public static bool CreatePatternInstanceWithTemplate(
                FakeModel model, FakeParent parent, string template, out object instance)
            {
                instance = null;
                return true;
            }

            public static bool CreatePatternInstanceWithTemplate(
                FakeModel model, FakeParent parent, FakeSettingsView settings, string template, out object instance)
            {
                instance = null;
                return true;
            }
        }

        private interface IFirst { }
        private interface ISecond { }
        private sealed class ImplementsBoth : IFirst, ISecond { }

        private static class TrulyAmbiguousInterface
        {
            public static bool Select(IFirst value) => true;
            public static bool Select(ISecond value) => true;
        }

        [Fact]
        public void U16Overloads_SelectsFourParameterTemplateMethod()
        {
            var method = PatternApplyService.ResolveCompatibleStaticOverload(
                typeof(U16LikePackageInterface),
                "CreatePatternInstanceWithTemplate",
                new object[] { new FakeModel(), new FakeParent(), "Empty", null },
                byRefArgumentIndex: 3,
                expectedReturnType: typeof(bool),
                out var candidates,
                out var error);

            Assert.NotNull(method);
            Assert.Null(error);
            Assert.Equal(4, method.GetParameters().Length);
            Assert.Equal(typeof(string), method.GetParameters()[2].ParameterType);
            Assert.True(method.GetParameters()[3].ParameterType.IsByRef);
            Assert.Contains("FakeSettingsView", candidates);
            Assert.Contains("System.String", candidates);
        }

        [Theory]
        [InlineData("WebPanel")]
        [InlineData("WebComponent")]
        public void WebTargets_SelectFiveParameterWebSettingsView(string parentType)
        {
            var method = PatternApplyService.ResolveWwpCreateCall(
                typeof(U16LikePackageInterface),
                parentType,
                new FakeModel(),
                new FakeParent(),
                "Empty",
                out var args,
                out var byRefIndex,
                out var candidates,
                out var error);

            Assert.NotNull(method);
            Assert.Null(error);
            Assert.Equal(5, method.GetParameters().Length);
            Assert.Equal(4, byRefIndex);
            Assert.Equal(FakeSettingsView.Web, args[2]);
            Assert.Equal("Empty", args[3]);
            Assert.Contains("FakeSettingsView", candidates);
        }

        [Fact]
        public void SdPanel_SelectsFourParameterNativeMobileOverload()
        {
            var method = PatternApplyService.ResolveWwpCreateCall(
                typeof(U16LikePackageInterface),
                "SDPanel",
                new FakeModel(),
                new FakeParent(),
                "Empty",
                out var args,
                out var byRefIndex,
                out _,
                out var error);

            Assert.NotNull(method);
            Assert.Null(error);
            Assert.Equal(4, method.GetParameters().Length);
            Assert.Equal(3, byRefIndex);
            Assert.Equal("Empty", args[2]);
        }

        [Fact]
        public void EnvironmentPreflight_UsesConfiguredUserAppDataPathAndSurfacesAccessFailure()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-wwp-env-" + Guid.NewGuid().ToString("N"));
            try
            {
                string dataRoot = Path.Combine(root, "custom-data");
                string environmentPath = Path.Combine(dataRoot, "GeneXus", "GeneXus", "18", "Environment.config");
                Directory.CreateDirectory(Path.GetDirectoryName(environmentPath));
                File.WriteAllText(environmentPath, "test");
                new XDocument(
                    new XElement("configuration",
                        new XElement("appSettings",
                            new XElement("add",
                                new XAttribute("key", "UserAppDataPath"),
                                new XAttribute("value", dataRoot)))))
                    .Save(Path.Combine(root, "GeneXus.exe.config"));

                var context = PatternApplyService.InspectWwpEnvironment(
                    root,
                    path => string.Equals(path, environmentPath, StringComparison.OrdinalIgnoreCase)
                        ? "System.UnauthorizedAccessException: simulated"
                        : "wrong path");

                Assert.Equal(environmentPath, context.EnvironmentConfigPath);
                Assert.True(context.EnvironmentConfigExists);
                Assert.False(context.EnvironmentConfigWritable);
                Assert.Contains("GeneXus.exe.config", context.ConfigSource);
                Assert.Contains("UnauthorizedAccessException", context.AccessError);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void EnvironmentPreflight_MalformedGeneXusConfigIsBlocking()
        {
            string root = Path.Combine(Path.GetTempPath(), "gxmcp-wwp-env-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "GeneXus.exe.config"), "<configuration><appSettings>");

                var context = PatternApplyService.InspectWwpEnvironment(root);

                Assert.False(context.EnvironmentConfigWritable);
                Assert.False(context.EnvironmentConfigExists);
                Assert.Contains("XmlException", context.AccessError);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void Resolver_DoesNotChooseArbitrarilyWhenBestCandidatesTie()
        {
            var method = PatternApplyService.ResolveCompatibleStaticOverload(
                typeof(TrulyAmbiguousInterface),
                "Select",
                new object[] { new ImplementsBoth() },
                byRefArgumentIndex: -1,
                expectedReturnType: typeof(bool),
                out var candidates,
                out var error);

            Assert.Null(method);
            Assert.Contains("equally compatible", error);
            Assert.Contains("IFirst", candidates);
            Assert.Contains("ISecond", candidates);
        }

        [Fact]
        public void Resolver_ReportsEveryCandidateWhenCallShapeIsUnsupported()
        {
            var method = PatternApplyService.ResolveCompatibleStaticOverload(
                typeof(U16LikePackageInterface),
                "CreatePatternInstanceWithTemplate",
                new object[] { new FakeModel(), new FakeParent(), "Empty" },
                byRefArgumentIndex: -1,
                expectedReturnType: typeof(bool),
                out var candidates,
                out var error);

            Assert.Null(method);
            Assert.Contains("No compatible overload", error);
            Assert.Contains("FakeSettingsView", candidates);
            Assert.Contains("out System.Object", candidates);
        }
    }
}
