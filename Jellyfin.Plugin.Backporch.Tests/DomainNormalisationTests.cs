using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The primary name is read raw in places where padding is destructive rather than
/// untidy, and the configuration page's own trimming does not reach a hand-edited XML
/// file. These pin the normalisation at the point every reader shares.
/// </summary>
public class DomainNormalisationTests
{
    [Theory]
    [InlineData("  media.example.com  ", "media.example.com")]
    [InlineData("\tmedia.example.com\n", "media.example.com")]
    [InlineData("media.example.com", "media.example.com")]
    [InlineData("   ", "")]
    public void ThePrimaryNameIsTrimmedOnTheWayIn(string given, string expected)
        => Assert.Equal(expected, new PluginConfiguration { Domain = given }.Domain);

    /// <summary>
    /// A padded name would otherwise reach the Location header verbatim, producing
    /// <c>https://  media.example.com  /web</c> — which no client can follow, and which
    /// nothing in the issuance path would have caught.
    /// </summary>
    [Fact]
    public async Task ThePaddedNameNeverReachesTheRedirect()
    {
        var config = new PluginConfiguration
        {
            Domain = "  media.example.com  ",
            PublicHttpsPort = 443
        };

        var handler = new AcmeHttpHandler(new Acme.HttpChallengeStore(), () => config);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Host = new HostString("somewhere.else");
        context.Request.Path = "/web";
        await handler.HandleAsync(context);

        Assert.Equal(
            "https://media.example.com/web", context.Response.Headers.Location.ToString());
    }

    /// <summary>
    /// The certificate order must carry the trimmed name too: a padded identifier is
    /// rejected by the certificate authority, failing the whole order rather than one
    /// name.
    /// </summary>
    [Fact]
    public void TheOrderCarriesTheTrimmedName()
    {
        var config = new PluginConfiguration { Domain = "  media.example.com  " };

        Assert.Equal(new[] { "media.example.com" }, config.AllDomains());
    }

    /// <summary>
    /// Trimming must survive the round trip through configuration storage, which is the
    /// path that reaches a value the page never touched.
    /// </summary>
    [Fact]
    public void TrimmingSurvivesACloneOfTheConfiguration()
    {
        var config = new PluginConfiguration { Domain = "  media.example.com  " };

        Assert.Equal("media.example.com", config.Clone().Domain);
    }
}
