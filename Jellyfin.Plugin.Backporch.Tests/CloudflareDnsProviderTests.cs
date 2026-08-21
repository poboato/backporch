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
    public async Task HtmlErrorPage_ThrowsJsonException_WhichCallersMustCatch()
    {
        // During a Cloudflare outage — or behind an intercepting proxy — the reply is an
        // HTML error page, not the JSON envelope. This pins the exception type that the
        // preflight endpoint's catch filter has to cover; without it the whole check 500s
        // and the page blames the token.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html><body>503 Service Unavailable</body></html>", Encoding.UTF8, "text/html")
        });

        var provider = new CloudflareDnsProvider(
            new HttpClient(handler), "secret-token", NullLogger.Instance);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => provider.VerifyAccessAsync("media.example.com", CancellationToken.None));
    }

    [Fact]
    public async Task ZoneNotFound_NamesTheMissingPermission()
    {
        // A token made from Cloudflare's "Edit zone DNS" template can write records but
        // cannot list zones, which looks exactly like "no such zone". The message has to
        // name the real cause or the user re-checks their domain forever.
        var handler = new ScriptedHandler(_ => Json("""{"success":true,"result":[],"errors":[]}"""));
        var provider = new CloudflareDnsProvider(
            new HttpClient(handler), "secret-token", NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.VerifyAccessAsync("media.example.com", CancellationToken.None));

        Assert.Contains("Zone → Read", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DNS → Edit", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", ex.Message, StringComparison.Ordinal);
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
