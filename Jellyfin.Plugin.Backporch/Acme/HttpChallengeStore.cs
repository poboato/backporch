using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Backporch.Acme;

/// <summary>
/// Holds active HTTP-01 challenge answers. The issuance pipeline puts them here and the
/// anonymous <c>/.well-known/acme-challenge</c> route serves them to the certificate
/// authority. Entries exist only for the seconds an authorization is in flight.
/// </summary>
public sealed class HttpChallengeStore
{
    private readonly ConcurrentDictionary<string, string> _answers = new(StringComparer.Ordinal);

    /// <summary>
    /// Publishes the key authorization for a challenge token.
    /// </summary>
    /// <param name="token">The challenge token from the CA.</param>
    /// <param name="keyAuthorization">The key authorization string to serve for it.</param>
    public void Put(string token, string keyAuthorization) => _answers[token] = keyAuthorization;

    /// <summary>
    /// Looks up the answer for a token.
    /// </summary>
    /// <param name="token">The challenge token.</param>
    /// <param name="keyAuthorization">The key authorization, when present.</param>
    /// <returns><c>true</c> when the token is active.</returns>
    public bool TryGet(string token, out string keyAuthorization)
    {
        if (_answers.TryGetValue(token, out var value))
        {
            keyAuthorization = value;
            return true;
        }

        keyAuthorization = string.Empty;
        return false;
    }

    /// <summary>
    /// Removes a token once its authorization has settled.
    /// </summary>
    /// <param name="token">The challenge token.</param>
    public void Remove(string token) => _answers.TryRemove(token, out _);
}
