using Jellyfin.Plugin.Acme.Acme;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acme.Tasks;

/// <summary>
/// Daily check that renews the certificate once it is inside the renewal window.
/// </summary>
public class RenewCertificateTask : IScheduledTask
{
    private readonly AcmeService _acmeService;
    private readonly ILogger<RenewCertificateTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RenewCertificateTask"/> class.
    /// </summary>
    /// <param name="acmeService">The issuance service.</param>
    /// <param name="logger">Logger.</param>
    public RenewCertificateTask(AcmeService acmeService, ILogger<RenewCertificateTask> logger)
    {
        _acmeService = acmeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Renew TLS certificate";

    /// <inheritdoc />
    public string Key => "AcmeRenewCertificate";

    /// <inheritdoc />
    public string Description =>
        "Renews the Let's Encrypt certificate when it is close to expiry. Does nothing when the "
        + "current certificate is still healthy.";

    /// <inheritdoc />
    public string Category => "Maintenance";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        var result = await _acmeService.RunAsync(force: false, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Scheduled renewal finished: {Result}", result);

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Daily, in the early hours. Renewal only acts inside the threshold window, so a
        // daily check costs nothing and leaves plenty of retries before expiry.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }
}
