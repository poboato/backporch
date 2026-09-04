using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
    /// <summary>
    /// How many times to retry a request the CA rejects for a stale anti-replay nonce.
    /// RFC 8555 requires clients to retry with a fresh one; Certes defaults to a single
    /// retry, which is thin when nonces expire mid-run (Let's Encrypt) or are rejected
    /// deliberately to test clients (Pebble rejects a percentage on purpose). Retries are
    /// cheap and never re-issue anything — a rejected request was never processed.
    /// </summary>
    private const int BadNonceRetries = 5;

    private static readonly TimeSpan _validationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _manualDnsTimeout = TimeSpan.FromMinutes(15);

    private readonly ILogger<AcmeService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IssuanceState _state;
    private readonly HttpChallengeStore _httpChallenges;

    /// <summary>
    /// Initializes a new instance of the <see cref="AcmeService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="httpClientFactory">Factory for provider HTTP clients.</param>
    /// <param name="state">Shared progress state polled by the configuration page.</param>
    /// <param name="httpChallenges">Store the anonymous challenge route serves from.</param>
    public AcmeService(
        ILogger<AcmeService> logger,
        IHttpClientFactory httpClientFactory,
        IssuanceState state,
        HttpChallengeStore httpChallenges)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _state = state;
        _httpChallenges = httpChallenges;
    }

    /// <summary>
    /// Issues a certificate if one is needed, or if <paramref name="force"/> is set.
    /// Used by the renewal task; honours the configured CA as-is.
    /// </summary>
    /// <param name="force">Issue even when the current certificate is still healthy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="unattended">
    /// Set for the scheduled task, where no one is at the configuration page: modes that
    /// need a human (manual DNS) fail immediately rather than waiting out their timeout.
    /// </param>
    /// <returns>A human-readable description of what happened.</returns>
    public async Task<string> RunAsync(
        bool force,
        CancellationToken cancellationToken,
        bool unattended = false)
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

            // Work from a copy: a save from the setup page would swap the live object out
            // from under a run that lasts minutes. Results merge back in Persist().
            var config = plugin.Configuration.Clone();

            ApplyDefaultCertificatePath(plugin, config);

            var problem = Validate(config) ?? (unattended ? UnattendedProblem(config) : null);
            if (problem is not null)
            {
                _state.Finish(false, problem);
                return Persist(plugin, config, problem, expiry: null);
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
            return Persist(plugin, config, message, expiry);
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
            return plugin is null ? failure : Persist(plugin, null, failure, expiry: null);
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

            // As in RunAsync: issue against a copy, merge at the end.
            var config = plugin.Configuration.Clone();

            ApplyDefaultCertificatePath(plugin, config);

            var problem = Validate(config);
            if (problem is not null)
            {
                _state.Finish(false, problem);
                return Persist(plugin, config, problem, expiry: null);
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
                        testConfig.Challenge == ChallengeKind.Dns ? CreateDnsProvider(testConfig) : null,
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
                // Persist through the merge so a save made during the dry run survives.
                config.AccountKeyPem = testConfig.AccountKeyPem;
                config.UseStaging = false;
                Persist(plugin, config, message: null, expiry: null);

                _state.SetTestRun(false);
                _logger.LogInformation("Staging dry run succeeded; issuing the real certificate");
            }

            var expiry = await IssueAsync(config, cancellationToken).ConfigureAwait(false);

            var message = string.Format(
                CultureInfo.InvariantCulture,
                "Issued a certificate for {0}, valid until {1:yyyy-MM-dd}.{2}",
                config.Domain,
                expiry,
                config.Challenge == ChallengeKind.Dns && config.DnsProvider == DnsProviderKind.Manual
                    ? " Renewal will ask you for a new TXT record — see the page before it expires."
                    : " Renewal is automatic.");

            _state.Finish(true, message);
            return Persist(plugin, config, message, expiry);
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
            return plugin is null ? failure : Persist(plugin, null, failure, expiry: null);
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

        // Every name on the certificate is validated the same way. One bad entry fails
        // the whole order at the certificate authority, so it is worth naming the exact
        // offender here rather than letting the CA answer with an identifier error.
        foreach (var name in config.AllDomains())
        {
            if (name.Contains('*', StringComparison.Ordinal))
            {
                return "Wildcard certificates are not supported \u2014 remove " + name
                    + " and list each name in full instead.";
            }

            if (!IsValidHostname(name))
            {
                return name + " is not a valid hostname \u2014 enter each name in full, "
                    + "like media.example.com, with no scheme, port, or path.";
            }
        }

        if (string.IsNullOrWhiteSpace(config.AccountEmail))
        {
            return "No contact email configured.";
        }

        if (config.Challenge == ChallengeKind.Dns)
        {
            if (config.DnsProvider == DnsProviderKind.None)
            {
                return "Choose how the DNS challenge record will be added.";
            }

            if (config.DnsProvider == DnsProviderKind.Cloudflare && string.IsNullOrWhiteSpace(config.DnsApiToken))
            {
                return "Cloudflare is selected but no API token is set.";
            }
        }

        if (string.IsNullOrWhiteSpace(config.CertificatePath))
        {
            return "No certificate output path configured.";
        }

        return null;
    }

    /// <summary>
    /// Whether the value is a syntactically valid DNS hostname of at least two labels.
    /// </summary>
    /// <remarks>
    /// Everything downstream treats this string as a name: it is asked of the resolver,
    /// sent to the certificate authority, and used to build the challenge record. Refusing
    /// anything that is not a hostname here keeps a URL, a port, or stray control
    /// characters from reaching any of them, and gives the user one clear message instead
    /// of an obscure failure several steps later.
    /// </remarks>
    internal static bool IsValidHostname(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253)
        {
            return false;
        }

        var labels = domain.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63
                || label.StartsWith('-')
                || label.EndsWith('-'))
            {
                return false;
            }

            foreach (var c in label)
            {
                var ok = c is >= 'a' and <= 'z'
                    || c is >= 'A' and <= 'Z'
                    || c is >= '0' and <= '9'
                    || c == '-';

                if (!ok)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Manual DNS needs someone at the configuration page to publish the TXT record. On
    /// the nightly task there is no one, so say so at once instead of blocking for the
    /// full manual-DNS timeout and failing anyway.
    /// </summary>
    private static string? UnattendedProblem(PluginConfiguration config) =>
        config.Challenge == ChallengeKind.Dns && config.DnsProvider == DnsProviderKind.Manual
            ? "Manual DNS mode needs someone to publish the challenge record, so it cannot "
              + "renew on a schedule. Open the Backporch page to renew by hand, or switch to "
              + "the automatic proof (or Cloudflare) to make renewals hands-off."
            : null;

    private static PluginConfiguration CloneForTestRun(PluginConfiguration config)
    {
        var test = config.Clone();
        test.UseStaging = true;
        test.CertificatePath = config.CertificatePath + ".test";

        // A rehearsal must not publish. The staging certificate is signed by a root no
        // browser trusts, so letting it land on the PEM paths would hand a reverse proxy
        // serving every other application a certificate that fails on every device the
        // moment it reloaded. The rehearsal only needs to prove the challenge answers.
        test.PemCertificatePath = string.Empty;
        test.PemPrivateKeyPath = string.Empty;
        return test;
    }

    private static void ApplyDefaultCertificatePath(Plugin plugin, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.CertificatePath))
        {
            config.CertificatePath = plugin.DefaultCertificatePath;
        }
    }

    /// <summary>
    /// The single point where a run's results reach persisted configuration. Re-reads the
    /// live object first, because the one the run started from may have been replaced by a
    /// save from the setup page while issuance was in flight; only the fields the run owns
    /// are copied across, so the user's edits survive and the run's results are not lost.
    /// </summary>
    /// <param name="plugin">The plugin whose configuration is being updated.</param>
    /// <param name="source">The run's working copy, or <c>null</c> when a run failed before producing one.</param>
    /// <param name="message">Outcome to record, or <c>null</c> to leave the last outcome alone.</param>
    /// <param name="expiry">Expiry of a freshly issued certificate, when there is one.</param>
    /// <returns>The message, for the caller to return onward.</returns>
    private string Persist(Plugin plugin, PluginConfiguration? source, string? message, DateTime? expiry)
    {
        var live = plugin.Configuration;

        if (message is not null)
        {
            live.LastAttemptUtc = DateTime.UtcNow;
            live.LastResult = message;
        }

        if (expiry is not null)
        {
            live.CertificateExpiryUtc = expiry;
        }

        if (source is not null)
        {
            // Server-owned fields the run may have established.
            if (!string.IsNullOrWhiteSpace(source.AccountKeyPem))
            {
                live.AccountKeyPem = source.AccountKeyPem;
            }

            if (string.IsNullOrWhiteSpace(live.CertificatePath)
                && !string.IsNullOrWhiteSpace(source.CertificatePath))
            {
                live.CertificatePath = source.CertificatePath;
            }

            // The dry run proves production is safe; never flip staging back on here.
            if (!source.UseStaging)
            {
                live.UseStaging = false;
            }
        }

        plugin.UpdateConfiguration(live);

        if (message is not null)
        {
            _logger.LogInformation("ACME: {Result}", message);
        }

        return message ?? string.Empty;
    }

    private Task<DateTime> IssueAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var directory = !string.IsNullOrWhiteSpace(config.DirectoryUrl)
            ? new Uri(config.DirectoryUrl)
            : config.UseStaging
                ? WellKnownServers.LetsEncryptStagingV2
                : WellKnownServers.LetsEncryptV2;

        return IssueCertificateAsync(
            config,
            config.Challenge == ChallengeKind.Dns ? CreateDnsProvider(config) : null,
            directory,
            acmeHttpClient: null,
            cancellationToken);
    }

    /// <summary>
    /// The full issuance pipeline: account, order, DNS-01 challenge, finalize, write PFX.
    /// Public and dependency-free of the Jellyfin runtime so integration tests can drive
    /// it against a test CA.
    /// </summary>
    /// <param name="config">The configuration to issue for.</param>
    /// <param name="dnsProvider">The DNS provider answering the challenge; required for DNS-01 only.</param>
    /// <param name="directory">The ACME directory endpoint.</param>
    /// <param name="acmeHttpClient">Optional HTTP client for ACME traffic (test CAs use self-signed TLS).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The UTC expiry of the issued certificate.</returns>
    public async Task<DateTime> IssueCertificateAsync(
        PluginConfiguration config,
        IDnsProvider? dnsProvider,
        Uri directory,
        HttpClient? acmeHttpClient,
        CancellationToken cancellationToken)
    {
        _state.Report(IssuancePhase.Account, "Checking the certificate authority account…");
        var acme = await GetOrCreateAccountAsync(config, directory, acmeHttpClient, cancellationToken).ConfigureAwait(false);

        _state.Report(IssuancePhase.PublishingDns, "Creating the certificate order…");
        // One order, every name. The certificate authority opens an authorization per
        // name and the loop below answers each in turn; a single HTTP-01 listener can
        // satisfy all of them, because every name resolves to this same host.
        var domains = config.AllDomains();
        _logger.LogInformation("Ordering a certificate for {Count} name(s)", domains.Count);
        var order = await acme.NewOrder(domains).ConfigureAwait(false);
        var authorizations = await order.Authorizations().ConfigureAwait(false);

        var placedRecords = new List<string>();
        var placedTokens = new List<string>();

        try
        {
            foreach (var authorization in authorizations)
            {
                // A certificate authority may hand back an authorization it already
                // considers valid (Let's Encrypt reuses them for about 30 days). Posting a
                // validation to one is an error — "authorization must be pending" — so
                // there is nothing to answer, and on the manual path it would otherwise
                // send the user off to publish a TXT record nothing will ever read.
                if (await IsAlreadyValidAsync(authorization).ConfigureAwait(false))
                {
                    _logger.LogInformation(
                        "Reusing an authorization the certificate authority already accepted");
                    continue;
                }

                if (config.Challenge == ChallengeKind.Http)
                {
                    await AnswerHttpChallengeAsync(acme, authorization, placedTokens, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await AnswerDnsChallengeAsync(
                        acme,
                        authorization,
                        dnsProvider ?? throw new InvalidOperationException("DNS-01 requires a DNS provider."),
                        config,
                        placedRecords,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            _state.Report(IssuancePhase.Finalizing, "Downloading the certificate…");
            var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            // The common name is the primary name; Certes adds every other identifier on
            // the order to the request as a subject alternative name.
            var chain = await order.Generate(new CsrInfo { CommonName = domains[0] }, privateKey)
                .ConfigureAwait(false);

            // Assemble the PKCS#12 with .NET's own crypto from exactly the chain the CA
            // returned. Certes' PfxBuilder resolves issuers against an embedded root
            // store instead, which breaks on any root it doesn't know — test CAs today,
            // a rotated production root tomorrow.
            var pfx = BuildPfx(chain, privateKey, config.CertificatePassword);

            _state.Report(IssuancePhase.WritingCertificate, "Writing the certificate to disk…");
            await WriteCertificateAsync(config.CertificatePath, pfx, secret: true, cancellationToken)
                .ConfigureAwait(false);
            await WritePemAsync(config, chain, privateKey, cancellationToken).ConfigureAwait(false);

            using var issued = X509CertificateLoader.LoadPkcs12(pfx, config.CertificatePassword);
            return issued.NotAfter.ToUniversalTime();
        }
        finally
        {
            foreach (var token in placedTokens)
            {
                _httpChallenges.Remove(token);
            }

            if (dnsProvider is not null)
            {
                foreach (var handle in placedRecords)
                {
                    await dnsProvider.DeleteTxtRecordAsync(handle, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// HTTP-01: publish the key authorization on this server's own well-known route and
    /// let the CA fetch it. Nothing external to create, no propagation to wait for.
    /// </summary>
    private async Task AnswerHttpChallengeAsync(
        IAcmeContext acme,
        IAuthorizationContext authorization,
        List<string> placedTokens,
        CancellationToken cancellationToken)
    {
        var challenge = await authorization.Http().ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The certificate authority offered no HTTP challenge for this order.");

        var keyAuthorization = acme.AccountKey.KeyAuthorization(challenge.Token);
        _httpChallenges.Put(challenge.Token, keyAuthorization);
        placedTokens.Add(challenge.Token);

        _state.Report(
            IssuancePhase.Validating,
            "The certificate authority is contacting this server on port 80…");
        await challenge.Validate().ConfigureAwait(false);
        await WaitForValidationAsync(authorization, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// DNS-01: publish the challenge TXT record (via API or the user's hands), wait out
    /// propagation, then ask the CA to look.
    /// </summary>
    private async Task AnswerDnsChallengeAsync(
        IAcmeContext acme,
        IAuthorizationContext authorization,
        IDnsProvider dnsProvider,
        PluginConfiguration config,
        List<string> placedRecords,
        CancellationToken cancellationToken)
    {
        var challenge = await authorization.Dns().ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The certificate authority offered no DNS challenge for this order. This "
                + "happens when it reuses an authorization from an earlier run that was "
                + "proven a different way; try again in a few minutes.");

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

    /// <summary>
    /// Whether the certificate authority already treats this authorization as proven, in
    /// which case no challenge should be answered or posted for it.
    /// </summary>
    private static async Task<bool> IsAlreadyValidAsync(IAuthorizationContext authorization)
    {
        var resource = await authorization.Resource().ConfigureAwait(false);
        return resource.Status == AuthorizationStatus.Valid;
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
            var existing = new AcmeContext(
                directory, KeyFactory.FromPem(config.AccountKeyPem), http, BadNonceRetries);

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

        var acme = new AcmeContext(directory, null, http, BadNonceRetries);
        await acme.NewAccount(config.AccountEmail, termsOfServiceAgreed: true).ConfigureAwait(false);

        // Recorded on the run's working copy; Persist() carries it into the live
        // configuration once, at the end of the run.
        config.AccountKeyPem = acme.AccountKey.ToPem();

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
    /// <summary>
    /// Writes the chain and key in PEM form for a reverse proxy to read, when paths for
    /// them are configured.
    /// </summary>
    /// <remarks>
    /// The certificate goes out as leaf first and then each issuer, which is the order
    /// every proxy expects; a chain assembled the other way round is accepted by the
    /// proxy at start and then rejected by clients, which is a miserable thing to
    /// diagnose. Failing to write these must not fail an issuance that has already
    /// succeeded \u2014 the certificate is on disk and valid \u2014 so a problem here is
    /// reported and swallowed.
    /// </remarks>
    private async Task WritePemAsync(
        PluginConfiguration config,
        CertificateChain chain,
        IKey privateKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.PemCertificatePath)
            && string.IsNullOrWhiteSpace(config.PemPrivateKeyPath))
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(config.PemCertificatePath))
            {
                var pem = new StringBuilder();
                pem.Append(chain.Certificate.ToPem());

                foreach (var issuer in chain.Issuers)
                {
                    pem.Append(issuer.ToPem());
                }

                await WriteCertificateAsync(
                    config.PemCertificatePath,
                    Encoding.ASCII.GetBytes(pem.ToString()),
                    secret: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(config.PemPrivateKeyPath))
            {
                await WriteCertificateAsync(
                    config.PemPrivateKeyPath,
                    Encoding.ASCII.GetBytes(privateKey.ToPem()),
                    secret: true,
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Wrote the certificate in PEM form for a reverse proxy");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                ex,
                "The certificate was issued, but writing the PEM copy failed. A reverse "
                + "proxy reading those files will keep serving the previous certificate.");
        }
    }

    private static async Task WriteCertificateAsync(
        string path,
        byte[] content,
        bool secret,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            var existed = System.IO.Directory.Exists(parent);
            System.IO.Directory.CreateDirectory(parent);

            // Only tighten a directory we just made — never re-permission one the
            // administrator pointed us at and may share with something else.
            if (!existed && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    parent,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // The bundle carries the private key, so it must never exist — even briefly —
        // at permissions anyone else can read. Creating it with the mode set (rather
        // than chmod-ing afterwards) closes that window. The name is unpredictable and
        // creation is exclusive, so nothing can pre-place a symlink for us to write
        // through, which matters because the output path is administrator-chosen and
        // may sit in a shared directory.
        var temp = Path.Combine(
            string.IsNullOrEmpty(parent) ? "." : parent,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = secret
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        }

        try
        {
            var stream = new FileStream(temp, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Rename keeps the mode, and replaces the old bundle in one step, so a
            // reader never sees a partial certificate.
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }
    }
}
