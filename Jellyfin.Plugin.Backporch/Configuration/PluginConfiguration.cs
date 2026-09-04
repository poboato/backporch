using System.Text.Json;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Backporch.Configuration;

/// <summary>
/// Which DNS provider hosts the zone for the configured domain.
/// </summary>
public enum DnsProviderKind
{
    /// <summary>No provider selected; issuance is disabled.</summary>
    None = 0,

    /// <summary>Cloudflare, via the v4 API.</summary>
    Cloudflare = 1,

    /// <summary>
    /// Any other DNS host: the user adds the challenge TXT record by hand when the
    /// configuration page shows it. No API token involved.
    /// </summary>
    Manual = 2
}

/// <summary>
/// How ownership of the domain is proven to the certificate authority.
/// </summary>
public enum ChallengeKind
{
    /// <summary>
    /// HTTP-01: the CA fetches a token from this very server over port 80. Nothing to
    /// create or copy — the domain's A record plus a port forward is the whole setup,
    /// and renewal is fully automatic. The default.
    /// </summary>
    Http = 0,

    /// <summary>
    /// DNS-01: a TXT record answers the challenge, via a provider API or by hand.
    /// Works without any inbound connectivity.
    /// </summary>
    Dns = 1
}

/// <summary>
/// Plugin settings. Persisted by Jellyfin as XML under the plugin configuration directory.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    private string _domain = string.Empty;

    /// <summary>
    /// Gets or sets how the domain-ownership challenge is answered.
    /// </summary>
    public ChallengeKind Challenge { get; set; } = ChallengeKind.Http;

    /// <summary>
    /// Gets or sets the fully qualified domain name the certificate is issued for,
    /// for example <c>media.example.com</c>.
    /// </summary>
    /// <remarks>
    /// Trimmed on the way in, because this value is read raw in places where padding is
    /// silently destructive rather than merely untidy: it is the destination of the
    /// plain-HTTP redirect, where surrounding spaces produce a malformed Location header,
    /// and it gates whether the challenge listener binds at all, where a padded value
    /// reads as an invalid hostname and the listener never starts \u2014 leaving an
    /// issuance that validates happily and then times out with nothing to show for it.
    /// Normalising here fixes every reader at once, including a hand-edited XML file,
    /// which the configuration page's own trimming does not reach.
    /// </remarks>
    public string Domain
    {
        get => _domain;
        set => _domain = value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// Gets or sets the additional names carried on the same certificate, one per entry,
    /// for example <c>home.example.com</c> and <c>sonarr.example.com</c>.
    /// </summary>
    /// <remarks>
    /// A certificate may carry many names, so one issuance can cover every application
    /// served from a single machine. Each name is proven separately — the certificate
    /// authority opens an authorization per name — which is why every one of them must
    /// resolve to this host before a request is made. <see cref="Domain"/> stays the
    /// primary name: it is the certificate's common name and the destination used when
    /// a plain-HTTP request arrives for a name that is not recognised.
    /// </remarks>
    public List<string> ExtraDomains { get; set; } = new();

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
    /// Gets or sets an ACME directory URL that overrides the Let's Encrypt
    /// endpoints entirely. Advanced: for alternative CAs or a local test CA.
    /// Leave empty to follow <see cref="UseStaging"/>.
    /// </summary>
    public string DirectoryUrl { get; set; } = string.Empty;

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
    /// Gets or sets an optional password for the PKCS#12 bundle. Empty by default:
    /// any password would have to be stored in plain text beside the file anyway, so
    /// it adds nothing — the owner-only file mode is the real boundary. Setting one
    /// means also entering it under Networking &#8594; Certificate password.
    /// </summary>
    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where to also write the certificate chain in PEM form. Empty to skip.
    /// </summary>
    /// <remarks>
    /// A PKCS#12 bundle is what .NET's own web server reads, but almost nothing else
    /// does: nginx, Apache, HAProxy and Caddy all want PEM. Writing both lets one
    /// issuance serve this application and a reverse proxy sitting in front of every
    /// other application on the same machine. The file holds only public certificates,
    /// so it is written world-readable \u2014 the proxy usually runs as another user.
    /// </remarks>
    public string PemCertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where to also write the private key in PEM form. Empty to skip.
    /// </summary>
    /// <remarks>
    /// Unencrypted, because that is the only form a reverse proxy can read without a
    /// passphrase prompt at every start. It is created readable only by the account
    /// that owns this process; anything that needs it, such as a proxy running as a
    /// different user, should be given access through group ownership on the containing
    /// directory rather than by widening the file.
    /// </remarks>
    public string PemPrivateKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where to reach Docker in order to list the other applications running
    /// on this machine. Empty means the usual socket path.
    /// </summary>
    /// <remarks>
    /// Either a socket path such as <c>/var/run/docker.sock</c>, or an
    /// <c>http://host:port</c> address for a read-only socket proxy \u2014 which is the
    /// better arrangement, because it can be limited to listing containers and nothing
    /// else. Only ever read from; discovery makes one call, and it is a listing.
    /// </remarks>
    public string DockerEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin opens the public HTTP port
    /// itself, serving the certificate authority's proof request and redirecting every
    /// other request to HTTPS.
    /// </summary>
    /// <remarks>
    /// On by default, and the reason a port-80 forward is safe: the forward reaches this
    /// listener, which has no route to any Jellyfin content, instead of reaching Jellyfin's
    /// own unencrypted interface. Turn it off only when something else already owns port 80
    /// &#8212; a reverse proxy that forwards <c>/.well-known/acme-challenge/</c> through to
    /// Jellyfin, where the plugin's anonymous route answers it instead.
    /// </remarks>
    public bool ServeHttpRedirect { get; set; } = true;

    /// <summary>
    /// Gets or sets the local port that listener binds. Default 80.
    /// </summary>
    /// <remarks>
    /// This is the port on <em>this</em> machine, which need not be the public one: a server
    /// that cannot bind privileged ports should forward the router's port 80 to an
    /// unprivileged port and name it here.
    /// </remarks>
    public int ChallengeListenPort { get; set; } = 80;

    /// <summary>
    /// Gets or sets the public HTTPS port devices connect to, used to build redirects.
    /// Default 443.
    /// </summary>
    public int PublicHttpsPort { get; set; } = 443;

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin's HTTPS responses carry a
    /// <c>Strict-Transport-Security</c> header, telling browsers never to try plain HTTP
    /// for this domain again.
    /// </summary>
    public bool EnableHsts { get; set; } = true;

    /// <summary>
    /// Gets or sets how long, in days, browsers should remember that promise. Default 180.
    /// </summary>
    /// <remarks>
    /// A browser cannot be told to forget early, so this is also how long a mistake would
    /// last. Six months is long enough to be protective and short enough to recover from.
    /// </remarks>
    public int HstsMaxAgeDays { get; set; } = 180;

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

    /// <summary>
    /// Returns an independent copy of this configuration.
    /// </summary>
    /// <remarks>
    /// Issuance runs for minutes, and Jellyfin's <c>UpdateConfiguration</c> replaces the
    /// whole configuration object — so a save from the setup page mid-run would otherwise
    /// leave the pipeline holding a detached instance whose writes go nowhere. The
    /// pipeline therefore works from a copy and merges its results into the live object
    /// at the end. Copying by serializer round-trip rather than by hand means a property
    /// added later cannot be silently left behind (which would also make the staging dry
    /// run prove a different configuration than the production run uses).
    /// </remarks>
    /// <returns>A deep copy carrying every serializable property.</returns>
    public PluginConfiguration Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<PluginConfiguration>(json)
            ?? throw new InvalidOperationException("Could not copy the plugin configuration.");
    }

    /// <summary>
    /// Gets every name the certificate should carry: the primary name first, then any
    /// extras, with blanks and repeats removed. A fresh list each call, so a caller may
    /// hand it straight to the certificate authority client without copying.
    /// </summary>
    /// <remarks>
    /// Order matters. The first name becomes the certificate's common name, and a
    /// duplicate identifier makes the certificate authority reject the whole order, so
    /// both are settled here rather than at each call site.
    /// </remarks>
    public List<string> AllDomains()
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[] { Domain }.Concat(ExtraDomains ?? new List<string>()))
        {
            var name = candidate?.Trim();
            if (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
