using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxMcp.Shared
{
    /// <summary>Parses protocol input without coercing ISO-8601 strings into JSON dates.</summary>
    internal static class JsonIngress
    {
        public static JObject ParseObject(string json)
        {
            using (var textReader = new StringReader(json))
            using (var jsonReader = new JsonTextReader(textReader)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double
            })
            {
                return JObject.Load(jsonReader);
            }
        }
    }
}
