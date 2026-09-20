using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Services.Structure
{
    internal sealed class AttributeMovePlan
    {
        public int OldPosition { get; set; }
        public int NewPosition { get; set; }
        public IReadOnlyList<string> OrderedNames { get; set; }
    }

    internal static class AttributeMovePlanner
    {
        public static AttributeMovePlan Create(
            IEnumerable<string> currentNames,
            string attribute,
            string before,
            string after,
            int? position)
        {
            var names = (currentNames ?? Enumerable.Empty<string>()).ToList();
            if (string.IsNullOrWhiteSpace(attribute))
                throw new ArgumentException("attribute is required.", nameof(attribute));

            int selectorCount = (string.IsNullOrWhiteSpace(before) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(after) ? 0 : 1)
                + (position.HasValue ? 1 : 0);
            if (selectorCount != 1)
                throw new ArgumentException("Exactly one of before, after, or position must be provided.");

            int oldPosition = IndexOf(names, attribute);
            if (oldPosition < 0)
                throw new InvalidOperationException("AttributeNotFound");

            if ((!string.IsNullOrWhiteSpace(before) && EqualsName(before, attribute))
                || (!string.IsNullOrWhiteSpace(after) && EqualsName(after, attribute)))
                throw new ArgumentException("The reference attribute must differ from attribute.");

            names.RemoveAt(oldPosition);
            int insertion;
            if (!string.IsNullOrWhiteSpace(before))
            {
                insertion = IndexOf(names, before);
                if (insertion < 0) throw new InvalidOperationException("ReferenceAttributeNotFound");
            }
            else if (!string.IsNullOrWhiteSpace(after))
            {
                int reference = IndexOf(names, after);
                if (reference < 0) throw new InvalidOperationException("ReferenceAttributeNotFound");
                insertion = reference + 1;
            }
            else
            {
                insertion = position.Value;
                if (insertion < 0 || insertion > names.Count)
                    throw new ArgumentOutOfRangeException(nameof(position),
                        $"position must be between 0 and {names.Count} (zero-based)." );
            }

            names.Insert(insertion, attribute);
            return new AttributeMovePlan
            {
                OldPosition = oldPosition,
                NewPosition = insertion,
                OrderedNames = names
            };
        }

        private static int IndexOf(IReadOnlyList<string> names, string wanted)
        {
            for (int i = 0; i < names.Count; i++)
                if (EqualsName(names[i], wanted)) return i;
            return -1;
        }

        private static bool EqualsName(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
