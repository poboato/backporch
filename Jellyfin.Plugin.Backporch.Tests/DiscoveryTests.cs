using System.Text.Json;
using Backporch.Docker;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// Discovery decides what a person is offered as publishable, so its mistakes are either
/// an application they cannot reach or one they never meant to expose. The fixture is a
/// real container listing captured from a running machine — 26 containers, including the
/// awkward ones — rather than an invented list that only contains what was thought of.
/// </summary>
public class DiscoveryTests
{
    private static IReadOnlyList<DiscoveredApp> FromFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "containers.json");
        var containers = JsonSerializer.Deserialize<List<ContainerSummary>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("fixture did not parse");

        return AppDiscovery.Find(containers);
    }

    private static DiscoveredApp? Find(string label)
        => FromFixture().FirstOrDefault(a => a.SuggestedLabel == label);

    [Fact]
    public void TheOrdinaryApplicationsAreFound()
    {
        var labels = FromFixture().Select(a => a.SuggestedLabel).ToList();

        foreach (var expected in new[] { "jellyfin", "sonarr", "radarr", "bazarr", "prowlarr" })
        {
            Assert.Contains(expected, labels);
        }
    }

    /// <summary>
    /// The Docker API is the whole machine. Reaching it means reading every container,
    /// and on a less restricted socket, controlling them — so it is never a candidate,
    /// no matter what the person deciding ticks.
    /// </summary>
    [Fact]
    public void TheDockerApiIsNeverOffered()
    {
        Assert.DoesNotContain(
            FromFixture(),
            a => a.Container.Contains("dockerproxy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Portainer is ordinary software that happens to be able to reconfigure every
    /// container on the machine. It stays on the list — it is the user's machine — but
    /// it must arrive carrying the reason it is a bad idea.
    /// </summary>
    [Fact]
    public void ContainerControlIsOfferedOnlyWithAWarning()
    {
        var portainer = Find("portainer");

        Assert.NotNull(portainer);
        Assert.Equal(ExposureRisk.Sensitive, portainer.Risk);
        Assert.NotEmpty(portainer.RiskReason);
    }

    [Fact]
    public void JellyfinIsNotTreatedAsSensitive()
    {
        var jellyfin = Find("jellyfin");

        Assert.NotNull(jellyfin);
        Assert.Equal(ExposureRisk.Ordinary, jellyfin.Risk);
        Assert.Equal(8096, jellyfin.Port);
    }

    /// <summary>
    /// A container that publishes nothing has no address to proxy to, so offering it
    /// would produce a name that resolves to a front door with nowhere to go.
    /// </summary>
    [Fact]
    public void ContainersThatPublishNoPortAreLeftOut()
    {
        var labels = FromFixture().Select(a => a.SuggestedLabel).ToList();

        Assert.DoesNotContain("byparr", labels);
        Assert.DoesNotContain("glances", labels);
    }

    /// <summary>
    /// gluetun publishes 6881 alongside its web ports. BitTorrent behind an HTTP proxy
    /// connects happily and then makes no sense, which is a miserable thing to diagnose,
    /// so a known non-HTTP port is never the one chosen.
    /// </summary>
    [Fact]
    public void AKnownNonHttpPortIsNeverChosen()
    {
        var gluetun = FromFixture().FirstOrDefault(a => a.Container == "gluetun");

        Assert.NotNull(gluetun);
        Assert.NotEqual(6881, gluetun.Port);
        Assert.DoesNotContain(6881, gluetun.AlternatePorts);
    }

    [Fact]
    public void TheVpnGatewayCarriesAWarningBecauseOfWhatSitsBehindIt()
    {
        var gluetun = FromFixture().FirstOrDefault(a => a.Container == "gluetun");

        Assert.NotNull(gluetun);
        Assert.Equal(ExposureRisk.Sensitive, gluetun.Risk);
    }

    /// <summary>
    /// A container with several web ports keeps the others as alternates rather than
    /// guessing silently, because the choice is frequently wrong and always invisible.
    /// </summary>
    [Fact]
    public void ExtraWebPortsAreOfferedAsAlternatives()
    {
        var homepage = FromFixture().FirstOrDefault(a => a.Container == "homepage");

        Assert.NotNull(homepage);
        Assert.NotEmpty(homepage.AlternatePorts);
        Assert.Contains(3000, new[] { homepage.Port }.Concat(homepage.AlternatePorts));
        Assert.Contains(80, new[] { homepage.Port }.Concat(homepage.AlternatePorts));
    }

    /// <summary>
    /// The container name is an internal detail; the visitor should not inherit it.
    /// </summary>
    [Theory]
    [InlineData("fogline-ui", "fogline")]
    [InlineData("intermission-ui", "intermission")]
    [InlineData("sdr-history", "sdr-history")]
    [InlineData("Media_Server", "media-server")]
    [InlineData("plex-web", "plex-web")]
    [InlineData("  spaced  name  ", "spaced-name")]
    [InlineData("weird!!!chars", "weird-chars")]
    [InlineData("--leading-and-trailing--", "leading-and-trailing")]
    public void ALabelIsSuggestedFromTheContainerName(string container, string expected)
        => Assert.Equal(expected, AppDiscovery.SuggestLabel(container));

    /// <summary>
    /// A label longer than 63 characters is not a legal DNS label, and a truncation that
    /// lands on a hyphen is not either.
    /// </summary>
    [Fact]
    public void AnOverlongNameIsTruncatedToALegalLabel()
    {
        var label = AppDiscovery.SuggestLabel(new string('a', 70) + "-b");

        Assert.Equal(63, label.Length);
        Assert.DoesNotContain('-', label);
    }

    [Fact]
    public void ATruncationNeverEndsOnAHyphen()
    {
        var label = AppDiscovery.SuggestLabel(new string('a', 63) + "-more");

        Assert.False(label.EndsWith('-'), "a label ending in a hyphen is not a legal DNS label");
    }

    /// <summary>
    /// Two applications reducing to one label would be de-duplicated on the certificate,
    /// leaving one of them quietly unreachable. The full container name breaks the tie.
    /// </summary>
    [Fact]
    public void TwoApplicationsNeverSuggestTheSameName()
    {
        var containers = new List<ContainerSummary>
        {
            Running("fogline", 8100),
            Running("fogline-ui", 8101)
        };

        var labels = AppDiscovery.Find(containers).Select(a => a.SuggestedLabel).ToList();

        Assert.Equal(2, labels.Count);
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheRealMachineProducesNoDuplicateNames()
    {
        var labels = FromFixture().Select(a => a.SuggestedLabel).ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The list is read top to bottom by someone deciding quickly, so what is safe comes
    /// first and what needs a second thought is not scattered among it.
    /// </summary>
    [Fact]
    public void TheRiskyOnesSortToTheBottom()
    {
        var risks = FromFixture().Select(a => (int)a.Risk).ToList();

        Assert.Equal(risks.OrderBy(r => r), risks);
    }

    [Fact]
    public void AStoppedContainerIsNotOffered()
    {
        var stopped = Running("sonarr", 8989);
        stopped.State = "exited";

        Assert.Empty(AppDiscovery.Find(new List<ContainerSummary> { stopped }));
    }

    [Fact]
    public void AUdpOnlyContainerIsNotOffered()
    {
        var container = new ContainerSummary
        {
            Names = new List<string> { "/wireguard" },
            Image = "wireguard",
            State = "running",
            Ports = new List<ContainerPort>
            {
                new() { PrivatePort = 51820, PublicPort = 51820, Type = "udp" }
            }
        };

        Assert.Empty(AppDiscovery.Find(new List<ContainerSummary> { container }));
    }

    [Fact]
    public void AHostnameIsBuiltUnderTheChosenDomain()
    {
        var app = AppDiscovery.Find(new List<ContainerSummary> { Running("sonarr", 8989) }).Single();

        Assert.Equal("sonarr.example.com", app.HostnameUnder("example.com"));
        Assert.Equal("sonarr.example.com", app.HostnameUnder("  example.com  "));
    }

    private static ContainerSummary Running(string name, int port) => new()
    {
        Names = new List<string> { "/" + name },
        Image = name + ":latest",
        State = "running",
        Ports = new List<ContainerPort>
        {
            new() { PrivatePort = port, PublicPort = port, Type = "tcp" }
        }
    };
}
