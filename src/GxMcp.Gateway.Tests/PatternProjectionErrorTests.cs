using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    public class PatternProjectionErrorTests
    {
        [Fact]
        public void TerseFailurePreservesProjectionCauseAndIndependentRollbackEvidence()
        {
            var error = JObject.Parse("{status:'error', error:{code:'PatternProjectionFailed',message:'parent save failed'},"
                + "projection:{parentSaved:false,failure:'src0265 undefined control',saveFailures:[{savePhase:'parent-save'}]},"
                + "commitCompleted:false,rollbackVerified:true,restorationVerification:{host:{equal:true},parent:{equal:true}}}");
            var actual = McpRouter.TrimErrorEnvelope(error, verbose: false);
            Assert.Equal("src0265 undefined control", (string)actual["projection"]!["failure"]!);
            Assert.False((bool)actual["commitCompleted"]!);
            Assert.True((bool)actual["rollbackVerified"]!);
            Assert.True((bool)actual["restorationVerification"]!["parent"]!["equal"]!);
        }
    }
}
