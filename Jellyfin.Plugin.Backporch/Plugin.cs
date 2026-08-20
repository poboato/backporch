using Jellyfin.Plugin.Backporch.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Backporch;

/// <summary>
/// Obtains and renews a publicly trusted TLS certificate for a self-hosted Jellyfin
/// server using ACME with the DNS-01 challenge.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Serializer used to persist configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DefaultCertificatePath = Path.Combine(applicationPaths.DataPath, "backporch", "certificate.pfx");
    }

    /// <summary>
    /// Gets the singleton instance, used by services that Jellyfin does not inject into.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the certificate path used when the user has not chosen one — inside
    /// Jellyfin's own data directory, so it survives container recreation without
    /// any extra volume mapping.
    /// </summary>
    public string DefaultCertificatePath { get; }

    /// <inheritdoc />
    public override string Name => "Backporch";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("ec59d7bc-0644-4bfe-a924-b6ec7b88c1fb");

    /// <inheritdoc />
    public override string Description =>
        "Automatically obtains and renews a Let's Encrypt certificate for your own domain, "
        + "so remote access is encrypted with a certificate every device already trusts.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
