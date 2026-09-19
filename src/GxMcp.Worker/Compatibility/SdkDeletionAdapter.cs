using System;

namespace GxMcp.Worker.Compatibility
{
    /// <summary>
    /// Deletes a KB object through the SDK member that the running GeneXus major
    /// actually exposes: modern SDKs expose <c>Delete()</c>, some legacy builds only
    /// expose <c>Remove()</c>. Centralizing the candidate order here keeps the
    /// major-specific member name out of service code.
    /// </summary>
    internal static class SdkDeletionAdapter
    {
        /// <summary>Member names tried in order; first present wins.</summary>
        private static readonly string[] CandidateMethods = { "Delete", "Remove" };

        /// <summary>
        /// Attempts to delete the target object. Returns false when none of the
        /// candidate members exist on the object's type; throws the underlying SDK
        /// failure when the member exists but the SDK rejects the deletion.
        /// </summary>
        internal static bool TryDeleteOrRemove(object kbObject)
        {
            if (kbObject == null) return false;

            foreach (string methodName in CandidateMethods)
            {
                var invocation = OptionalSdkInvoker.InvokeNoArgs(kbObject, methodName);
                if (invocation.Available)
                {
                    if (!invocation.Succeeded)
                        throw new InvalidOperationException(invocation.Error);
                    return true;
                }
            }
            return false;
        }
    }
}
