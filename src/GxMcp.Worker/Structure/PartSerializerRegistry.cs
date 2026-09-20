using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Structure
{
    public interface IPartSerializer
    {
        string PartName { get; }
        Guid PartId { get; }
        bool CanSerialize(string partName);
        string Serialize(KBObject obj, KBObjectPart part, int offset = 0, int limit = 0);
    }

    public sealed class SourcePartSerializer : IPartSerializer
    {
        private static readonly HashSet<string> HandledParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Source", "Events", "Rules", "Conditions", "Code"
        };

        public string PartName => "Source";
        public Guid PartId => Guid.Empty;

        public bool CanSerialize(string partName)
        {
            return !string.IsNullOrWhiteSpace(partName) && HandledParts.Contains(partName.Trim());
        }

        public string Serialize(KBObject obj, KBObjectPart part, int offset = 0, int limit = 0)
        {
            if (part == null) return string.Empty;
            string rawSource = null;
            try
            {
                var srcProp = part.GetType().GetProperty("Source");
                if (srcProp != null)
                {
                    rawSource = srcProp.GetValue(part, null)?.ToString();
                }
            }
            catch { }

            if (rawSource == null)
            {
                try { rawSource = part.ToString(); } catch { rawSource = string.Empty; }
            }

            if (offset > 0 || limit > 0)
            {
                var lines = rawSource.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                int take = limit > 0 ? limit : lines.Length - offset;
                var slice = lines.Skip(offset).Take(take);
                return string.Join("\r\n", slice);
            }

            return rawSource;
        }
    }

    public sealed class WebFormPartSerializer : IPartSerializer
    {
        private static readonly HashSet<string> HandledParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WebForm", "Layout"
        };

        public string PartName => "WebForm";
        public Guid PartId => Guid.Empty;

        public bool CanSerialize(string partName)
        {
            return !string.IsNullOrWhiteSpace(partName) && HandledParts.Contains(partName.Trim());
        }

        public string Serialize(KBObject obj, KBObjectPart part, int offset = 0, int limit = 0)
        {
            if (obj == null) return string.Empty;
            try
            {
                return WebFormXmlHelper.ReadEditableXml(obj) ?? string.Empty;
            }
            catch (Exception ex)
            {
                return $"<!-- Failed to read visual WebForm XML: {ex.Message} -->";
            }
        }
    }

    public sealed class VariablesPartSerializer : IPartSerializer
    {
        public string PartName => "Variables";
        public Guid PartId => Guid.Empty;

        public bool CanSerialize(string partName)
        {
            return string.Equals(partName, "Variables", StringComparison.OrdinalIgnoreCase);
        }

        public string Serialize(KBObject obj, KBObjectPart part, int offset = 0, int limit = 0)
        {
            if (part == null) return string.Empty;
            try
            {
                var vp = part as VariablesPart;
                if (vp != null && vp.Variables != null)
                {
                    var lines = vp.Variables.Cast<object>().Select(v => v.ToString());
                    return string.Join("\r\n", lines);
                }
            }
            catch { }
            return string.Empty;
        }
    }

    /// <summary>
    /// Deep registry providing polymorphic serialization across all GeneXus KBObject parts.
    /// Eliminates hardcoded part-handling switch statements.
    /// </summary>
    public static class PartSerializerRegistry
    {
        private static readonly List<IPartSerializer> _serializers = new List<IPartSerializer>();
        private static readonly ConcurrentDictionary<string, IPartSerializer> _namedSerializers =
            new ConcurrentDictionary<string, IPartSerializer>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<Guid, IPartSerializer> _guidSerializers =
            new ConcurrentDictionary<Guid, IPartSerializer>();

        static PartSerializerRegistry()
        {
            Register(new SourcePartSerializer());
            Register(new WebFormPartSerializer());
            Register(new VariablesPartSerializer());
        }

        public static void Register(IPartSerializer serializer)
        {
            if (serializer == null) return;
            _serializers.Add(serializer);

            if (!string.IsNullOrWhiteSpace(serializer.PartName))
            {
                _namedSerializers[serializer.PartName] = serializer;
            }
            if (serializer.PartId != Guid.Empty)
            {
                _guidSerializers[serializer.PartId] = serializer;
            }
        }

        public static IPartSerializer Find(string partName)
        {
            if (string.IsNullOrWhiteSpace(partName)) return null;
            if (_namedSerializers.TryGetValue(partName, out var serializer)) return serializer;

            var match = _serializers.FirstOrDefault(s => s.CanSerialize(partName));
            if (match != null)
            {
                _namedSerializers[partName] = match;
                return match;
            }
            return null;
        }

        public static IPartSerializer Find(Guid partId)
        {
            if (partId == Guid.Empty) return null;
            _guidSerializers.TryGetValue(partId, out var serializer);
            return serializer;
        }

        public static bool IsRegistered(string partName)
        {
            return Find(partName) != null;
        }

        public static string Serialize(KBObject obj, string partName, KBObjectPart part = null, int offset = 0, int limit = 0)
        {
            var serializer = Find(partName);
            if (serializer == null) return null;
            return serializer.Serialize(obj, part, offset, limit);
        }
    }
}
