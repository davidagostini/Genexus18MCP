using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Xml.Linq;
using GxMcp.Worker.Helpers;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public class Issue122ColorRegressionTests
    {
        [Fact]
        public void Issue122_ColorHelper_TryParseColor_ParsesDotNetColorToStringFormat()
        {
            Assert.True(ColorHelper.TryParseColor("Color [A=255, R=200, G=255, B=200]", out var c1));
            Assert.Equal(255, c1.A);
            Assert.Equal(200, c1.R);
            Assert.Equal(255, c1.G);
            Assert.Equal(200, c1.B);

            Assert.True(ColorHelper.TryParseColor("Color [R=144, G=238, B=144]", out var c2));
            Assert.Equal(255, c2.A);
            Assert.Equal(144, c2.R);
            Assert.Equal(238, c2.G);
            Assert.Equal(144, c2.B);

            Assert.True(ColorHelper.TryParseColor("A=128, R=10, G=20, B=30", out var c3));
            Assert.Equal(128, c3.A);
            Assert.Equal(10, c3.R);
            Assert.Equal(20, c3.G);
            Assert.Equal(30, c3.B);
        }

        [Theory]
        [InlineData("200; 255; 200|", 200, 255, 200)]
        [InlineData("200; 255; 200", 200, 255, 200)]
        [InlineData("200, 255, 200", 200, 255, 200)]
        [InlineData("200,255,200|", 200, 255, 200)]
        [InlineData("rgb(200, 255, 200)", 200, 255, 200)]
        [InlineData("rgba(200, 255, 200, 1.0)", 200, 255, 200)]
        [InlineData("#90EE90", 144, 238, 144)]
        [InlineData("90EE90", 144, 238, 144)]
        [InlineData("#FF90EE90", 144, 238, 144)]
        [InlineData("#f00", 255, 0, 0)]
        [InlineData("Red", 255, 0, 0)]
        [InlineData("Color [Red]", 255, 0, 0)]
        [InlineData("Color [Color [Red]]", 255, 0, 0)]
        public void Issue122_ColorHelper_TryParseColor_ParsesVariousFormats(string input, int expectedR, int expectedG, int expectedB)
        {
            Assert.True(ColorHelper.TryParseColor(input, out var color), $"Failed to parse '{input}'");
            Assert.Equal(expectedR, color.R);
            Assert.Equal(expectedG, color.G);
            Assert.Equal(expectedB, color.B);
        }

        [Fact]
        public void Issue122_ColorHelper_TryParseColor_ParsesTransparentAndEmpty()
        {
            Assert.True(ColorHelper.TryParseColor("Transparent", out var c1));
            Assert.Equal(0, c1.A);

            Assert.True(ColorHelper.TryParseColor("Color [Transparent]", out var c2));
            Assert.Equal(0, c2.A);

            Assert.True(ColorHelper.TryParseColor("Empty", out var c3));
            Assert.Equal(0, c3.A);

            Assert.True(ColorHelper.TryParseColor("Color [Empty]", out var c4));
            Assert.Equal(0, c4.A);
        }

        [Fact]
        public void Issue122_ColorHelper_NormalizeColorToken_EmitsCanonicalGeneXusFormat()
        {
            Assert.Equal("144; 238; 144|", ColorHelper.NormalizeColorToken("Color [A=255, R=144, G=238, B=144]"));
            Assert.Equal("144; 238; 144|", ColorHelper.NormalizeColorToken("#90EE90"));
            Assert.Equal("144; 238; 144|", ColorHelper.NormalizeColorToken("144, 238, 144"));
            Assert.Equal("144; 238; 144|", ColorHelper.NormalizeColorToken("rgb(144, 238, 144)"));
            Assert.Equal("Transparent", ColorHelper.NormalizeColorToken("Transparent"));
            Assert.Equal("Transparent", ColorHelper.NormalizeColorToken("Color [Transparent]"));
        }

        [Fact]
        public void Issue122_ColorHelper_IsColorEquivalent_RecognizesCrossFormatEquality()
        {
            Assert.True(ColorHelper.IsColorEquivalent("Color [A=255, R=144, G=238, B=144]", "144; 238; 144|"));
            Assert.True(ColorHelper.IsColorEquivalent("#90EE90", "144; 238; 144|"));
            Assert.True(ColorHelper.IsColorEquivalent("144, 238, 144", "144; 238; 144|"));
            Assert.True(ColorHelper.IsColorEquivalent("rgb(144, 238, 144)", "#90EE90"));
            Assert.True(ColorHelper.IsColorEquivalent("Red", "Color [Red]"));
            Assert.True(ColorHelper.IsColorEquivalent("Red", "255; 0; 0|"));
            Assert.True(ColorHelper.IsColorEquivalent("Transparent", "Color [Transparent]"));
        }

        [Fact]
        public void Issue122_XmlEquivalence_RecognizesColorEquivalence()
        {
            string xml1 = "<Report><PrintBlock Name=\"pb\"><Control BackColor=\"144; 238; 144|\" /></PrintBlock></Report>";
            string xml2 = "<Report><PrintBlock Name=\"pb\"><Control BackColor=\"#90EE90\" /></PrintBlock></Report>";
            string xml3 = "<Report><PrintBlock Name=\"pb\"><Control BackColor=\"144, 238, 144\" /></PrintBlock></Report>";
            string xml4 = "<Report><PrintBlock Name=\"pb\"><Control BackColor=\"Color [A=255, R=144, G=238, B=144]\" /></PrintBlock></Report>";

            Assert.True(XmlEquivalence.AreEquivalent(xml1, xml2, out var diff1), diff1);
            Assert.True(XmlEquivalence.AreEquivalent(xml1, xml3, out var diff2), diff2);
            Assert.True(XmlEquivalence.AreEquivalent(xml1, xml4, out var diff3), diff3);
        }

        [Fact]
        public void Issue122_ReportLayoutHelper_WriteLayout_PreservesUntouchedRectangleColor()
        {
            var layout = new FakeReportLayoutWithColors
            {
                ReportBands = new[]
                {
                    new FakeReportBandWithColorControls
                    {
                        Name = "p_header",
                        Items = new List<FakeReportColorControl>
                        {
                            new FakeReportColorControl
                            {
                                Name = "rect_green",
                                X = 10,
                                Y = 10,
                                Width = 100,
                                Height = 50,
                                BackColor = Color.FromArgb(144, 238, 144),
                                ForeColor = Color.FromArgb(0, 128, 0)
                            }
                        }
                    },
                    new FakeReportBandWithColorControls
                    {
                        Name = "p_body",
                        Items = new List<FakeReportColorControl>
                        {
                            new FakeReportColorControl
                            {
                                Name = "lbl_title",
                                X = 10,
                                Y = 70,
                                Width = 200,
                                Height = 20,
                                Caption = "Old Title"
                            }
                        }
                    }
                }
            };

            var part = CreateFakeLayoutPartTyped(layout);

            string baselineXml =
                "<Report>" +
                "  <PrintBlock Name=\"p_header\">" +
                "    <Control ControlName=\"rect_green\" Left=\"10\" Top=\"10\" Width=\"100\" Height=\"50\" BackColor=\"144; 238; 144|\" ForeColor=\"0; 128; 0|\" />" +
                "  </PrintBlock>" +
                "  <PrintBlock Name=\"p_body\">" +
                "    <Control ControlName=\"lbl_title\" Left=\"10\" Top=\"70\" Width=\"200\" Height=\"20\" Caption=\"Old Title\" />" +
                "  </PrintBlock>" +
                "</Report>";

            string incomingXml =
                "<Report>" +
                "  <PrintBlock Name=\"p_header\">" +
                "    <Control ControlName=\"rect_green\" Left=\"10\" Top=\"10\" Width=\"100\" Height=\"50\" BackColor=\"144; 238; 144|\" ForeColor=\"0; 128; 0|\" />" +
                "  </PrintBlock>" +
                "  <PrintBlock Name=\"p_body\">" +
                "    <Control ControlName=\"lbl_title\" Left=\"10\" Top=\"70\" Width=\"200\" Height=\"20\" Caption=\"New Title\" />" +
                "  </PrintBlock>" +
                "</Report>";

            ReportLayoutHelper.WriteLayout(part, incomingXml, baselineXml);

            var header = layout.ReportBands.Single(b => b.Name == "p_header");
            var rect = header.Items.Single(c => c.Name == "rect_green");
            var body = layout.ReportBands.Single(b => b.Name == "p_body");
            var lbl = body.Items.Single(c => c.Name == "lbl_title");

            // Untouched rectangle color must NOT be turned to black (0, 0, 0)
            Assert.Equal(Color.FromArgb(144, 238, 144), rect.BackColor);
            Assert.Equal(Color.FromArgb(0, 128, 0), rect.ForeColor);
            Assert.Equal("New Title", lbl.Caption);
        }

        [Fact]
        public void Issue122_ReportLayoutHelper_WriteLayout_UpdatesRectangleColorCorrectly()
        {
            var layout = new FakeReportLayoutWithColors
            {
                ReportBands = new[]
                {
                    new FakeReportBandWithColorControls
                    {
                        Name = "p_header",
                        Items = new List<FakeReportColorControl>
                        {
                            new FakeReportColorControl
                            {
                                Name = "rect_target",
                                X = 10,
                                Y = 10,
                                Width = 100,
                                Height = 50,
                                BackColor = Color.FromArgb(144, 238, 144)
                            }
                        }
                    }
                }
            };

            var part = CreateFakeLayoutPartTyped(layout);

            string baselineXml =
                "<Report>" +
                "  <PrintBlock Name=\"p_header\">" +
                "    <Control ControlName=\"rect_target\" Left=\"10\" Top=\"10\" Width=\"100\" Height=\"50\" BackColor=\"144; 238; 144|\" />" +
                "  </PrintBlock>" +
                "</Report>";

            // Change color using hex #FFFF00 (Yellow)
            string incomingXml =
                "<Report>" +
                "  <PrintBlock Name=\"p_header\">" +
                "    <Control ControlName=\"rect_target\" Left=\"10\" Top=\"10\" Width=\"100\" Height=\"50\" BackColor=\"#FFFF00\" />" +
                "  </PrintBlock>" +
                "</Report>";

            ReportLayoutHelper.WriteLayout(part, incomingXml, baselineXml);

            var header = layout.ReportBands.Single(b => b.Name == "p_header");
            var rect = header.Items.Single(c => c.Name == "rect_target");

            Assert.Equal(Color.FromArgb(255, 255, 0), rect.BackColor);
        }

        private sealed class FakeReportLayoutWithColors
        {
            public IEnumerable<FakeReportBandWithColorControls> ReportBands { get; set; }
        }

        private sealed class FakeReportBandWithColorControls
        {
            public string Name { get; set; }
            public string ControlName => Name;
            public List<FakeReportColorControl> Items { get; set; }
        }

        private sealed class FakeReportColorControl
        {
            public string Name { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Caption { get; set; }
            public Color BackColor { get; set; }
            public Color ForeColor { get; set; }
        }

        private static Artech.Architecture.Common.Objects.KBObjectPart CreateFakeLayoutPartTyped(object layout)
        {
            var assemblyName = new AssemblyName("GxMcpWorkerTests.DynamicReportPart_" + Guid.NewGuid().ToString("N"));
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
    }
}
