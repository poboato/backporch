using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Renewal used to be decided by expiry alone, which was right while a certificate
/// carried one name and could never change. Now that names can be added after issuance,
/// expiry is no longer the only reason a certificate is out of date: the one on disk can
/// be perfectly valid for another two months and still not cover what was asked for.
/// </summary>
public sealed class RenewalNameCoverageTests : IDisposable
{
    private const string Password = "test-password";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "backporch-names-" + Guid.NewGuid().ToString("N"));

    public RenewalNameCoverageTests() => System.IO.Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory))
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// Writes a self-signed certificate covering exactly the given names. Self-signed is
    /// enough here: nothing under test verifies a chain, only which names are on it.
    /// </summary>
    private string WriteCertificate(params string[] names)
    {
        var path = Path.Combine(_directory, "certificate.pfx");

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + names[0], key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var alternatives = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            alternatives.AddDnsName(name);
        }

        request.CertificateExtensions.Add(alternatives.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));

        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return path;
    }

    private PluginConfiguration Configured(string path, string domain, params string[] extra)
        => new()
        {
            CertificatePath = path,
            CertificatePassword = Password,
            CertificateExpiryUtc = DateTime.UtcNow.AddDays(89),
            RenewDaysBeforeExpiry = 30,
            Domain = domain,
            ExtraDomains = extra.ToList()
        };

    [Fact]
    public void ACertificateCoveringEveryNameIsLeftAlone()
    {
        var path = WriteCertificate("media.example.com", "books.example.com");
        var config = Configured(path, "media.example.com", "books.example.com");

        Assert.Empty(AcmeService.MissingNames(config));
        Assert.False(AcmeService.NeedsRenewal(config));
    }

    /// <summary>
    /// The regression this exists for. Adding a name and saving used to change nothing:
    /// the daily task read an expiry two months out, answered "no action needed", and
    /// went on answering it — while the new name served nothing and the setup page said
    /// the certificate was valid and renewing automatically.
    /// </summary>
    [Fact]
    public void ANameAddedAfterIssuanceForcesRenewal()
    {
        var path = WriteCertificate("media.example.com");
        var config = Configured(path, "media.example.com", "books.example.com");

        Assert.True(AcmeService.NeedsRenewal(config));
        Assert.Equal(new[] { "books.example.com" }, AcmeService.MissingNames(config));
    }

    [Fact]
    public void ChangingThePrimaryNameForcesRenewal()
    {
        var path = WriteCertificate("media.example.com");
        var config = Configured(path, "cinema.example.com");

        Assert.True(AcmeService.NeedsRenewal(config));
    }

    /// <summary>
    /// A certificate authority normalises names to lower case, so a name typed with
    /// capitals must not read as missing — that would re-issue on every check for as long
    /// as the capitals stayed.
    /// </summary>
    [Fact]
    public void CapitalsDoNotCountAsAMissingName()
    {
        var path = WriteCertificate("media.example.com");
        var config = Configured(path, "Media.Example.COM");

        Assert.Empty(AcmeService.MissingNames(config));
        Assert.False(AcmeService.NeedsRenewal(config));
    }

    /// <summary>
    /// Removing a name is not a reason to re-issue. The certificate still covers
    /// everything asked of it; the extra name is merely unused, and renewing over it
    /// would spend a rate limit to remove something harmless.
    /// </summary>
    [Fact]
    public void ANameNoLongerWantedIsNotAReasonToReIssue()
    {
        var path = WriteCertificate("media.example.com", "books.example.com");
        var config = Configured(path, "media.example.com");

        Assert.False(AcmeService.NeedsRenewal(config));
    }

    /// <summary>
    /// A file that cannot be parsed must not read as "covers nothing" and so force a
    /// renewal on every check — that would spend the authority's rate limit daily over a
    /// file we cannot even open. Expiry has the last word when the names are unknowable.
    /// </summary>
    [Fact]
    public void AnUnreadableCertificateFallsBackToExpiry()
    {
        var path = Path.Combine(_directory, "certificate.pfx");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        var config = Configured(path, "media.example.com", "books.example.com");

        Assert.Empty(AcmeService.MissingNames(config));
        Assert.False(AcmeService.NeedsRenewal(config));
    }

    /// <summary>
    /// The wrong password is the same situation as an unreadable file, and is worth
    /// pinning separately because it is the one an administrator can actually cause.
    /// </summary>
    [Fact]
    public void TheWrongPasswordFallsBackToExpiry()
    {
        var path = WriteCertificate("media.example.com");
        var config = Configured(path, "media.example.com", "books.example.com");
        config.CertificatePassword = "not-the-password";

        Assert.Empty(AcmeService.MissingNames(config));
        Assert.False(AcmeService.NeedsRenewal(config));
    }

    /// <summary>
    /// Reading the names is memoised so the setup page's two-second poll does not re-open
    /// a PKCS#12 each time. A cache allowed to go stale would put the original bug
    /// straight back: a certificate that has just gained a name would still read as
    /// missing it, and one that lost a name would read as covering it.
    /// </summary>
    [Fact]
    public void ReplacingTheCertificateIsNoticed()
    {
        var path = WriteCertificate("media.example.com");
        var config = Configured(path, "media.example.com", "books.example.com");

        Assert.Equal(new[] { "books.example.com" }, AcmeService.MissingNames(config));

        WriteCertificate("media.example.com", "books.example.com");

        Assert.Empty(AcmeService.MissingNames(config));
    }

    /// <summary>
    /// Expiry still decides when the names are all present, so the original behaviour has
    /// to survive the new check sitting in front of it.
    /// </summary>
    [Fact]
    public void ExpiryStillDecidesWhenEveryNameIsCovered()
    {
        var path = WriteCertificate("media.example.com");
        var config = Configured(path, "media.example.com");
        config.CertificateExpiryUtc = DateTime.UtcNow.AddDays(10);

        Assert.True(AcmeService.NeedsRenewal(config));
    }
}
