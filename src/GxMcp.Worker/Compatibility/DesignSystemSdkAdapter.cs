using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Structure;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Compatibility
{
    internal sealed class DesignSystemReadResult
    {
        internal JObject TokenGroups { get; set; }
        internal JArray Classes { get; set; }
        internal JArray Images { get; set; }
        internal JArray ReferencedDSOs { get; set; }
        internal string Source { get; set; }
        internal JObject Compatibility { get; set; }
        internal JArray Warnings { get; set; }
    }

    /// <summary>
    /// Reads the Design System helper when its members exist and fills each missing member
    /// independently from the two native source parts. A method removed or renamed by a
    /// future SDK therefore degrades only that field instead of taking down the tool.
    /// </summary>
    internal static class DesignSystemSdkAdapter
    {
        private const string HelperTypeName =
            "Artech.Genexus.Common.Helpers.DesignSystemHelper";

        internal static DesignSystemReadResult Read(KBObject dso)
        {
            SourceParts parts = ReadSourceParts(dso);
            DesignSystemSourceParseResult parsed =
                DesignSystemSourceParser.Parse(parts.Tokens, parts.Styles);

            JObject tokenGroups = ToTokenGroups(parsed.TokenGroups);
            JArray classes = ToArray(parsed.Classes);
            JArray images = ToArray(parsed.Images);
            JArray referencedDsos = ToArray(parsed.ReferencedDSOs);
            var fallbacks = new List<string>();
            var warnings = new JArray();

            object helper;
            string helperError;
            bool helperAvailable = OptionalSdkInvoker.TryCreate(
                HelperTypeName, dso, out helper, out helperError);

            if (helperAvailable)
            {
                OptionalSdkInvocation tokens =
                    OptionalSdkInvoker.InvokeNoArgs(helper, "GetTokensNames");
                if (tokens.Succeeded) tokenGroups = ToTokenGroups(tokens.Value);
                else
                {
                    fallbacks.Add("GetTokensNames");
                    AddInvocationWarning(warnings, "GetTokensNames", tokens);
                }

                classes = ReadOptionalArray(
                    helper, "GetClassesNames", classes, fallbacks, warnings);
                images = ReadOptionalArray(
                    helper, "GetAllImagesNames", images, fallbacks, warnings);
                referencedDsos = ReadOptionalArray(
                    helper, "GetAllDSOsNames", referencedDsos, fallbacks, warnings);
            }
            else
            {
                fallbacks.Add("DesignSystemHelper");
                if (!string.IsNullOrWhiteSpace(helperError))
                    warnings.Add("DesignSystemHelper unavailable: " + helperError);
            }

            bool usedSourceFallback = fallbacks.Count > 0;
            string source;
            if (!usedSourceFallback)
                source = "sdk:DesignSystemHelper";
            else if (helperAvailable)
                source = "sdk:DesignSystemHelper+source-parser";
            else
                source = "source:DesignSystemParts";

            var sourceParts = new JArray();
            if (!string.IsNullOrWhiteSpace(parts.Tokens)) sourceParts.Add("Tokens");
            if (!string.IsNullOrWhiteSpace(parts.Styles)) sourceParts.Add("Styles");

            foreach (string warning in parsed.Warnings) warnings.Add(warning);
            if (!string.IsNullOrWhiteSpace(parts.TokensError))
                warnings.Add("Could not read Tokens source part: " + parts.TokensError);
            if (!string.IsNullOrWhiteSpace(parts.StylesError))
                warnings.Add("Could not read Styles source part: " + parts.StylesError);

            var compatibility = new JObject
            {
                ["sdkHelperAvailable"] = helperAvailable,
                ["sourceParts"] = sourceParts,
                ["fallbacks"] = new JArray(fallbacks),
                ["completeness"] = parsed.Completeness,
                ["warnings"] = warnings,
                ["unparsedConstructs"] = new JArray(parsed.UnparsedConstructs)
            };

            return new DesignSystemReadResult
            {
                TokenGroups = tokenGroups,
                Classes = classes,
                Images = images,
                ReferencedDSOs = referencedDsos,
                Source = source,
                Compatibility = compatibility,
                Warnings = warnings
            };
        }

        private static void AddInvocationWarning(
            JArray warnings,
            string methodName,
            OptionalSdkInvocation invocation)
        {
            if (!string.IsNullOrWhiteSpace(invocation.Error))
                warnings.Add(methodName + " failed: " + invocation.Error);
        }

        private static JArray ReadOptionalArray(
            object helper,
            string methodName,
            JArray fallback,
            List<string> fallbacks,
            JArray warnings)
        {
            OptionalSdkInvocation invocation =
                OptionalSdkInvoker.InvokeNoArgs(helper, methodName);
            if (invocation.Succeeded) return ToArray(invocation.Value);

            fallbacks.Add(methodName);
            AddInvocationWarning(warnings, methodName, invocation);
            return fallback;
        }

        private static SourceParts ReadSourceParts(KBObject dso)
        {
            string tokens = ReadSource(dso, styles: false, out string tokensError);
            string styles = ReadSource(dso, styles: true, out string stylesError);
            return new SourceParts
            {
                Tokens = tokens,
                Styles = styles,
                TokensError = tokensError,
                StylesError = stylesError
            };
        }

        private static string ReadSource(KBObject dso, bool styles, out string error)
        {
            error = null;
            try
            {
                var part = PartAccessor.GetDesignSystemPart(dso, styles) as ISource;
                return part?.Source ?? string.Empty;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return string.Empty;
            }
        }

        private static JObject ToTokenGroups(Dictionary<string, JObject> groups)
        {
            var result = new JObject();
            foreach (KeyValuePair<string, JObject> group in groups)
                result[group.Key] = ToArray(group.Value.Properties().Select(p => p.Name));
            return result;
        }

        private static JObject ToTokenGroups(object value)
        {
            var result = new JObject();
            foreach (KeyValuePair<string, object> entry in GetMapEntries(value))
                result[entry.Key] = ToArray(entry.Value);
            return result;
        }

        private static IEnumerable<KeyValuePair<string, object>> GetMapEntries(object value)
        {
            if (value == null) yield break;

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                foreach (DictionaryEntry entry in dictionary)
                    if (entry.Key != null)
                        yield return new KeyValuePair<string, object>(entry.Key.ToString(), entry.Value);
                yield break;
            }

            var enumerable = value as IEnumerable;
            if (enumerable == null) yield break;

            foreach (object entry in enumerable)
            {
                if (entry == null) continue;
                PropertyInfo key = entry.GetType().GetProperty("Key");
                PropertyInfo item = entry.GetType().GetProperty("Value");
                if (key == null || item == null) continue;
                object keyValue = key.GetValue(entry, null);
                if (keyValue != null)
                    yield return new KeyValuePair<string, object>(
                        keyValue.ToString(), item.GetValue(entry, null));
            }
        }

        private static JArray ToArray(object value)
        {
            var result = new JArray();
            if (value == null) return result;

            if (value is string)
            {
                result.Add(value.ToString());
                return result;
            }

            var dictionary = value as IDictionary;
            if (dictionary != null)
            {
                foreach (object key in dictionary.Keys)
                    if (key != null) result.Add(key.ToString());
                return result;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                foreach (object item in enumerable)
                    if (item != null) result.Add(item.ToString());
                return result;
            }

            result.Add(value.ToString());
            return result;
        }

        private static JArray ToArray(IEnumerable<string> values)
        {
            var result = new JArray();
            if (values == null) return result;
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            return result;
        }

        private sealed class SourceParts
        {
            internal string Tokens { get; set; }
            internal string Styles { get; set; }
            internal string TokensError { get; set; }
            internal string StylesError { get; set; }
        }
    }
}
