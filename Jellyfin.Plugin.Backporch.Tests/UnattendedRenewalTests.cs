using System.Reflection;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The nightly task runs with nobody at the configuration page. Modes that need a human
/// must say so immediately instead of blocking on a timeout that cannot be satisfied.
/// </summary>
public class UnattendedRenewalTests
{
    private static string? UnattendedProblem(PluginConfiguration config)
    {
        var method = typeof(AcmeService).GetMethod(
            "UnattendedProblem",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("UnattendedProblem is missing.");

        return (string?)method.Invoke(null, new object[] { config });
    }

    [Fact]
    public void ManualDns_CannotRenewUnattended()
    {
        var config = new PluginConfiguration
        {
            Challenge = ChallengeKind.Dns,
            DnsProvider = DnsProviderKind.Manual
        };

        var problem = UnattendedProblem(config);

        Assert.NotNull(problem);
        Assert.Contains("Manual DNS", problem, StringComparison.Ordinal);
        // It must point somewhere useful, not just refuse.
        Assert.Contains("Backporch page", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpProof_RenewsUnattended()
    {
        var config = new PluginConfiguration { Challenge = ChallengeKind.Http };

        Assert.Null(UnattendedProblem(config));
    }

    [Fact]
    public void CloudflareDns_RenewsUnattended()
    {
        var config = new PluginConfiguration
        {
            Challenge = ChallengeKind.Dns,
            DnsProvider = DnsProviderKind.Cloudflare,
            DnsApiToken = "token"
        };

        Assert.Null(UnattendedProblem(config));
    }
}
