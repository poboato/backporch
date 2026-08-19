using System.Net.Mime;
using Jellyfin.Plugin.Backporch.Acme;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="BackporchController"/> class.
    /// </summary>
    /// <param name="acmeService">The issuance service.</param>
    public BackporchController(AcmeService acmeService)
    {
        _acmeService = acmeService;
    }

    /// <summary>
    /// Reports the current certificate state without contacting the CA.
    /// </summary>
    /// <returns>The current status.</returns>
    [HttpGet("Status")]
    public ActionResult<BackporchStatusDto> GetStatus()
    {
        var config = Plugin.Instance!.Configuration;

        return Ok(new BackporchStatusDto
        {
            LastResult = config.LastResult,
            LastAttemptUtc = config.LastAttemptUtc,
            CertificateExpiryUtc = config.CertificateExpiryUtc,
            RenewalDue = AcmeService.NeedsRenewal(config),
            UsingStaging = config.UseStaging
        });
    }

    /// <summary>
    /// Requests a certificate immediately, regardless of how much life the current one has left.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status after the attempt.</returns>
    [HttpPost("Request")]
    public async Task<ActionResult<BackporchStatusDto>> RequestCertificate(CancellationToken cancellationToken)
    {
        await _acmeService.RunAsync(force: true, cancellationToken).ConfigureAwait(false);
        return GetStatus();
    }
}

/// <summary>
/// Certificate state as shown on the configuration page.
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
}
