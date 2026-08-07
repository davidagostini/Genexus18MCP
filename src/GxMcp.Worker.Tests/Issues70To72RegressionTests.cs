using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Xunit;
using GxMcp.Worker.Services;
using GxMcp.Worker.Helpers;
using System.Reflection;
using System.Xml.Linq;

namespace GxMcp.Worker.Tests
{
    public class Issues70To72RegressionTests
    {
        [Fact]
        public void Issue70_WhitespaceInsensitiveEquals_IgnoresCase_ForProcedureSourceNormalization()
        {
            string requested = "for each Customer\n    where CustomerId = &CustomerId\n    Msg(\"Hello World\")\nendfor";
            string persisted = "For Each Customer\n    Where CustomerId = &CustomerId\n    Msg(\"Hello World\")\nEndfor";

            var method = typeof(WriteService).GetMethod("WhitespaceInsensitiveEquals", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            bool matches = (bool)method.Invoke(null, new object[] { persisted, requested });
            Assert.True(matches, "WhitespaceInsensitiveEquals should treat case-normalized procedure source as equivalent to avoid false WriteNotPersisted rollback.");
        }

        [Fact]
        public void Issue70_WhitespaceInsensitiveEquals_PreservesStringLiteralSpaces()
        {
            string requested = "Msg(\"Hello World\")";
            string modified = "Msg(\"HelloWorld\")";

            var method = typeof(WriteService).GetMethod("WhitespaceInsensitiveEquals", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            bool matches = (bool)method.Invoke(null, new object[] { modified, requested });
            Assert.False(matches, "WhitespaceInsensitiveEquals must NOT collapse spaces inside string literals.");
        }

        [Fact]
        public void Issue71_WhitespaceInsensitiveEquals_TreatsNormalizedXml_AsEquivalent()
        {
            string requested = "<PatternInstance defaultWidth=\"100\" defaultVisible=\"true\"><table name=\"Table1\"><tabularTab></tabularTab></table></PatternInstance>";
            string persisted = "<PatternInstance><table name=\"Table1\"><tabularTab/></table></PatternInstance>";

            var method = typeof(WriteService).GetMethod("WhitespaceInsensitiveEquals", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            bool matches = (bool)method.Invoke(null, new object[] { persisted, requested });
            Assert.True(matches, "WhitespaceInsensitiveEquals should treat XML with SDK-dropped default attributes as equivalent to avoid false WriteNotPersisted on PatternInstance.");
        }

        [Fact]
        public void Issue72_ReportLayoutHelper_TryParseColor_ParsesCommaSeparatedRGB()
        {
            var tryParseColor = typeof(ReportLayoutHelper).GetMethod("TryParseColor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(tryParseColor);

            object[] args = new object[] { "192, 0, 0", null };
            bool parsed = (bool)tryParseColor.Invoke(null, args);
            Assert.True(parsed);

            System.Drawing.Color color = (System.Drawing.Color)args[1];
            Assert.Equal(192, color.R);
            Assert.Equal(0, color.G);
            Assert.Equal(0, color.B);
        }

        [Fact]
        public void Issue72_ReportLayoutHelper_IsPropertyEquivalent_RecognizesEquivalentValues()
        {
            var isPropertyEquivalent = typeof(ReportLayoutHelper).GetMethod("IsPropertyEquivalent", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(isPropertyEquivalent);

            // Color equivalence (comma vs semicolon/pipe)
            bool colorEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "ForeColor", "ForeColor", "192, 0, 0", "192; 0; 0|" });
            Assert.True(colorEq, "Comma RGB '192, 0, 0' should be equivalent to normalized '192; 0; 0|'.");

            // Numeric geometry equivalence
            bool numEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "X", "X", "499", "499.0" });
            Assert.True(numEq, "Numeric X '499' should be equivalent to '499.0'.");

            // String properties must NOT use double parsing
            bool stringEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "Caption", "Caption", "1E2", "100" });
            Assert.False(stringEq, "String property 'Caption' must NOT treat '1E2' as equivalent to '100'.");

            // Case insensitive text for enums
            bool textEq = (bool)isPropertyEquivalent.Invoke(null, new object[] { "Alignment", "Alignment", "TopRight", "topright" });
            Assert.True(textEq, "Text alignment 'TopRight' should be equivalent to 'topright'.");
        }

