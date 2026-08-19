using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acme.Dns;

/// <summary>
/// DNS-01 provider backed by the Cloudflare v4 API.
/// </summary>
public sealed class CloudflareDnsProvider : IDnsProvider
{
    private const string ApiBase = "https://api.cloudflare.com/client/v4";

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private string? _zoneId;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudflareDnsProvider"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client used for API calls.</param>
    /// <param name="apiToken">A token scoped to Zone.DNS:Edit on the target zone.</param>
    /// <param name="logger">Logger.</param>
    public CloudflareDnsProvider(HttpClient httpClient, string apiToken, ILogger logger)
    {
        _http = httpClient;
        _logger = logger;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
    }

    /// <inheritdoc />
    public async Task<string> CreateTxtRecordAsync(string recordName, string value, CancellationToken cancellationToken)
    {
        var zoneId = await ResolveZoneIdAsync(recordName, cancellationToken).ConfigureAwait(false);

        var payload = new
        {
            type = "TXT",
            name = recordName,
            content = value,
            ttl = 60
        };

        using var response = await _http.PostAsJsonAsync(
            $"{ApiBase}/zones/{zoneId}/dns_records", payload, cancellationToken).ConfigureAwait(false);

        var result = await ReadEnvelopeAsync(response, "create TXT record", cancellationToken).ConfigureAwait(false);
        var id = result.GetProperty("id").GetString()
                 ?? throw new InvalidOperationException("Cloudflare did not return a record id.");

        _logger.LogInformation("Published DNS-01 challenge record {RecordName}", recordName);
        return id;
    }

    /// <inheritdoc />
    public async Task DeleteTxtRecordAsync(string recordHandle, CancellationToken cancellationToken)
    {
        if (_zoneId is null)
        {
            return;
        }

        try
        {
            using var response = await _http.DeleteAsync(
                $"{ApiBase}/zones/{_zoneId}/dns_records/{recordHandle}", cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Could not remove the DNS-01 challenge record; it may need deleting by hand. Status {Status}",
                    response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Could not remove the DNS-01 challenge record");
        }
    }

    /// <summary>
    /// Finds the zone that owns a record by walking the name from most to least specific,
    /// so both apex and delegated-subdomain zones resolve correctly.
    /// </summary>
    private async Task<string> ResolveZoneIdAsync(string recordName, CancellationToken cancellationToken)
    {
        if (_zoneId is not null)
        {
            return _zoneId;
        }

        var labels = recordName.TrimEnd('.').Split('.');

        // A zone always has at least two labels; stop before running out.
        for (var i = 0; i <= labels.Length - 2; i++)
        {
            var candidate = string.Join('.', labels.Skip(i));

            using var response = await _http.GetAsync(
                $"{ApiBase}/zones?name={Uri.EscapeDataString(candidate)}", cancellationToken).ConfigureAwait(false);

            var result = await ReadEnvelopeAsync(response, "look up zone", cancellationToken).ConfigureAwait(false);

            if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0)
            {
                _zoneId = result[0].GetProperty("id").GetString();
                if (_zoneId is not null)
                {
                    _logger.LogInformation("Matched Cloudflare zone {Zone}", candidate);
                    return _zoneId;
                }
            }
        }

        throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"No Cloudflare zone found for '{recordName}'. Check that the domain is on this Cloudflare account and the token covers its zone."));
    }

    /// <summary>
    /// Unwraps Cloudflare's success/errors envelope, turning API-level failures into
    /// exceptions carrying the provider's own message but never the credential.
    /// </summary>
    private static async Task<JsonElement> ReadEnvelopeAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonAsync<CloudflareEnvelope>(cancellationToken).ConfigureAwait(false);

        if (body is null)
        {
            throw new InvalidOperationException($"Cloudflare returned an empty response when asked to {operation}.");
        }

        if (!body.Success)
        {
            var detail = body.Errors is { Count: > 0 }
                ? string.Join("; ", body.Errors.Select(e => $"{e.Code}: {e.Message}"))
                : $"HTTP {(int)response.StatusCode}";

            throw new InvalidOperationException($"Cloudflare refused to {operation} — {detail}");
        }

        return body.Result
            ?? throw new InvalidOperationException($"Cloudflare returned no result when asked to {operation}.");
    }

    private sealed class CloudflareEnvelope
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("result")]
        public JsonElement? Result { get; set; }

        [JsonPropertyName("errors")]
        public IReadOnlyList<CloudflareError>? Errors { get; set; }
    }

    private sealed class CloudflareError
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
