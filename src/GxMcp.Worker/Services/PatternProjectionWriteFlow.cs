using System;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    // The SDK transaction stays open through projection and verification. Keeping
    // this sequence independent of SDK objects lets failure paths run without a KB.
    internal static class PatternProjectionWriteFlow
    {
        internal const string SaveContract = "transactional-pattern-projection";

        internal static bool PromoteReceipt(JObject response)
        {
            var receipt = response["result"] as JObject ?? response;
            if ((string)receipt["saveContract"] != SaveContract) return false;
            if (!ReferenceEquals(receipt, response))
                foreach (var property in receipt.Properties()) response[property.Name] = property.Value.DeepClone();
            return true;
        }

        internal static Exception Run(JObject receipt, Action saveAndProject, Action verify,
            Action commit, Action rollback)
        {
            try
            {
                receipt["savePhase"] = "save-and-project";
                saveAndProject();
                receipt["savePhase"] = "pre-commit-verification";
                verify();
                receipt["savePhase"] = "commit";
                commit();
                receipt["commitCompleted"] = true;
                return null;
            }
            catch (Exception ex)
            {
                receipt["saveFailureMessage"] = IsolatedEventsSaveDiagnostics.Sanitize(ex.Message);
                receipt["rollbackAttempted"] = true;
                try { rollback(); receipt["transactionRolledBack"] = true; }
                catch (Exception rollbackError)
                {
                    receipt["rollbackError"] = IsolatedEventsSaveDiagnostics.Sanitize(rollbackError.Message);
                }
                return ex;
            }
        }

        internal static bool ConfirmReadback(JObject receipt, bool? originalHost, bool? originalParent,
            bool? projectedHost, bool? projectedParent, bool? requestedPattern)
        {
            bool known = originalHost.HasValue && originalParent.HasValue;
            bool restored = known && originalHost == true && originalParent == true;
            bool committed = receipt["commitCompleted"]?.Value<bool>() == true;
            bool confirmed = committed && projectedHost == true && projectedParent == true && requestedPattern == true;
            receipt["persistedStateKnown"] = known;
            receipt["persisted"] = known ? (JToken)confirmed : JValue.CreateNull();
            receipt["persistedVerified"] = confirmed;
            // saved is the physical save (the commit), as in every other write receipt; the re-read decides verified.
            receipt["saved"] = committed;
            receipt["verified"] = confirmed;
            receipt["postSaveVerification"] = PostSaveVerification(receipt, committed, known, confirmed);
            receipt["rollbackVerified"] = receipt["transactionRolledBack"]?.Value<bool>() == true && restored;
            receipt["rollback"] = new JObject
            {
                ["attempted"] = receipt["rollbackAttempted"]?.Value<bool>() == true,
                ["verified"] = receipt["rollbackVerified"],
                ["error"] = receipt["rollbackError"]
            };
            receipt["partialPersistenceDetected"] = known && !restored && !confirmed;
            return confirmed;
        }

        // The shape the generic write receipt uses: a confirmed re-read carries the token it read; any other
        // outcome says the re-read did not confirm the write, and why. Failure paths never claim confirmation.
        private static JObject PostSaveVerification(JObject receipt, bool committed, bool known, bool confirmed)
        {
            if (confirmed)
                return new JObject { ["reReadConfirmed"] = true, ["versionToken"] = receipt["versionToken"]?.DeepClone() };
            string reason = !committed ? "The SDK commit did not complete."
                : !known ? "The independent re-read was not available after the transaction."
                : "The independent re-read did not confirm the requested PatternInstance and the parent WebForm.";
            return new JObject { ["reReadConfirmed"] = false, ["reason"] = reason };
        }
    }
}
