using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Jellyfin.Plugin.Backporch.Configuration;
using Jellyfin.Plugin.Backporch.Dns;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Backporch.Acme;

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
    private static readonly TimeSpan _manualDnsTimeout = TimeSpan.FromMinutes(15);

    private readonly ILogger<AcmeService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IssuanceState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcmeService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="httpClientFactory">Factory for provider HTTP clients.</param>
    /// <param name="state">Shared progress state polled by the configuration page.</param>
    public AcmeService(ILogger<AcmeService> logger, IHttpClientFactory httpClientFactory, IssuanceState state)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _state = state;
    }

    /// <summary>
    /// Issues a certificate if one is needed, or if <paramref name="force"/> is set.
    /// Used by the renewal task; honours the configured CA as-is.
    /// </summary>
    /// <param name="force">Issue even when the current certificate is still healthy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A human-readable description of what happened.</returns>
    public async Task<string> RunAsync(bool force, CancellationToken cancellationToken)
    {
        if (!_state.TryBegin())
        {
            return "An issuance is already running.";
        }

        Plugin? plugin = null;

        try
        {
            plugin = Plugin.Instance
                ?? throw new InvalidOperationException("Plugin instance is not available.");
            var config = plugin.Configuration;

            ApplyDefaultCertificatePath(plugin, config);

            var problem = Validate(config);
            if (problem is not null)
            {
                _state.Finish(false, problem);
                return Record(plugin, problem);
            }

            if (!force && !NeedsRenewal(config))
            {
                var days = (config.CertificateExpiryUtc!.Value - DateTime.UtcNow).TotalDays;
                var healthy = string.Create(
                    CultureInfo.InvariantCulture,
                    $"No action needed — certificate is valid for another {days:F0} days.");
                _state.Finish(true, healthy);
                return healthy;
            }

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

            _state.Finish(true, message);
            return Record(plugin, message);
        }
        catch (OperationCanceledException)
        {
            _state.Finish(false, "Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Certificate issuance failed");
            var failure = "Failed — " + ex.Message;
            _state.Finish(false, failure);
            return plugin is null ? failure : Record(plugin, failure);
        }
    }

    /// <summary>
    /// The guided-setup path behind the page's one button: while the configuration is
    /// unproven it first performs a staging dry run (discarded afterwards), then issues
    /// the real certificate from production. The user never has to know staging exists.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A human-readable description of what happened.</returns>
    public async Task<string> RunGuidedAsync(CancellationToken cancellationToken)
    {
        if (!_state.TryBegin())
        {
            return "An issuance is already running.";
        }

        Plugin? plugin = null;

        try
        {
            plugin = Plugin.Instance
                ?? throw new InvalidOperationException("Plugin instance is not available.");
            var config = plugin.Configuration;

            ApplyDefaultCertificatePath(plugin, config);

            var problem = Validate(config);
            if (problem is not null)
            {
                _state.Finish(false, problem);
                return Record(plugin, problem);
            }

            if (config.UseStaging && string.IsNullOrWhiteSpace(config.DirectoryUrl))
            {
                _state.SetTestRun(true);
                _state.Report(
                    IssuancePhase.Starting,
                    "Running a test issuance first, so mistakes cost nothing.");

                var testConfig = CloneForTestRun(config);

                try
                {
                    await IssueCertificateAsync(
                        testConfig,
                        CreateDnsProvider(testConfig),
                        WellKnownServers.LetsEncryptStagingV2,
                        acmeHttpClient: null,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (File.Exists(testConfig.CertificatePath))
                    {
                        File.Delete(testConfig.CertificatePath);
                    }
                }

                // Keep the account the dry run registered, and mark the setup proven.
                config.AccountKeyPem = testConfig.AccountKeyPem;
                config.UseStaging = false;
                plugin.UpdateConfiguration(config);

                _state.SetTestRun(false);
                _logger.LogInformation("Staging dry run succeeded; issuing the real certificate");
            }

            var expiry = await IssueAsync(config, cancellationToken).ConfigureAwait(false);
            config.CertificateExpiryUtc = expiry;

            var message = string.Format(
                CultureInfo.InvariantCulture,
                "Issued a certificate for {0}, valid until {1:yyyy-MM-dd}. Renewal is automatic.",
                config.Domain,
                expiry);

            _state.Finish(true, message);
            return Record(plugin, message);
        }
        catch (OperationCanceledException)
        {
            _state.Finish(false, "Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guided issuance failed");
            var failure = "Failed — " + ex.Message;
            _state.Finish(false, failure);
            return plugin is null ? failure : Record(plugin, failure);
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

        if (config.DnsProvider == DnsProviderKind.None)
        {
            return "Choose how the DNS challenge record will be added.";
        }

        if (config.DnsProvider == DnsProviderKind.Cloudflare && string.IsNullOrWhiteSpace(config.DnsApiToken))
        {
            return "Cloudflare is selected but no API token is set.";
        }

        if (string.IsNullOrWhiteSpace(config.CertificatePath))
        {
            return "No certificate output path configured.";
        }

        return null;
    }

    private static void ApplyDefaultCertificatePath(Plugin plugin, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.CertificatePath))
        {
            config.CertificatePath = plugin.DefaultCertificatePath;
            plugin.UpdateConfiguration(config);
        }
    }

    private static PluginConfiguration CloneForTestRun(PluginConfiguration config) => new()
    {
        Enabled = config.Enabled,
        Domain = config.Domain,
        AccountEmail = config.AccountEmail,
        DnsProvider = config.DnsProvider,
        DnsApiToken = config.DnsApiToken,
        UseStaging = true,
        DnsPropagationSeconds = config.DnsPropagationSeconds,
        RenewDaysBeforeExpiry = config.RenewDaysBeforeExpiry,
        CertificatePath = config.CertificatePath + ".test",
        CertificatePassword = config.CertificatePassword,
        AccountKeyPem = config.AccountKeyPem
    };

    private string Record(Plugin plugin, string message)
    {
        plugin.Configuration.LastAttemptUtc = DateTime.UtcNow;
        plugin.Configuration.LastResult = message;
        plugin.UpdateConfiguration(plugin.Configuration);
        _logger.LogInformation("ACME: {Result}", message);
        return message;
    }

    private Task<DateTime> IssueAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var directory = !string.IsNullOrWhiteSpace(config.DirectoryUrl)
            ? new Uri(config.DirectoryUrl)
            : config.UseStaging
                ? WellKnownServers.LetsEncryptStagingV2
                : WellKnownServers.LetsEncryptV2;

        return IssueCertificateAsync(config, CreateDnsProvider(config), directory, acmeHttpClient: null, cancellationToken);
    }

    /// <summary>
    /// The full issuance pipeline: account, order, DNS-01 challenge, finalize, write PFX.
    /// Public and dependency-free of the Jellyfin runtime so integration tests can drive
    /// it against a test CA.
    /// </summary>
    /// <param name="config">The configuration to issue for.</param>
    /// <param name="dnsProvider">The DNS provider used to answer the challenge.</param>
    /// <param name="directory">The ACME directory endpoint.</param>
    /// <param name="acmeHttpClient">Optional HTTP client for ACME traffic (test CAs use self-signed TLS).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The UTC expiry of the issued certificate.</returns>
    public async Task<DateTime> IssueCertificateAsync(
        PluginConfiguration config,
        IDnsProvider dnsProvider,
        Uri directory,
        HttpClient? acmeHttpClient,
        CancellationToken cancellationToken)
    {
        _state.Report(IssuancePhase.Account, "Checking the certificate authority account…");
        var acme = await GetOrCreateAccountAsync(config, directory, acmeHttpClient, cancellationToken).ConfigureAwait(false);

        _state.Report(IssuancePhase.PublishingDns, "Creating the certificate order…");
        var order = await acme.NewOrder(new[] { config.Domain }).ConfigureAwait(false);
        var authorizations = await order.Authorizations().ConfigureAwait(false);

        var placedRecords = new List<string>();

        try
        {
            foreach (var authorization in authorizations)
            {
                var challenge = await authorization.Dns().ConfigureAwait(false);
                var digest = acme.AccountKey.DnsTxt(challenge.Token);

                var resource = await authorization.Resource().ConfigureAwait(false);
                var recordName = "_acme-challenge." + resource.Identifier.Value.TrimStart('*', '.');

                // The manual provider flips the phase to AwaitingDnsRecord while it
                // waits for the user; API providers pass straight through.
                _state.Report(IssuancePhase.PublishingDns, "Publishing the DNS challenge record…");
                var handle = await dnsProvider
                    .CreateTxtRecordAsync(recordName, digest, cancellationToken).ConfigureAwait(false);
                placedRecords.Add(handle);

                _logger.LogInformation(
                    "Waiting {Seconds}s for DNS propagation before asking the CA to validate",
                    config.DnsPropagationSeconds);

                var remaining = TimeSpan.FromSeconds(config.DnsPropagationSeconds);
                while (remaining > TimeSpan.Zero)
                {
                    _state.Report(
                        IssuancePhase.Propagating,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Waiting for DNS to propagate — about {(int)remaining.TotalSeconds}s left…"));

                    var step = remaining > _pollInterval ? _pollInterval : remaining;
                    await Task.Delay(step, cancellationToken).ConfigureAwait(false);
                    remaining -= step;
                }

                _state.Report(IssuancePhase.Validating, "The certificate authority is checking the record…");
                await challenge.Validate().ConfigureAwait(false);
                await WaitForValidationAsync(authorization, cancellationToken).ConfigureAwait(false);
            }

            _state.Report(IssuancePhase.Finalizing, "Downloading the certificate…");
            var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var chain = await order.Generate(new CsrInfo { CommonName = config.Domain }, privateKey)
                .ConfigureAwait(false);

            // Assemble the PKCS#12 with .NET's own crypto from exactly the chain the CA
            // returned. Certes' PfxBuilder resolves issuers against an embedded root
            // store instead, which breaks on any root it doesn't know — test CAs today,
            // a rotated production root tomorrow.
            var pfx = BuildPfx(chain, privateKey, config.CertificatePassword);

            _state.Report(IssuancePhase.WritingCertificate, "Writing the certificate to disk…");
            await WriteCertificateAsync(config.CertificatePath, pfx, cancellationToken).ConfigureAwait(false);

            using var issued = X509CertificateLoader.LoadPkcs12(pfx, config.CertificatePassword);
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
        HttpClient? acmeHttpClient,
        CancellationToken cancellationToken)
    {
        var http = acmeHttpClient is null ? null : new AcmeHttpClient(directory, acmeHttpClient);

        if (!string.IsNullOrWhiteSpace(config.AccountKeyPem))
        {
            var existing = new AcmeContext(directory, KeyFactory.FromPem(config.AccountKeyPem), http);

            try
            {
                // Confirms the stored key corresponds to an account registered at THIS CA.
                await existing.Account().ConfigureAwait(false);
                return existing;
            }
            catch (AcmeException)
            {
                // The key exists but this CA has never seen it — typical when switching
                // from staging to production. Register the same key here rather than
                // discarding it.
                _logger.LogInformation("Registering the existing account key with {Directory}", directory);
                await existing.NewAccount(config.AccountEmail, termsOfServiceAgreed: true).ConfigureAwait(false);
                return existing;
            }
        }

        _logger.LogInformation("Registering a new ACME account against {Directory}", directory);

        var acme = new AcmeContext(directory, null, http);
        await acme.NewAccount(config.AccountEmail, termsOfServiceAgreed: true).ConfigureAwait(false);

        config.AccountKeyPem = acme.AccountKey.ToPem();

        // Persist only when we hold the live configuration — a dry-run clone must never
        // replace the real one (UpdateConfiguration swaps the whole object in).
        if (Plugin.Instance is { } plugin && ReferenceEquals(plugin.Configuration, config))
        {
            plugin.UpdateConfiguration(config);
        }

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
        DnsProviderKind.Manual => new ManualDnsProvider(_state, _manualDnsTimeout),
        _ => throw new InvalidOperationException("No supported DNS provider is configured.")
    };

    /// <summary>
    /// Builds a PKCS#12 bundle from the CA-provided chain: the leaf bound to its private
    /// key, followed by the intermediates exactly as the CA served them. The root is
    /// whatever the chain carries — no external trust store is consulted.
    /// </summary>
    private static byte[] BuildPfx(CertificateChain chain, IKey privateKey, string password)
    {
        var collection = new X509Certificate2Collection();

        using var leaf = X509Certificate2.CreateFromPem(chain.Certificate.ToPem(), privateKey.ToPem());
        collection.Add(leaf);

        if (chain.Issuers is not null)
        {
            foreach (var issuer in chain.Issuers)
            {
                collection.Add(X509Certificate2.CreateFromPem(issuer.ToPem()));
            }
        }

        return collection.Export(X509ContentType.Pfx, password)
            ?? throw new InvalidOperationException("PKCS#12 export produced no data.");
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
