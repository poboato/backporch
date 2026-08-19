using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Acme.Configuration;

/// <summary>
/// Which DNS provider hosts the zone for the configured domain.
/// </summary>
public enum DnsProviderKind
{
    /// <summary>No provider selected; issuance is disabled.</summary>
    None = 0,

    /// <summary>Cloudflare, via the v4 API.</summary>
    Cloudflare = 1
}

/// <summary>
/// Plugin settings. Persisted by Jellyfin as XML under the plugin configuration directory.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the fully qualified domain name the certificate is issued for,
    /// for example <c>media.example.com</c>.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the contact address registered with the ACME account. Let's Encrypt
    /// uses it only for expiry warnings.
    /// </summary>
    public string AccountEmail { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the DNS provider holding the zone.
    /// </summary>
    public DnsProviderKind DnsProvider { get; set; } = DnsProviderKind.None;

    /// <summary>
    /// Gets or sets the DNS provider API token.
    /// </summary>
    /// <remarks>
    /// Scope this token as narrowly as the provider allows — for Cloudflare, a token
    /// limited to <c>Zone.DNS:Edit</c> on the single zone. It is stored on disk in the
    /// plugin configuration and is never written to logs.
    /// </remarks>
    public string DnsApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to use the Let's Encrypt staging
    /// environment. Staging issues untrusted certificates but has far looser rate
    /// limits, so it is the default until a configuration is proven to work.
    /// </summary>
    public bool UseStaging { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin may request or renew
    /// certificates. Off until explicitly enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets how many days before expiry a renewal is attempted.
    /// </summary>
    public int RenewDaysBeforeExpiry { get; set; } = 30;

    /// <summary>
    /// Gets or sets how long to wait for the challenge TXT record to propagate, in seconds.
    /// </summary>
    public int DnsPropagationSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the absolute path the PKCS#12 bundle is written to. Point Jellyfin's
    /// network settings at this same path.
    /// </summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password protecting the PKCS#12 bundle. Generated on first use.
    /// </summary>
    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the PEM-encoded ACME account key. Generated on first registration and
    /// reused thereafter so the account is not re-created on every run.
    /// </summary>
    public string AccountKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC time of the last issuance attempt.
    /// </summary>
    public DateTime? LastAttemptUtc { get; set; }

    /// <summary>
    /// Gets or sets a human-readable outcome of the last issuance attempt.
    /// </summary>
    public string LastResult { get; set; } = "Never run";

    /// <summary>
    /// Gets or sets the expiry of the certificate currently on disk.
    /// </summary>
    public DateTime? CertificateExpiryUtc { get; set; }
}
