using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Full HTTP-01 issuance against Pebble — the tokenless default path. The test plays
/// Jellyfin's part: an HTTP listener on Pebble's validation port (5002) answering
/// from the same <see cref="HttpChallengeStore"/> the service publishes into, while
/// challtestsrv's DNS points the test domain at this machine. Requires
/// BACKPORCH_PEBBLE_DIR, BACKPORCH_CHALLTESTSRV, and BACKPORCH_HTTP01_IP (an address
/// for this host that the Pebble container can reach, e.g. the docker network gateway).
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
        var pfxPath = Path.Combine(Path.GetTempPath(), $"backporch-http01-{Guid.NewGuid():N}.pfx");
        var config = new PluginConfiguration
        {
            Enabled = true,
            Challenge = ChallengeKind.Http,
            Domain = domain,
            AccountEmail = "http@backporch.test",
            CertificatePath = pfxPath
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

        // Stand in for Jellyfin: serve /.well-known/acme-challenge/{token} from the store,
        // exactly as the plugin's anonymous controller does.
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://*:5002/");
        listener.Start();
        var served = new List<string>();
        var serveLoop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                var token = context.Request.Url!.AbsolutePath.Split('/')[^1];
                if (context.Request.Url.AbsolutePath.StartsWith("/.well-known/acme-challenge/", StringComparison.Ordinal)
                    && store.TryGet(token, out var keyAuthorization))
                {
                    served.Add(token);
                    var bytes = Encoding.ASCII.GetBytes(keyAuthorization);
                    context.Response.ContentType = "text/plain";
                    context.Response.OutputStream.Write(bytes);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                context.Response.Close();
            }
        });

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
            Assert.NotEmpty(served);

            var issued = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadPkcs12FromFile(pfxPath, config.CertificatePassword);
            Assert.True(issued.HasPrivateKey, "no private key in PFX");
            Assert.True(issued.MatchesHostname(domain), "certificate does not match the domain");

            // The pipeline must clean up after itself: every served token is gone.
            foreach (var token in served)
            {
                Assert.False(store.TryGet(token, out _), "challenge answer left behind after issuance");
            }
        }
        finally
        {
            listener.Stop();
            await serveLoop;
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
