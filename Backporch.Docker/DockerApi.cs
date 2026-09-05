using System.Net.Http.Json;
using System.Net.Sockets;

namespace Backporch.Docker;

/// <summary>
/// Reads the container listing from Docker, over a Unix socket or a TCP endpoint.
/// </summary>
/// <remarks>
/// Only ever reads. The listing endpoint is the sole call made, which is what allows the
/// recommended deployment: a read-only socket proxy exposing containers alone, so that
/// discovery cannot start, stop or reconfigure anything even if it were compromised.
/// </remarks>
public sealed class DockerApi : IDisposable
{
    /// <summary>The usual location of the Docker socket on a Linux host.</summary>
    public const string DefaultSocket = "/var/run/docker.sock";

    private readonly HttpClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="DockerApi"/> class.
    /// </summary>
    /// <param name="endpoint">
    /// Either a filesystem path to a Docker socket, or an <c>http://host:port</c> address
    /// for a socket proxy. A <c>unix://</c> prefix is accepted and stripped.
    /// </param>
    public DockerApi(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var target = endpoint.Trim();

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _client = new HttpClient { BaseAddress = new Uri(target.TrimEnd('/') + "/") };
        }
        else
        {
            var path = target.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
                ? target["unix://".Length..]
                : target;

            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // A missing socket path and a refused permission are the ordinary
                        // cases here, not the exceptional ones — the caller expects both
                        // and reports them. Without this the handle survives until the
                        // finalizer runs, so a page reloaded against a bad endpoint leaks
                        // one per attempt.
                        socket.Dispose();
                        throw;
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
            };

            // The host in the URI is ignored for a Unix socket, but one has to be present
            // for the request line to be well formed.
            _client = new HttpClient(handler) { BaseAddress = new Uri("http://docker/") };
        }

        _client.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>Reads the running containers.</summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The container listing, or an empty list if Docker returned nothing.</returns>
    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(
        CancellationToken cancellationToken)
    {
        // A pinned API version keeps a newer daemon from answering in a shape this does
        // not expect; 1.41 is old enough to be present everywhere still supported.
        var containers = await _client
            .GetFromJsonAsync<List<ContainerSummary>>("v1.41/containers/json", cancellationToken)
            .ConfigureAwait(false);

        return containers ?? new List<ContainerSummary>();
    }

    /// <summary>
    /// Finds the applications on this machine that could be given a public name.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The publishable applications, most obviously publishable first.</returns>
    public async Task<IReadOnlyList<DiscoveredApp>> DiscoverAsync(CancellationToken cancellationToken)
        => AppDiscovery.Find(await ListContainersAsync(cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
