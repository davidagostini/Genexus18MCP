using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Artech.Architecture.Common.Objects;

namespace GxMcp.Worker.Structure
{
    public interface IPartSerializer
    {
        string PartName { get; }
        Guid PartId { get; }
        string Serialize(KBObject obj, KBObjectPart part, int offset = 0, int limit = 0);
    }

    /// <summary>
    /// Deep registry providing polymorphic serialization across all GeneXus KBObject parts.
    /// </summary>
    public class PartSerializerRegistry
    {
        private static readonly ConcurrentDictionary<string, IPartSerializer> _namedSerializers =
            new ConcurrentDictionary<string, IPartSerializer>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<Guid, IPartSerializer> _guidSerializers =
            new ConcurrentDictionary<Guid, IPartSerializer>();

        public static void Register(IPartSerializer serializer)
        {
            if (serializer == null) return;
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
            _namedSerializers.TryGetValue(partName, out var serializer);
            return serializer;
        }

        public static IPartSerializer Find(Guid partId)
        {
            if (partId == Guid.Empty) return null;
            _guidSerializers.TryGetValue(partId, out var serializer);
            return serializer;
        }

        public static bool IsRegistered(string partName)
        {
            return !string.IsNullOrWhiteSpace(partName) && _namedSerializers.ContainsKey(partName);
        }
    }
}
