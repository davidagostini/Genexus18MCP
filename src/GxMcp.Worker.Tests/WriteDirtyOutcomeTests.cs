using GxMcp.Worker.Services;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class WriteDirtyOutcomeTests
    {
        [Theory]
        [InlineData("{\"status\":\"ok\",\"code\":\"WriteApplied\",\"persisted\":true,\"changed\":true}", true)]
        [InlineData("{\"status\":\"ok\",\"code\":\"WriteNoChange\",\"persisted\":true,\"changed\":false}", false)]
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"WriteNotPersisted\"},\"partialPersistenceDetected\":true}", true)]
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"WriteNotPersisted\"},\"rollbackFailed\":true}", true)]
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"WriteNotPersisted\"}}", false)]
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"WriteVerificationUnavailable\"}}", true)]
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"ValidationFailed\"}}", false)]
        [InlineData("{\"status\":\"error\",\"partialPersistenceDetected\":true}", true)]
        [InlineData("{\"status\":\"error\",\"rollbackFailed\":true}", true)]
        public void DirtyClassificationUsesFinalPersistedOutcome(string response, bool expected)
        {
            Assert.Equal(expected, WriteService.ShouldMarkTargetDirty(response));
        }

        // The pattern write receipts say "unknown" with an explicit JSON null in persisted. JToken.Value<bool>()
        // throws on that null, which turned the whole receipt into a dispatcher failure.
        [Theory]
        // Failed before the commit and the transaction was rolled back: nothing was saved, the target stays clean.
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"PatternSaveFailed\"},\"persisted\":null,\"changed\":null,\"saved\":null,\"sdkSaveCompleted\":null,\"partialPersistenceDetected\":false,\"rollback\":{\"attempted\":true,\"verified\":false}}", false)]
        // Every flag the classification reads can be an explicit null.
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"PatternSaveFailed\",\"partialPersistenceDetected\":null},\"persisted\":null,\"changed\":null,\"partialPersistenceDetected\":null,\"rollbackFailed\":null,\"rollback\":{\"rolledBack\":null,\"reReadConfirmed\":null}}", false)]
        // The save committed but no confirming read was available: the persisted state is unknown, the target stays dirty.
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"PatternVerificationUnavailable\"},\"persisted\":null,\"changed\":null,\"saved\":true,\"sdkSaveCompleted\":true,\"partialPersistenceDetected\":false}", true)]
        // WorkWithPlus projection whose read after the transaction failed once the commit had completed.
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"PatternProjectionFailed\"},\"persisted\":null,\"persistedStateKnown\":false,\"commitCompleted\":true,\"partialPersistenceDetected\":false}", true)]
        // The SDK save completed inside a transaction that was rolled back, and no read proves the rollback.
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"PatternProjectionFailed\"},\"persisted\":null,\"commitCompleted\":false,\"sdkSaveCompleted\":true,\"transactionRolledBack\":true}", true)]
        // The projection never reached a save: there is nothing to rebuild.
        [InlineData("{\"status\":\"error\",\"error\":{\"code\":\"PatternProjectionFailed\"},\"persisted\":null,\"commitCompleted\":false,\"saveAttempted\":false}", false)]
        public void UnknownPersistedStateIsClassifiedWithoutThrowing(string response, bool expected)
        {
            Assert.Equal(expected, WriteService.ShouldMarkTargetDirty(response));
        }

        [Fact]
        public void ConfirmedRollbackDoesNotLeaveNewDirtyState()
        {
            const string response = "{\"status\":\"error\",\"rollback\":{\"rolledBack\":true,\"reReadConfirmed\":true},\"persisted\":false}";
            Assert.False(WriteService.ShouldMarkTargetDirty(response));
        }

        [Fact]
        public void UnknownPostSaveStateNeverTriggersAutomaticRollback()
        {
            Assert.True(WriteService.IsPostSaveVerificationIndeterminate(
                "{\"status\":\"error\",\"error\":{\"code\":\"WriteVerificationUnavailable\"}}"));
            Assert.True(WriteService.IsPostSaveVerificationIndeterminate(
                "{\"status\":\"error\",\"postSaveVerification\":{\"reReadConfirmed\":false}}"));
            Assert.False(WriteService.IsPostSaveVerificationIndeterminate(
                "{\"status\":\"error\",\"postSaveVerification\":{\"reReadConfirmed\":true}}"));
        }

        [Fact]
        public void InvalidResponseMarksTargetDirtyBecauseWriteOutcomeIsUnknown()
        {
            Assert.True(WriteService.ShouldMarkTargetDirty("not-json"));
        }
    }
}
