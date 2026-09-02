using System;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public enum VariableKind
    {
        Primitive,
        Domain,
        DottedSdtItem,
        Sdt,
        BusinessComponent,
        GeneXusBuiltIn,
        Unrecognized
    }

    public class TypeBindingResult
    {
        public bool Success { get; set; }
        public VariableKind Kind { get; set; }
        public string CanonicalType { get; set; } = string.Empty;
        public int? Length { get; set; }
        public int? Decimals { get; set; }
        public bool IsCollection { get; set; }
        public string TargetReferenceName { get; set; } = string.Empty;
        public string ErrorMessage { get; set; }
        public string SuggestedAlternative { get; set; }
    }

    public interface ITypeBindingEngine
    {
        TypeBindingResult Bind(string typeSpec, bool? isCollection = null, int? explicitLength = null, int? explicitDecimals = null);
        bool IsFrameworkProtected(string variableName, out string protectedBy);
        bool ShouldSkipUnusedCheck(string variableName);
    }
}
