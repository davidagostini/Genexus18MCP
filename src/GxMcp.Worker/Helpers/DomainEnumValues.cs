using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Helpers
{
    /// <summary>
    /// Maps a JSON enumValues array to DomainEnumValueSpecs. ISSUE-55 ground truth
    /// (2026-07-31, GeneXus 18.0.10): enum values are stored RAW in the version XML for
    /// every data family — verified against the template's own character enum (HttpMethod
    /// stores &lt;Value&gt;GET&lt;/Value&gt;, unquoted) — so values pass through verbatim.
    ///
    /// The write is gated by the SDK's EnumValuesValidResolver: a set where two values
    /// share a description (an empty one included) is rejected and the whole enum write
    /// silently no-ops ("empty combobox / enum not persisted"). Description therefore
    /// defaults to the value's Name — names are unique by SDK validation, so
    /// name-defaulted descriptions are unique too.
    /// </summary>
    public static class DomainEnumValues
    {
        public static List<DomainEnumValueSpec> FromJson(JArray enumArr)
        {
            var specs = new List<DomainEnumValueSpec>();
            if (enumArr == null) return specs;
            foreach (var item in enumArr)
            {
                if (!(item is JObject jo)) continue;
                string name = jo["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                string description = jo["description"]?.ToString();
                if (string.IsNullOrEmpty(description)) description = name;
                specs.Add(new DomainEnumValueSpec
                {
                    Name = name,
                    Value = jo["value"]?.ToString(),
                    Description = description
                });
            }
            return specs;
        }
    }
}
