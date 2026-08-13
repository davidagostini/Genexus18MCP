using System.Reflection;
using Xunit;

namespace GxMcp.Worker.Tests
{
    /// <summary>
    /// Unit coverage for the WorkWithPlus apply-on-save helper. The helper is
    /// internal and uses reflection over a package that isn't loaded in the
    /// unit-test process, so we exercise the null/missing-assembly fallbacks —
    /// the live integration scenario is covered by manual KB verification.
    /// </summary>
    public class WwpApplyOnSaveHelperTests
    {
        private static (MethodInfo tryEnable, PropertyInfo invocationCount) LoadHelper()
        {
            // WwpApplyOnSaveHelper is internal to the worker assembly. Loading
            // it via reflection avoids adding a wide InternalsVisibleTo just
            // for one helper.
            var asm = typeof(GxMcp.Worker.Services.PatternApplyService).Assembly;
            var t = asm.GetType("GxMcp.Worker.Helpers.WwpApplyOnSaveHelper", throwOnError: true)!;
            var tryEnable = t.GetMethod("TryEnable", BindingFlags.Static | BindingFlags.NonPublic);
            var invocationCount = t.GetProperty("InvocationCount", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(tryEnable);
            Assert.NotNull(invocationCount);
            return (tryEnable!, invocationCount!);
        }

        [Fact]
        public void TryEnable_NullHost_ReturnsFalseWithoutInvoking()
        {
            var (tryEnable, invocationCount) = LoadHelper();
            long before = (long)invocationCount.GetValue(null)!;

            bool result = (bool)tryEnable.Invoke(null, new object[] { null })!;

            Assert.False(result);
            long after = (long)invocationCount.GetValue(null)!;
            Assert.Equal(before, after);
        }

        [Fact]
        public void InvocationCount_StaysAtZeroInUnitTestProcess()
        {
            // The unit-test process never loads DVelop.Patterns.WorkWithPlus
            // and never instantiates a KBObject, so no real invocation can
            // happen here. If a future change accidentally bumps the counter
            // from a static initializer or a side-effect path, this test
            // catches it.
            var (_, invocationCount) = LoadHelper();
            long count = (long)invocationCount.GetValue(null)!;
            Assert.Equal(0, count);
        }
    }
}
