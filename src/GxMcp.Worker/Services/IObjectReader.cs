using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GxMcp.Worker.Services
{
    public class ObjectReadRequest
    {
        public string Target { get; set; } = string.Empty;
        public string PartName { get; set; }
        public IEnumerable<string> RequestedParts { get; set; }
        public int? Offset { get; set; }
        public int? Limit { get; set; }
        public string ClientFormat { get; set; } = "mcp";
        public bool Minimize { get; set; }
        public string TypeFilter { get; set; }
        public bool FullObject { get; set; }
        public JArray BatchTargets { get; set; }
    }

    public class ObjectReadResult
    {
        public bool Success { get; set; }
        public string Target { get; set; } = string.Empty;
        public string Part { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public bool Truncated { get; set; }
        public int TotalLines { get; set; }
        public int TotalBytes { get; set; }
        public int? NextOffset { get; set; }
        public string ContentVersionToken { get; set; } = string.Empty;
        public string RawJson { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
    }

    public interface IObjectReader
    {
        string Read(ObjectReadRequest request);
        void Invalidate(string target, string part = null);
        bool TryGetCached(string target, string part, int? offset, int? limit, string client, out string cachedJson);
    }
}
