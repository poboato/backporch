using System.Reflection;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// One certificate carrying several names is what lets a single issuance serve every
/// application on a machine rather than only this one. These cover the name list itself,
/// the validation that guards it, and where a plain-HTTP request for each name lands.
/// </summary>
public class MultiNameTests
{
    private static PluginConfiguration Configured(string primary, params string[] extra)
        => new()
        {
            Enabled = true,
            Challenge = ChallengeKind.Http,
            Domain = primary,
            ExtraDomains = extra.ToList(),
            AccountEmail = "someone@example.com",
            CertificatePath = "/tmp/backporch-test.pfx"
        };

    [Fact]
    public void ThePrimaryNameComesFirst()
    {
        var names = Configured("jellyfin.example.com", "home.example.com").AllDomains();
        Assert.Equal("jellyfin.example.com", names[0]);
    }

    [Fact]
    public void EveryNameIsCarried()
    {
        var names = Configured(
            "jellyfin.example.com", "home.example.com", "sonarr.example.com").AllDomains();

        Assert.Equal(
            new[] { "jellyfin.example.com", "home.example.com", "sonarr.example.com" }, names);
    }

    /// <summary>
    /// A repeated identifier makes the certificate authority reject the entire order, so
    /// a name typed twice must never reach it — including one that differs only in case.
    /// </summary>
    [Fact]
    public void ARepeatedNameIsDropped()
    {
        var names = Configured(
            "home.example.com", "HOME.example.com", "sonarr.example.com").AllDomains();

        Assert.Equal(new[] { "home.example.com", "sonarr.example.com" }, names);
    }

    [Fact]
    public void BlankAndPaddedEntriesAreTidiedAway()
    {
        var names = Configured("home.example.com", "  sonarr.example.com  ", "", "   ").AllDomains();
        Assert.Equal(new[] { "home.example.com", "sonarr.example.com" }, names);
    }

    [Fact]
    public void NoExtraNamesIsStillTheOldSingleNameBehaviour()
    {
        Assert.Equal(new[] { "media.example.com" }, Configured("media.example.com").AllDomains());
    }

    private static string? Validate(PluginConfiguration config)
    {
        var method = typeof(AcmeService).GetMethod(
            "Validate", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Validate is missing.");

        return (string?)method.Invoke(null, new object[] { config });
    }

    [Fact]
    public void AGoodListOfNamesValidates()
        => Assert.Null(Validate(Configured("jellyfin.example.com", "home.example.com")));

    /// <summary>
    /// The offender has to be named. "Domain is not a valid hostname" is unhelpful when
    /// the domain is fine and the third entry in the list is not.
    /// </summary>
    [Fact]
    public void ABadExtraNameIsRejectedAndNamed()
    {
        var problem = Validate(Configured("home.example.com", "sonarr.example.com", "http://nope"));

        Assert.NotNull(problem);
        Assert.Contains("http://nope", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AWildcardAnywhereInTheListIsRejectedAndNamed()
    {
        var problem = Validate(Configured("home.example.com", "*.example.com"));

        Assert.NotNull(problem);
        Assert.Contains("*.example.com", problem, StringComparison.Ordinal);
    }

    private static async Task<HttpContext> RequestAsync(
        PluginConfiguration config, string host, string path)
    {
        var handler = new AcmeHttpHandler(new HttpChallengeStore(), () => config);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        await handler.HandleAsync(context);
        return context;
    }

    /// <summary>
    /// Each name has to land back on itself. Redirecting a request for one application to
    /// another application's address would be both wrong and baffling to the visitor.
    /// </summary>
    [Fact]
    public async Task ANameOnTheCertificateRedirectsToItself()
    {
        var config = Configured("jellyfin.example.com", "sonarr.example.com");
        var context = await RequestAsync(config, "sonarr.example.com", "/queue");

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.Equal(
            "https://sonarr.example.com/queue", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task TheMatchIsCaseInsensitiveLikeDnsItself()
    {
        var config = Configured("jellyfin.example.com", "sonarr.example.com");
        var context = await RequestAsync(config, "SONARR.example.com", "/");

        Assert.Equal("https://sonarr.example.com/", context.Response.Headers.Location.ToString());
    }

    /// <summary>
    /// The guard that keeps this from becoming an open redirect: the host header is
    /// matched against the configured names, never echoed back on trust.
    /// </summary>
    [Fact]
    public async Task AHostWeDoNotServeFallsBackToThePrimaryName()
    {
        var config = Configured("jellyfin.example.com", "sonarr.example.com");
        var context = await RequestAsync(config, "evil.example.net", "/");

        Assert.Equal("https://jellyfin.example.com/", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task ASuffixOfAServedNameIsNotAServedName()
    {
        var config = Configured("jellyfin.example.com");
        var context = await RequestAsync(config, "jellyfin.example.com.evil.net", "/");

        Assert.Equal("https://jellyfin.example.com/", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task TheNonDefaultHttpsPortIsKeptOnEveryName()
    {
        var config = Configured("jellyfin.example.com", "sonarr.example.com");
        config.PublicHttpsPort = 8443;
        var context = await RequestAsync(config, "sonarr.example.com", "/x");

        Assert.Equal(
            "https://sonarr.example.com:8443/x", context.Response.Headers.Location.ToString());
    }

    /// <summary>
    /// The rehearsal issues from the staging environment, whose root no browser trusts.
    /// If it wrote to the PEM paths, a reverse proxy serving every other application on
    /// the machine would pick up an untrusted certificate at its next reload.
    /// </summary>
    [Fact]
    public void ARehearsalNeverWritesOverThePublishedPem()
    {
        var config = Configured("home.example.com");
        config.PemCertificatePath = "/etc/ssl/live/fullchain.pem";
        config.PemPrivateKeyPath = "/etc/ssl/live/privkey.pem";

        var method = typeof(AcmeService).GetMethod(
            "CloneForTestRun", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("CloneForTestRun is missing.");

        var rehearsal = (PluginConfiguration)method.Invoke(null, new object[] { config })!;

        Assert.Equal(string.Empty, rehearsal.PemCertificatePath);
        Assert.Equal(string.Empty, rehearsal.PemPrivateKeyPath);
        Assert.True(rehearsal.UseStaging);

        // And the real configuration is untouched.
        Assert.Equal("/etc/ssl/live/fullchain.pem", config.PemCertificatePath);
    }
}
