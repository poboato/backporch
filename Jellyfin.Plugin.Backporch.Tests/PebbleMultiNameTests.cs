using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// One certificate for three names, proven end to end: the certificate authority opens an
/// authorization per name and every one of them is answered by the single listener the
/// plugin ships. This is the property that lets one issuance serve a whole machine, and
/// it cannot be checked without a real certificate authority, so it lives here.
/// </summary>
[Collection("Pebble")]
public class PebbleMultiNameTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task IssuesOneCertificateCoveringEveryName()
    {
        var directory = Environment.GetEnvironmentVariable("BACKPORCH_PEBBLE_DIR");
        var challSrv = Environment.GetEnvironmentVariable("BACKPORCH_CHALLTESTSRV");
        var selfIp = Environment.GetEnvironmentVariable("BACKPORCH_HTTP01_IP");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(challSrv) || string.IsNullOrEmpty(selfIp))
        {
            return; // Pebble not running; exercised in CI.
        }

        const int ValidationPort = 5002;
        const string Primary = "jellyfin.multi.test";
        var names = new[] { Primary, "home.multi.test", "sonarr.multi.test" };

        var dir = Path.Combine(Path.GetTempPath(), "backporch-multi-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);

        var config = new PluginConfiguration
        {
            Enabled = true,
            Challenge = ChallengeKind.Http,
            Domain = Primary,
            ExtraDomains = names.Skip(1).ToList(),
            AccountEmail = "multi@backporch.test",
            CertificatePath = Path.Combine(dir, "certificate.pfx"),
            PemCertificatePath = Path.Combine(dir, "fullchain.pem"),
            PemPrivateKeyPath = Path.Combine(dir, "privkey.pem"),
            ChallengeListenPort = ValidationPort,
            PublicHttpsPort = 443
        };

        using var management = new HttpClient();

        // Every name has to resolve to this machine, because every name is validated.
        foreach (var name in names)
        {
            var addA = await management.PostAsync(
                challSrv + "/add-a",
                new StringContent(
                    JsonSerializer.Serialize(new { host = name + ".", addresses = new[] { selfIp } }),
                    Encoding.UTF8,
                    "application/json"));
            addA.EnsureSuccessStatusCode();
        }

        var store = new HttpChallengeStore();
        var service = new AcmeService(
            NullLogger<AcmeService>.Instance, new SingleClientFactory(), new IssuanceState(), store);

        var listener = await AcmeHttpServer.StartForTestAsync(store, () => config, ValidationPort);

        var insecureHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var acmeHttp = new HttpClient(insecureHandler);

        try
        {
            await service.IssueCertificateAsync(
                config, dnsProvider: null, new Uri(directory), acmeHttp, CancellationToken.None);

            using var issued = X509CertificateLoader.LoadPkcs12FromFile(
                config.CertificatePath, config.CertificatePassword);

            // The point of the whole exercise: one certificate, every name.
            foreach (var name in names)
            {
                Assert.True(issued.MatchesHostname(name), name + " is not on the certificate");
            }

            Assert.False(
                issued.MatchesHostname("plex.multi.test"),
                "the certificate matched a name that was never ordered");

            // The PEM pair is what a reverse proxy reads, so its shape matters as much as
            // its existence: leaf first, then the issuers that complete the chain.
            var pem = await File.ReadAllTextAsync(config.PemCertificatePath);
            Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem, StringComparison.Ordinal);
            Assert.True(
                pem.Split("-----BEGIN CERTIFICATE-----").Length - 1 >= 2,
                "the PEM chain has no issuer, so clients would reject it");

            var key = await File.ReadAllTextAsync(config.PemPrivateKeyPath);
            Assert.Contains("PRIVATE KEY", key, StringComparison.Ordinal);

            if (!OperatingSystem.IsWindows())
            {
                // The key is a secret and the chain is not. Both are written by the same
                // routine, so this is really a check that the distinction survived it.
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(config.PemPrivateKeyPath));

                Assert.True(
                    File.GetUnixFileMode(config.PemCertificatePath).HasFlag(UnixFileMode.OtherRead),
                    "a proxy running as another user could not read the chain");
            }

            Assert.Equal(0, store.Count);
        }
        finally
        {
            await listener.StopAsync();
            listener.Dispose();

            foreach (var name in names)
            {
                await management.PostAsync(
                    challSrv + "/clear-a",
                    new StringContent(
                        JsonSerializer.Serialize(new { host = name + "." }),
                        Encoding.UTF8,
                        "application/json"));
            }

            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }
}
