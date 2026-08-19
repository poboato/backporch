using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

public class RenewalWindowTests
{
    private static PluginConfiguration ConfigWithCert(double daysLeft, int threshold)
    {
        var path = Path.GetTempFileName();
        return new PluginConfiguration
        {
            CertificatePath = path,
            CertificateExpiryUtc = DateTime.UtcNow.AddDays(daysLeft),
            RenewDaysBeforeExpiry = threshold
        };
    }

    [Fact]
    public void MissingFile_NeedsRenewal()
    {
        var config = new PluginConfiguration
        {
            CertificatePath = "/nonexistent/never.pfx",
            CertificateExpiryUtc = DateTime.UtcNow.AddDays(90)
        };

        Assert.True(AcmeService.NeedsRenewal(config));
    }

    [Fact]
    public void UnknownExpiry_NeedsRenewal()
    {
        var path = Path.GetTempFileName();
        var config = new PluginConfiguration { CertificatePath = path };

        Assert.True(AcmeService.NeedsRenewal(config));
        File.Delete(path);
    }

    [Fact]
    public void HealthyCertificate_NoRenewal()
    {
        var config = ConfigWithCert(daysLeft: 60, threshold: 30);
        Assert.False(AcmeService.NeedsRenewal(config));
        File.Delete(config.CertificatePath);
    }

    [Fact]
    public void InsideThreshold_NeedsRenewal()
    {
        var config = ConfigWithCert(daysLeft: 29, threshold: 30);
        Assert.True(AcmeService.NeedsRenewal(config));
        File.Delete(config.CertificatePath);
    }

    [Fact]
    public void ExpiredCertificate_NeedsRenewal()
    {
        var config = ConfigWithCert(daysLeft: -1, threshold: 30);
        Assert.True(AcmeService.NeedsRenewal(config));
        File.Delete(config.CertificatePath);
    }
}
