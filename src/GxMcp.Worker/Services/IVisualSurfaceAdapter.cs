using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public sealed class VisualSurfaceMutationResult
    {
        public bool Success { get; set; }
        public string MergedXml { get; set; }
        public string Error { get; set; }
        public List<string> TouchedControls { get; set; } = new List<string>();

        public bool ColorsEquivalent(string c1, string c2) => ColorHelper.IsColorEquivalent(c1, c2);
    }

    public interface IVisualSurfaceAdapter
    {
        string SurfaceKind { get; }
        bool SupportsObject(string objectType, string partName);
        JObject ReadVisualTree(string target, string partName, string typeFilter);
        VisualSurfaceMutationResult Mutate(string baselineXml, string requestedXml);
        bool ColorsEquivalent(string color1, string color2);
    }

    public sealed class WebFormSurfaceAdapter : IVisualSurfaceAdapter
    {
        private readonly UIService _uiService;
        private readonly ObjectService _objectService;

        public string SurfaceKind => "WebForm";

        public WebFormSurfaceAdapter(UIService uiService = null, ObjectService objectService = null)
        {
            _uiService = uiService;
            _objectService = objectService;
        }

        public bool SupportsObject(string objectType, string partName)
        {
            return string.Equals(partName, "WebForm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Layout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(objectType, "WebPanel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(objectType, "Transaction", StringComparison.OrdinalIgnoreCase);
        }

        public JObject ReadVisualTree(string target, string partName, string typeFilter)
        {
            if (_uiService == null || _objectService == null) return new JObject();
            var obj = _objectService.FindObject(target, typeFilter);
            if (obj == null) return new JObject { ["error"] = "ObjectNotFound" };

            var part = obj.Parts?.Get<WebFormPart>();
            return _uiService.GetSimplifiedUIStructure(obj, part);
        }

        public VisualSurfaceMutationResult Mutate(string baselineXml, string requestedXml)
        {
            var res = new VisualSurfaceMutationResult();
            try
            {
                if (string.IsNullOrWhiteSpace(requestedXml))
                {
                    res.Success = false;
                    res.Error = "Requested visual XML is empty";
                    return res;
                }

                if (string.IsNullOrWhiteSpace(baselineXml))
                {
                    res.Success = true;
                    res.MergedXml = requestedXml;
                    return res;
                }

                var baseDoc = XDocument.Parse(baselineXml);
                var reqDoc = XDocument.Parse(requestedXml);

                // Preserve untouched elements from baselineDoc if container exists
                MergeElements(baseDoc.Root, reqDoc.Root, res.TouchedControls);

                res.Success = true;
                res.MergedXml = reqDoc.ToString();
                return res;
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Error = ex.Message;
                return res;
            }
        }

        private void MergeElements(XElement baseElem, XElement reqElem, List<string> touched)
        {
            if (baseElem == null || reqElem == null) return;

            string reqId = reqElem.Attribute("id")?.Value ?? reqElem.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(reqId) && !touched.Contains(reqId))
            {
                touched.Add(reqId);
            }

            var allReqIds = new HashSet<string>(
                reqElem.Descendants()
                       .Select(e => e.Attribute("id")?.Value ?? e.Attribute("Name")?.Value)
                       .Where(id => !string.IsNullOrEmpty(id)),
                StringComparer.OrdinalIgnoreCase
            );

            var baseList = baseElem.Elements().ToList();
            var reqList = reqElem.Elements().ToList();

            for (int i = 0; i < baseList.Count; i++)
            {
                var baseChild = baseList[i];
                string baseChildId = baseChild.Attribute("id")?.Value ?? baseChild.Attribute("Name")?.Value;

                if (!string.IsNullOrEmpty(baseChildId))
                {
                    if (!allReqIds.Contains(baseChildId))
                    {
                        reqElem.Add(new XElement(baseChild));
                    }
                    else
                    {
                        var match = reqElem.Descendants()
                            .FirstOrDefault(e => string.Equals(e.Attribute("id")?.Value ?? e.Attribute("Name")?.Value, baseChildId, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            MergeElements(baseChild, match, touched);
                        }
                    }
                }
                else
                {
                    // Structural tag without ID (TR, TD, TABLE, etc.)
                    var matchingTag = i < reqList.Count && reqList[i].Name == baseChild.Name
                        ? reqList[i]
                        : reqList.FirstOrDefault(e => e.Name == baseChild.Name);

                    if (matchingTag != null)
                    {
                        MergeElements(baseChild, matchingTag, touched);
                    }
                    else
                    {
                        reqElem.Add(new XElement(baseChild));
                    }
                }
            }
        }

        public bool ColorsEquivalent(string color1, string color2) => ColorHelper.IsColorEquivalent(color1, color2);
    }

    public sealed class ReportLayoutSurfaceAdapter : IVisualSurfaceAdapter
    {
        private readonly ObjectService _objectService;

        public string SurfaceKind => "ReportLayout";

        public ReportLayoutSurfaceAdapter(ObjectService objectService = null)
        {
            _objectService = objectService;
        }

        public bool SupportsObject(string objectType, string partName)
        {
            return string.Equals(partName, "Report", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "LayoutPart", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "MyLayout", StringComparison.OrdinalIgnoreCase)
                || string.Equals(objectType, "Procedure", StringComparison.OrdinalIgnoreCase);
        }

        public JObject ReadVisualTree(string target, string partName, string typeFilter)
        {
            return new JObject
            {
                ["surface"] = "ReportLayout",
                ["target"] = target,
                ["status"] = "ok"
            };
        }

        public VisualSurfaceMutationResult Mutate(string baselineXml, string requestedXml)
        {
            var res = new VisualSurfaceMutationResult();
            try
            {
                if (string.IsNullOrWhiteSpace(requestedXml))
                {
                    res.Success = false;
                    res.Error = "Requested report layout XML is empty";
                    return res;
                }

                if (string.IsNullOrWhiteSpace(baselineXml))
                {
                    res.Success = true;
                    res.MergedXml = requestedXml;
                    return res;
                }

                var baseDoc = XDocument.Parse(baselineXml);
                var reqDoc = XDocument.Parse(requestedXml);

                // Preserve untouched PrintBlocks from baseline
                var reqBlockIds = new HashSet<string>(
                    reqDoc.Root.Elements()
                          .Select(e => e.Attribute("id")?.Value ?? e.Attribute("Name")?.Value)
                          .Where(id => !string.IsNullOrEmpty(id)),
                    StringComparer.OrdinalIgnoreCase
                );

                foreach (var baseBlock in baseDoc.Root.Elements())
                {
                    string blockId = baseBlock.Attribute("id")?.Value ?? baseBlock.Attribute("Name")?.Value;
                    if (!string.IsNullOrEmpty(blockId) && !reqBlockIds.Contains(blockId))
                    {
                        reqDoc.Root.Add(new XElement(baseBlock));
                    }
                }

                res.Success = true;
                res.MergedXml = reqDoc.ToString();
                return res;
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Error = ex.Message;
                return res;
            }
        }

        public bool ColorsEquivalent(string color1, string color2) => ColorHelper.IsColorEquivalent(color1, color2);
    }

    public sealed class DsoSurfaceAdapter : IVisualSurfaceAdapter
    {
        public string SurfaceKind => "DesignSystem";

        public bool SupportsObject(string objectType, string partName)
        {
            return string.Equals(partName, "Tokens", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partName, "Styles", StringComparison.OrdinalIgnoreCase)
                || string.Equals(objectType, "DesignSystem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(objectType, "DSO", StringComparison.OrdinalIgnoreCase);
        }

        public JObject ReadVisualTree(string target, string partName, string typeFilter)
        {
            return new JObject
            {
                ["surface"] = "DesignSystem",
                ["target"] = target,
                ["status"] = "ok"
            };
        }

        public VisualSurfaceMutationResult Mutate(string baselineXml, string requestedXml)
        {
            return new VisualSurfaceMutationResult
            {
                Success = true,
                MergedXml = requestedXml
            };
        }

        public bool ColorsEquivalent(string color1, string color2) => ColorHelper.IsColorEquivalent(color1, color2);
    }

    /// <summary>
    /// Deep Authoritative Visual Surface Domain module for GeneXus UI surfaces.
    /// Encapsulates visual DOM projection, baseline delta preservation, and semantic color equivalence.
    /// </summary>
    public sealed class VisualSurfaceDomain
    {
        private readonly List<IVisualSurfaceAdapter> _adapters = new List<IVisualSurfaceAdapter>();

        public VisualSurfaceDomain(UIService uiService = null, ObjectService objectService = null)
        {
            _adapters.Add(new WebFormSurfaceAdapter(uiService, objectService));
            _adapters.Add(new ReportLayoutSurfaceAdapter(objectService));
            _adapters.Add(new DsoSurfaceAdapter());
        }

        public IVisualSurfaceAdapter GetAdapter(string objectType, string partName)
        {
            return _adapters.FirstOrDefault(a => a.SupportsObject(objectType, partName));
        }

        public VisualSurfaceMutationResult Mutate(string objectType, string partName, string baselineXml, string requestedXml)
        {
            var adapter = GetAdapter(objectType, partName);
            if (adapter == null)
            {
                return new VisualSurfaceMutationResult
                {
                    Success = false,
                    Error = $"No visual surface adapter found for {objectType}:{partName}"
                };
            }
            return adapter.Mutate(baselineXml, requestedXml);
        }
    }
}