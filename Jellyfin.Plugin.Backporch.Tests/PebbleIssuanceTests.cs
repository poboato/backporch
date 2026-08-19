using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Full end-to-end issuance against Let's Encrypt's Pebble test CA. Requires the
/// Pebble + challtestsrv containers; set BACKPORCH_PEBBLE_DIR (e.g.
/// https://localhost:14000/dir) and BACKPORCH_CHALLTESTSRV (e.g.
/// http://localhost:8055) to enable, otherwise the test is skipped.
/// </summary>
public class PebbleIssuanceTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task IssuesARealCertificateEndToEnd()
    {
        var directory = Environment.GetEnvironmentVariable("BACKPORCH_PEBBLE_DIR");
        var challSrv = Environment.GetEnvironmentVariable("BACKPORCH_CHALLTESTSRV");
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(challSrv))
        {
            return; // Pebble not running; covered by CI's unit lane only when present.
        }

        var pfxPath = Path.Combine(Path.GetTempPath(), $"backporch-pebble-{Guid.NewGuid():N}.pfx");
        var config = new PluginConfiguration
        {
            Enabled = true,
            Domain = "backporch.test",
            AccountEmail = "test@backporch.test",
            CertificatePath = pfxPath,
            DnsPropagationSeconds = 1,
            RenewDaysBeforeExpiry = 30
        };

        // Pebble serves self-signed TLS; trust it for the test only.
        var insecureHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        using var acmeHttp = new HttpClient(insecureHandler);

        var service = new AcmeService(NullLogger<AcmeService>.Instance, new SingleClientFactory());
        var dns = new ChallTestSrvDnsProvider(new HttpClient(), challSrv);

        try
        {
            var expiry = await service.IssueCertificateAsync(
                config, dns, new Uri(directory), acmeHttp, CancellationToken.None);

            Assert.True(File.Exists(pfxPath), "PFX was not written");
            Assert.True(expiry > DateTime.UtcNow.AddDays(1), "expiry not in the future");

            // The bundle must load with the generated password, carry a private key,
            // and be issued to the requested domain.
            var issued = X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath, config.CertificatePassword);
            Assert.True(issued.HasPrivateKey, "no private key in PFX");

            // Modern CAs issue with an empty Subject; the identity lives in the SAN,
            // so verify it the way TLS clients do.
            Assert.True(issued.MatchesHostname("backporch.test"), "certificate does not match the domain");

            // Unix permissions: owner read/write only.
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(pfxPath);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }
        }
        finally
        {
            if (File.Exists(pfxPath))
            {
                File.Delete(pfxPath);
            }
        }
    }
}
