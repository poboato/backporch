using Jellyfin.Plugin.Backporch.Acme;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The by-hand DNS path is the only place the pipeline stops and waits for a person, and
/// the only signal it gets back is one button press with no argument. These cover what that
/// press may and may not be taken to mean — the cases a real user reaches by clicking twice,
/// or by having more than one name on the certificate.
/// </summary>
public class ManualDnsHandshakeTests
{
    private const string FirstRecord = "_acme-challenge.jellyfin.example.com";
    private const string SecondRecord = "_acme-challenge.sonarr.example.com";

    private static readonly TimeSpan _patient = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One press confirms one record. The controller turns a refusal into 409, which is
    /// what stops a double-click — or an impatient second press while the CA is checking —
    /// from being banked and later spent on a record the user has not added yet.
    /// </summary>
    [Fact]
    public async Task OnePressConfirmsExactlyOneRecord()
    {
        var state = new IssuanceState();
        state.TryBegin();

        var wait = state.WaitForDnsConfirmationAsync(
            FirstRecord, "digest-one", _patient, CancellationToken.None);
        await WaitUntilAsync(() => state.Snapshot().PendingRecordName is not null);

        Assert.True(state.ConfirmDnsRecord());
        await wait;

        Assert.False(state.ConfirmDnsRecord());
        Assert.False(state.ConfirmDnsRecord());
    }

    /// <summary>
    /// A certificate carrying several names opens one authorization per name, so the by-hand
    /// path asks for a TXT record per name in turn. Each has to be confirmed on its own
    /// merits: if presses made during the first name's validation could satisfy the second
    /// name's wait, the run would race ahead to a record that is not in DNS, and the CA
    /// would fail the whole order on a name the user never got the chance to publish.
    /// </summary>
    [Fact]
    public async Task EachNameNeedsItsOwnConfirmation()
    {
        var state = new IssuanceState();
        state.TryBegin();

        var first = state.WaitForDnsConfirmationAsync(
            FirstRecord, "digest-one", _patient, CancellationToken.None);
        await WaitUntilAsync(() => state.Snapshot().PendingRecordName == FirstRecord);
        Assert.True(state.ConfirmDnsRecord());
        await first;

        // Impatient presses while the certificate authority checks the first name.
        Assert.False(state.ConfirmDnsRecord());
        Assert.False(state.ConfirmDnsRecord());

        var second = state.WaitForDnsConfirmationAsync(
            SecondRecord, "digest-two", _patient, CancellationToken.None);
        await WaitUntilAsync(() => state.Snapshot().PendingRecordValue == "digest-two");

        // The user must be shown the new record, and the run must still be waiting on it.
        var snapshot = state.Snapshot();
        Assert.Equal(IssuancePhase.AwaitingDnsRecord, snapshot.Phase);
        Assert.Equal(SecondRecord, snapshot.PendingRecordName);
        Assert.False(second.IsCompleted);

        Assert.True(state.ConfirmDnsRecord());
        await second;
    }

    /// <summary>
    /// The configuration page prefixes the whole progress line with "[practice run]" from
    /// this flag alone. Left set from the rehearsal, it would label the real issuance as a
    /// practice — telling the user to disregard a failure that actually cost them a
    /// production rate limit, or that a real certificate was only a dry run.
    /// </summary>
    [Fact]
    public void ANewRunIsNeverLabelledWithTheLastRunsPracticeFlag()
    {
        var state = new IssuanceState();

        state.TryBegin();
        state.SetTestRun(true);
        Assert.True(state.Snapshot().IsTestRun);
        state.Finish(success: false, "the rehearsal failed");

        Assert.True(state.TryBegin());
        Assert.False(state.Snapshot().IsTestRun);
    }

    /// <summary>
    /// A run that ends while the user is still looking at a TXT record must take the
    /// waiting run down with it, not leave a press able to resolve a task nobody owns.
    /// </summary>
    [Fact]
    public async Task AConfirmationAfterTheRunEndedIsRefused()
    {
        var state = new IssuanceState();
        state.TryBegin();

        var wait = state.WaitForDnsConfirmationAsync(
            FirstRecord, "digest-one", _patient, CancellationToken.None);
        await WaitUntilAsync(() => state.Snapshot().PendingRecordName is not null);

        state.Finish(success: false, "gave up");
        await Assert.ThrowsAsync<TaskCanceledException>(() => wait);

        Assert.False(state.ConfirmDnsRecord());
        Assert.False(state.Snapshot().Running);
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
