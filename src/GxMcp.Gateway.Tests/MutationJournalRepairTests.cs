using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxMcp.Gateway.Tests;

public sealed class MutationJournalRepairTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gx-journal-repair-" + Guid.NewGuid().ToString("N"));
    private string Journal => Path.Combine(_directory, "journal.json");
    public MutationJournalRepairTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void ReplaceFailureRetainsFenceAcrossRestartAndExplicitRepair()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Old", "Source", "old");
        using (var blocker = new FileStream(Journal, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            registry.RequireRead("kb", "New", "Source", "new");
            Assert.False(registry.IsHealthy);
            Assert.False(registry.ConfirmRead("kb", "New", "Source"));
            Assert.NotEmpty(Directory.GetFiles(_directory, "*.tmp-*"));
            Assert.False(registry.RepairJournal(false).Value<bool>("healthy"));
        }
        var reopened = new MutationRecoveryRegistry(Journal);
        Assert.False(reopened.IsHealthy);
        var preview = reopened.RepairJournal();
        Assert.True(preview.Value<bool>("repairable"));
        Assert.False(preview.Value<bool>("persisted"));
        Assert.Equal(2, preview.Value<int>("pendingCount"));
        Assert.False(reopened.IsHealthy);
        var repaired = reopened.RepairJournal(false);
        Assert.True(repaired.Value<bool>("verified"));
        Assert.True(repaired.Value<bool>("persisted"));
        Assert.True(reopened.IsHealthy);
        Assert.True(reopened.TryGet("kb", "New", out _));
        Assert.Equal(2, new MutationRecoveryRegistry(Journal).Count);
        Assert.NotEmpty(Directory.GetFiles(_directory, "*.reconciled"));
        Assert.True(reopened.ConfirmRead("kb", "New", "Source"));
        Assert.Equal(1, new MutationRecoveryRegistry(Journal).Count);
    }

    [Fact]
    public void CorruptCandidateCannotBeDiscardedByRepair()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Object", "Source", "op");
        string before = File.ReadAllText(Journal);
        File.WriteAllText(Journal + ".tmp-broken", "{broken");
        registry.Refresh();
        Assert.False(registry.IsHealthy);
        Assert.False(registry.RepairJournal(false).Value<bool>("healthy"));
        Assert.Equal(before, File.ReadAllText(Journal));
        Assert.Equal("{broken", File.ReadAllText(Journal + ".tmp-broken"));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void FencesNeverExpireWithoutReadConfirmation()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Object", "Source", "op");
        var document = JObject.Parse(File.ReadAllText(Journal));
        document["entries"]![0]!["RequiredAtUtc"] = DateTime.UtcNow.AddYears(-1);
        File.WriteAllText(Journal, document.ToString());
        Assert.Equal(1, new MutationRecoveryRegistry(Journal).Count);
    }

    [Fact]
    public void IndependentInstancesMergeConcurrentFences()
    {
        var first = new MutationRecoveryRegistry(Journal);
        var second = new MutationRecoveryRegistry(Journal);
        first.RequireRead("kb", "First", "Source", "one");
        second.RequireRead("kb", "Second", "Source", "two");
        first.Refresh();
        Assert.Equal(2, first.Count);
        Assert.Equal(2, new MutationRecoveryRegistry(Journal).Count);
    }

    [Fact]
    public void HealthyStatusOmitsErrorAndRepairKeepsBackup()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Object", "Source", "op");
        var original = File.ReadAllText(Journal);
        Assert.Null(registry.GetJournalStatus()["error"]);
        Assert.True(registry.RepairJournal(false).Value<bool>("healthy"));
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.backup-*"))));
    }

    [Fact]
    public void NewFenceOnUnhealthyRegistrySurvivesRestart()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Old", "Source", "old");
        File.Copy(Journal, Journal + ".tmp-orphan");
        registry.Refresh();
        registry.RequireRead("kb", "New", "Source", "new");
        var reopened = new MutationRecoveryRegistry(Journal);
        Assert.Equal(2, reopened.RepairJournal().Value<int>("pendingCount"));
        Assert.True(reopened.RepairJournal(false).Value<bool>("healthy"));
        Assert.Equal(2, new MutationRecoveryRegistry(Journal).Count);
    }

    [Fact]
    public void ConfirmedFenceIsNotResurrectedByStaleInstance()
    {
        var first = new MutationRecoveryRegistry(Journal);
        first.RequireRead("kb", "First", "Source", "one");
        var second = new MutationRecoveryRegistry(Journal);
        Assert.True(second.ConfirmRead("kb", "First", "Source"));
        first.Refresh();
        Assert.Equal(0, first.Count);
        first.RequireRead("kb", "Second", "Source", "two");
        Assert.False(new MutationRecoveryRegistry(Journal).TryGet("kb", "First", out _));
    }

    [Fact]
    public void ReadStartedBeforeNewFenceCannotConfirmIt()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Object", "Source", "old");
        Assert.True(registry.TryGet("kb", "Object", "Source", out var observed));
        registry.RequireRead("kb", "Object", "Source", "new");
        Assert.False(registry.ConfirmRead("kb", "Object", "Source", observed));
        Assert.False(registry.ConfirmRead("kb", "Object", "Source", null));
        Assert.True(registry.TryGet("kb", "Object", "Source", out var latest));
        Assert.Equal("new", latest.OperationId);
        Assert.True(registry.ConfirmRead("kb", "Object", "Source", latest));
    }

    [Fact]
    public void LockTimeoutIsRetryableAndRefreshClearsOnlyBusy()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Object", "Source", "op");
        using (var blocker = new FileStream(Journal + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            registry.Refresh();
            Assert.False(registry.IsHealthy);
            var error = MutationRecoveryRegistry.BuildJournalBlockedEnvelope(registry.JournalError)["error"]!;
            Assert.Equal("MutationRecoveryJournalBusy", error.Value<string>("code"));
            Assert.True(error.Value<bool>("retryable"));
        }
        registry.Refresh();
        Assert.True(registry.IsHealthy);
        File.WriteAllText(Journal + ".tmp-bad", "{");
        registry.Refresh();
        using (var blocker = new FileStream(Journal + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            registry.Refresh();
        registry.Refresh();
        Assert.False(registry.IsHealthy);
        Assert.Equal("MutationRecoveryJournalUnavailable", MutationRecoveryRegistry.BuildJournalBlockedEnvelope(registry.JournalError)["error"]!.Value<string>("code"));
    }

    [Fact]
    public void StatusLimitsProjectionAndDoesNotExposeOwner()
    {
        var registry = new MutationRecoveryRegistry();
        for (int index = 0; index < 40; index++) registry.RequireRead("kb", "Object" + index, "Source", "op");
        var status = registry.GetJournalStatus();
        Assert.Equal(40, status.Value<int>("pendingCount"));
        Assert.True(status.Value<bool>("truncated"));
        Assert.Equal(32, ((JArray)status["pending"]!).Count);
        Assert.All(status["pending"]!, item => Assert.Null(item["OwnerKey"]));
    }

    [Fact]
    public void FailedConfirmationPreservesOriginalFence()
    {
        var registry = new MutationRecoveryRegistry(Journal);
        registry.RequireRead("kb", "Object", "Source", "op");
        using (var blocker = new FileStream(Journal, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(registry.ConfirmRead("kb", "Object", "Source"));
        Assert.True(registry.TryGet("kb", "Object", out _));
        Assert.False(registry.IsHealthy);
        Assert.True(registry.RepairJournal(false).Value<bool>("verified"));
        Assert.Equal(1, new MutationRecoveryRegistry(Journal).Count);
    }
}
