namespace Jellyfin.Plugin.Backporch.Acme;

/// <summary>
/// Where a running issuance currently is, coarse enough to narrate to a person.
/// </summary>
public enum IssuancePhase
{
    /// <summary>Nothing is running.</summary>
    Idle,

    /// <summary>An issuance has been requested and is spinning up.</summary>
    Starting,

    /// <summary>Registering with or verifying the ACME account.</summary>
    Account,

    /// <summary>Publishing the DNS-01 challenge record via the provider API.</summary>
    PublishingDns,

    /// <summary>Manual mode: waiting for the user to add the TXT record themselves.</summary>
    AwaitingDnsRecord,

    /// <summary>Waiting for the challenge record to propagate through DNS.</summary>
    Propagating,

    /// <summary>The certificate authority is checking the record.</summary>
    Validating,

    /// <summary>Generating the key, finalizing the order, downloading the chain.</summary>
    Finalizing,

    /// <summary>Writing the PKCS#12 bundle to disk.</summary>
    WritingCertificate,

    /// <summary>The last run completed successfully.</summary>
    Succeeded,

    /// <summary>The last run failed; <see cref="IssuanceSnapshot.Detail"/> says why.</summary>
    Failed
}

/// <summary>
/// A point-in-time copy of the issuance state, safe to serialize to the UI.
/// </summary>
public sealed record IssuanceSnapshot(
    IssuancePhase Phase,
    string Detail,
    bool IsTestRun,
    string? PendingRecordName,
    string? PendingRecordValue,
    DateTime? StartedUtc)
{
    /// <summary>Gets a value indicating whether an issuance is currently running.</summary>
    public bool Running => Phase is not (IssuancePhase.Idle or IssuancePhase.Succeeded or IssuancePhase.Failed);
}

/// <summary>
/// Shared, thread-safe progress state for the one issuance that may run at a time.
/// The configuration page polls it, so progress survives page reloads — the truth
/// lives on the server, not in the browser tab.
/// </summary>
public sealed class IssuanceState
{
    private readonly object _gate = new();

    private IssuancePhase _phase = IssuancePhase.Idle;
    private string _detail = string.Empty;
    private bool _isTestRun;
    private string? _pendingRecordName;
    private string? _pendingRecordValue;
    private DateTime? _startedUtc;
    private TaskCompletionSource<bool>? _dnsConfirmed;

    /// <summary>
    /// Claims the run slot. Only one issuance may be in flight; a second request is
    /// refused rather than queued.
    /// </summary>
    /// <returns><c>true</c> when the caller may proceed.</returns>
    public bool TryBegin()
    {
        lock (_gate)
        {
            if (Snapshot().Running)
            {
                return false;
            }

            _phase = IssuancePhase.Starting;
            _detail = string.Empty;
            _isTestRun = false;
            _pendingRecordName = null;
            _pendingRecordValue = null;
            _startedUtc = DateTime.UtcNow;
            _dnsConfirmed = null;
            return true;
        }
    }

    /// <summary>
    /// Records progress. No-op detail keeps the previous phase's message readable.
    /// </summary>
    /// <param name="phase">The phase now underway.</param>
    /// <param name="detail">A human-readable line for the UI.</param>
    public void Report(IssuancePhase phase, string detail = "")
    {
        lock (_gate)
        {
            _phase = phase;
            _detail = detail;
        }
    }

    /// <summary>
    /// Marks whether the run in progress is the staging dry run or the real issuance,
    /// so the UI can label the progress correctly.
    /// </summary>
    /// <param name="isTestRun"><c>true</c> during the staging dry run.</param>
    public void SetTestRun(bool isTestRun)
    {
        lock (_gate)
        {
            _isTestRun = isTestRun;
        }
    }

    /// <summary>
    /// Ends the run, releasing the slot and cancelling any pending manual confirmation.
    /// </summary>
    /// <param name="success">Whether the run succeeded.</param>
    /// <param name="detail">The final outcome line.</param>
    public void Finish(bool success, string detail)
    {
        lock (_gate)
        {
            _phase = success ? IssuancePhase.Succeeded : IssuancePhase.Failed;
            _detail = detail;
            _pendingRecordName = null;
            _pendingRecordValue = null;
            _dnsConfirmed?.TrySetCanceled();
            _dnsConfirmed = null;
        }
    }

    /// <summary>
    /// Publishes a TXT record for the user to add by hand and waits until they confirm
    /// they have, or the wait times out.
    /// </summary>
    /// <param name="recordName">The full record name, e.g. <c>_acme-challenge.media.example.com</c>.</param>
    /// <param name="value">The record's required content.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the user confirms.</returns>
    public async Task WaitForDnsConfirmationAsync(
        string recordName,
        string value,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> tcs;

        lock (_gate)
        {
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRecordName = recordName;
            _pendingRecordValue = value;
            _dnsConfirmed = tcs;
            _phase = IssuancePhase.AwaitingDnsRecord;
            _detail = "Add the TXT record shown, then confirm.";
        }

        try
        {
            await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "Timed out waiting for the DNS record to be added. Open the Backporch page and run the request again when you are ready.");
        }
        finally
        {
            lock (_gate)
            {
                // The record itself stays visible until the provider's cleanup runs, so a
                // failed validation still shows the user what should have been in DNS.
                _dnsConfirmed = null;
            }
        }
    }

    /// <summary>
    /// Called by the UI when the user says the record is in place.
    /// </summary>
    /// <returns><c>true</c> if an issuance was actually waiting on it.</returns>
    public bool ConfirmDnsRecord()
    {
        lock (_gate)
        {
            var tcs = _dnsConfirmed;
            if (tcs is null)
            {
                return false;
            }

            _dnsConfirmed = null;
            return tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Clears the manual record display once it is no longer needed.
    /// </summary>
    public void ClearPendingRecord()
    {
        lock (_gate)
        {
            _pendingRecordName = null;
            _pendingRecordValue = null;
        }
    }

    /// <summary>
    /// Takes a consistent copy of the state for serialization.
    /// </summary>
    /// <returns>The snapshot.</returns>
    public IssuanceSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new IssuanceSnapshot(
                _phase, _detail, _isTestRun, _pendingRecordName, _pendingRecordValue, _startedUtc);
        }
    }
}
