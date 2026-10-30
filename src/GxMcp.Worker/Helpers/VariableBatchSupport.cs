using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The parts of a variable batch that need no Knowledge Base. PopulateVariablesInto builds every item
    /// detached, so a bad item never touches the live Variables part, and adds them only when every item
    /// passed. The SDK checks an item again as it is added (VariablesPart.BeforeItemAdd throws for a name it
    /// reserves for a standard variable or one already in the part), so that last step can still be refused
    /// for one item: <see cref="TryAddAll{T}"/> takes the items it had added out again, which keeps the batch
    /// all-or-nothing, and <see cref="MarkRefused"/> names the refused item in the outcomes.
    /// </summary>
    internal static class VariableBatchSupport
    {
        /// <summary>
        /// Adds every staged item, in order. When <paramref name="add"/> refuses one, the items already added
        /// are removed again, last first, so the target is exactly as it was, and the index and the exception
        /// of the refused item are reported; the items after it are never attempted. An item that cannot be
        /// removed again is not hidden: the others are still removed, then the failure is thrown, because the
        /// target is no longer as it was.
        /// </summary>
        internal static bool TryAddAll<T>(IReadOnlyList<T> staged, Action<T> add, Action<T> remove,
            out int refusedIndex, out Exception refusal)
        {
            for (int index = 0; index < staged.Count; index++)
            {
                try
                {
                    add(staged[index]);
                }
                catch (Exception ex)
                {
                    RemoveAgain(staged, index, remove, ex);
                    refusedIndex = index;
                    refusal = ex;
                    return false;
                }
            }

            refusedIndex = -1;
            refusal = null;
            return true;
        }

        private static void RemoveAgain<T>(IReadOnlyList<T> staged, int addedCount, Action<T> remove, Exception refusal)
        {
            Exception stuck = null;
            for (int index = addedCount - 1; index >= 0; index--)
            {
                try
                {
                    remove(staged[index]);
                }
                catch (Exception ex)
                {
                    if (stuck == null) stuck = ex;
                }
            }

            if (stuck != null)
                throw new InvalidOperationException(
                    "A variable batch was refused (" + ExceptionRoot.Message(refusal)
                    + ") and the items already added could not all be removed again: " + ExceptionRoot.Message(stuck),
                    stuck);
        }

        /// <summary>
        /// Turns the outcome of the item the SDK refused into a failure, with the SDK's message as the reason.
        /// The other items stay as they are: the caller discards the batch, which marks the ones that would
        /// have been added as <c>NotAdded</c>.
        /// </summary>
        internal static void MarkRefused(JArray outcomes, string name, string reason)
        {
            JObject outcome = outcomes.OfType<JObject>().FirstOrDefault(o =>
                string.Equals((string)o["status"], "Added", StringComparison.Ordinal)
                && string.Equals((string)o["name"], name, StringComparison.OrdinalIgnoreCase));
            if (outcome == null)
            {
                outcome = new JObject { ["name"] = name };
                outcomes.Add(outcome);
            }
            outcome["status"] = "Failed";
            outcome["reason"] = reason;
        }

        // The SDK refuses to add a plain variable under a name it reserves for its standard variables
        // (VariablesPart.BeforeItemAdd asks VariableDefinition.IsStandard). That predicate is public and
        // static and needs no Knowledge Base, so a batch can name such an item before building anything.
        // It is looked up by reflection, like the dimension properties, so a GeneXus build without it only
        // loses the early refusal: the commit still catches the item.
        private static readonly Func<string, bool> SdkIsStandard = ResolveSdkIsStandard();

        internal static bool IsSdkStandardName(string name)
        {
            return !string.IsNullOrEmpty(name) && SdkIsStandard != null && SdkIsStandard(name);
        }

        private static Func<string, bool> ResolveSdkIsStandard()
        {
            try
            {
                Type definition = typeof(global::Artech.Genexus.Common.Parts.VariablesPart).Assembly
                    .GetType("Artech.Genexus.Common.Parts.Variables.VariableDefinition", false);
                MethodInfo method = definition?.GetMethod("IsStandard",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (method != null)
                    return (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), method);
                Logger.Warn("[VARIABLES] VariableDefinition.IsStandard(string) is not available in this GeneXus build; standard variable names are refused when the batch is committed.");
            }
            catch (Exception ex)
            {
                Logger.Warn("[VARIABLES] VariableDefinition.IsStandard(string) could not be bound (" + ExceptionRoot.Message(ex) + "); standard variable names are refused when the batch is committed.");
            }
            return null;
        }
    }
}
