using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class DatabaseProviderResolverTests
    {
        private sealed class PropertyBag
        {
            private readonly System.Collections.Generic.Dictionary<string, object> _values
                = new System.Collections.Generic.Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);

            public void Set(string name, object value) => _values[name] = value;
            public object GetPropertyValue(string name) => _values.TryGetValue(name, out var value) ? value : null;
        }

        private sealed class DataStore
        {
            public string Dbms { get; set; }
            public PropertyBag Properties { get; } = new PropertyBag();
        }

        [Fact]
        public void PropertyBagCodeIsUsedWhenDirectDbmsIsSentinel()
        {
            var store = new DataStore { Dbms = "Unknown" };
            store.Properties.Set("DBMS_CODE", 15);

            Assert.Equal(15, DatabaseProviderResolver.GetDbmsCode(store));
            Assert.Equal("postgres", DatabaseProviderResolver.ResolveFamily(store));
        }

        [Theory]
        [InlineData("None")]
        [InlineData("Unknown")]
        [InlineData("NotSet")]
        public void SentinelDbmsValuesRemainUnknownWithoutFallback(string value)
        {
            var store = new DataStore { Dbms = value };

            Assert.Equal(0, DatabaseProviderResolver.GetDbmsCode(store));
            Assert.Equal("unknown", DatabaseProviderResolver.ResolveFamily(store));
        }
    }
}
