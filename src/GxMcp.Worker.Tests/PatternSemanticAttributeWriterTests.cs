using System;
using System.Collections.Generic;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class PatternSemanticAttributeWriterTests
    {
        [Fact]
        public void ApplyGxObjectAttributes_UsesPatternChangeCommandWhenXmlDroppedIt()
        {
            var action = new FakeElement("userAction", "Run");
            var group = new FakeElement("actionGroup", null);
            group.Children.Add(action);
            var root = new FakeElement("instance", null);
            root.Children.Add(group);

            int applied = PatternSemanticAttributeWriter.ApplyGxObjectAttributes(
                new FakePart(root),
                "<instance><actionGroup><userAction name='Run' gxobject='Proc.Run'/></actionGroup></instance>");

            Assert.Equal(1, applied);
            Assert.Equal("Proc.Run", action.Attributes.GetPropertyValueString("gxobject"));
        }

        private sealed class FakePart
        {
            public FakePart(FakeElement root) { RootElement = root; }
            public FakeElement RootElement { get; }
        }

        private sealed class FakeElement
        {
            public FakeElement(string type, string name)
            {
                Type = type;
                Name = name;
            }

            public string Type { get; }
            public string Name { get; }
            public FakeAttributes Attributes { get; } = new FakeAttributes();
            public List<FakeElement> Children { get; } = new List<FakeElement>();
        }

        private sealed class FakeAttributes
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public object GetPropertyValue(string name)
            {
                return GetPropertyValueString(name);
            }

            public string GetPropertyValueString(string name)
            {
                return _values.TryGetValue(name, out string value) ? value : null;
            }

            public void SetPropertyValue(string name, object value)
            {
                _values[name] = value?.ToString();
            }
        }
    }
}

namespace Artech.Packages.Patterns.Objects
{
    internal sealed class ChangeAttributeValueCommand
    {
        private readonly object _element;
        private readonly string _attributeName;
        private readonly object _newValue;

        public ChangeAttributeValueCommand(object element, string attributeName, object oldValue, object newValue)
        {
            _element = element;
            _attributeName = attributeName;
            _newValue = newValue;
        }

        public bool IsSafeToExecute() => true;

        public void Execute()
        {
            object attributes = _element.GetType().GetProperty("Attributes").GetValue(_element, null);
            attributes.GetType().GetMethod("SetPropertyValue", new[] { typeof(string), typeof(object) })
                .Invoke(attributes, new object[] { _attributeName, _newValue });
        }
    }
}
