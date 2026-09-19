using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using GxMcp.Worker.Models;
using GxMcp.Worker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using XmlReader = System.Xml.XmlReader;
using XmlReaderSettings = System.Xml.XmlReaderSettings;
using DtdProcessing = System.Xml.DtdProcessing;
using XmlException = System.Xml.XmlException;

namespace GxMcp.Worker.Services
{
    /// <summary>
    /// Batch Object Text exchange, matching the four high-value operations exposed by
    /// GeneXus for Agents: export_kb_to_text, import_text_to_kb,
    /// validate_kb_text_files and delete_kb_objects.
    ///
    /// The manifest is intentionally small and portable. Files are relative to the
    /// selected directory and are resolved beneath that directory on import, so a
    /// hand-edited manifest cannot escape its input root.
    /// </summary>
    public sealed partial class ObjectTextService
    {
        public const string ManifestFileName = "_gxmcp-object-text-manifest.json";
        private const string ManifestKind = "GeneXusObjectText";

        private readonly ObjectService _objectService;
        private readonly IndexCacheService _indexCacheService;
        private readonly TextTreeSelectionService _selectionService;
        private readonly SdkTextTreeService _sdkTextTreeService;

        public ObjectTextService(ObjectService objectService, IndexCacheService indexCacheService)
        {
            _objectService = objectService;
            _indexCacheService = indexCacheService;
            _selectionService = new TextTreeSelectionService(indexCacheService);
            _sdkTextTreeService = new SdkTextTreeService(_selectionService, objectService, indexCacheService);
        }

        public string Execute(string action, string target, JObject args, CancellationToken cancellationToken)
        {
            action = action ?? string.Empty;
            args = args ?? new JObject();
            if (SdkTextTreeService.IsNativeFormat(args)
                && action.IndexOf("memory", StringComparison.OrdinalIgnoreCase) < 0)
                return _sdkTextTreeService.Execute(action, target, args, cancellationToken);
            switch (action.ToLowerInvariant())
            {
                case "exporttextbatch":
                case "export_kb_to_text":
                    return Export(target, args, cancellationToken);
                case "importtextbatch":
                case "import_text_to_kb":
                    return Import(target, args, cancellationToken);
                case "validatetextbatch":
                case "validate_kb_text_files":
                    return Validate(target, args, cancellationToken);
                case "deletetextbatch":
                case "delete_kb_objects":
                    return Delete(target, args, cancellationToken);
                case "validate_text_in_memory":
                case "validatetextinmemory":
                    return ValidateInMemory(args, cancellationToken);
                case "list_text_in_memory":
                case "listtextinmemory":
                case "list_text_files":
                    return ListInMemory(args, cancellationToken);
                default:
                    return McpResponse.Err(
                        code: "UnknownObjectTextAction",
                        message: "Unknown Object Text action: " + action,
                        hint: "Use export_kb_to_text, import_text_to_kb, validate_kb_text_files or delete_kb_objects.");
            }
        }

    }
}
