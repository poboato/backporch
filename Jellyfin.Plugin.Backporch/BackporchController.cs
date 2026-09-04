using System.Net;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text.Json;
using Backporch.Docker;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Dns;
using Jellyfin.Plugin.Backporch.Http;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Backporch;

/// <summary>
/// Administrative endpoints backing the plugin's configuration page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Backporch")]
[Produces(MediaTypeNames.Application.Json)]
public class BackporchController : ControllerBase
{
    private readonly AcmeService _acmeService;
    private readonly IssuanceState _state;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerApplicationHost _appHost;
    private readonly IConfigurationManager _configurationManager;
    private readonly AcmeHttpServer _httpServer;
    private readonly ILogger<BackporchController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackporchController"/> class.
    /// </summary>
    /// <param name="acmeService">The issuance service.</param>
    /// <param name="state">Shared issuance progress state.</param>
    /// <param name="httpClientFactory">Factory for the preflight check's HTTP calls.</param>
    /// <param name="appHost">The server host, for its listening ports.</param>
    /// <param name="configurationManager">Server configuration, for the network base URL.</param>
    /// <param name="httpServer">The plugin's own HTTP listener, for its bound state.</param>
    /// <param name="logger">Logger.</param>
    public BackporchController(
        AcmeService acmeService,
        IssuanceState state,
        IHttpClientFactory httpClientFactory,
        IServerApplicationHost appHost,
        IConfigurationManager configurationManager,
        AcmeHttpServer httpServer,
        ILogger<BackporchController> logger)
    {
        _acmeService = acmeService;
        _state = state;
        _httpClientFactory = httpClientFactory;
        _appHost = appHost;
        _configurationManager = configurationManager;
        _httpServer = httpServer;
        _logger = logger;
    }

    /// <summary>
    /// Reports certificate state and live issuance progress. Polled by the
    /// configuration page; never contacts the CA.
    /// </summary>
    /// <returns>The current status.</returns>
    [HttpGet("Status")]
    public ActionResult<BackporchStatusDto> GetStatus() => Ok(BuildStatus());

