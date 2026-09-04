using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class TransactionRecordsServiceTests
    {
        [Fact]
        public void PersistedWriteRequiresAnOptimisticVersion()
        {
            Assert.False(TransactionRecordsService.IsWriteAllowed(false, null));
            Assert.False(TransactionRecordsService.IsWriteAllowed(false, "   "));
            Assert.True(TransactionRecordsService.IsWriteAllowed(false, "trn-v1:current"));
        }

        [Fact]
        public void DryRunIsAlwaysAllowedWithoutChangingState()
        {
            Assert.True(TransactionRecordsService.IsWriteAllowed(true, null));
        }

        [Theory]
        [InlineData("sqlserver", "[sales].[Order]")]
        [InlineData("oracle", "\"sales\".\"Order\"")]
        public void IdentifiersAreQuotedByProvider(string family, string expected)
        {
            Assert.Equal(expected, TransactionRecordsService.QuoteIdentifier("sales.Order", family));
        }
    }
}
