using System;
using System.Collections.Generic;
using System.Reflection;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class VariableSdtPersistenceTests
    {
        private static object NativeCustomType(string serialized, int category = 254)
        {
            var sdk = Assembly.Load("Artech.Genexus.Common");
            return Activator.CreateInstance(sdk.GetType("Artech.Genexus.Common.CustomTypes.AttCustomType", true), serialized, category);
        }

        [Fact]
        public void PersistedSdtReference_UsesNativeEntityIdentity()
        {
            var sdk = Assembly.Load("Artech.Genexus.Common");
            var referenceType = sdk.GetType("Artech.Genexus.Common.Types.StructureTypeReference", true);
            var expectedType = Guid.NewGuid();
            var reference = Activator.CreateInstance(referenceType, expectedType, 42);
            var serialized = (string)referenceType.GetMethod("SerializeToString").Invoke(null, new[] { reference });

            // The form the SDK saves after a save: a structural reference, not a GUID.
            Assert.False(Guid.TryParse(serialized, out _));
            Assert.True(VariableInjector.TryGetStructuralReferenceKey(NativeCustomType(serialized), out var actualType, out var actualId));
            Assert.Equal(expectedType, actualType);
            Assert.Equal(42, actualId);
        }

        // A form the SDK validator refuses is not an identity, even when its decoder could read it.
        [Theory]
        [InlineData("SampleRecord")]
        [InlineData("00000000-0000-0000-0000-000000000042")]
        [InlineData("<StructureTypeReference><Type>invalid</Type><Id>42</Id></StructureTypeReference>")]
        [InlineData("<StructureTypeReference />")]
        [InlineData("<StructureTypeReference><Type>00000000-0000-0000-0000-000000000000</Type><Id>42</Id></StructureTypeReference>")]
        [InlineData("<StructureTypeReference><Type>00000000-0000-0000-0000-000000000042</Type><Id>0</Id></StructureTypeReference>")]
        [InlineData(" <StructureTypeReference><Type>00000000-0000-0000-0000-000000000042</Type><Id>42</Id></StructureTypeReference>")]
        public void MissingOrInvalidNativeIdentity_DoesNotTrustTheTypeLabel(string serialized)
        {
            Assert.False(VariableInjector.TryGetStructuralReferenceKey(NativeCustomType(serialized), out _, out _));
        }

        [Fact]
        public void PrimitiveCategoryWithStructuralReference_IsRejected()
        {
            const string serialized = "<StructureTypeReference><Type>00000000-0000-0000-0000-000000000042</Type><Id>42</Id></StructureTypeReference>";
            Assert.False(VariableInjector.TryGetStructuralReferenceKey(NativeCustomType(serialized, 1), out _, out _));
            Assert.False(VariableInjector.TryGetStructuralReferenceKey(null, out _, out _));
        }

        private sealed class ResolvedType
        {
        }

        private static VariableInjector.BoundTypeLookups<ResolvedType> Lookups(
            Func<string, ResolvedType> byTypeName = null, Func<string, string, ResolvedType> businessComponentByName = null)
        {
            return new VariableInjector.BoundTypeLookups<ResolvedType>
            {
                FromKey = key => null,
                FromEntityKey = (typeGuid, id) => null,
                ByTypeName = byTypeName ?? (name => null),
                BusinessComponentByName = businessComponentByName ?? ((name, module) => null)
            };
        }

        [Fact]
        public void DanglingNativeReference_DoesNotFallBackToSameNamedType()
        {
            const string serialized = "<StructureTypeReference><Type>00000000-0000-0000-0000-000000000042</Type><Id>42</Id></StructureTypeReference>";
            var values = new Dictionary<string, object>
            {
                ["ATTCUSTOMTYPE"] = NativeCustomType(serialized),
                ["DataTypeString"] = "Record"
            };
            var lookups = Lookups(byTypeName: name => name == "Record" ? new ResolvedType() : null);

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                name => values.TryGetValue(name, out object value) ? value : null, "GX_SDT", null, lookups, out string resolutionPath);

            Assert.Null(bound);
            Assert.Equal(VariableInjector.ResolutionPathNone, resolutionPath);
        }

        [Theory]
        [InlineData("GX_CHAR")]
        [InlineData("GX_SDT")]
        public void BusinessComponentNameFallback_DoesNotApplyToOtherVariableTypes(string variableType)
        {
            var values = new Dictionary<string, object> { ["DataTypeString"] = "Customer, Module" };
            var lookups = Lookups(businessComponentByName: (name, module) => name == "Customer" ? new ResolvedType() : null);

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                name => values.TryGetValue(name, out object value) ? value : null, variableType, null, lookups, out string resolutionPath);

            Assert.Null(bound);
            Assert.Equal(VariableInjector.ResolutionPathNone, resolutionPath);
        }

        [Fact]
        public void BusinessComponentNameFallback_ResolvesBusinessComponentVariables()
        {
            var values = new Dictionary<string, object> { ["DataTypeString"] = "Customer, Module" };
            var customer = new ResolvedType();
            var lookups = Lookups(businessComponentByName: (name, module) => name == "Customer" && module == "Module" ? customer : null);

            var bound = VariableInjector.ResolveBoundTypeObjectCore(
                name => values.TryGetValue(name, out object value) ? value : null, "GX_BUSCOMP", null, lookups, out string resolutionPath);

            Assert.Same(customer, bound);
            Assert.Equal(VariableInjector.ResolutionPathDataTypeName, resolutionPath);
        }
    }
}
