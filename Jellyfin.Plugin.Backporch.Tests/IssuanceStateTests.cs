using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Dns;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The progress state machine and the manual-DNS handshake behind it.
/// </summary>
public class IssuanceStateTests
{
    [Fact]
    public void OnlyOneRunMayHoldTheSlot()
    {
        var state = new IssuanceState();

        Assert.True(state.TryBegin());
        Assert.False(state.TryBegin());

        state.Finish(success: true, "done");
        Assert.True(state.TryBegin());
    }

    [Fact]
    public void SnapshotReflectsProgressAndOutcome()
    {
        var state = new IssuanceState();
        state.TryBegin();
        state.Report(IssuancePhase.Propagating, "waiting");

        var mid = state.Snapshot();
        Assert.Equal(IssuancePhase.Propagating, mid.Phase);
        Assert.Equal("waiting", mid.Detail);
        Assert.True(mid.Running);

        state.Finish(success: false, "boom");
        var done = state.Snapshot();
        Assert.Equal(IssuancePhase.Failed, done.Phase);
        Assert.Equal("boom", done.Detail);
        Assert.False(done.Running);
    }

    [Fact]
    public void ConfirmWithNothingPendingIsRefused()
    {
        var state = new IssuanceState();
        Assert.False(state.ConfirmDnsRecord());
    }

    [Fact]
    public async Task ManualProviderPublishesRecordAndReturnsOnConfirmation()
    {
        var state = new IssuanceState();
        state.TryBegin();
        var provider = new ManualDnsProvider(state, TimeSpan.FromSeconds(30));

        var create = provider.CreateTxtRecordAsync(
            "_acme-challenge.backporch.test", "digest-value", CancellationToken.None);

        // The record must be visible to the polling UI before confirmation.
        await WaitUntilAsync(() => state.Snapshot().PendingRecordName is not null);
        var snapshot = state.Snapshot();
        Assert.Equal(IssuancePhase.AwaitingDnsRecord, snapshot.Phase);
        Assert.Equal("_acme-challenge.backporch.test", snapshot.PendingRecordName);
        Assert.Equal("digest-value", snapshot.PendingRecordValue);
        Assert.False(create.IsCompleted);

        Assert.True(state.ConfirmDnsRecord());
        var handle = await create;
        Assert.Equal("_acme-challenge.backporch.test", handle);

        // The record stays visible for a failed validation; cleanup clears it.
        Assert.NotNull(state.Snapshot().PendingRecordName);
        await provider.DeleteTxtRecordAsync(handle, CancellationToken.None);
        Assert.Null(state.Snapshot().PendingRecordName);
    }

    [Fact]
    public async Task ManualProviderTimesOutWithAFriendlyMessage()
    {
        var state = new IssuanceState();
        state.TryBegin();
        var provider = new ManualDnsProvider(state, TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => provider.CreateTxtRecordAsync("_acme-challenge.x.test", "v", CancellationToken.None));

        Assert.Contains("Backporch page", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinishCancelsAPendingManualWait()
    {
        var state = new IssuanceState();
        state.TryBegin();
        var provider = new ManualDnsProvider(state, TimeSpan.FromSeconds(30));

        var create = provider.CreateTxtRecordAsync("_acme-challenge.x.test", "v", CancellationToken.None);
        await WaitUntilAsync(() => state.Snapshot().PendingRecordName is not null);

        state.Finish(success: false, "aborted");

        await Assert.ThrowsAsync<TaskCanceledException>(() => create);
        Assert.False(state.ConfirmDnsRecord());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition never became true");
    }
}
