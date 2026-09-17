using System;
using System.Collections.Generic;
using System.Linq;

namespace GxMcp.Worker.Helpers
{
    public static class DslParserUtils
    {
        public class ParsedNode
        {
            public string Name { get; set; }
            public string TypeStr { get; set; }
            public bool IsCollection { get; set; }
            public bool IsCompound { get; set; }
            public bool IsKey { get; set; }
            public string Description { get; set; }
            public string Formula { get; set; }
            public List<ParsedNode> Children { get; set; } = new List<ParsedNode>();
        }

        public static List<ParsedNode> ParseLinesIntoNodes(List<string> lines)
        {
            var rootNodes = new List<ParsedNode>();
            var stack = new Stack<(ParsedNode Node, int Indent)>();

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                int indent = line.TakeWhile(c => c == ' ').Count();
                string trimmed = line.Trim();
                var node = new ParsedNode();
                
                int commentIndex = trimmed.IndexOf("//");
                if (commentIndex >= 0)
                {
                    ParseCommentMetadata(trimmed.Substring(commentIndex + 2), node);
                    trimmed = trimmed.Substring(0, commentIndex).Trim();
                }
                if (string.IsNullOrEmpty(trimmed)) continue;
                
                if (trimmed == "{" || trimmed == "}") {
                    if (trimmed == "}" && stack.Count > 0) stack.Pop();
                    continue;
                }

                if (trimmed.EndsWith("Collection", StringComparison.OrdinalIgnoreCase)) {
                    trimmed = trimmed.Substring(0, trimmed.Length - 10).Trim();
                    node.IsCollection = true;
                }

                // Key marker `*` belongs to the attribute Name and may appear
                //   1) at end of bare name:   "TrnId*"
                //   2) before the colon:      "TrnId* : Numeric(4)"
                //   3) at end of full line:   "TrnId" + "*" trailing the whole line (legacy form)
                // Strip it after the colon split so it doesn't bleed into the name field.

                int colonIndex = trimmed.IndexOf(':');
                if (colonIndex > 0) {
                    node.Name = trimmed.Substring(0, colonIndex).Trim();
                    node.TypeStr = trimmed.Substring(colonIndex + 1).Trim();
                    node.IsCompound = false;
                } else {
                    node.Name = trimmed;
                    node.IsCompound = true;
                    if (i + 1 < lines.Count && lines[i + 1].Trim() != "{") {
                        node.IsCompound = false;
                        node.TypeStr = "Unknown";
                    }
                }

                if (!string.IsNullOrEmpty(node.Name) && node.Name.EndsWith("*")) {
                    node.IsKey = true;
                    node.Name = node.Name.Substring(0, node.Name.Length - 1).Trim();
                }

                // Bug 1: `*` key marker can also bleed into the type substring when written
                // as "TrnId : Numeric*" or "TrnId*:Numeric(4)*". AttributeTypeApplier.Parse
                // rejects "Numeric*" (silent drop). Strip trailing `*` here — the marker
                // belongs to the name role, not the type. Only `*` is documented as a key
                // marker; leave other punctuation (e.g. `&` in domain refs) alone.
                if (!string.IsNullOrEmpty(node.TypeStr) && node.TypeStr.EndsWith("*")) {
                    node.IsKey = true;
                    node.TypeStr = node.TypeStr.Substring(0, node.TypeStr.Length - 1).TrimEnd();
                }

                // Bug 2: in Transaction/Table/SDT structure DSL, `&Name` is not valid syntax
                // for attribute or item names — but some inputs (LLM-generated, copy/paste
                // from variable contexts) carry the `&` prefix. None of the three callers
                // (TransactionDslParser, TableDslParser, SdtDslParser) treat node.Name as a
                // variable reference; they all do case-insensitive attribute/item lookup,
                // and the `&` prefix breaks that lookup. Strip it here.
                if (!string.IsNullOrEmpty(node.Name) && node.Name.StartsWith("&")) {
                    node.Name = node.Name.Substring(1).TrimStart();
                }

                while (stack.Count > 0 && stack.Peek().Indent >= indent) stack.Pop();
                if (stack.Count == 0) rootNodes.Add(node);
                else stack.Peek().Node.Children.Add(node);
                if (node.IsCompound) stack.Push((node, indent));
            }
            return rootNodes;
        }

        private static void ParseCommentMetadata(string comment, ParsedNode node)
        {
            if (string.IsNullOrWhiteSpace(comment) || node == null) return;
            var formulaStart = comment.IndexOf("[Formula:", StringComparison.OrdinalIgnoreCase);
            if (formulaStart >= 0)
            {
                int valueStart = formulaStart + "[Formula:".Length;
                int close = comment.IndexOf(']', valueStart);
                node.Formula = (close >= 0 ? comment.Substring(valueStart, close - valueStart) : comment.Substring(valueStart)).Trim();
            }

            int quoteStart = comment.IndexOf('"');
            if (quoteStart >= 0)
            {
                int quoteEnd = comment.IndexOf('"', quoteStart + 1);
                if (quoteEnd > quoteStart)
                    node.Description = comment.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
            }
        }
    }
}
