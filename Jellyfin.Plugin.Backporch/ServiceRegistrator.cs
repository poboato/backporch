using Jellyfin.Plugin.Backporch.Acme;
using Jellyfin.Plugin.Backporch.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Backporch;

/// <summary>
/// Registers the plugin's services with Jellyfin's container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<IssuanceState>();
        serviceCollection.AddSingleton<HttpChallengeStore>();
        serviceCollection.AddSingleton<AcmeService>();

        // Registered twice on purpose: once so the host starts and stops it, once so the
        // setup page can ask the same instance whether its port is actually bound.
        serviceCollection.AddSingleton<AcmeHttpServer>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<AcmeHttpServer>());

        // Hardens Jellyfin's own HTTPS responses. A startup filter is the only seam a plugin
        // has into the request pipeline; it is a no-op unless HSTS is enabled and the
        // request arrived over TLS.
        serviceCollection.AddTransient<IStartupFilter, HstsStartupFilter>();
    }
}
