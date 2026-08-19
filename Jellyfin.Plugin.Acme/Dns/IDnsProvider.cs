namespace Jellyfin.Plugin.Acme.Dns;

/// <summary>
/// Writes and removes the TXT record used to answer an ACME DNS-01 challenge.
/// </summary>
/// <remarks>
/// Implementations must never write the provider credential to logs or exceptions.
/// </remarks>
public interface IDnsProvider
{
    /// <summary>
    /// Creates a TXT record.
    /// </summary>
    /// <param name="recordName">Fully qualified record name, e.g. <c>_acme-challenge.media.example.com</c>.</param>
    /// <param name="value">The challenge digest to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An opaque handle used to remove the record again.</returns>
    Task<string> CreateTxtRecordAsync(string recordName, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a previously created TXT record. Implementations should swallow
    /// "already gone" conditions so cleanup is safe to retry.
    /// </summary>
    /// <param name="recordHandle">The handle returned by <see cref="CreateTxtRecordAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the removal.</returns>
    Task DeleteTxtRecordAsync(string recordHandle, CancellationToken cancellationToken);
}
