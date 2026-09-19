using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests
{
    // v2.8.0 (S2) — verified-source GeneXus development skills exposed via
    // MCP resources/. These tests pin: (a) all curated keys are advertised,
    // (b) each body is non-trivial and cites its source, (c) the
    // hallucination-killer facts (CallProtocol does NOT accept "Modal";
    // CallProtocol does NOT apply to WebPanel) are spelled out so a future
    // refactor can't quietly drop the LLM-correction wording.
    public class SkillCatalogTests
    {
        [Fact]
        public void EveryCuratedSkill_HasTitleDescriptionBody()
        {
            Assert.NotEmpty(SkillCatalog.All);
            foreach (var e in SkillCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Key), "skill key missing");
                Assert.False(string.IsNullOrWhiteSpace(e.Title), $"{e.Key}: title missing");
                Assert.False(string.IsNullOrWhiteSpace(e.Description), $"{e.Key}: description missing");
                Assert.True(e.Body.Length >= 400, $"{e.Key}: body suspiciously short");
                // Each body must cite its source(s).
                Assert.Contains("docs.genexus.com", e.Body, System.StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void CuratedKeysArePresent()
        {
            // These four are the LLM-anti-hallucination minimum for v2.8.0.
            var keys = SkillCatalog.All.Select(e => e.Key).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
            Assert.Contains("navigation", keys);
            Assert.Contains("gam-integrated-security", keys);
            Assert.Contains("sd-panel-mobile", keys);
            Assert.Contains("webpanel-events", keys);
        }

        [Fact]
        public void OfficialNexaSkill_IsEmbeddedWithSafeReferenceLookup()
        {
            var nexa = SkillCatalog.FindByKey("nexa");

            Assert.NotNull(nexa);
            Assert.Contains("MCP", nexa.Body, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("gxnext", nexa.Body, System.StringComparison.OrdinalIgnoreCase);
            Assert.True(NexaSkillPack.ReferenceCount >= 100);

            Assert.True(NexaSkillPack.TryRead("references/object-transaction.md", out var transaction));
            Assert.Contains("Transaction", transaction);
            Assert.False(NexaSkillPack.TryRead("references/../SKILL.md", out _));
            Assert.False(NexaSkillPack.TryRead("references/object-transaction.md/extra", out _));
        }

        [Fact]
        public void NavigationSkill_KillsTheCallProtocolModalHallucination()
        {
            // The motivating example: an LLM suggested `CallProtocol = Modal`,
            // which doesn't exist. The navigation skill must spell out both
            // facts: (1) CallProtocol does NOT apply to WebPanel/SDPanel,
            // (2) "Modal" is not a value.
            var nav = SkillCatalog.FindByKey("navigation");
            Assert.NotNull(nav);
            Assert.Contains("CallProtocol", nav.Body);
            Assert.Contains("Modal", nav.Body);
            Assert.Contains("does **NOT**", nav.Body);
            // Must list the real CallProtocol values verbatim.
            Assert.Contains("Internal", nav.Body);
            Assert.Contains("Command Line", nav.Body);
            Assert.Contains("HTTP", nav.Body);
            Assert.Contains("SOAP", nav.Body);
            Assert.Contains("Enterprise Java Bean", nav.Body);
        }

        [Fact]
        public void GamSkill_NamesTheRealProperty()
        {
            // Real property is "Integrated Security Level" (NOT "Enable Integrated Security").
            var gam = SkillCatalog.FindByKey("gam-integrated-security");
            Assert.NotNull(gam);
            Assert.Contains("Integrated Security Level", gam.Body);
            // Real enum values verbatim.
            Assert.Contains("Authorization", gam.Body);
            Assert.Contains("Authentication", gam.Body);
            Assert.Contains("None", gam.Body);
        }

        [Fact]
        public void SdPanelSkill_DocumentsMainProperty()
        {
            var sd = SkillCatalog.FindByKey("sd-panel-mobile");
            Assert.NotNull(sd);
            // The IDE-facing name is "Main program" — important fact to pin.
            Assert.Contains("Main program", sd.Body);
            // Object types that can be Main.
            Assert.Contains("Menu", sd.Body);
            Assert.Contains("Panel", sd.Body);
            Assert.Contains("Work With", sd.Body);
        }

        [Fact]
        public void WebPanelEventsSkill_PinsRefreshLoadOrder()
        {
            var wp = SkillCatalog.FindByKey("webpanel-events");
            Assert.NotNull(wp);
            Assert.Contains("Refresh", wp.Body);
            Assert.Contains("Load", wp.Body);
            // The canonical sequence Start → Refresh → Load must be present
            // as a literal "Refresh event ... followed by ... Load" statement
            // so an LLM reading the body sees the ordering explicitly.
            Assert.Contains("followed by the Load", wp.Body);
            Assert.Contains("Start event", wp.Body);
        }

        [Fact]
        public void FindByKey_UnknownKey_ReturnsNull()
        {
            Assert.Null(SkillCatalog.FindByKey("nonexistent-skill"));
            Assert.Null(SkillCatalog.FindByKey(""));
            Assert.Null(SkillCatalog.FindByKey(null));
        }
    }
}