    /// <summary>
    /// Starts a certificate request in the background; progress is followed via
    /// <see cref="GetStatus"/>.
    /// </summary>
    /// <param name="guided">
    /// When set (the setup page's default), an unproven configuration is first tested
    /// against the staging CA and, on success, the real certificate is issued from
    /// production — one button, no staging concept for the user.
    /// </param>
    /// <returns>The status at the moment the request was accepted.</returns>
    [HttpPost("Request")]
    public ActionResult<BackporchStatusDto> RequestCertificate([FromQuery] bool guided = false)
    {
        if (_state.Snapshot().Running)
        {
            return Conflict(BuildStatus());
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = guided
                    ? await _acmeService.RunGuidedAsync(CancellationToken.None).ConfigureAwait(false)
                    : await _acmeService.RunAsync(force: true, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Background issuance finished: {Outcome}", outcome);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background issuance crashed");
            }
        });

        return Accepted(BuildStatus());
    }

    /// <summary>
    /// Manual DNS mode: the user says the TXT record shown on the page is in place.
    /// </summary>
    /// <returns>The current status.</returns>
    [HttpPost("ConfirmDns")]
    public ActionResult<BackporchStatusDto> ConfirmDns()
    {
        if (!_state.ConfirmDnsRecord())
        {
            return Conflict(BuildStatus());
        }

        return Ok(BuildStatus());
    }

    /// <summary>
    /// Lists the other applications running on this machine that could be given a name
    /// on the certificate. Read-only; changes nothing anywhere.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The applications found, and any reason discovery could not run.</returns>
    /// <remarks>
    /// Nothing here is acted on. It exists so the person setting this up is offered what
    /// is actually running rather than having to remember each application's port, and
    /// every entry arrives with the risk of publishing it already worked out.
    /// </remarks>
    [HttpGet("Discover")]
    public async Task<ActionResult<BackporchDiscoveryDto>> Discover(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var endpoint = string.IsNullOrWhiteSpace(config.DockerEndpoint)
            ? DockerApi.DefaultSocket
            : config.DockerEndpoint;

        var dto = new BackporchDiscoveryDto { Domain = config.Domain, Endpoint = endpoint };

        try
        {
            using var docker = new DockerApi(endpoint);
            var apps = await docker.DiscoverAsync(cancellationToken).ConfigureAwait(false);

            dto.Apps = apps.Select(a => new BackporchAppDto
            {
                Container = a.Container,
                Image = a.Image,
                Port = a.Port,
                AlternatePorts = a.AlternatePorts,
                Label = a.SuggestedLabel,
                Hostname = string.IsNullOrWhiteSpace(config.Domain)
                    ? string.Empty
                    : a.HostnameUnder(config.Domain),
                Risk = a.Risk.ToString(),
                RiskReason = a.RiskReason,

                // The application asking the question is already reachable at the primary
                // name, so offering it a second one under itself is noise at best and
                // "jellyfin.jellyfin.example.com" at worst. The container-side port
                // identifies it; the published port is whatever the compose file chose.
                IsThisServer = a.ContainerPort == _appHost.HttpPort
            }).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            or UnauthorizedAccessException or SocketException or NotSupportedException)
        {
            // Not being able to see Docker is ordinary - there may be none, or no
            // permission to its socket. It is not an error worth failing the page over,
            // so say what happened and let the names be typed by hand.
            _logger.LogInformation(ex, "Could not list containers for discovery");
            dto.Problem = "Could not read the container list from " + endpoint
                + ". Add the names by hand, or point this at a Docker socket under Advanced.";
        }

        return Ok(dto);
    }

    /// <summary>
    /// Preflight for the setup page: detects the server's public address, checks what
    /// the configured domain currently resolves to, and (for Cloudflare) verifies the
    /// token can see the zone. Read-only; changes nothing anywhere.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The check results.</returns>
    [HttpGet("Check")]
    public async Task<ActionResult<BackporchCheckDto>> Check(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var dto = new BackporchCheckDto
        {
            Domain = config.Domain,
            HttpPort = _appHost.HttpPort,
            ChallengeListenerExpectedPort = AcmeHttpServer.WantedPort(config),
            ChallengeListenerPort = _httpServer.BoundPort,
            ChallengeListenerError = _httpServer.LastError,

            // A base URL makes Jellyfin redirect every unprefixed request to the web
            // client — including the well-known path the CA must fetch, which it is
            // required to request unprefixed. HTTP proof cannot work while one is set.
            BaseUrl = _configurationManager.GetNetworkConfiguration().BaseUrl ?? string.Empty
        };

        try
        {
            using var client = _httpClientFactory.CreateClient(nameof(BackporchController));
            client.Timeout = TimeSpan.FromSeconds(5);

            // A third party answers this, so take as little as possible on trust: cap the
            // body, and only accept a value that really parses as an IPv4 address rather
            // than letting an arbitrary string reach the page or the comparison below.
            client.MaxResponseContentBufferSize = 128;
            var body = await client.GetStringAsync(new Uri("https://ipv4.icanhazip.com/"), cancellationToken)
                .ConfigureAwait(false);

            if (IPAddress.TryParse(body.Trim(), out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetwork)
            {
                dto.PublicIp = parsed.ToString();
            }
            else
            {
                _logger.LogWarning("Public IP lookup returned something that is not an IPv4 address");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Public IP detection failed");
        }

        if (!string.IsNullOrWhiteSpace(config.Domain))
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(
                    config.Domain, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
                dto.ResolvedAddresses = addresses.Select(a => a.ToString()).ToArray();
                dto.DomainMatchesPublicIp = dto.PublicIp is not null
                    && dto.ResolvedAddresses.Contains(dto.PublicIp, StringComparer.Ordinal);
            }
            catch (SocketException)
            {
                dto.ResolvedAddresses = Array.Empty<string>();
                dto.DomainMatchesPublicIp = false;
            }
        }

        if (config.DnsProvider == DnsProviderKind.Cloudflare
            && !string.IsNullOrWhiteSpace(config.DnsApiToken)
            && !string.IsNullOrWhiteSpace(config.Domain))
        {
            try
            {
                var provider = new CloudflareDnsProvider(
                    _httpClientFactory.CreateClient(nameof(CloudflareDnsProvider)),
                    config.DnsApiToken,
                    _logger);
                dto.ZoneName = await provider.VerifyAccessAsync(config.Domain, cancellationToken).ConfigureAwait(false);
                dto.ZoneOk = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or HttpRequestException
                or TaskCanceledException
                or JsonException)
            {
                // Provider errors carry Cloudflare's own message, never the token.
                // JsonException included: an outage or intercepting proxy answers with an
                // HTML error page, and letting that escape would fail the whole preflight
                // — losing the public IP and A-record card over an unrelated hiccup.
                dto.ZoneOk = false;
                dto.ZoneError = ex.Message;
            }
        }

        return Ok(dto);
    }

    private BackporchStatusDto BuildStatus()
    {
        var config = Plugin.Instance!.Configuration;
        var snapshot = _state.Snapshot();
        var effectivePath = string.IsNullOrWhiteSpace(config.CertificatePath)
            ? Plugin.Instance!.DefaultCertificatePath
            : config.CertificatePath;

        return new BackporchStatusDto
        {
            LastResult = config.LastResult,
            LastAttemptUtc = config.LastAttemptUtc,
            CertificateExpiryUtc = config.CertificateExpiryUtc,
            RenewalDue = AcmeService.NeedsRenewal(config),
            UsingStaging = config.UseStaging,
            Enabled = config.Enabled,
            Domain = config.Domain,
            CertificatePath = effectivePath,
            HasCertificateFile = System.IO.File.Exists(effectivePath),
            Phase = snapshot.Phase.ToString(),
            PhaseDetail = snapshot.Detail,
            Running = snapshot.Running,
            IsTestRun = snapshot.IsTestRun,
            PendingRecordName = snapshot.PendingRecordName,
            PendingRecordValue = snapshot.PendingRecordValue
        };
    }
}

