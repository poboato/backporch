using System.Net;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text.Json;
using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Dns;
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
    private readonly ILogger<BackporchController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackporchController"/> class.
    /// </summary>
    /// <param name="acmeService">The issuance service.</param>
    /// <param name="state">Shared issuance progress state.</param>
    /// <param name="httpClientFactory">Factory for the preflight check's HTTP calls.</param>
    /// <param name="appHost">The server host, for its listening ports.</param>
    /// <param name="configurationManager">Server configuration, for the network base URL.</param>
    /// <param name="logger">Logger.</param>
    public BackporchController(
        AcmeService acmeService,
        IssuanceState state,
        IHttpClientFactory httpClientFactory,
        IServerApplicationHost appHost,
        IConfigurationManager configurationManager,
        ILogger<BackporchController> logger)
    {
        _acmeService = acmeService;
        _state = state;
        _httpClientFactory = httpClientFactory;
        _appHost = appHost;
        _configurationManager = configurationManager;
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

    /// <summary>Gets or sets the port Jellyfin serves plain HTTP on (port-80 forward target).</summary>
    public int HttpPort { get; set; }

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
