using System.Reflection;
using Jellyfin.Plugin.Backporch.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Guards the configuration-copying rules that keep a long issuance from losing its
/// results — or from undoing edits made while it ran.
/// </summary>
public class ConfigurationLifecycleTests
{
    private static PluginConfiguration Populated() => new()
    {
        Enabled = true,
        Challenge = ChallengeKind.Dns,
        Domain = "media.example.com",
        AccountEmail = "someone@example.com",
        DnsProvider = DnsProviderKind.Cloudflare,
        DnsApiToken = "token-value",
        UseStaging = false,
        DirectoryUrl = "https://example.test/dir",
        RenewDaysBeforeExpiry = 21,
        DnsPropagationSeconds = 90,
        CertificatePath = "/data/cert.pfx",
        CertificatePassword = "pw",
        AccountKeyPem = "-----BEGIN KEY-----",
        LastAttemptUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        LastResult = "Issued",
        CertificateExpiryUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Clone_CarriesEveryProperty()
    {
        // The staging dry run issues against a copy. Any property left behind would make
        // the practice run prove a different configuration than production uses — so this
        // asserts against the property list itself rather than a hand-written field list.
        var original = Populated();
        var copy = original.Clone();

        var properties = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

        foreach (var property in properties)
        {
            Assert.Equal(property.GetValue(original), property.GetValue(copy));
        }
    }

    [Fact]
    public void Clone_IsIndependentOfTheOriginal()
    {
        var original = Populated();
        var copy = original.Clone();

        copy.Domain = "other.example.com";
        copy.UseStaging = true;

        Assert.Equal("media.example.com", original.Domain);
        Assert.False(original.UseStaging);
    }

    [Fact]
    public void Clone_OfDefaults_KeepsTokenlessDefault()
    {
        var copy = new PluginConfiguration().Clone();

        Assert.Equal(ChallengeKind.Http, copy.Challenge);
        Assert.True(copy.UseStaging);
        Assert.False(copy.Enabled);
    }
}