/// <summary>
/// Certificate state and issuance progress as shown on the configuration page.
/// </summary>
public class BackporchStatusDto
{
    /// <summary>Gets or sets the outcome of the most recent attempt.</summary>
    public string LastResult { get; set; } = string.Empty;

    /// <summary>Gets or sets when the most recent attempt ran.</summary>
    public DateTime? LastAttemptUtc { get; set; }

    /// <summary>Gets or sets the expiry of the certificate currently on disk.</summary>
    public DateTime? CertificateExpiryUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether a renewal is currently due.</summary>
    public bool RenewalDue { get; set; }

    /// <summary>Gets or sets a value indicating whether the staging CA is in use.</summary>
    public bool UsingStaging { get; set; }

    /// <summary>Gets or sets a value indicating whether the plugin is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the configured domain.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective certificate path (configured or default).</summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a certificate file exists at that path.</summary>
    public bool HasCertificateFile { get; set; }

    /// <summary>Gets or sets the current issuance phase name.</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable progress line for the current phase.</summary>
    public string PhaseDetail { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether an issuance is running right now.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets a value indicating whether the running issuance is the staging dry run.</summary>
    public bool IsTestRun { get; set; }

    /// <summary>Gets or sets the TXT record name the user must add (manual DNS mode).</summary>
    public string? PendingRecordName { get; set; }

    /// <summary>Gets or sets the TXT record value the user must add (manual DNS mode).</summary>
    public string? PendingRecordValue { get; set; }
}

/// <summary>
/// Results of the read-only preflight check.
/// </summary>
public class BackporchCheckDto
{
    /// <summary>Gets or sets the domain the check ran against.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Gets or sets the server's detected public IPv4 address, if reachable.</summary>
    public string? PublicIp { get; set; }

    /// <summary>Gets or sets the port Jellyfin serves plain HTTP on.</summary>
    /// <remarks>
    /// Shown so it can be pointed out that this is the port <em>not</em> to forward.
    /// </remarks>
    public int HttpPort { get; set; }

    /// <summary>
    /// Gets or sets the port the plugin's own HTTP listener should be holding, or zero
    /// when it is switched off (a reverse proxy owns port 80 instead).
    /// </summary>
    public int ChallengeListenerExpectedPort { get; set; }

    /// <summary>
    /// Gets or sets the port that listener actually bound, or zero if it is not running.
    /// </summary>
    public int ChallengeListenerPort { get; set; }

    /// <summary>
    /// Gets or sets why the listener is not running, when it should be. A port that
    /// silently failed to bind is a renewal that fails silently two months later, so this
    /// is surfaced rather than left in the log.
    /// </summary>
    public string? ChallengeListenerError { get; set; }

    /// <summary>
    /// Gets or sets Jellyfin's configured base URL. Non-empty means HTTP proof cannot
    /// work: the server redirects the unprefixed challenge path to the web client.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the addresses the domain currently resolves to.</summary>
    public IReadOnlyList<string>? ResolvedAddresses { get; set; }

    /// <summary>Gets or sets whether the domain points at this server's public address.</summary>
    public bool? DomainMatchesPublicIp { get; set; }

    /// <summary>Gets or sets the Cloudflare zone the token was able to see.</summary>
    public string? ZoneName { get; set; }

    /// <summary>Gets or sets whether the Cloudflare token check passed.</summary>
    public bool? ZoneOk { get; set; }

    /// <summary>Gets or sets the Cloudflare error when the token check failed.</summary>
    public string? ZoneError { get; set; }
}

/// <summary>
/// The applications discovery found running on this machine.
/// </summary>
public class BackporchDiscoveryDto
{
    /// <summary>Gets or sets the configured domain the names would sit under.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Gets or sets the Docker endpoint that was read.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the applications found, safest first.</summary>
    public IReadOnlyList<BackporchAppDto> Apps { get; set; } = Array.Empty<BackporchAppDto>();

    /// <summary>Gets or sets why discovery could not run, when it could not.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// One application that could be given a name on the certificate.
/// </summary>
public class BackporchAppDto
{
    /// <summary>Gets or sets the container name.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the image it runs.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Gets or sets the host port that serves it.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the other published ports it could be served on.</summary>
    public IReadOnlyList<int> AlternatePorts { get; set; } = Array.Empty<int>();

    /// <summary>Gets or sets the suggested host label, such as <c>sonarr</c>.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the full name it would answer to, when a domain is set.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Gets or sets how dangerous publishing it would be.</summary>
    public string Risk { get; set; } = string.Empty;

    /// <summary>Gets or sets why, when it is not ordinary.</summary>
    public string RiskReason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this is the server hosting this plugin, which the primary
    /// name already covers.
    /// </summary>
    public bool IsThisServer { get; set; }
}
