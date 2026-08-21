using System.Globalization;
using System.Net.Sockets;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Backporch.Http;

/// <summary>
/// Owns the public plain-HTTP port on the plugin's own socket, so that opening it to the
/// internet publishes the ACME challenge path and nothing else.
/// </summary>
/// <remarks>
/// A separate listener rather than a route inside Jellyfin, because the point is to have a
/// port that <em>cannot</em> reach Jellyfin: the forward stays open permanently for
/// renewals, so anything reachable through it is reachable forever. Kestrel does the HTTP
/// parsing — hand-rolling that for an internet-facing socket would be the least defensible
/// choice in the project — with request limits and timeouts set tight, since the only
/// legitimate caller sends a handful of small GETs every couple of months.
/// </remarks>
public sealed class AcmeHttpServer : IHostedService, IAsyncDisposable
{
    private readonly HttpChallengeStore _store;
    private readonly ILogger<AcmeHttpServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IWebHost? _host;
    private int _boundPort;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcmeHttpServer"/> class.
    /// </summary>
    /// <param name="store">The active challenge answers.</param>
    /// <param name="logger">Logger for lifecycle and bind failures.</param>
    /// <param name="loggerFactory">Routes Kestrel's own diagnostics into Jellyfin's log.</param>
    public AcmeHttpServer(
        HttpChallengeStore store,
        ILogger<AcmeHttpServer> logger,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Gets the port currently bound, or zero when the listener is not running.
    /// </summary>
    public int BoundPort => _boundPort;

    /// <summary>
    /// Gets the reason the listener is not running, when it was supposed to be. Surfaced on
    /// the setup page: a silently unbound port is a renewal that fails in two months.
    /// </summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        await ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopHostAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopHostAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>
    /// Decides whether the listener should be running, and on which port, from the current
    /// configuration — then makes reality match.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the listener state matches the configuration.</returns>
    internal async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        var wanted = WantedPort(config);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_boundPort == wanted && (_host is not null) == (wanted != 0))
            {
                return;
            }

            await StopHostAsync().ConfigureAwait(false);

            if (wanted == 0)
            {
                return;
            }

            await StartHostAsync(wanted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The port the listener should occupy, or zero for "stay off".
    /// </summary>
    /// <param name="config">The current configuration, if the plugin is loaded.</param>
    /// <returns>A TCP port, or zero.</returns>
    /// <remarks>
    /// Deliberately not gated on <see cref="PluginConfiguration.Enabled"/>: the listener has
    /// to be answering <em>before</em> the first issuance runs, and a domain typed into step
    /// one of the setup page is enough to know where redirects should point.
    /// </remarks>
    internal static int WantedPort(PluginConfiguration? config)
    {
        if (config is null
            || !config.ServeHttpRedirect
            || config.Challenge != ChallengeKind.Http
            || config.ChallengeListenPort is <= 0 or > 65535
            || !AcmeService.IsValidHostname(config.Domain))
        {
            return 0;
        }

        return config.ChallengeListenPort;
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
    {
        // The event is raised on the caller's thread mid-save; rebinding a socket there would
        // block the setup page's save request, so hand it off.
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backporch: could not apply the new HTTP listener settings");
            }
        });
    }

    private async Task StartHostAsync(int port, CancellationToken cancellationToken)
    {
        var handler = new AcmeHttpHandler(_store, static () => Plugin.Instance?.Configuration);

        var host = new WebHostBuilder()
            .UseKestrel(options =>
            {
                options.ListenAnyIP(port);

                // Nothing here identifies the software behind the port.
                options.AddServerHeader = false;

                // No request to this listener has a body, and none is large. Tight limits
                // mean a hostile caller cannot hold resources that Jellyfin needs.
                var limits = options.Limits;
                limits.MaxRequestBodySize = 0;
                limits.MaxRequestLineSize = 8 * 1024;
                limits.MaxRequestHeadersTotalSize = 16 * 1024;
                limits.MaxRequestHeaderCount = 40;
                limits.MaxConcurrentConnections = 100;
                limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            })
            .UseContentRoot(AppContext.BaseDirectory)

            // Both of these are load-bearing, and neither is optional. Calling Configure
            // names this assembly as the web host's "application", and the host then tries
            // to Assembly.Load it by name to look for hosting-startup attributes. A plugin
            // assembly is loaded from a path the default resolver knows nothing about, so
            // that lookup fails and logs a FileNotFoundException wrapped in "Startup
            // assembly ... failed to execute" on every server start — harmless, and
            // indistinguishable from a broken plugin to anyone reading the log.
            .UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")
            .UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Empty)
            .ConfigureServices(services => services.AddSingleton(_loggerFactory))
            .Configure(app => app.Run(handler.HandleAsync))
            .Build();

        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            host.Dispose();
            LastError = DescribeBindFailure(ex, port);
            _logger.LogError(
                ex,
                "Backporch: could not listen on port {Port} for certificate challenges. {Advice}",
                port,
                LastError);
            return;
        }

        _host = host;
        _boundPort = port;
        LastError = null;
        _logger.LogInformation(
            "Backporch: answering certificate challenges on port {Port}; every other request "
            + "on that port is redirected to HTTPS",
            port);
    }

    private async Task StopHostAsync()
    {
        var host = _host;
        _host = null;
        _boundPort = 0;

        if (host is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backporch: the challenge listener did not stop cleanly");
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// Turns a bind failure into the sentence that actually tells someone what to do, since
    /// the two realistic causes have completely different fixes.
    /// </summary>
    /// <param name="ex">The exception Kestrel raised.</param>
    /// <param name="port">The port that could not be bound.</param>
    /// <returns>An operator-facing explanation.</returns>
    private static string DescribeBindFailure(Exception ex, int port)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var socket = ex as SocketException ?? ex.InnerException as SocketException;

        return socket?.SocketErrorCode switch
        {
            SocketError.AccessDenied => $"Port {portText} is privileged, and this server is not "
                + "running as root. Map the router's port 80 to an unprivileged port on this "
                + "machine (8080, say) and set that number as the challenge listen port.",
            SocketError.AddressAlreadyInUse => $"Port {portText} is already taken by something "
                + "else on this machine. Either free it, or forward the router's port 80 to a "
                + "different port here and set that number as the challenge listen port.",
            _ => $"Port {portText} could not be opened: {ex.Message}"
        };
    }

    /// <summary>
    /// A listener wired to a fixed port and an explicit configuration source, for tests.
    /// </summary>
    /// <param name="store">The challenge store to answer from.</param>
    /// <param name="configuration">Reads the configuration used for redirects.</param>
    /// <param name="port">The port to bind.</param>
    /// <returns>A started web host; dispose to release the port.</returns>
    internal static async Task<IWebHost> StartForTestAsync(
        HttpChallengeStore store,
        Func<PluginConfiguration?> configuration,
        int port)
    {
        var handler = new AcmeHttpHandler(store, configuration);
        var host = new WebHostBuilder()
            .UseKestrel(options =>
            {
                options.ListenAnyIP(port);
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 0;
            })
            .UseContentRoot(AppContext.BaseDirectory)
            .UseSetting(WebHostDefaults.PreventHostingStartupKey, "true")
            .UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Empty)
            .ConfigureServices(services => services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance))
            .Configure(app => app.Run(handler.HandleAsync))
            .Build();

        await host.StartAsync().ConfigureAwait(false);
        return host;
    }
}
