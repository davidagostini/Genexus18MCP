using System.Collections.Generic;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// The two halves of a rendered attribute declaration: the declaration itself,
    /// and the trailing comment carrying the details that are not part of it.
    /// </summary>
    internal sealed class AttributeDeclarationText
    {
        /// <summary>
        /// The declaration: <c>Name(+) : Type</c>.
        /// </summary>
        public string Head = string.Empty;

        /// <summary>
        /// The trailing comment, including its leading <c>" // "</c>, or empty when
        /// the declaration carries no extra detail.
        /// </summary>
        public string Comment = string.Empty;
    }

    /// <summary>
    /// Renders one attribute declaration in the DSL the Table and Transaction
    /// parsers read back.
    ///
    /// The rules were written out identically in both parsers: the description is
    /// quoted and appended only when it says something the name does not already
    /// say, a formula becomes <c>[Formula: ...]</c>, a nullable attribute becomes
    /// <c>[Nullable]</c>, and everything after the declaration is joined into one
    /// trailing comment.
    ///
    /// This is a round-trip format - it is rendered here and parsed back by the same
    /// two parsers - so the two renderings have to agree exactly. A rule that
    /// drifted between them would emit an attribute the other one reads differently,
    /// which shows up as an object that round-trips to something else rather than as
    /// an error. The callers keep their own indentation, which genuinely differs.
    /// </summary>
    internal static class AttributeDeclaration
    {
        /// <summary>
        /// Opening part of the formula marker, shared with the parsers that have
        /// to recognise it in text they did not render.
        ///
        /// <see cref="Render"/> builds the marker from this, so the two cannot
        /// drift: a parser looking for a different prefix would silently stop
        /// seeing every formula.
        /// </summary>
        internal const string FormulaMarkerPrefix = "[" + "Formula" + ":";

        // Both DSL grouping and semantic replacement must ignore brackets inside
        // either GeneXus string delimiter, including doubled delimiter escapes.
        internal static int FindFormulaEnd(string text, int start, out int depth)
        {
            depth = 0;
            char quote = '\0';
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        if (i + 1 < text.Length && text[i + 1] == quote) i++;
                        else quote = '\0';
                    }
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; continue; }
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// Renders <paramref name="name"/> with its type and optional detail.
        /// </summary>
        internal static AttributeDeclarationText Render(
            string name,
            string keyMarker,
            string type,
            string description,
            string formula,
            bool isNullable)
        {
            var detail = new List<string>();

            // The description is dropped when it merely repeats the name, which is
            // the common case: the SDK fills it in from the attribute's own name.
            if (!string.IsNullOrEmpty(description)
                && !description.Equals(name, System.StringComparison.OrdinalIgnoreCase))
            {
                detail.Add("\"" + description + "\"");
            }

            if (!string.IsNullOrEmpty(formula))
                detail.Add(FormulaMarkerPrefix + " " + formula + "]");

            if (isNullable)
                detail.Add("[Nullable]");

            return new AttributeDeclarationText
            {
                Head = name + (keyMarker ?? string.Empty) + " : " + type,
                Comment = detail.Count > 0 ? " // " + string.Join(", ", detail) : string.Empty,
            };
        }
    }
}