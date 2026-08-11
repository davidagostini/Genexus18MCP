using System.Reflection;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    // Plan 072 — ObjectMover type-selection hardening.
    //  1. FindFirstType (the broad "EntityManager" fallback) previously accepted ANY type
    //     with the matching simple name in ANY loaded assembly — a non-GeneXus
    //     EntityManager could have been reflectively invoked as the Udm EntityManager.
    //     It is now constrained to Artech.* types from Artech/GxMcp assemblies.
    //  2. FindMethod (overload resolution for SaveWithParent/UpdateParent) previously took
    //     the FIRST compatible overload, but reflection enumeration order is unspecified —
    //     the binding could differ across runs. It now scores candidates and prefers the
    //     longest compatible overload deterministically.
    public class ObjectMoverHardeningTests
    {
        // ---- namespace constraint on the fallback type scan -------------------

        [Fact]
        public void IsArtechSdkType_RejectsNonArtechTypes()
        {
            Assert.False(ObjectMover.IsArtechSdkType(typeof(string)));
            Assert.False(ObjectMover.IsArtechSdkType(typeof(FakeEntity)));
        }

        [Fact]
        public void IsArtechSdkType_AcceptsArtechTypes()
        {
            // The Worker.Tests project does not reference the SDK assemblies at compile
            // time, so resolve a real Artech type at runtime (the Worker assembly loads
            // the SDK). When the SDK isn't on the probing path, the constraint is simply
            // unverifiable here — skip rather than fail the suite.
            var groupType = typeof(ObjectMover).Assembly.GetType("Artech.Genexus.Common.Objects.Group");
            if (groupType == null) return;
            Assert.True(ObjectMover.IsArtechSdkType(groupType));
        }

        [Fact]
        public void IsArtechSdkType_Null_IsFalse()
        {
            Assert.False(ObjectMover.IsArtechSdkType(null));
        }

        [Fact]
        public void IsArtechAssembly_RejectsForeignAssemblies()
        {
            Assert.False(ObjectMover.IsArtechAssembly(typeof(string).Assembly)); // mscorlib/System.Private.CoreLib
            Assert.False(ObjectMover.IsArtechAssembly(null));
        }

        [Fact]
        public void IsArtechAssembly_AcceptsSdkAssembly()
        {
            // The Worker assembly itself is GxMcp.* (accepted); an Artech.* assembly is
            // accepted too when the SDK is on the probing path.
            Assert.True(ObjectMover.IsArtechAssembly(typeof(ObjectMover).Assembly));
            var groupType = typeof(ObjectMover).Assembly.GetType("Artech.Genexus.Common.Objects.Group");
            if (groupType != null)
                Assert.True(ObjectMover.IsArtechAssembly(groupType.Assembly));
        }

        // ---- deterministic overload resolution ----------------------------------

        // A host mirroring the SDK's SaveWithParent/UpdateParent overload families:
        // a 2-arg and a 3-arg SaveWithParent, a wrong-typed 2-arg, and two UpdateParent
        // arities. Reflection order is unspecified, so the tests must pass regardless
        // of the order GetMethods returns them in.
        private class OverloadHost
        {
            public static void SaveWithParent(FakeEntity e, FakeContainer c) { }
            public static void SaveWithParent(FakeEntity e, FakeContainer c, object prefs) { }
            public static void SaveWithParent(FakeEntity e, string wrong) { }
            public static void UpdateParent(FakeEntity e) { }
            public static void UpdateParent(FakeEntity e, object prefs) { }
        }

        private class FakeEntity { }
        private class FakeContainer : FakeEntity { }

        [Fact]
        public void FindMethod_PrefersLongestCompatibleOverload_Deterministically()
        {
            var entity = new FakeEntity();
            var container = new FakeContainer();

            var mi = ObjectMover.FindMethod(typeof(OverloadHost), "SaveWithParent", entity, container);

            // The 3-arg overload must win regardless of GetMethods enumeration order.
            Assert.NotNull(mi);
            Assert.Equal("SaveWithParent", mi!.Name);
            Assert.Equal(3, mi.GetParameters().Length);
        }

        [Fact]
        public void FindMethod_SkipsOverloadsWhoseSecondParamRejectsParent()
        {
            var entity = new FakeEntity();
            var container = new FakeContainer();

            // Only (entity, string) accepts a string in slot 2; it must be selected over
            // the (entity, FakeContainer) overloads even though they sort earlier.
            var mi = ObjectMover.FindMethod(typeof(OverloadHost), "SaveWithParent", entity, "not-a-container");

            Assert.NotNull(mi);
            Assert.Equal(typeof(string), mi!.GetParameters()[1].ParameterType);
        }

        [Fact]
        public void FindMethod_NoArg1_PicksLongestCompatibleUpdateParent()
        {
            var entity = new FakeEntity();

            var mi = ObjectMover.FindMethod(typeof(OverloadHost), "UpdateParent", entity);

            // (entity, prefs) declares the most parameters we can supply; prefer it over
            // the 1-arg variant deterministically.
            Assert.NotNull(mi);
            Assert.Equal(2, mi!.GetParameters().Length);
        }

        [Fact]
        public void FindMethod_NoCompatibleOverload_ReturnsNull()
        {
            Assert.Null(ObjectMover.FindMethod(typeof(OverloadHost), "DoesNotExist", new FakeEntity()));
        }
    }
}
