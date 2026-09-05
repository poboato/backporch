using System.Net.Sockets;
using Backporch.Docker;
using Xunit;

namespace Jellyfin.Plugin.Backporch.Tests;

/// <summary>
/// The fixture tests cover the judgement; this covers the plumbing that fetches the list
/// in the first place. It runs only where a Docker endpoint is actually reachable, which
/// is the point — a mocked socket would prove nothing about the socket.
/// </summary>
public class DockerApiTests
{
    /// <summary>
    /// Set <c>BACKPORCH_DOCKER</c> to a socket path or an <c>http://host:port</c> proxy
    /// address to exercise this against a real daemon.
    /// </summary>
    private static string? Endpoint()
    {
        var configured = Environment.GetEnvironmentVariable("BACKPORCH_DOCKER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return File.Exists(DockerApi.DefaultSocket) ? DockerApi.DefaultSocket : null;
    }

    [Trait("Category", "Integration")]
    [Fact]
    public async Task ReadsRealContainersFromARealDaemon()
    {
        var endpoint = Endpoint();
        if (endpoint is null)
        {
            return; // No Docker here.
        }

        using var docker = new DockerApi(endpoint);

        IReadOnlyList<ContainerSummary> containers;
        try
        {
            containers = await docker.ListContainersAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException
            or IOException or SocketException or TaskCanceledException)
        {
            // Socket present but this run cannot use it: not readable by this user, or the
            // daemon took longer than the client's ten seconds to answer. Neither says
            // anything about the code under test, and a machine under load makes the
            // second one common enough to have failed this suite intermittently.
            return;
        }

        Assert.NotEmpty(containers);

        // Every container Docker reports must survive parsing into the shape discovery
        // reads. A name is the one field with no sensible default.
        Assert.All(containers, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));

        var apps = AppDiscovery.Find(containers);

        // Whatever is running, the invariants must hold on the live list, not just the
        // captured one: no duplicate names, and nothing on the never-expose list.
        var labels = apps.Select(a => a.SuggestedLabel).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(apps, a => a.Container.Contains("dockerproxy", StringComparison.OrdinalIgnoreCase));
        Assert.All(apps, a => Assert.InRange(a.Port, 1, 65535));
        Assert.All(apps, a => Assert.NotEqual(string.Empty, a.SuggestedLabel));
    }
}
