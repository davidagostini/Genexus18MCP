using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;
using GxMcp.Worker.Services;

namespace GxMcp.Worker.Tests
{
    // Issue #97: a subtype attribute (IS_SUBTYPE=True) added programmatically can be
    // left classified as stored (SECONDARY) instead of derived (INFERRED), silently
    // creating a physical column and breaking supertype propagation. The detection
    // kernel is pure (no GeneXus SDK objects), so the mismatch rule is unit-testable.
    public class SubtypeClassificationTests
    {
        private static StructureService.SubtypeAttrView Attr(string name, string supertype, bool inferred, string level = "ChildLevel")
            => new StructureService.SubtypeAttrView
            {
                Level = level,
                Name = name,
                Supertype = supertype,
                IsInferred = inferred,
                Guid = "guid-" + name
            };

        [Fact]
        public void MixedGroup_FlagsTheStoredAttribute()
        {
            // ChildFlagA (IDE-added, INFERRED) + ChildFlagC (MCP-added, SECONDARY) share
            // the same supertype ParentFlagC on the same level — exactly the #97 repro.
            var issues = StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>
            {
                Attr("ChildFlagA", "ParentFlagC", inferred: true),
                Attr("ChildFlagC", "ParentFlagC", inferred: false)
            });

            var single = Assert.Single(issues);
            Assert.Equal("ChildFlagC", single["attribute"]?.ToString());
            Assert.Equal("ParentFlagC", single["supertype"]?.ToString());
            Assert.Equal("INFERRED", single["expected"]?.ToString());
            Assert.Equal("SECONDARY", single["actual"]?.ToString());
            Assert.Equal("ChildLevel", single["level"]?.ToString());
        }

        [Fact]
        public void AllInferred_NoIssue()
        {
            var issues = StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>
            {
                Attr("ChildFlagA", "ParentFlagC", inferred: true),
                Attr("ChildFlagB", "ParentFlagC", inferred: true)
            });

            Assert.Empty(issues);
        }

        [Fact]
        public void AllStored_NoIssue()
        {
            // Supertype absent from the structure — every subtype is legitimately stored,
            // so there is no divergence to warn about.
            var issues = StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>
            {
                Attr("ChildFlagA", "ParentFlagC", inferred: false),
                Attr("ChildFlagC", "ParentFlagC", inferred: false)
            });

            Assert.Empty(issues);
        }

        [Fact]
        public void NonSubtypeAttributes_Ignored()
        {
            var issues = StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>
            {
                Attr("CustomerId", null, inferred: false),
                Attr("CustomerName", "", inferred: false)
            });

            Assert.Empty(issues);
        }

        [Fact]
        public void NullOrEmptyInput_NoIssue()
        {
            Assert.Empty(StructureService.FindMismatchedSubtypeClassifications(null));
            Assert.Empty(StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>()));
        }

        [Fact]
        public void SameSupertypeOnDifferentLevels_NotFlaggedAsSiblings()
        {
            // CustomerId is INFERRED on the root level but a stored (SECONDARY)
            // membership of the SAME supertype on a detail level is a different,
            // independent membership — not the #97 bug. Grouping must be per level.
            var issues = StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>
            {
                Attr("CustomerId", "Customer", inferred: true, level: "root"),
                Attr("CustomerId2", "Customer", inferred: false, level: "ChildLevel")
            });

            Assert.Empty(issues);
        }

        [Fact]
        public void SameSupertypeOnSameLevel_Flagged_EvenWithAnotherLevelClean()
        {
            // Mixed group on ChildLevel must be flagged even though the root level
            // holds the same supertype homogeneously inferred.
            var issues = StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>
            {
                Attr("CustomerId", "Customer", inferred: true, level: "root"),
                Attr("CustomerIdB", "Customer", inferred: true, level: "root"),
                Attr("CustomerIdC", "Customer", inferred: true, level: "ChildLevel"),
                Attr("CustomerIdD", "Customer", inferred: false, level: "ChildLevel")
            });

            var single = Assert.Single(issues);
            Assert.Equal("CustomerIdD", single["attribute"]?.ToString());
            Assert.Equal("ChildLevel", single["level"]?.ToString());
        }

        [Fact]
        public void DistinctSupertypesOnSameLevel_IndependentGroups()
        {
            var issues = StructureService.FindMismatchedSubtypeClassifications(new List<StructureService.SubtypeAttrView>
            {
                Attr("ChildFlagC", "ParentFlagC", inferred: false), // mixed group → flagged
                Attr("ChildFlagA", "ParentFlagC", inferred: true),
                Attr("ChildOtherC", "ParentOther", inferred: true), // all-inferred group → clean
                Attr("ChildOtherB", "ParentOther", inferred: true)
            });

            var single = Assert.Single(issues);
            Assert.Equal("ChildFlagC", single["attribute"]?.ToString());
        }
    }
}
