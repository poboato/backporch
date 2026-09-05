using System.Reflection;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Http;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The gate every setup attempt passes through. It runs before a single byte reaches the
/// certificate authority, so what it refuses — and what it says while refusing — is the
/// only feedback the person at the configuration page gets. A message that names the wrong
/// thing sends them off fixing a setting that was never the problem.
/// </summary>
public class SetupValidationTests
{
    private static string? Validate(PluginConfiguration config)
    {
        var method = typeof(AcmeService).GetMethod(
            "Validate", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Validate is missing.");

        return (string?)method.Invoke(null, new object[] { config });
    }

    /// <summary>A configuration the guided flow would accept, for each test to spoil in one way.</summary>
    private static PluginConfiguration Ready() => new()
    {
        Enabled = true,
        Challenge = ChallengeKind.Http,
        Domain = "jellyfin.example.com",
        AccountEmail = "someone@example.com",
        CertificatePath = "/data/backporch/certificate.pfx"
    };

    [Fact]
    public void AReadyConfigurationPasses()
        => Assert.Null(Validate(Ready()));

    /// <summary>
    /// Being switched off is reported ahead of every other fault. It is the one condition
    /// that is deliberate rather than a mistake, and reporting a missing domain to someone
    /// who simply has the plugin turned off sends them to the wrong box.
    /// </summary>
    [Fact]
    public void BeingSwitchedOffIsSaidFirst()
    {
        var config = Ready();
        config.Enabled = false;
        config.Domain = string.Empty;
        config.AccountEmail = string.Empty;

        var problem = Validate(config);

        Assert.NotNull(problem);
        Assert.Contains("Disabled", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankDomainIsRefused()
    {
        var config = Ready();
        config.Domain = "   ";

        Assert.Equal("No domain configured.", Validate(config));
    }

    /// <summary>
    /// The primary name is the certificate's common name and the address a stray
    /// plain-HTTP request is sent to. With it blank, <c>AllDomains()</c> would happily
    /// promote the first extra name into both roles — issuing a certificate whose subject
    /// is some other application, for a server that then redirects to it.
    /// </summary>
    [Fact]
    public void ExtraNamesCannotStandInForAMissingPrimaryName()
    {
        var config = Ready();
        config.Domain = string.Empty;
        config.ExtraDomains = new List<string> { "sonarr.example.com", "home.example.com" };

        // The extras alone would look like a perfectly good name list.
        Assert.Equal(new[] { "sonarr.example.com", "home.example.com" }, config.AllDomains());
        Assert.Equal("No domain configured.", Validate(config));
    }

    [Fact]
    public void AMissingContactEmailIsRefused()
    {
        var config = Ready();
        config.AccountEmail = "  ";

        Assert.Equal("No contact email configured.", Validate(config));
    }

    [Fact]
    public void AMissingOutputPathIsRefused()
    {
        var config = Ready();
        config.CertificatePath = string.Empty;

        var problem = Validate(config);

        Assert.NotNull(problem);
        Assert.Contains("certificate output path", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The setup page collapses the proof choice into one dropdown but only ever writes
    /// <c>DnsProvider</c> when a DNS option is picked — so someone who looks at Cloudflare
    /// and then goes back to the recommended server proof is left with
    /// <c>Challenge = Http</c> and a leftover <c>DnsProvider = Cloudflare</c> carrying no
    /// token. That is the default setup, and it must not be refused for want of a token
    /// nothing is going to use.
    /// </summary>
    [Fact]
    public void ALeftoverDnsProviderDoesNotBlockTheServerProof()
    {
        var config = Ready();
        config.Challenge = ChallengeKind.Http;
        config.DnsProvider = DnsProviderKind.Cloudflare;
        config.DnsApiToken = string.Empty;

        Assert.Null(Validate(config));
    }

    [Fact]
    public void DnsProofWithoutAProviderIsRefused()
    {
        var config = Ready();
        config.Challenge = ChallengeKind.Dns;
        config.DnsProvider = DnsProviderKind.None;

        var problem = Validate(config);

        Assert.NotNull(problem);
        Assert.Contains("DNS challenge record", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudflareWithoutATokenIsRefusedAndNamed()
    {
        var config = Ready();
        config.Challenge = ChallengeKind.Dns;
        config.DnsProvider = DnsProviderKind.Cloudflare;
        config.DnsApiToken = "   ";

        var problem = Validate(config);

        Assert.NotNull(problem);
        Assert.Contains("Cloudflare", problem, StringComparison.Ordinal);
        Assert.Contains("token", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// The by-hand option is the fallback for every DNS host without an API, so it must
    /// never be made to want a token.
    /// </summary>
    [Fact]
    public void ManualDnsNeedsNoToken()
    {
        var config = Ready();
        config.Challenge = ChallengeKind.Dns;
        config.DnsProvider = DnsProviderKind.Manual;
        config.DnsApiToken = string.Empty;

        Assert.Null(Validate(config));
    }

    /// <summary>
    /// DOCUMENTS A BUG (asserts today's behaviour so the suite stays green).
    /// <para>
    /// Validation tolerates surrounding whitespace on the primary name, because it checks
    /// <c>AllDomains()</c>, which trims. The plain-HTTP listener does not: it validates the
    /// raw <see cref="PluginConfiguration.Domain"/> and gives up on anything that is not a
    /// hostname. So a padded domain passes the gate and then issues with no listener bound
    /// — the certificate authority's fetch reaches nothing, and the only clue is a
    /// connection failure from the CA. The two should agree; today they do not.
    /// </para>
    /// <para>
    /// Not reachable through the setup page, which trims on save, but reachable by editing
    /// the persisted configuration by hand.
    /// </para>
    /// </summary>
    [Fact]
    public void PaddedDomain_StillBindsTheListener()
    {
        var config = Ready();
        config.Domain = "  jellyfin.example.com  ";

        Assert.Null(Validate(config));

        // Validation reads the trimmed name list while the listener gate read the raw
        // value, so a padded domain used to validate happily and then never bind — an
        // issuance that could only fail by timing out, blamed on the user's port forward.
        Assert.Equal(80, AcmeHttpServer.WantedPort(config));
        Assert.Equal("jellyfin.example.com", config.Domain);
    }
}
