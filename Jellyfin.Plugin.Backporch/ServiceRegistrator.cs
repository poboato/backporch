using Jellyfin.Plugin.Backporch.Acme;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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
        serviceCollection.AddSingleton<AcmeService>();
    }
}
