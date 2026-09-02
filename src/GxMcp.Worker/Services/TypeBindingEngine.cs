using System;
using System.Text.RegularExpressions;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Deep authoritative engine implementing 6-tier variable type binding,
    /// domain reference resolution, framework protection rules, and type normalization.
    /// </summary>
    public class TypeBindingEngine : ITypeBindingEngine
    {
        private static readonly Regex DottedSdtItemRegex = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        public TypeBindingResult Bind(string typeSpec, bool? isCollection = null, int? explicitLength = null, int? explicitDecimals = null)
        {
            if (string.IsNullOrWhiteSpace(typeSpec))
            {
                return new TypeBindingResult
                {
                    Success = false,
                    Kind = VariableKind.Unrecognized,
                    ErrorMessage = "Type specification cannot be empty.",
                    SuggestedAlternative = "Character(40)"
                };
            }

            var trimmed = typeSpec.Trim();
            string normalized = Regex.Replace(trimmed, @"\(\s*(\d+)\s*([.,])\s*(\d+)\s*\)", "($1$2$3)");
            normalized = Regex.Replace(normalized, @"\(\s*(\d+)\s*\)", "($1)");
            var resolution = VariableTypeResolver.Resolve(normalized);

            if (!resolution.Recognized)
            {
                return new TypeBindingResult
                {
                    Success = false,
                    Kind = VariableKind.Unrecognized,
                    ErrorMessage = $"Unrecognized type specification '{trimmed}'.",
                    SuggestedAlternative = resolution.Suggestion
                };
            }

            var result = new TypeBindingResult
            {
                Success = true,
                IsCollection = isCollection ?? false,
                CanonicalType = resolution.CanonicalType,
                Length = explicitLength ?? resolution.Length,
                Decimals = explicitDecimals ?? resolution.Decimals
            };

            if (resolution.CanonicalType == "DomainReference")
            {
                string domainOrObject = resolution.DomainName ?? trimmed.TrimStart('&');
                result.TargetReferenceName = domainOrObject;

                if (DottedSdtItemRegex.IsMatch(domainOrObject))
                {
                    result.Kind = VariableKind.DottedSdtItem;
                }
                else if (domainOrObject.StartsWith("sdt", StringComparison.OrdinalIgnoreCase))
                {
                    result.Kind = VariableKind.Sdt;
                }
                else if (domainOrObject.EndsWith("_BC", StringComparison.OrdinalIgnoreCase) || domainOrObject.EndsWith("BC", StringComparison.OrdinalIgnoreCase))
                {
                    result.Kind = VariableKind.BusinessComponent;
                }
                else
                {
                    result.Kind = VariableKind.Domain;
                }
            }
            else
            {
                result.Kind = VariableKind.Primitive;
            }

            return result;
        }

        public bool IsFrameworkProtected(string variableName, out string protectedBy)
        {
            protectedBy = FrameworkManagedVariables.GetManagedBy(variableName);
            return protectedBy != null;
        }

        public bool ShouldSkipUnusedCheck(string variableName)
        {
            return FrameworkManagedVariables.ShouldSkipUnusedCheck(variableName);
        }
    }
}
