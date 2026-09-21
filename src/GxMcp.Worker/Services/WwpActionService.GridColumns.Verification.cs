using System;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        // Bindings are resolved by the caller from SDK identity, never inferred from
        // a control name. gxGrid/gxColumn and ColAttId are the native SDK representation.
        internal static JObject VerifyGridColumnProjection(string beforeXml, string afterXml,
            string action, JObject args, string targetBinding, string beforeBinding)
        {
            var result = new JObject
            {
                ["confirmed"] = false, ["orderConfirmed"] = false,
                ["captionConfirmed"] = false, ["bindingConfirmed"] = false,
                ["unrelatedWebFormConfirmed"] = false
            };
            try
            {
                bool adding = action == "add_grid_variable";
                if ((!adding && action != "move_grid_column") || string.IsNullOrWhiteSpace(targetBinding))
                    return GridProjectionFailure(result, "WwpProjectionIdentityUnavailable");
                var left = XDocument.Parse(beforeXml);
                var right = XDocument.Parse(afterXml);
                var oldMatches = left.Descendants().Where(e => Is(e, "gxColumn")
                    && Attr(e, "ColAttId") == targetBinding).ToList();
                var matches = right.Descendants().Where(e => Is(e, "gxColumn")
                    && Attr(e, "ColAttId") == targetBinding).ToList();
                if (matches.Count != 1 || oldMatches.Count != (adding ? 0 : 1))
                    return GridProjectionFailure(result, "WwpProjectionColumnIdentityNotConfirmed");
                var column = matches[0];
                var oldColumn = oldMatches.SingleOrDefault();
                var grid = column.Parent;
                if (grid == null || !Is(grid, "gxGrid") || (oldColumn != null
                    && (oldColumn.Parent == null || !Is(oldColumn.Parent, "gxGrid")
                        || GridProjectionPath(oldColumn.Parent) != GridProjectionPath(grid))))
                    return GridProjectionFailure(result, "WwpProjectionSchemaUnsupported");
                result["bindingConfirmed"] = true;
                string caption = args?["caption"]?.ToString();
                bool captionConfirmed = caption == null
                    ? oldColumn != null && Attr(column, "ColTitle") == Attr(oldColumn, "ColTitle")
                    : Attr(column, "ColTitle") == caption;
                // An expression can override the displayed literal. Unknown expression
                // serialization must not be reported as a confirmed caption.
                string expression = Attr(column, "ColTitleExpression");
                if (!string.IsNullOrEmpty(expression)) captionConfirmed = false;
                result["captionConfirmed"] = captionConfirmed;

                bool orderConfirmed;
                if (!string.IsNullOrWhiteSpace(args?["before"]?.ToString()))
                {
                    if (string.IsNullOrWhiteSpace(beforeBinding) || beforeBinding == targetBinding)
                        return GridProjectionFailure(result, "WwpProjectionAnchorIdentityUnavailable");
                    var anchors = right.Descendants().Where(e => Is(e, "gxColumn")
                        && Attr(e, "ColAttId") == beforeBinding).ToList();
                    var oldAnchors = left.Descendants().Where(e => Is(e, "gxColumn")
                        && Attr(e, "ColAttId") == beforeBinding).ToList();
                    orderConfirmed = anchors.Count == 1 && oldAnchors.Count == 1
                        && anchors[0].Parent == grid && oldAnchors[0].Parent != null
                        && GridProjectionPath(oldAnchors[0].Parent) == GridProjectionPath(grid)
                        && column.ElementsAfterSelf().FirstOrDefault() == anchors[0];
                }
                else orderConfirmed = adding
                    ? left.Descendants().Count(e => Is(e, "gxGrid")) == 1 && column.ElementsAfterSelf().Any() == false
                    : oldColumn.ElementsBeforeSelf().Count() == column.ElementsBeforeSelf().Count();
                result["orderConfirmed"] = orderConfirmed;

                // Removing only the requested column makes order and all unrelated
                // controls/properties independently comparable. Compare the moved
                // column itself too, excluding only the explicitly requested caption.
                bool columnPreserved = true;
                if (oldColumn != null)
                {
                    var oldCopy = new XElement(oldColumn);
                    var newCopy = new XElement(column);
                    if (caption != null)
                    {
                        oldCopy.Attributes().Where(a => a.Name.LocalName.Equals("ColTitle", StringComparison.OrdinalIgnoreCase)).Remove();
                        newCopy.Attributes().Where(a => a.Name.LocalName.Equals("ColTitle", StringComparison.OrdinalIgnoreCase)).Remove();
                    }
                    columnPreserved = GridProjectionXmlEquals(oldCopy, newCopy);
                    oldColumn.Remove();
                }
                column.Remove();
                bool unrelated = columnPreserved && GridProjectionXmlEquals(left.Root, right.Root);
                result["unrelatedWebFormConfirmed"] = unrelated;
                result["confirmed"] = orderConfirmed && captionConfirmed && unrelated;
                if (result["confirmed"].Value<bool>() != true)
                    result["code"] = "WwpProjectionNotConfirmed";
                return result;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException || ex is ArgumentException)
            {
                return GridProjectionFailure(result, "WwpProjectionSchemaUnsupported");
            }
        }

        private static JObject GridProjectionFailure(JObject result, string code)
        {
            result["code"] = code;
            return result;
        }

        private static string GridProjectionPath(XElement element) => string.Join("/",
            element.AncestorsAndSelf().Reverse().Select(e => e.Name + "[" + e.ElementsBeforeSelf().Count() + "]"));

        private static bool GridProjectionXmlEquals(XElement left, XElement right)
        {
            if (left == null || right == null) return false;
            // Attribute order is not XML content. All nodes, comments and meaningful
            // text remain in the comparison; parsing only discards indentation.
            foreach (var element in left.DescendantsAndSelf().Concat(right.DescendantsAndSelf()))
            {
                var attributes = element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).ToList();
                element.ReplaceAttributes(attributes);
            }
            return XNode.DeepEquals(left, right);
        }

        internal static JObject VerifyGridColumnVariables(JArray before, JArray after, string action, JObject args)
        {
            bool adding = action == "add_grid_variable";
            string name = args?["variable"]?.ToString()?.TrimStart('&');
            var result = new JObject { ["confirmed"] = false, ["declarationConfirmed"] = false,
                ["existingVariablesConfirmed"] = false };
            if (before == null || after == null) return result;
            var oldVariables = before.OfType<JObject>().ToList();
            var newVariables = after.OfType<JObject>().ToList();
            if (oldVariables.Count != before.Count || newVariables.Count != after.Count
                || oldVariables.Any(v => string.IsNullOrWhiteSpace(v["name"]?.ToString()))
                || newVariables.Any(v => string.IsNullOrWhiteSpace(v["name"]?.ToString()))
                || oldVariables.GroupBy(v => v["name"].ToString(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)
                || newVariables.GroupBy(v => v["name"].ToString(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1)) return result;
            bool preserved = oldVariables.All(old => newVariables.Any(current => JToken.DeepEquals(old, current)));
            bool declaration = !adding;
            if (adding)
            {
                var added = newVariables.SingleOrDefault(v => string.Equals(v["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
                declaration = !oldVariables.Any(v => string.Equals(v["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    && added != null && added["basicType"]?.ToString() == args?["basicType"]?.ToString()
                    && (added["basicType"]?.ToString() == "Character" || added["basicType"]?.ToString() == "VarChar")
                    && added["length"]?.Value<int?>() == args?["length"]?.Value<int?>()
                    && added["length"]?.Value<int?>() > 0
                    && added["decimals"]?.Value<int?>() == 0;
            }
            preserved &= newVariables.Count == oldVariables.Count + (adding ? 1 : 0);
            result["declarationConfirmed"] = declaration;
            result["existingVariablesConfirmed"] = preserved;
            result["confirmed"] = declaration && preserved;
            return result;
        }

        internal static JObject VerifyGridColumnEvents(string before, string after) => new JObject
        {
            // Until generated-region changes can be proven safe for a given SDK,
            // require the whole Events source, including protected blocks, unchanged.
            ["confirmed"] = before != null && after != null && string.Equals(before, after, StringComparison.Ordinal),
            ["comparison"] = "exact"
        };
    }
}
