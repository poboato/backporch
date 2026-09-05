using Backporch.Docker;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The cases a captured listing cannot cover, because a real machine happened not to be
/// arranged that way. Each of these is a mistake discovery could make that the person
/// reading the list has no way to catch: a name they would never knowingly publish, a
/// port that is not a website, or two applications quietly given one name.
/// </summary>
public class DiscoverySafetyTests
{
    private static ContainerSummary Running(string name, string image, params (int Host, int Inside)[] ports)
        => new()
        {
            Names = new List<string> { "/" + name },
            Image = image,
            State = "running",
            Ports = ports
                .Select(p => new ContainerPort { PublicPort = p.Host, PrivatePort = p.Inside, Type = "tcp" })
                .ToList()
        };

    /// <summary>
    /// Every socket proxy image names itself for the job, but only one spelling was
    /// recognised. The rest published the Docker API under a public name — the single
    /// outcome the never-expose list exists to prevent, and the one nobody would catch by
    /// reading the list, because a proxy looks as ordinary as anything else on it.
    /// </summary>
    [Theory]
    [InlineData("dockerproxy", "ghcr.io/tecnativa/docker-socket-proxy:latest")]
    [InlineData("socket-proxy", "lscr.io/linuxserver/socket-proxy:latest")]
    [InlineData("dockersocket", "wollomatic/socket-proxy:1")]
    [InlineData("proxy", "11notes/socket-proxy:stable")]
    [InlineData("docker-api", "someone/docker-api:latest")]
    public void NoSpellingOfTheDockerApiIsOffered(string name, string image)
    {
        // Published on an ordinary-looking port, which is what makes it dangerous: there
        // is nothing about 2375 that the port rules would refuse on their own.
        var found = AppDiscovery.Find(new[] { Running(name, image, (2375, 2375)) });

        Assert.Empty(found);
    }

    /// <summary>
    /// What the software speaks is decided by the port inside the container. The host
    /// side is whatever the compose file picked, so checking only that lets BitTorrent
    /// and SSH through — a front door that connects happily and then makes no sense.
    /// </summary>
    [Theory]
    [InlineData(51413, 6881)]
    [InlineData(2222, 22)]
    [InlineData(15432, 5432)]
    public void ANonHttpPortIsRefusedWhateverItIsPublishedOn(int host, int inside)
    {
        var found = AppDiscovery.Find(new[] { Running("thing", "example/thing", (host, inside)) });

        Assert.Empty(found);
    }

    /// <summary>
    /// The same container is still offered for the web port it also publishes — refusing
    /// the whole application because one of its ports is not HTTP would lose it.
    /// </summary>
    [Fact]
    public void AWebPortBesideANonHttpOneIsStillOffered()
    {
        var found = AppDiscovery.Find(new[] { Running("gateway", "example/gateway", (51413, 6881), (8080, 8080)) });

        var app = Assert.Single(found);
        Assert.Equal(8080, app.Port);
        Assert.DoesNotContain(51413, app.AlternatePorts);
    }

    /// <summary>
    /// Two names collapsing into one would put a single name on the certificate for two
    /// applications, leaving one unreachable behind a front door that appeared to work.
    /// Both spellings are legal Docker names and both reduce to the same label twice
    /// over — once by suffix stripping, and again by the fallback meant to rescue it.
    /// </summary>
    [Fact]
    public void TwoContainersNeverShareALabel()
    {
        var found = AppDiscovery.Find(new[]
        {
            Running("fogline-ui", "example/fogline", (8081, 80)),
            Running("fogline_ui", "example/fogline", (8082, 80)),
            Running("fogline", "example/fogline", (8083, 80))
        });

        Assert.Equal(3, found.Count);
        Assert.Equal(3, found.Select(a => a.SuggestedLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// A label over 63 characters is not a legal DNS label, and one bad identifier fails
    /// the whole certificate order at the authority — so an over-long container name
    /// would cost the user every other application on the certificate, not just its own.
    /// </summary>
    [Fact]
    public void AnOverLongNameStillYieldsALegalLabel()
    {
        var long1 = new string('a', 70) + "-one";
        var long2 = new string('a', 70) + "-two";

        var found = AppDiscovery.Find(new[]
        {
            Running(long1, "example/one", (8081, 80)),
            Running(long2, "example/two", (8082, 80))
        });

        Assert.Equal(2, found.Count);

        foreach (var app in found)
        {
            Assert.InRange(app.SuggestedLabel.Length, 1, 63);
            Assert.DoesNotContain("--", app.SuggestedLabel, StringComparison.Ordinal);
            Assert.False(app.SuggestedLabel.StartsWith('-'), "a label may not begin with a hyphen");
            Assert.False(app.SuggestedLabel.EndsWith('-'), "a label may not end with a hyphen");
        }

        // Both truncate to the same 63 characters, so the collision rule has to settle it
        // after the cap rather than before.
        Assert.Equal(2, found.Select(a => a.SuggestedLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
