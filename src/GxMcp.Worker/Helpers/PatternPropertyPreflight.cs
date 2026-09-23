using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Artech.Packages.Patterns.Objects;

namespace GxMcp.Worker.Helpers
{
    internal static class PatternPropertyPreflight
    {
        internal static string UnsupportedAttribute(XElement before, XElement after, IEnumerable<string> supported)
        {
            var names = new HashSet<string>(supported, StringComparer.Ordinal);
            return after.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration
                && (string)before.Attribute(a.Name) != a.Value
                && !names.Contains(a.Name.LocalName))?.Name.ToString();
        }

        internal static string Validate(PatternInstanceElement node, XElement before, XElement after)
        {
            // Missing/dynamic schemas cannot certify property support; the normal
            // post-save receipt must still report any partial persistence.
            if (node?.Specification == null) return null;
            string unsupported = node.Specification.CustomAttributes || !string.IsNullOrEmpty(node.Specification.CustomAttributesFrom) ? null
                : UnsupportedAttribute(before, after, node.Specification.Attributes.Select(a => a.Name));
            if (unsupported != null) return before.Name + "@" + unsupported;
            foreach (var child in before.Elements())
            {
                string name = (string)child.Attribute("name");
                // Inspect only unambiguous SDK identities; serialization-only metadata
                // remains governed by PatternXmlEditPlan, not guessed schema rules.
                var native = node.Children.Cast<PatternInstanceElement>().Where(c => MatchesChild(c, child.Name.LocalName, name)).ToArray();
                var requested = after.Elements(child.Name).Where(c => (string)c.Attribute("name") == name).ToArray();
                if (native.Length != 1 || requested.Length != 1) continue;
                unsupported = Validate(native[0], child, requested[0]);
                if (unsupported != null) return unsupported;
            }
            return null;
        }

        internal static bool MatchesChild(PatternInstanceElement child, string elementName, string identity)
            => child?.Specification != null && string.Equals(child.Name, elementName, StringComparison.OrdinalIgnoreCase)
                && (identity == null || child.Specification.Attributes.FirstOrDefault(a => a.Name == "name")?.GetValueString(child) == identity);
    }
}
