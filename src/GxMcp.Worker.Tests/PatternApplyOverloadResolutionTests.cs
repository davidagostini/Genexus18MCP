using System;
using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternApplyOverloadResolutionTests
    {
        private sealed class FakeModel { }
        private sealed class FakeParent { }
        private sealed class FakeSettingsView { }

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
