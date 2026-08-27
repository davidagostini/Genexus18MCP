using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using Artech.Architecture.Common.Objects;
using Artech.Genexus.Common.Parts;

namespace GxMcp.Worker.Services
{
    public interface IVisualSurfaceAdapter
    {
        string SurfaceKind { get; }
        bool SupportsObject(string objectType, string partName);
        JObject ReadVisualTree(string target, string partName, string typeFilter);
        bool ApplyVisualEdits(string target, string partName, JObject editPayload, out string error);
    }

    public sealed class WebFormSurfaceAdapter : IVisualSurfaceAdapter
    {
        private readonly UIService _uiService;
        private readonly ObjectService _objectService;

        public string SurfaceKind => "WebForm";

        public WebFormSurfaceAdapter(UIService uiService, ObjectService objectService)
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

        public bool ApplyVisualEdits(string target, string partName, JObject editPayload, out string error)
        {
            error = null;
            return true;
        }
    }

    public sealed class ReportLayoutSurfaceAdapter : IVisualSurfaceAdapter
    {
        private readonly ObjectService _objectService;

        public string SurfaceKind => "ReportLayout";

        public ReportLayoutSurfaceAdapter(ObjectService objectService)
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

        public bool ApplyVisualEdits(string target, string partName, JObject editPayload, out string error)
        {
            error = null;
            return true;
        }
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

        public bool ApplyVisualEdits(string target, string partName, JObject editPayload, out string error)
        {
            error = null;
            return true;
        }
    }
}