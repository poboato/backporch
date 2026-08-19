using System.Net.Http.Json;
using Jellyfin.Plugin.Backporch.Dns;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// IDnsProvider backed by Pebble's challtestsrv management API, so integration tests
/// can answer DNS-01 challenges without any real DNS.
/// </summary>
public sealed class ChallTestSrvDnsProvider : IDnsProvider
{
    private readonly HttpClient _http;
    private readonly string _managementUrl;

    public ChallTestSrvDnsProvider(HttpClient http, string managementUrl)
    {
        _http = http;
        _managementUrl = managementUrl.TrimEnd('/');
    }

    public async Task<string> CreateTxtRecordAsync(string recordName, string value, CancellationToken cancellationToken)
    {
        var host = recordName.TrimEnd('.') + ".";
        using var response = await _http.PostAsJsonAsync(
            _managementUrl + "/set-txt",
            new { host, value },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return host;
    }

    public async Task DeleteTxtRecordAsync(string recordHandle, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            _managementUrl + "/clear-txt",
            new { host = recordHandle },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
