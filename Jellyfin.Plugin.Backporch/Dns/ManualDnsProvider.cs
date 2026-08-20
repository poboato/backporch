using Jellyfin.Plugin.Backporch.Acme;

namespace Jellyfin.Plugin.Backporch.Dns;

/// <summary>
/// DNS-01 "provider" for zones the plugin has no API access to: it shows the user the
/// exact TXT record to add at their DNS host and waits for them to confirm. This makes
/// setup work with any DNS provider at the cost of a copy-and-paste per issuance.
/// </summary>
public sealed class ManualDnsProvider : IDnsProvider
{
    private readonly IssuanceState _state;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualDnsProvider"/> class.
    /// </summary>
    /// <param name="state">The shared issuance state the UI polls.</param>
    /// <param name="timeout">How long to wait for the user before giving up.</param>
    public ManualDnsProvider(IssuanceState state, TimeSpan timeout)
    {
        _state = state;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async Task<string> CreateTxtRecordAsync(string recordName, string value, CancellationToken cancellationToken)
    {
        await _state.WaitForDnsConfirmationAsync(recordName, value, _timeout, cancellationToken)
            .ConfigureAwait(false);
        return recordName;
    }

    /// <inheritdoc />
    public Task DeleteTxtRecordAsync(string recordHandle, CancellationToken cancellationToken)
    {
        // Nothing to call — the user owns the record. A leftover _acme-challenge TXT
        // record is harmless; the UI tells them it can be removed.
        _state.ClearPendingRecord();
        return Task.CompletedTask;
    }
}
