using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Issues twice for the same domain against a CA configured to reuse authorizations —
/// what Let's Encrypt does for about 30 days after a successful validation.
/// </summary>
/// <remarks>
/// The second run receives an authorization the CA already considers valid. Posting a
/// challenge validation to one of those is an error ("authorization must be pending"),
/// so the pipeline has to recognise the state and skip straight to finalizing. Run the
/// Pebble container with PEBBLE_AUTHZREUSE=100 to force it on every order; without that
/// variable this test still passes, it just isn't exercising reuse.
/// </remarks>
[Collection("Pebble")]
public class PebbleReissueTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task SecondIssuance_SucceedsWhenAuthorizationIsReused()
    {
        var directory = Environment.GetEnvironmentVariable("BACKPORCH_PEBBLE_DIR");
        var challSrv = Environment.GetEnvironmentVariable("BACKPORCH_CHALLTESTSRV");
        var selfIp = Environment.GetEnvironmentVariable("BACKPORCH_HTTP01_IP");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(challSrv) || string.IsNullOrEmpty(selfIp))
        {
            return; // Pebble not running; exercised in CI.
        }

        const string domain = "reissue.backporch.test";
        var pfxPath = Path.Combine(Path.GetTempPath(), $"backporch-reissue-{Guid.NewGuid():N}.pfx");
        var config = new PluginConfiguration
        {
            Enabled = true,
            Challenge = ChallengeKind.Http,
            Domain = domain,
            AccountEmail = "reissue@backporch.test",
            CertificatePath = pfxPath
        };

        using var management = new HttpClient();
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

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://*:5002/");
        listener.Start();
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
                if (store.TryGet(token, out var keyAuthorization))
                {
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
            var first = await service.IssueCertificateAsync(
                config, dnsProvider: null, new Uri(directory), acmeHttp, CancellationToken.None);
            Assert.True(first > DateTime.UtcNow.AddDays(1));

            // Same account, same domain, immediately again — the CA hands back the
            // authorization it just validated.
            var second = await service.IssueCertificateAsync(
                config, dnsProvider: null, new Uri(directory), acmeHttp, CancellationToken.None);

            Assert.True(second > DateTime.UtcNow.AddDays(1), "reissued expiry not in the future");
            Assert.True(File.Exists(pfxPath), "PFX was not rewritten on reissue");

            var issued = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadPkcs12FromFile(pfxPath, config.CertificatePassword);
            Assert.True(issued.MatchesHostname(domain), "reissued certificate does not match the domain");
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
