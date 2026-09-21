using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Artech.Architecture.Common.Objects;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public sealed partial class WwpActionService
    {
        internal static JObject ApplyGridColumnXml(XDocument document, string operation, JObject args)
        {
            try
            {
                ValidateGridColumnRequest(operation, args);
                XElement grid = ResolveGridPath(document.Root, args["gridPath"].ToString(),
                    e => e.Name.LocalName, e => e.Elements().ToList());
                string selector = (args["attribute"] ?? args["variable"]).ToString();
                bool variable = args["variable"] != null;
                XElement column = FindGridColumn(grid.Elements().ToList(), selector, variable, e => e.Name.LocalName, Attr);
                bool add = operation == "add_grid_variable";
                if (add && document.Descendants().Any(e => e.Name.LocalName.IndexOf("variable", StringComparison.OrdinalIgnoreCase) >= 0
                    && string.Equals(Attr(e, "name"), selector, StringComparison.OrdinalIgnoreCase)))
                    throw new WwpTabException("VariableAlreadyExists", "The variable already exists in the PatternInstance; it was not rebound.");
                if (!add && column == null) throw new WwpTabException("GridColumnNotFound", "The requested column was not found in the selected grid.");
                XElement anchor = args["before"] == null ? null : FindGridColumn(grid.Elements().ToList(), args["before"].ToString(), null, e => e.Name.LocalName, Attr);
                if (args["before"] != null && anchor == null) throw new WwpTabException("GridColumnNotFound", "The before column was not found in the selected grid.");
                if (column != null && ReferenceEquals(column, anchor)) throw new WwpTabException("InvalidGridColumnMove", "A column cannot be moved before itself.");
                int oldIndex = column == null ? -1 : grid.Elements().ToList().IndexOf(column);
                string binding = column == null ? selector : Attr(column, variable ? "name" : "attribute");
                if (add)
                {
                    column = new XElement(grid.Name.Namespace + "gridVariable",
                        new XAttribute("name", selector), new XAttribute("dataType", "Basic"),
                        new XAttribute("basicType", args["basicType"].ToString()), new XAttribute("basicCLength", args["length"].ToString()),
                        new XAttribute("readOnly", "True"));
                    if (anchor != null) anchor.AddBeforeSelf(column); else grid.Add(column);
                }
                else if (anchor != null)
                {
                    column.Remove();
                    anchor.AddBeforeSelf(column);
                }
                if (args["caption"] != null)
                {
                    XAttribute description = column.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("description", StringComparison.OrdinalIgnoreCase));
                    if (description == null) column.SetAttributeValue("description", args["caption"].ToString());
                    else description.Value = args["caption"].ToString();
                }
                return new JObject
                {
                    ["gridPath"] = args["gridPath"], ["column"] = selector, ["binding"] = binding,
                    ["movedColumn"] = add ? null : selector, ["addedVariable"] = add ? selector : null,
                    ["before"] = args["before"], ["caption"] = Attr(column, "description"),
                    ["basicType"] = add ? args["basicType"] : null, ["length"] = add ? args["length"] : null,
                    ["oldIndex"] = oldIndex, ["newIndex"] = grid.Elements().ToList().IndexOf(column)
                };
            }
            catch (WwpTabException ex) { return GridError(ex.Code, ex.Message); }
        }

        private static void ValidateGridColumnRequest(string operation, JObject args)
        {
            if (operation != "move_grid_column" && operation != "add_grid_variable") throw new WwpTabException("InvalidGridColumnOperation", "Unsupported grid column operation.");
            if (args == null || string.IsNullOrWhiteSpace(args["gridPath"]?.ToString())) throw new WwpTabException("MissingGridPath", "An unambiguous absolute gridPath is required.");
            bool variable = args["variable"] != null;
            if ((args["attribute"] != null) == variable) throw new WwpTabException("InvalidGridColumnSelector", "Supply exactly one of attribute or variable.");
            string selector = (args["attribute"] ?? args["variable"]).ToString();
            if (string.IsNullOrWhiteSpace(selector) || (variable && !Regex.IsMatch(selector, @"\A[A-Za-z_][A-Za-z0-9_]*\z"))) throw new WwpTabException("InvalidGridColumnSelector", "A valid column identity is required; variable names do not include &.");
            if (args["caption"] != null && string.IsNullOrWhiteSpace(args["caption"].ToString())) throw new WwpTabException("MissingCaption", "caption cannot be empty.");
            if (args["before"] != null && string.IsNullOrWhiteSpace(args["before"].ToString())) throw new WwpTabException("InvalidGridColumnSelector", "before cannot be empty.");
            if (operation == "move_grid_column" && args["before"] == null && args["caption"] == null) throw new WwpTabException("InvalidGridColumnMove", "before or caption is required.");
            if (operation == "add_grid_variable")
            {
                if (!variable || args["caption"] == null) throw new WwpTabException("MissingCaption", "variable and caption are required.");
                string basicType = args["basicType"]?.ToString();
                if (basicType != "VarChar" && basicType != "Character") throw new WwpTabException("InvalidVariableType", "Presentation variables support explicit basicType VarChar or Character.");
                if (args["length"]?.Type != JTokenType.Integer || !int.TryParse(args["length"].ToString(), out int length) || length < 1 || length > 9999) throw new WwpTabException("InvalidVariableLength", "length must be an integer from 1 to 9999.");
            }
        }

        private static T ResolveGridPath<T>(T root, string path, Func<T, string> type, Func<T, List<T>> children) where T : class
        {
            if (root == null) throw new WwpTabException("GridNotFound", "PatternInstance root is unavailable.");
            if (!Regex.IsMatch(path ?? "", @"\A/[A-Za-z_][A-Za-z0-9_]*(\[[0-9]+\])?(/[A-Za-z_][A-Za-z0-9_]*(\[[0-9]+\])?)*\z")) throw new WwpTabException("InvalidGridPath", "gridPath must be an absolute type path with optional zero-based indices.");
            T cursor = root;
            string[] segments = path.Substring(1).Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                Match match = Regex.Match(segments[i], @"\A(?<type>[A-Za-z_][A-Za-z0-9_]*)(\[(?<index>[0-9]+)\])?\z");
                List<T> candidates = (i == 0 ? new List<T> { root } : children(cursor)).Where(e => string.Equals(type(e), match.Groups["type"].Value, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Groups["index"].Success)
                {
                    if (!int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= candidates.Count) throw new WwpTabException("GridNotFound", "gridPath index is outside its matching children.");
                    cursor = candidates[index];
                }
                else
                {
                    if (candidates.Count != 1) throw new WwpTabException(candidates.Count == 0 ? "GridNotFound" : "AmbiguousGrid", "Each unindexed gridPath segment must match exactly one element.");
                    cursor = candidates[0];
                }
            }
            if (!string.Equals(type(cursor), "grid", StringComparison.OrdinalIgnoreCase)) throw new WwpTabException("InvalidGridPath", "gridPath must resolve to a grid.");
            return cursor;
        }

        private static T FindGridColumn<T>(List<T> children, string selector, bool? variable, Func<T, string> type, Func<T, string, string> attribute) where T : class
        {
            List<T> matches = children.Where(e =>
                (variable != true && string.Equals(type(e), "gridAttribute", StringComparison.OrdinalIgnoreCase) && IsExactGridAttributeReference(attribute(e, "attribute"), selector)) ||
                (variable != false && string.Equals(type(e), "gridVariable", StringComparison.OrdinalIgnoreCase) && string.Equals(attribute(e, "name"), selector, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count > 1) throw new WwpTabException("AmbiguousGridColumn", "The column selector matched multiple bindings; use its exact identity.");
            return matches.SingleOrDefault();
        }

        private static bool IsExactGridAttributeReference(string reference, string selector)
        {
            if (string.Equals(reference, selector, StringComparison.OrdinalIgnoreCase)) return true;
            return reference != null && reference.Length > 37 && reference[36] == '-' && Guid.TryParse(reference.Substring(0, 36), out _)
                && string.Equals(reference.Substring(37), selector, StringComparison.OrdinalIgnoreCase);
        }

        internal static JObject VerifyGridColumnXml(XDocument before, XDocument expected, XDocument persisted, string operation, JObject args)
        {
            if (before == null || expected == null || persisted == null) return GridError("WwpGridColumnNotPersisted", "An independent PatternInstance read is required.");
            var actual = new XDocument(persisted);
            if (operation == "add_grid_variable")
            {
                try
                {
                    XElement expectedGrid = ResolveGridPath(expected.Root, args["gridPath"].ToString(), e => e.Name.LocalName, e => e.Elements().ToList());
                    XElement actualGrid = ResolveGridPath(actual.Root, args["gridPath"].ToString(), e => e.Name.LocalName, e => e.Elements().ToList());
                    XElement requested = FindGridColumn(expectedGrid.Elements().ToList(), args["variable"].ToString(), true, e => e.Name.LocalName, Attr);
                    XElement saved = FindGridColumn(actualGrid.Elements().ToList(), args["variable"].ToString(), true, e => e.Name.LocalName, Attr);
                    if (requested == null || saved == null || saved.Elements().Any() || requested.Attributes().Any(a => (string)saved.Attribute(a.Name) != a.Value)) return GridError("WwpGridColumnNotPersisted", "Variable binding, caption or type does not match the requested state.");
                    // Only defaults materialized by the SDK on the NEW node may differ.
                    if (saved.Attributes().Any(a => requested.Attribute(a.Name) == null && !a.Name.LocalName.StartsWith("default", StringComparison.OrdinalIgnoreCase))) return GridError("WwpGridColumnNotPersisted", "The new variable contains an unexpected non-default property.");
                    foreach (XAttribute a in saved.Attributes().Where(a => requested.Attribute(a.Name) == null).ToList()) a.Remove();
                }
                catch (WwpTabException ex) { return GridError(ex.Code, ex.Message); }
            }
            return XNode.DeepEquals(GridComparable(expected.Root), GridComparable(actual.Root))
                ? new JObject { ["matches"] = true }
                : GridError("WwpGridColumnNotPersisted", "PatternInstance differs from the requested isolated column change; unrelated metadata must remain unchanged.");
        }

        private static XElement GridComparable(XElement value) => new XElement(value.Name,
            value.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a)),
            value.Nodes().Where(n => !(n is XText t) || !string.IsNullOrWhiteSpace(t.Value)).Select(n => n is XElement e ? GridComparable(e) : n));

        private static JObject ApplyNativeGridColumnMutation(KBObjectPart part, string operation, JObject args)
        {
            ValidateGridColumnRequest(operation, args);
            object grid = ResolveGridPath(GetProperty(part, "RootElement"), args["gridPath"].ToString(), NativeType, NativeChildren);
            bool add = operation == "add_grid_variable";
            string selector = (args["attribute"] ?? args["variable"]).ToString();
            object column = FindGridColumn(NativeChildren(grid), selector, args["variable"] != null, NativeType, NativeAttribute);
            object anchor = args["before"] == null ? null : FindGridColumn(NativeChildren(grid), args["before"].ToString(), null, NativeType, NativeAttribute);
            if ((!add && column == null) || (args["before"] != null && anchor == null)) throw new WwpTabException("GridColumnNotFound", "Native column identity differs from the planned state.");
            if (add && column != null) throw new WwpTabException("VariableAlreadyExists", "Native variable already exists.");
            if (column != null && ReferenceEquals(column, anchor)) throw new WwpTabException("InvalidGridColumnMove", "A column cannot be moved before itself.");
            Action mutation = () =>
            {
                if (add)
                {
                    column = CreateNativeChild(grid, "gridVariable");
                    SetNativeAttribute(column, "name", selector);
                    SetNativeAttribute(column, "dataType", "Basic");
                    SetNativeAttribute(column, "basicType", args["basicType"].ToString());
                    SetNativeAttribute(column, "basicCLength", args["length"].ToString());
                    SetNativeAttribute(column, "readOnly", "True");
                    SetNativeAttribute(column, "description", args["caption"].ToString());
                    if (anchor == null) ExecuteElementCommand(grid, column, "AddElementCommand", null);
                    else ExecuteElementCommand(grid, column, "InsertElementCommand", NativeChildren(grid).IndexOf(anchor));
                }
                else
                {
                    if (anchor != null)
                    {
                        var children = NativeChildren(grid);
                        int oldIndex = children.IndexOf(column), newIndex = children.IndexOf(anchor);
                        if (oldIndex < newIndex) newIndex--;
                        if (oldIndex != newIndex) ExecuteMoveCommand(grid, column, oldIndex, newIndex);
                    }
                    if (args["caption"] != null) SetNativeAttribute(column, "description", args["caption"].ToString());
                }
            };
            var update = part.GetType().GetMethod("ExecuteUpdate", new[] { typeof(string), typeof(Action) });
            if (update == null) throw new WwpTabException("WwpNativeUpdateUnavailable", "Native ExecuteUpdate is required; no mutation was applied.");
            update.Invoke(part, new object[] { "genexus_wwp " + operation, mutation });
            return new JObject { ["changed"] = true };
        }
    }
}
