using System;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class SessionKbContextStoreTests
    {
        [Fact]
        public void Stores_selection_per_session()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));

            store.Set("session-a", "customer");
            store.Set("session-b", "order");

            Assert.Equal("customer", store.Get("session-a"));
            Assert.Equal("order", store.Get("session-b"));
        }

        [Fact]
        public void Clear_removes_only_the_requested_session()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));

            store.Set("session-a", "customer");
            store.Set("session-b", "order");
            store.Clear("session-a");

            Assert.Null(store.Get("session-a"));
            Assert.Equal("order", store.Get("session-b"));
        }

        [Fact]
        public void Initialize_does_not_overwrite_an_existing_selection()
        {
            var store = new SessionKbContextStore(TimeSpan.FromMinutes(10));

            Assert.True(store.Initialize("session-a", "customer"));
            Assert.False(store.Initialize("session-a", "order"));

            Assert.Equal("customer", store.Get("session-a"));
        }
    }
}
