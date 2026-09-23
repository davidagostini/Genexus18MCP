using System;
using System.Reflection;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternEngineReapplyTests
    {
        public sealed class Settings { public bool Fail { get; set; } }
        public sealed class InvocationMarker { public bool Invoked; }
        private static void UnsupportedNativeApply(object instance, Settings settings) => ((InvocationMarker)instance).Invoked = true;
        public static bool NativeApply(object instance, Settings settings)
        {
            if (settings == null) throw new NullReferenceException("Native SDK dereferences settings.");
            return !settings.Fail;
        }

        private static ReflectionPatternEngineAdapter Adapter(string method = nameof(NativeApply))
        {
            var adapter = new ReflectionPatternEngineAdapter();
            void Set(string name, object value) => typeof(ReflectionPatternEngineAdapter)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(adapter, value);
            Set("_probed", true);
            Set("_patternEngineType", typeof(PatternEngineReapplyTests));
            Set("_applySettingsType", typeof(Settings));
            Set("_applyPatternReapply", typeof(PatternEngineReapplyTests).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
            return adapter;
        }

        [Fact]
        public void UnsupportedNativeReturnContractIsRejectedBeforeInvocation()
        {
            var marker = new InvocationMarker();
            Assert.Throws<NotSupportedException>(() => Adapter(nameof(UnsupportedNativeApply)).ReapplyPattern(marker, null));
            Assert.False(marker.Invoked);
        }

        [Fact]
        public void InvalidTypedSettingIsRejectedBeforeInvocation()
        {
            Assert.Throws<ArgumentException>(() => Adapter().ReapplyPattern(new object(), new JObject { ["Fail"] = "not-a-boolean" }));
        }

        [Fact]
        public void OmittedSettingsConstructsNativeDefaults()
        {
            Assert.True(Adapter().ReapplyPattern(new object(), null).NativeApplySucceeded);
            Adapter().InvokeReapply(new object(), null);
        }

        [Fact]
        public void EmptySettingsConstructsNativeDefaults()
        {
            Assert.True(Adapter().ReapplyPattern(new object(), new JObject()).NativeApplySucceeded);
        }

        [Fact]
        public void NativeFalseCannotBecomeSuccessfulApply()
        {
            var settings = new JObject { ["Fail"] = true };
            Assert.Contains("partially saved", Assert.Throws<InvalidOperationException>(() =>
                Adapter().ReapplyPattern(new object(), settings)).Message);
            Assert.Throws<InvalidOperationException>(() => Adapter().InvokeReapply(new object(), settings));
        }

        [Fact]
        public void UnknownSettingsAreRejectedBeforeApply()
        {
            Assert.Throws<ArgumentException>(() => Adapter().ReapplyPattern(new object(), new JObject { ["NoSuchSetting"] = true }));
        }
    }
}
