using System;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class ReflectionHelperTests
    {
        private class SimpleClass
        {
            public string Name { get; set; } = "Simple";
            public int Value { get; set; } = 42;
        }

        private class ShadowBase
        {
            public Guid Type { get; set; } = Guid.NewGuid();
            public string BaseProp { get; set; } = "Base";
        }

        private class ShadowDerived : ShadowBase
        {
            public new string Type { get; set; } = "Design";
            public string DerivedProp { get; set; } = "Derived";
        }

        private class PropertyBagClass
        {
            public string GetPropertyValue(string name)
            {
                if (string.Equals(name, "CustomKey", StringComparison.OrdinalIgnoreCase))
                    return "CustomValue";
                return null;
            }
        }

        [Fact]
        public void TryGetMember_NormalProperty_ReturnsValue()
        {
            var obj = new SimpleClass { Name = "TestName", Value = 123 };
            Assert.Equal("TestName", ReflectionHelper.TryGetMember(obj, "Name"));
            Assert.Equal("TestName", ReflectionHelper.TryGetMember(obj, "name")); // Case insensitive
            Assert.Equal(123, ReflectionHelper.TryGetMember(obj, "Value"));
        }

        [Fact]
        public void TryGetMember_NullTargetOrName_ReturnsNull()
        {
            Assert.Null(ReflectionHelper.TryGetMember(null, "Name"));
            Assert.Null(ReflectionHelper.TryGetMember(new SimpleClass(), null));
            Assert.Null(ReflectionHelper.TryGetMember(new SimpleClass(), ""));
        }

        [Fact]
        public void TryGetMember_NonExistentProperty_ReturnsNull()
        {
            var obj = new SimpleClass();
            Assert.Null(ReflectionHelper.TryGetMember(obj, "NonExistent"));
        }

        [Fact]
        public void TryGetMember_ShadowedProperty_ResolvesDerivedValueWithoutAmbiguousMatchException()
        {
            var obj = new ShadowDerived { Type = "Production", DerivedProp = "Sub", BaseProp = "Parent" };

            // In ordinary Type.GetProperty, obj.GetType().GetProperty("Type") throws AmbiguousMatchException
            // ReflectionHelper must safely resolve the most-derived property
            var typeVal = ReflectionHelper.TryGetMember(obj, "Type");
            Assert.NotNull(typeVal);
            Assert.Equal("Production", typeVal.ToString());

            // Inherited non-shadowed property still resolves
            Assert.Equal("Parent", ReflectionHelper.TryGetMember(obj, "BaseProp"));
            Assert.Equal("Sub", ReflectionHelper.TryGetMember(obj, "DerivedProp"));
        }

        [Fact]
        public void TryGetPropertyBagValue_ResolvesViaGetPropertyValueMethod()
        {
            var bag = new PropertyBagClass();
            var val = ReflectionHelper.TryGetPropertyBagValue(bag, "CustomKey");
            Assert.Equal("CustomValue", val);

            var missing = ReflectionHelper.TryGetPropertyBagValue(bag, "MissingKey");
            Assert.Null(missing);
        }
    }
}