        [Fact]
        public void Issue72_ReportLayoutHelper_BaselineOnlyAllowsChangedControlAttributes()
        {
            var baseline = XDocument.Parse(
                "<Report><PrintBlock Name=\"header\"><Control ControlName=\"untouched\" X=\"707\" Y=\"40\" ForeColor=\"192, 0, 0\" /><Control ControlName=\"target\" Caption=\"old\" /></PrintBlock></Report>");
            var incoming = XDocument.Parse(
                "<Report><PrintBlock Name=\"header\"><Control ControlName=\"untouched\" X=\"707\" Y=\"40\" ForeColor=\"192, 0, 0\" /><Control ControlName=\"target\" Caption=\"new\" /></PrintBlock></Report>");

            var findBaseline = typeof(ReportLayoutHelper).GetMethod("FindBaselineControl", BindingFlags.NonPublic | BindingFlags.Static);
            var hasChanged = typeof(ReportLayoutHelper).GetMethod("HasReportAttributeChanged", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(findBaseline);
            Assert.NotNull(hasChanged);

            var untouched = incoming.Descendants("Control").First(e => (string)e.Attribute("ControlName") == "untouched");
            var target = incoming.Descendants("Control").First(e => (string)e.Attribute("ControlName") == "target");
            var untouchedBaseline = findBaseline.Invoke(null, new object[] { untouched, baseline }) as XElement;
            var targetBaseline = findBaseline.Invoke(null, new object[] { target, baseline }) as XElement;

            Assert.NotNull(untouchedBaseline);
            Assert.NotNull(targetBaseline);
            Assert.False((bool)hasChanged.Invoke(null, new object[] { untouched, untouchedBaseline, "X" }));
            Assert.False((bool)hasChanged.Invoke(null, new object[] { untouched, untouchedBaseline, "ForeColor" }));
            Assert.True((bool)hasChanged.Invoke(null, new object[] { target, targetBaseline, "Caption" }));
        }

        [Fact]
        public void Issue72_ReportLayoutHelper_WriteLayout_DoesNotReplayUntouchedProjectionValues()
        {
            var layout = new FakeReportLayout
            {
                ReportBands = new[]
                {
                    new FakeReportBand
                    {
                        Name = "header",
                        Controls = new List<FakeReportControl>
                        {
                            new FakeReportControl
                            {
                                Name = "untouched",
                                X = 707,
                                Y = 40,
                                ForeColor = "192, 0, 0",
                                Alignment = "TopRight"
                            },
                            new FakeReportControl { Name = "target", Text = "old", X = 321, Y = 18, ForeColor = "192, 0, 0" }
                        }
                    },
                    new FakeReportBand
                    {
                        Name = "footer",
                        Controls = new List<FakeReportControl>
                        {
                            new FakeReportControl { Name = "untouched", X = 901, Y = 80, ForeColor = "0, 128, 0" },
                            new FakeReportControl { Name = "target", Text = "footer" }
                        }
                    }
                }
            };
            var part = CreateFakeLayoutPart(layout);

            const string baseline = "<Report><PrintBlock Name=\"header\"><Control Name=\"untouched\" X=\"400\" Y=\"150\" ForeColor=\"192, 0, 0\" Alignment=\"TopRight\" /><Control Name=\"target\" X=\"400\" Y=\"150\" ForeColor=\"192, 0, 0\" Caption=\"old\" /></PrintBlock><PrintBlock Name=\"footer\"><Control Name=\"untouched\" X=\"500\" Y=\"180\" ForeColor=\"0, 128, 0\" /><Control Name=\"target\" Caption=\"footer\" /></PrintBlock></Report>";
            const string incoming = "<Report><PrintBlock Name=\"header\"><Control Name=\"untouched\" X=\"400\" Y=\"150\" ForeColor=\"192, 0, 0\" Alignment=\"TopRight\" /><Control Name=\"target\" X=\"400\" Y=\"150\" ForeColor=\"192, 0, 0\" Caption=\"new\" /></PrintBlock><PrintBlock Name=\"footer\"><Control Name=\"untouched\" X=\"500\" Y=\"180\" ForeColor=\"0, 128, 0\" /><Control Name=\"target\" Caption=\"footer\" /></PrintBlock></Report>";

            ReportLayoutHelper.WriteLayout(part, incoming, baseline);

            var headerControls = layout.ReportBands.Single(b => b.Name == "header").Controls;
            var footerControls = layout.ReportBands.Single(b => b.Name == "footer").Controls;
            var headerUntouched = headerControls.Single(c => c.Name == "untouched");
            var headerTarget = headerControls.Single(c => c.Name == "target");
            var footerUntouched = footerControls.Single(c => c.Name == "untouched");
            var footerTarget = footerControls.Single(c => c.Name == "target");
            Assert.Equal(707, headerUntouched.X);
            Assert.Equal(40, headerUntouched.Y);
            Assert.Equal("192, 0, 0", headerUntouched.ForeColor);
            Assert.Equal("TopRight", headerUntouched.Alignment);
            Assert.Equal("new", headerTarget.Text);
            Assert.Equal(321, headerTarget.X);
            Assert.Equal(18, headerTarget.Y);
            Assert.Equal("192, 0, 0", headerTarget.ForeColor);
            Assert.Equal(901, footerUntouched.X);
            Assert.Equal(80, footerUntouched.Y);
            Assert.Equal("0, 128, 0", footerUntouched.ForeColor);
            Assert.Equal("footer", footerTarget.Text);
        }

        private static Artech.Architecture.Common.Objects.KBObjectPart CreateFakeLayoutPart(FakeReportLayout layout)
        {
            var assemblyName = new AssemblyName("GxMcpWorkerTests.DynamicReportPart");
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule(assemblyName.Name);
            var typeBuilder = module.DefineType(
                "FakeLayoutPart_" + Guid.NewGuid().ToString("N"),
                TypeAttributes.Public | TypeAttributes.Class,
                typeof(Artech.Architecture.Common.Objects.KBObjectPart));
            var baseKbObjectType = typeof(Artech.Architecture.Common.Objects.KBObjectPart).GetProperty("KBObject").PropertyType;
            var baseConstructor = typeof(Artech.Architecture.Common.Objects.KBObjectPart).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Guid), baseKbObjectType },
                null);
            var constructor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(Guid), baseKbObjectType });
            var constructorIl = constructor.GetILGenerator();
            constructorIl.Emit(OpCodes.Ldarg_0);
            constructorIl.Emit(OpCodes.Ldarg_1);
            constructorIl.Emit(OpCodes.Ldarg_2);
            constructorIl.Emit(OpCodes.Call, baseConstructor);
            constructorIl.Emit(OpCodes.Ret);

            var field = typeBuilder.DefineField("_layout", typeof(object), FieldAttributes.Private);
            var property = typeBuilder.DefineProperty("Layout", PropertyAttributes.None, typeof(object), Type.EmptyTypes);
            var getter = typeBuilder.DefineMethod("get_Layout", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(object), Type.EmptyTypes);
            var getterIl = getter.GetILGenerator();
            getterIl.Emit(OpCodes.Ldarg_0);
            getterIl.Emit(OpCodes.Ldfld, field);
            getterIl.Emit(OpCodes.Ret);
            var setter = typeBuilder.DefineMethod("set_Layout", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(void), new[] { typeof(object) });
            var setterIl = setter.GetILGenerator();
            setterIl.Emit(OpCodes.Ldarg_0);
            setterIl.Emit(OpCodes.Ldarg_1);
            setterIl.Emit(OpCodes.Stfld, field);
            setterIl.Emit(OpCodes.Ret);
            property.SetGetMethod(getter);
            property.SetSetMethod(setter);

            var type = typeBuilder.CreateType();
            var part = (Artech.Architecture.Common.Objects.KBObjectPart)FormatterServices.GetUninitializedObject(type);
            type.GetProperty("Layout").SetValue(part, layout, null);
            return part;
        }

        private sealed class FakeReportLayout
        {
            public IEnumerable<FakeReportBand> ReportBands { get; set; }
        }

        private sealed class FakeReportBand
        {
            public string Name { get; set; }
            public IEnumerable<FakeReportControl> Controls { get; set; }
        }

        private sealed class FakeReportControl
        {
            public string Name { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public string ForeColor { get; set; }
            public string Alignment { get; set; }
            public string Text { get; set; }
        }
    }
}
