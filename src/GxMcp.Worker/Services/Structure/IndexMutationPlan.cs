using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services.Structure
{
    /// <summary>
    /// Pure validation, projection and comparison for create_index. SDK object creation and
    /// persistence stay in IndexService, so dry-run can exercise the complete plan without
    /// constructing an Index or dirtying a TableIndexesPart.
    /// </summary>
    public static class IndexMutationPlanner
    {
        private static readonly Regex ValidName = new Regex(
            "^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        public static IndexCreatePlan Create(
            JObject payload,
            IEnumerable<string> tableAttributes,
            IEnumerable<TableIndexState> existing)
        {
            if (payload == null)
                throw new IndexPlanException("InvalidIndexPayload", "payload is required.");

            var attrToken = payload["attributes"];
            if (!(attrToken is JArray attrArray) || attrArray.Count == 0)
                throw new IndexPlanException(
                    "InvalidIndexPayload",
                    "payload.attributes must be a non-empty array of attribute names.");

            var attributes = new List<string>();
            foreach (JToken token in attrArray)
            {
                if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace(token.ToString()))
                    throw new IndexPlanException(
                        "InvalidIndexAttribute",
                        "Every payload.attributes item must be a non-empty string.");
                attributes.Add(token.ToString().Trim());
            }

            string duplicateMember = attributes
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .FirstOrDefault();
            if (duplicateMember != null)
                throw new IndexPlanException(
                    "DuplicateIndexAttribute",
                    "Attribute '" + duplicateMember + "' appears more than once in the requested index.");

            var available = new HashSet<string>(
                tableAttributes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string outsideTable = attributes.FirstOrDefault(a => !available.Contains(a));
            if (outsideTable != null)
                throw new IndexPlanException(
                    "AttributeNotInTable",
                    "Attribute '" + outsideTable + "' is not part of the associated table.");

            bool unique = true;
            if (payload["unique"] != null)
            {
                if (payload["unique"].Type != JTokenType.Boolean)
                    throw new IndexPlanException(
                        "InvalidIndexType", "payload.unique must be a boolean.");
                unique = payload["unique"].Value<bool>();
            }

            string order = payload["order"]?.ToString();
            if (string.IsNullOrWhiteSpace(order)) order = "Ascending";
            else if (order.Equals("Ascending", StringComparison.OrdinalIgnoreCase)) order = "Ascending";
            else if (order.Equals("Descending", StringComparison.OrdinalIgnoreCase)) order = "Descending";
            else throw new IndexPlanException(
                "InvalidIndexOrder", "payload.order must be Ascending or Descending.");

            string name = payload["name"]?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(name) && (!ValidName.IsMatch(name) || name.Length > 128))
                throw new IndexPlanException(
                    "InvalidIndexName",
                    "payload.name must start with a letter, contain only letters, digits or underscores, and be at most 128 characters.");

            var before = (existing ?? Enumerable.Empty<TableIndexState>()).Select(x => x.Clone()).ToList();
            if (!string.IsNullOrEmpty(name) && before.Any(x =>
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new IndexPlanException(
                    "IndexAlreadyExists", "An index named '" + name + "' already exists on the table.");

            var wouldCreate = new TableIndexState
            {
                Name = name ?? string.Empty,
                IndexType = unique ? "Unique" : "Duplicate",
                Source = "User",
                NameGeneratedBySdk = string.IsNullOrEmpty(name)
            };
            foreach (string attribute in attributes)
                wouldCreate.Members.Add(new IndexMemberState { Name = attribute, Order = order });

            if (before.Any(x => EquivalentDefinition(x, wouldCreate, ignoreName: true)))
                throw new IndexPlanException(
                    "DuplicateIndexDefinition",
                    "An index with the same type, member order and attributes already exists on the table.");

            return new IndexCreatePlan
            {
                RequestedName = name,
                Unique = unique,
                Order = order,
                Attributes = attributes,
                Before = before,
                WouldCreate = wouldCreate
            };
        }

        public static bool EquivalentDefinition(TableIndexState left, TableIndexState right, bool ignoreName = false)
        {
            if (left == null || right == null) return false;
            if (!ignoreName && !string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(NormalizeType(left.IndexType), NormalizeType(right.IndexType), StringComparison.OrdinalIgnoreCase)) return false;
            if (left.Members.Count != right.Members.Count) return false;
            for (int i = 0; i < left.Members.Count; i++)
            {
                if (!string.Equals(left.Members[i].Name, right.Members[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(NormalizeOrder(left.Members[i].Order), NormalizeOrder(right.Members[i].Order), StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public static bool SameState(IEnumerable<TableIndexState> left, IEnumerable<TableIndexState> right)
        {
            var a = (left ?? Enumerable.Empty<TableIndexState>()).ToList();
            var b = (right ?? Enumerable.Empty<TableIndexState>()).ToList();
            if (a.Count != b.Count) return false;
            foreach (TableIndexState expected in a)
            {
                TableIndexState actual = b.SingleOrDefault(x => string.Equals(
                    x.Name, expected.Name, StringComparison.OrdinalIgnoreCase));
                if (actual == null || !EquivalentDefinition(expected, actual)) return false;
                if (!string.Equals(expected.Source ?? string.Empty, actual.Source ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public static string ComputeVersionToken(
            string targetToken,
            string tableToken,
            IEnumerable<TableIndexState> indexes)
        {
            var canonical = new JObject
            {
                ["target"] = targetToken ?? string.Empty,
                ["table"] = tableToken ?? string.Empty,
                ["indexes"] = ToJson((indexes ?? Enumerable.Empty<TableIndexState>())
                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            }.ToString(Formatting.None);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        public static JArray ToJson(IEnumerable<TableIndexState> indexes) => new JArray(
            (indexes ?? Enumerable.Empty<TableIndexState>()).Select(x => x.ToJson()));

        public static JObject Diff(IEnumerable<TableIndexState> before, IEnumerable<TableIndexState> after)
        {
            var oldList = (before ?? Enumerable.Empty<TableIndexState>()).ToList();
            var newList = (after ?? Enumerable.Empty<TableIndexState>()).ToList();
            return new JObject
            {
                ["added"] = new JArray(newList.Where(n => !oldList.Any(o =>
                    string.Equals(o.Name, n.Name, StringComparison.OrdinalIgnoreCase))).Select(x => x.ToJson())),
                ["removed"] = new JArray(oldList.Where(o => !newList.Any(n =>
                    string.Equals(n.Name, o.Name, StringComparison.OrdinalIgnoreCase))).Select(x => x.ToJson())),
                ["changed"] = new JArray(newList.Where(n => oldList.Any(o =>
                    string.Equals(o.Name, n.Name, StringComparison.OrdinalIgnoreCase)
                    && !EquivalentDefinition(o, n))).Select(x => x.ToJson()))
            };
        }

        public static TableIndexState FindRollbackCandidate(
            IEnumerable<TableIndexState> before,
            IEnumerable<TableIndexState> current,
            TableIndexState requested,
            string createdName)
        {
            var snapshot = (before ?? Enumerable.Empty<TableIndexState>()).ToList();
            var candidates = (current ?? Enumerable.Empty<TableIndexState>()).Where(index =>
                !snapshot.Any(old => string.Equals(old.Name, index.Name, StringComparison.OrdinalIgnoreCase)));
            if (!string.IsNullOrWhiteSpace(createdName))
            {
                TableIndexState exact = candidates.FirstOrDefault(index => string.Equals(
                    index.Name, createdName, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;
            }
            return candidates.FirstOrDefault(index =>
                EquivalentDefinition(index, requested, ignoreName: true));
        }

        private static string NormalizeType(string value)
        {
            value = value ?? string.Empty;
            if (value.IndexOf("Primary", StringComparison.OrdinalIgnoreCase) >= 0) return "Primary";
            if (value.IndexOf("Unique", StringComparison.OrdinalIgnoreCase) >= 0) return "Unique";
            if (value.IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0) return "Duplicate";
            return value;
        }

        private static string NormalizeOrder(string value) =>
            (value ?? string.Empty).IndexOf("Descending", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Descending" : "Ascending";
    }

    public sealed class IndexCreatePlan
    {
        public string RequestedName { get; set; }
        public bool Unique { get; set; }
        public string Order { get; set; }
        public List<string> Attributes { get; set; } = new List<string>();
        public List<TableIndexState> Before { get; set; } = new List<TableIndexState>();
        public TableIndexState WouldCreate { get; set; }

        public List<TableIndexState> Projected()
        {
            var result = Before.Select(x => x.Clone()).ToList();
            result.Add(WouldCreate.Clone());
            return result;
        }
    }

    public sealed class TableIndexState
    {
        public string Name { get; set; } = string.Empty;
        public string IndexType { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public bool NameGeneratedBySdk { get; set; }
        public List<IndexMemberState> Members { get; } = new List<IndexMemberState>();

        public TableIndexState Clone()
        {
            var clone = new TableIndexState
            {
                Name = Name,
                IndexType = IndexType,
                Source = Source,
                NameGeneratedBySdk = NameGeneratedBySdk
            };
            clone.Members.AddRange(Members.Select(x => new IndexMemberState { Name = x.Name, Order = x.Order }));
            return clone;
        }

        public JObject ToJson() => new JObject
        {
            ["name"] = string.IsNullOrEmpty(Name) ? JValue.CreateNull() : (JToken)Name,
            ["nameGeneratedBySdk"] = NameGeneratedBySdk,
            ["indexType"] = IndexType,
            ["isUnique"] = IndexType.IndexOf("Unique", StringComparison.OrdinalIgnoreCase) >= 0
                || IndexType.IndexOf("Primary", StringComparison.OrdinalIgnoreCase) >= 0,
            ["source"] = Source,
            ["attributes"] = new JArray(Members.Select(x => new JObject
            {
                ["name"] = x.Name,
                ["order"] = x.Order,
                ["isAscending"] = x.Order.IndexOf("Descending", StringComparison.OrdinalIgnoreCase) < 0
            }))
        };
    }

    public sealed class IndexMemberState
    {
        public string Name { get; set; } = string.Empty;
        public string Order { get; set; } = "Ascending";
    }

    public sealed class IndexPlanException : Exception
    {
        public IndexPlanException(string code, string message, string hint = null) : base(message)
        {
            Code = code;
            Hint = hint;
        }

        public string Code { get; }
        public string Hint { get; }
    }
}
