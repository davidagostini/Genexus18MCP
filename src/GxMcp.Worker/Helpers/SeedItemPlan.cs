#nullable enable

using System;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Single canonical model for the seed item a new Transaction or SDT receives
    /// when created. Owns the requested-vs-default resolution (firstItem/firstItemType)
    /// and the "name : type [Key]" description format so previews and execution
    /// can never drift apart.
    /// </summary>
    public sealed class SeedItemPlan
    {
        public string ItemName { get; }
        public string ItemType { get; }

        private SeedItemPlan(string itemName, string itemType)
        {
            ItemName = itemName;
            ItemType = itemType;
        }

        public static readonly SeedItemPlan SdtDefault = new("Item1", "VARCHAR");

        /// <summary>
        /// Resolves the Transaction key seed. Default: "&lt;TrnName&gt;Id : Numeric(4)".
        /// A requested firstItem is normalized like the executor does (trim, no leading '&amp;').
        /// </summary>
        public static SeedItemPlan ForTransaction(string trnName, string? requestedName, string? requestedType)
        {
            string itemName = string.IsNullOrWhiteSpace(requestedName)
                ? trnName + "Id"
                : NormalizeIdentifier(requestedName!);
            return new SeedItemPlan(itemName, ResolveType(requestedType, DefaultTransactionKeyType));
        }

        public static SeedItemPlan ForSdt(string? requestedName, string? requestedType)
        {
            return new SeedItemPlan(
                string.IsNullOrWhiteSpace(requestedName) ? SdtDefault.ItemName : NormalizeIdentifier(requestedName!),
                ResolveType(requestedType, SdtDefault.ItemType));
        }

        /// <summary>"&lt;name&gt; : &lt;type&gt; [Key]" — same format for preview and execution.</summary>
        public string KeyDescription => ItemName + " : " + ItemType + " [Key]";

        /// <summary>"&lt;name&gt; : &lt;type&gt;" (SDT seed items are not keys).</summary>
        public string Description => ItemName + " : " + ItemType;

        /// <summary>Canonical Transaction key type when the caller does not request one.</summary>
        public const string DefaultTransactionKeyType = "Numeric(4)";

        private static string NormalizeIdentifier(string value)
            => value.Trim().TrimStart('&');

        private static string ResolveType(string? requestedType, string fallback)
            => string.IsNullOrWhiteSpace(requestedType) ? fallback : requestedType!.Trim();
    }
}
