using System;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class VariableSdtPersistenceTests
    {
        private static object NativeCustomType(string token, int category = 254)
        {
            var sdk = Assembly.Load("Artech.Genexus.Common");
            return Activator.CreateInstance(sdk.GetType("Artech.Genexus.Common.CustomTypes.AttCustomType", true), token, category);
        }

        [Fact]
        public void PersistedSdtReference_UsesNativeEntityIdentity()
        {
            var sdk = Assembly.Load("Artech.Genexus.Common");
            var referenceType = sdk.GetType("Artech.Genexus.Common.Types.StructureTypeReference", true);
            var expectedType = Guid.NewGuid();
            var reference = Activator.CreateInstance(referenceType, expectedType, 42);
            var token = (string)referenceType.GetMethod("SerializeToString").Invoke(null, new[] { reference });

            // This is the SDK's saved form, which the former GUID-only resolver rejected.
            Assert.False(Guid.TryParse(token, out _));
            Assert.True(VariableInjector.TryGetStructuralReferenceKey(NativeCustomType(token), out var actualType, out var actualId));
            Assert.Equal(expectedType, actualType);
            Assert.Equal(42, actualId);
        }

        [Theory]
        [InlineData("SampleRecord")]
        [InlineData("00000000-0000-0000-0000-000000000042")]
        [InlineData("<StructureTypeReference><Type>invalid</Type><Id>42</Id></StructureTypeReference>")]
        [InlineData("<StructureTypeReference />")]
        public void MissingOrInvalidNativeIdentity_DoesNotTrustTheTypeLabel(string token)
        {
            Assert.False(VariableInjector.TryGetStructuralReferenceKey(NativeCustomType(token), out _, out _));
        }

        [Fact]
        public void PrimitiveCategoryWithStructuralToken_IsRejected()
        {
            const string token = "<StructureTypeReference><Type>00000000-0000-0000-0000-000000000042</Type><Id>42</Id></StructureTypeReference>";
            Assert.False(VariableInjector.TryGetStructuralReferenceKey(NativeCustomType(token, 1), out _, out _));
            Assert.False(VariableInjector.TryGetStructuralReferenceKey(null, out _, out _));
        }
    }
}
