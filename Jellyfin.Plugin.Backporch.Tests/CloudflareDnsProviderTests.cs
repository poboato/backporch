using System.Net;
using System.Text;
using Jellyfin.Plugin.Backporch.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Exercises zone-walking, envelope handling, and credential hygiene against a
/// scripted HTTP handler — no network involved.
/// </summary>
public class CloudflareDnsProviderTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public List<string> Requests { get; } = new();

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task ZoneWalk_FindsApexZone_ForDeepRecord()
    {
        // _acme-challenge.media.example.com: no zone for media.example.com,
        // but example.com exists — the walk must find it and create the record there.
        var handler = new ScriptedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/zones?name="))
            {
                var wanted = url.Contains("name=example.com");
                return Json(wanted
                    ? """{"success":true,"result":[{"id":"zone123"}],"errors":[]}"""
                    : """{"success":true,"result":[],"errors":[]}""");
            }

            if (url.EndsWith("/zones/zone123/dns_records", StringComparison.Ordinal))
            {
                return Json("""{"success":true,"result":{"id":"rec456"},"errors":[]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = new CloudflareDnsProvider(
            new HttpClient(handler), "secret-token", NullLogger.Instance);

        var handle = await provider.CreateTxtRecordAsync(
            "_acme-challenge.media.example.com", "digest", CancellationToken.None);

        Assert.Equal("rec456", handle);
        Assert.Contains(handler.Requests, r => r.Contains("zones/zone123/dns_records"));
    }

    [Fact]
    public async Task NoZoneAnywhere_ThrowsWithoutLeakingToken()
    {
        var handler = new ScriptedHandler(_ => Json("""{"success":true,"result":[],"errors":[]}"""));
        var provider = new CloudflareDnsProvider(
            new HttpClient(handler), "secret-token", NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CreateTxtRecordAsync("_acme-challenge.nozone.example", "v", CancellationToken.None));

        Assert.DoesNotContain("secret-token", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No Cloudflare zone found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiError_SurfacesCloudflareMessage()
    {
        var handler = new ScriptedHandler(_ =>
            Json("""{"success":false,"result":null,"errors":[{"code":9109,"message":"Invalid access token"}]}"""));

        var provider = new CloudflareDnsProvider(
            new HttpClient(handler), "secret-token", NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CreateTxtRecordAsync("_acme-challenge.example.com", "v", CancellationToken.None));

        Assert.Contains("9109", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", ex.Message, StringComparison.Ordinal);
    }
}
