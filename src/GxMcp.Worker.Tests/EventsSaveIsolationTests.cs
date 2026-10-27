using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;
using GxMcp.Worker.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Worker.Tests
{
    public sealed class EventsSaveIsolationTests
    {
        [Theory]
        [InlineData("AuditedAssemblies", "18.0.16.189550")]
        [InlineData("UpdatedU16Assemblies", "18.0.16.189595")]
        public void AuditedBuild_RequiresOneCompleteBundle(string field, string build)
        {
            var hashes = new Dictionary<string, string>((Dictionary<string, string>)typeof(EventsSaveIsolation)
                .GetField(field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            Assert.Equal(build, EventsSaveIsolation.MatchAuditedBuild(hashes));
            hashes.Remove("DVelop.Patterns.WorkWithPlus");
            Assert.Equal(build, EventsSaveIsolation.MatchAuditedBuild(hashes));
            hashes.Remove("Artech.Architecture.Common");
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(hashes));
        }

        [Fact]
        public void AuditedBuild_RejectsMixedBuildAndUnknownExtension()
        {
            var hashes = new Dictionary<string, string>((Dictionary<string, string>)typeof(EventsSaveIsolation)
                .GetField("UpdatedU16Assemblies", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            hashes["Artech.Architecture.Common"] = "5de4b9fa160b2aca8fa08be8e070870c00286415d7ecdfad816aa5bdc406f283";
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(hashes));
            hashes["Artech.Architecture.Common"] = "978fab2afad4daaa28aecffd5cd327367b1bb9c0a8f027cfda74911a294eac12";
            hashes["DVelop.Patterns.WorkWithPlus"] = "unknown";
            Assert.Null(EventsSaveIsolation.MatchAuditedBuild(hashes));
        }

        [Fact]
        public void PinDiagnostics_DoNotExposePathsOrClaimPersistence()
        {
            var report = EventsSaveIsolation.DescribeConfiguredPins();
            Assert.Equal("isolation-preflight", report["phase"]);
            Assert.Equal(2, ((JArray)report["requiredPins"]).Count);
            Assert.False(report["persisted"].Value<bool>());
            Assert.False(report["saved"].Value<bool>());
            Assert.Empty((JArray)report["implicitOperations"]);
            Assert.Null(report.SelectToken("effectivePins.kbPath"));
        }

        [Theory]
        [InlineData("{}", false)]
        [InlineData("{writeAttempted:false}", false)]
        [InlineData("{writeAttempted:true, rollbackVerified:true, persistenceUncertain:true}", false)]
        [InlineData("{writeAttempted:true, persistenceUncertain:true}", true)]
        [InlineData("{writeAttempted:true, rollbackError:'failed'}", true)]
        [InlineData("{writeAttempted:true, objectSaved:true, sourceChanged:true, partPersisted:true}", true)]
        [InlineData("{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:true, otherObjectMetadataIntact:true, sourceChanged:false}", false)]
        [InlineData("{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:true, otherObjectMetadataIntact:true, sourceChanged:false, persistenceUncertain:true}", true)]
        [InlineData("{writeAttempted:true, verificationCompleted:true, objectSaved:true, partPersisted:true, otherPartsIntact:false, otherObjectMetadataIntact:true, sourceChanged:false}", true)]
        public void DirtyTracking_UsesPersistenceOutcome(string json, bool expected)
            => Assert.Equal(expected, WriteService.ShouldMarkIsolatedEventsDirty(JObject.Parse(json)));

        [Fact]
        public void TransactionOutcome_InvalidatesAllReadLayersAndIdentityAliasesWithoutSdk()
        {
            string name = "IsolationCache_" + Guid.NewGuid().ToString("N");
            string guid = Guid.NewGuid().ToString();
            var reader = new ObjectReader(null);
            var nameRequest = new ObjectReadRequest { Target = name, PartName = "Events", ClientFormat = "mcp" };
            var guidRequest = new ObjectReadRequest { Target = guid, PartName = "Events", ClientFormat = "mcp" };
            var readerCache = PopulateCache(typeof(ObjectReader), "_cache", ObjectReader.BuildCacheKey(nameRequest));
            PopulateCache(typeof(ObjectReader), "_cache", ObjectReader.BuildCacheKey(guidRequest));
            var patchCache = PopulateCache(typeof(PatchService), "_sourceCache", "WebPanel|" + name + "|Events");
            PopulateCache(typeof(PatchService), "_sourceCache", "WebPanel|" + guid + "|Events");
            var sourceCache = PopulateCache(typeof(ObjectService), "_readCache", guid.Replace("-", "") + "|events|raw");
            PopulateCache(typeof(ObjectService), "_readCache", guid.Replace("-", "") + "|full|all");
            Assert.True(reader.TryGetCached(name, "Events", null, null, "mcp", out _));
            Assert.True(reader.TryGetCached(guid, "Events", null, null, "mcp", out _));

            WriteService.InvalidateIsolatedEventsReadCaches();

            Assert.False(reader.TryGetCached(name, "Events", null, null, "mcp", out _));
            Assert.False(reader.TryGetCached(guid, "Events", null, null, "mcp", out _));
            Assert.Empty(readerCache);
            Assert.Empty(patchCache);
            Assert.Empty(sourceCache);
        }

        private static IDictionary PopulateCache(Type owner, string field, string key)
        {
            var cache = (IDictionary)owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Type entryType = cache.GetType().GetGenericArguments()[1];
            object entry = Activator.CreateInstance(entryType, nonPublic: true);
            foreach (var property in entryType.GetProperties())
            {
                if (property.PropertyType == typeof(string)) property.SetValue(entry, "stale persisted source");
                if (property.PropertyType == typeof(DateTime)) property.SetValue(entry, DateTime.UtcNow);
            }
            cache[key] = entry;
            return cache;
        }

        private class SyntheticBase
        {
            private readonly Action _onSave;
            public int Calls;
            public SyntheticBase() { _onSave = () => Calls++; }
        }
        private sealed class SyntheticChild : SyntheticBase { }

        [Fact]
        public void UnknownDirectCallbackInBaseClass_IsRejectedWithoutCallingIt()
        {
            var instance = new SyntheticChild();
            Assert.Throws<InvalidOperationException>(() => EventsSaveIsolation.InspectDirectCallbacks(instance, new JArray()));
            Assert.Equal(0, instance.Calls);
        }

        [Fact]
        public void MissingObject_IsRejectedBeforeSdkAccess()
        {
            // The refusal is an InvalidOperationException that also carries its named finding.
            var refusal = Assert.ThrowsAny<InvalidOperationException>(() => EventsSaveIsolation.Preflight(null));
            var typed = Assert.IsType<EventsIsolationRefusalException>(refusal);
            Assert.Equal("IsolationTargetTypeUnsupported", Assert.Single(typed.Findings).Code);
        }

        [Fact]
        public void PatternVirtualPart_IsTheOnlyCertifiedVirtualPart()
        {
            Assert.True(EventsSaveIsolation.IsAllowedPartTypeName(
                "Artech.Packages.Patterns.Objects.PatternVirtualPart"));
            Assert.False(EventsSaveIsolation.IsAllowedPartType(typeof(KBObjectPart)));
        }

        [Fact]
        public void SourceComparison_AllowsSdkEolNormalizationOnly()
        {
            Assert.True(EventsSaveIsolation.SourceEquivalent("a" + Environment.NewLine + "b" + Environment.NewLine, "a" + (char)10 + "b" + (char)10));
            Assert.False(EventsSaveIsolation.SourceEquivalent("a\nb", "a\nc"));
        }

        [Fact]
        public void PatternProjection_DoesNotTrustPartNamesWithoutExactEquality()
        {
            Assert.True(EventsSaveIsolation.IsExpectedPatternProjection(
                ObjectMoveSnapshot.Comparison.Verified("same")));
            Assert.False(EventsSaveIsolation.IsExpectedPatternProjection(
                ObjectMoveSnapshot.Comparison.Failed(new[] { "Rules" }, "different")));
            Assert.False(EventsSaveIsolation.IsExpectedPatternProjection(
                ObjectMoveSnapshot.Comparison.Failed(new[] { "Conditions" }, "different")));
        }

        [Fact]
        public void MetadataAudit_DetectsOtherObjectsChangedAddedRemoved_ExcludesOnlyTarget()
        {
            var target = Guid.NewGuid(); var changed = Guid.NewGuid();
            var removed = Guid.NewGuid(); var added = Guid.NewGuid(); var unchanged = Guid.NewGuid();
            var before = new Dictionary<Guid, string> { [target] = "1", [changed] = "1", [removed] = "1", [unchanged] = "1" };
            var after = new Dictionary<Guid, string> { [target] = "2", [changed] = "2", [added] = "1", [unchanged] = "1" };
            var result = EventsSaveIsolation.ChangedOthers(before, after, target);
            Assert.Equal(3, result.Count);
            Assert.Contains(changed.ToString(), result.ToObject<string[]>());
            Assert.Contains(removed.ToString(), result.ToObject<string[]>());
            Assert.Contains(added.ToString(), result.ToObject<string[]>());
        }
    }
}
