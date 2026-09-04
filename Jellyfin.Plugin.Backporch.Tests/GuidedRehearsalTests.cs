using System.Reflection;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The guided flow issues twice: once against staging to prove the setup costs nothing to
/// get wrong, then for real. Both runs work from copies of the same configuration, and the
/// rehearsal's leftovers are deleted afterwards — so what the copy shares with the original,
/// and what it deliberately does not, is load-bearing.
/// </summary>
public class GuidedRehearsalTests
{
    private static PluginConfiguration Rehearsal(PluginConfiguration config)
    {
        var method = typeof(AcmeService).GetMethod(
            "CloneForTestRun", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CloneForTestRun is missing.");

        return (PluginConfiguration)method.Invoke(null, new object[] { config })!;
    }

    private static PluginConfiguration Live() => new()
    {
        Enabled = true,
        Challenge = ChallengeKind.Http,
        Domain = "jellyfin.example.com",
        ExtraDomains = new List<string> { "home.example.com", "sonarr.example.com" },
        AccountEmail = "someone@example.com",
        CertificatePath = "/data/backporch/certificate.pfx"
    };

    /// <summary>
    /// The guided run deletes the rehearsal's output in a <c>finally</c>, without checking
    /// what it is deleting. If the two runs ever shared an output path, a single press of
    /// "Get my certificate" would delete the certificate Jellyfin is currently serving —
    /// and the server would come back on a self-signed one at its next restart.
    /// </summary>
    [Fact]
    public void TheRehearsalNeverWritesWhereTheRealCertificateLives()
    {
        var config = Live();
        var rehearsal = Rehearsal(config);

        Assert.NotEqual(config.CertificatePath, rehearsal.CertificatePath);
        Assert.StartsWith(config.CertificatePath, rehearsal.CertificatePath, StringComparison.Ordinal);
        Assert.Equal("/data/backporch/certificate.pfx", config.CertificatePath);
    }

    /// <summary>
    /// A rehearsal that proved a different set of names has proved nothing useful: the CA
    /// opens one authorization per name, so a name only production carries is a name only
    /// production ever tries — which is exactly the failure the rehearsal exists to catch
    /// before it costs a production rate limit.
    /// </summary>
    [Fact]
    public void TheRehearsalProvesEveryNameTheRealRunWillAskFor()
    {
        var config = Live();

        Assert.Equal(config.AllDomains(), Rehearsal(config).AllDomains());
    }

    /// <summary>
    /// <c>Clone</c> goes through a serializer precisely so nothing is shared by reference.
    /// A shallow copy would hand the rehearsal the live name list, and anything it did to
    /// that list would silently rewrite what the user typed.
    /// </summary>
    [Fact]
    public void TheRehearsalGetsItsOwnNameListNotTheLiveOne()
    {
        var config = Live();
        var rehearsal = Rehearsal(config);

        rehearsal.ExtraDomains.Clear();
        rehearsal.ExtraDomains.Add("something-else.example.com");

        Assert.Equal(
            new[] { "home.example.com", "sonarr.example.com" }, config.ExtraDomains);
    }

    /// <summary>
    /// <c>AllDomains</c> promises a fresh list each call so callers can hand it straight to
    /// the ACME client. The client sorts and mutates what it is given; if that were the
    /// configuration's own list, one issuance would reorder the user's names on disk.
    /// </summary>
    [Fact]
    public void AllDomainsHandsBackAListTheCallerMayKeep()
    {
        var config = Live();

        var names = config.AllDomains();
        names.Add("injected.example.com");
        names.RemoveAt(0);

        Assert.Equal(
            new[] { "jellyfin.example.com", "home.example.com", "sonarr.example.com" },
            config.AllDomains());
        Assert.Equal(new[] { "home.example.com", "sonarr.example.com" }, config.ExtraDomains);
    }
}
