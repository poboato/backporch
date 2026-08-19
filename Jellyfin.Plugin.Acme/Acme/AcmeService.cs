using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Jellyfin.Plugin.Acme.Configuration;
using Jellyfin.Plugin.Acme.Dns;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Acme.Acme;

/// <summary>
/// Requests and renews certificates over ACME using the DNS-01 challenge.
/// </summary>
/// <remarks>
/// Everything here runs off the request path — from a scheduled task or an explicit
/// button press — so certificate work never adds latency to playback.
/// </remarks>
public sealed class AcmeService
{
    private static readonly TimeSpan _validationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger<AcmeService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcmeService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="httpClientFactory">Factory for provider HTTP clients.</param>
    public AcmeService(ILogger<AcmeService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Issues a certificate if one is needed, or if <paramref name="force"/> is set.
    /// </summary>
    /// <param name="force">Issue even when the current certificate is still healthy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A human-readable description of what happened.</returns>
    public async Task<string> RunAsync(bool force, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException("Plugin instance is not available.");
        var config = plugin.Configuration;

        var problem = Validate(config);
        if (problem is not null)
        {
            return Record(plugin, problem);
        }

        if (!force && !NeedsRenewal(config))
        {
            var days = (config.CertificateExpiryUtc!.Value - DateTime.UtcNow).TotalDays;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"No action needed — certificate is valid for another {days:F0} days.");
        }

        try
        {
            var expiry = await IssueAsync(config, cancellationToken).ConfigureAwait(false);
            config.CertificateExpiryUtc = expiry;

            var message = string.Format(
                CultureInfo.InvariantCulture,
                "Issued a certificate for {0}, valid until {1:yyyy-MM-dd}.",
                config.Domain,
                expiry);

            if (config.UseStaging)
            {
                message += " Staging certificate — not trusted by browsers.";
            }

            return Record(plugin, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Certificate issuance failed");
            return Record(plugin, "Failed — " + ex.Message);
        }
    }

    /// <summary>
    /// Reports whether the certificate on disk is missing, unreadable, or close enough
    /// to expiry to warrant renewal.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns><c>true</c> when a renewal should be attempted.</returns>
    public static bool NeedsRenewal(PluginConfiguration config)
    {
        if (!File.Exists(config.CertificatePath) || config.CertificateExpiryUtc is null)
        {
            return true;
        }

        return DateTime.UtcNow.AddDays(config.RenewDaysBeforeExpiry) >= config.CertificateExpiryUtc.Value;
    }

    private static string? Validate(PluginConfiguration config)
    {
        if (!config.Enabled)
        {
            return "Disabled — turn the plugin on to request certificates.";
        }

        if (string.IsNullOrWhiteSpace(config.Domain))
        {
            return "No domain configured.";
        }

        if (config.Domain.Contains('/', StringComparison.Ordinal)
            || config.Domain.Contains(' ', StringComparison.Ordinal)
            || !config.Domain.Contains('.', StringComparison.Ordinal))
        {
            return "Domain is not a valid hostname.";
        }

        if (string.IsNullOrWhiteSpace(config.AccountEmail))
        {
            return "No contact email configured.";
        }

        if (config.DnsProvider == DnsProviderKind.None || string.IsNullOrWhiteSpace(config.DnsApiToken))
        {
            return "No DNS provider configured.";
        }

        if (string.IsNullOrWhiteSpace(config.CertificatePath))
        {
            return "No certificate output path configured.";
        }

        return null;
    }

    private string Record(Plugin plugin, string message)
    {
        plugin.Configuration.LastAttemptUtc = DateTime.UtcNow;
        plugin.Configuration.LastResult = message;
        plugin.UpdateConfiguration(plugin.Configuration);
        _logger.LogInformation("ACME: {Result}", message);
        return message;
    }

    private async Task<DateTime> IssueAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var directory = config.UseStaging
            ? WellKnownServers.LetsEncryptStagingV2
            : WellKnownServers.LetsEncryptV2;

        var acme = await GetOrCreateAccountAsync(config, directory, cancellationToken).ConfigureAwait(false);

        var order = await acme.NewOrder(new[] { config.Domain }).ConfigureAwait(false);
        var authorizations = await order.Authorizations().ConfigureAwait(false);

        var dnsProvider = CreateDnsProvider(config);
        var placedRecords = new List<string>();

        try
        {
            foreach (var authorization in authorizations)
            {
                var challenge = await authorization.Dns().ConfigureAwait(false);
                var digest = acme.AccountKey.DnsTxt(challenge.Token);

                var resource = await authorization.Resource().ConfigureAwait(false);
                var recordName = "_acme-challenge." + resource.Identifier.Value.TrimStart('*', '.');

                var handle = await dnsProvider
                    .CreateTxtRecordAsync(recordName, digest, cancellationToken).ConfigureAwait(false);
                placedRecords.Add(handle);

                _logger.LogInformation(
                    "Waiting {Seconds}s for DNS propagation before asking the CA to validate",
                    config.DnsPropagationSeconds);
                await Task.Delay(TimeSpan.FromSeconds(config.DnsPropagationSeconds), cancellationToken)
                    .ConfigureAwait(false);

                await challenge.Validate().ConfigureAwait(false);
                await WaitForValidationAsync(authorization, cancellationToken).ConfigureAwait(false);
            }

            var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var chain = await order.Generate(new CsrInfo { CommonName = config.Domain }, privateKey)
                .ConfigureAwait(false);

            var password = EnsurePassword(config);
            var pfx = chain.ToPfx(privateKey).Build(config.Domain, password);

            await WriteCertificateAsync(config.CertificatePath, pfx, cancellationToken).ConfigureAwait(false);

            using var issued = X509CertificateLoader.LoadPkcs12(pfx, password);
            return issued.NotAfter.ToUniversalTime();
        }
        finally
        {
            foreach (var handle in placedRecords)
            {
                await dnsProvider.DeleteTxtRecordAsync(handle, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<IAcmeContext> GetOrCreateAccountAsync(
        PluginConfiguration config,
        Uri directory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(config.AccountKeyPem))
        {
            var existing = new AcmeContext(directory, KeyFactory.FromPem(config.AccountKeyPem));

            // Confirms the stored key still corresponds to a registered account.
            await existing.Account().ConfigureAwait(false);
            return existing;
        }

        _logger.LogInformation("Registering a new ACME account against {Directory}", directory);

        var acme = new AcmeContext(directory);
        await acme.NewAccount(config.AccountEmail, termsOfServiceAgreed: true).ConfigureAwait(false);

        config.AccountKeyPem = acme.AccountKey.ToPem();
        Plugin.Instance!.UpdateConfiguration(config);

        cancellationToken.ThrowIfCancellationRequested();
        return acme;
    }

    private async Task WaitForValidationAsync(
        IAuthorizationContext authorization,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _validationTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var resource = await authorization.Resource().ConfigureAwait(false);

            if (resource.Status == AuthorizationStatus.Valid)
            {
                return;
            }

            if (resource.Status == AuthorizationStatus.Invalid)
            {
                var detail = resource.Challenges?
                    .FirstOrDefault(c => c.Error is not null)?.Error?.Detail;

                throw new InvalidOperationException(
                    "The certificate authority could not validate the challenge"
                    + (detail is null ? "." : " — " + detail));
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            "Timed out waiting for the certificate authority to validate the DNS challenge. "
            + "The TXT record may not have propagated yet; try a longer propagation delay.");
    }

    private IDnsProvider CreateDnsProvider(PluginConfiguration config) => config.DnsProvider switch
    {
        DnsProviderKind.Cloudflare => new CloudflareDnsProvider(
            _httpClientFactory.CreateClient(nameof(CloudflareDnsProvider)),
            config.DnsApiToken,
            _logger),
        _ => throw new InvalidOperationException("No supported DNS provider is configured.")
    };

    private static string EnsurePassword(PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(config.CertificatePassword))
        {
            config.CertificatePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Plugin.Instance!.UpdateConfiguration(config);
        }

        return config.CertificatePassword;
    }

    /// <summary>
    /// Writes the bundle to a temporary file, restricts it to the server account, then
    /// moves it into place — so a reader never observes a half-written certificate.
    /// </summary>
    private static async Task WriteCertificateAsync(string path, byte[] pfx, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, pfx, cancellationToken).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }
}
