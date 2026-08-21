using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Full HTTP-01 issuance against Pebble — the tokenless default path — through the very
/// listener the plugin ships. Nothing stands in for the server here: Pebble's validation
/// request lands on <see cref="AcmeHttpServer"/> itself, on the port Pebble validates
/// against (5002), while challtestsrv's DNS points the test domain at this machine.
/// Requires BACKPORCH_PEBBLE_DIR, BACKPORCH_CHALLTESTSRV, and BACKPORCH_HTTP01_IP (an
/// address for this host that the Pebble container can reach).
/// </summary>
[Collection("Pebble")]
public class PebbleHttpIssuanceTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task IssuesOverHttp01EndToEnd()
    {
        var directory = Environment.GetEnvironmentVariable("BACKPORCH_PEBBLE_DIR");
        var challSrv = Environment.GetEnvironmentVariable("BACKPORCH_CHALLTESTSRV");
        var selfIp = Environment.GetEnvironmentVariable("BACKPORCH_HTTP01_IP");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(challSrv) || string.IsNullOrEmpty(selfIp))
        {
            return; // Pebble not running; exercised in CI.
        }

        const string domain = "http.backporch.test";
        const int ValidationPort = 5002;

        var pfxPath = Path.Combine(Path.GetTempPath(), $"backporch-http01-{Guid.NewGuid():N}.pfx");
        var config = new PluginConfiguration
        {
            Enabled = true,
            Challenge = ChallengeKind.Http,
            Domain = domain,
            AccountEmail = "http@backporch.test",
            CertificatePath = pfxPath,
            ChallengeListenPort = ValidationPort,
            PublicHttpsPort = 443
        };

        using var management = new HttpClient();

        // Point the domain at this machine so Pebble's validation lands on our listener.
        var addA = await management.PostAsync(
            challSrv + "/add-a",
            new StringContent(
                JsonSerializer.Serialize(new { host = domain + ".", addresses = new[] { selfIp } }),
                Encoding.UTF8,
                "application/json"));
        addA.EnsureSuccessStatusCode();

        var store = new HttpChallengeStore();
        var service = new AcmeService(
            NullLogger<AcmeService>.Instance, new SingleClientFactory(), new IssuanceState(), store);

        // The shipped listener, on the shipped code path — not a test stand-in.
        var listener = await AcmeHttpServer.StartForTestAsync(store, () => config, ValidationPort);

        var insecureHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var acmeHttp = new HttpClient(insecureHandler);

        try
        {
            var expiry = await service.IssueCertificateAsync(
                config, dnsProvider: null, new Uri(directory), acmeHttp, CancellationToken.None);

            Assert.True(File.Exists(pfxPath), "PFX was not written");
            Assert.True(expiry > DateTime.UtcNow.AddDays(1), "expiry not in the future");

            var issued = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadPkcs12FromFile(pfxPath, config.CertificatePassword);
            Assert.True(issued.HasPrivateKey, "no private key in PFX");
            Assert.True(issued.MatchesHostname(domain), "certificate does not match the domain");

            // The pipeline must clean up after itself: no answer outlives its authorization.
            Assert.Equal(0, store.Count);

            // The same socket the certificate authority just used must not be a way into
            // Jellyfin. This is the property the whole design exists for, checked against
            // the live listener rather than inferred.
            using var plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri("http://127.0.0.1:" + ValidationPort)
            };

            var web = await plain.GetAsync("/web/index.html");
            Assert.Equal(HttpStatusCode.MovedPermanently, web.StatusCode);
            Assert.Equal(new Uri("https://" + domain + "/web/index.html"), web.Headers.Location);

            var spent = await plain.GetAsync("/.well-known/acme-challenge/anything");
            Assert.Equal(HttpStatusCode.NotFound, spent.StatusCode);
        }
        finally
        {
            await listener.StopAsync();
            listener.Dispose();
            await management.PostAsync(
                challSrv + "/clear-a",
                new StringContent(
                    JsonSerializer.Serialize(new { host = domain + "." }), Encoding.UTF8, "application/json"));
            if (File.Exists(pfxPath))
            {
                File.Delete(pfxPath);
            }
        }
    }
}
