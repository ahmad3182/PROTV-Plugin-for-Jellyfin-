using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Xml.Serialization;

namespace PROTV.Plugin;

public sealed class ProTvServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // With RequireCompanionPlugins on, PROTV only starts when the plugins that draw its
        // sidebar entry (Plugin Pages), its Jellyfin home page tab (Custom Tabs) and the web
        // client patches both of them need (File Transformation) are installed. The hosted
        // service explains the decision in the log, and the dashboard page shows it too.
        var configuration = ResolveStartupConfiguration();
        var report = ProTvRequirements.Check();
        var requireCompanionPlugins = configuration?.RequireCompanionPlugins == true;
        var disabled = requireCompanionPlugins && !report.AllInstalled;

        // Registered either way so the reason for a disabled PROTV is always in the log.
        serviceCollection.AddSingleton(new ProTvStartupReport(
            report,
            requireCompanionPlugins,
            configuration?.FrontpageTabEnabled ?? true,
            disabled));
        serviceCollection.AddHostedService<ProTvLifecycleService>();

        if (disabled)
        {
            return;
        }

        serviceCollection.AddSingleton<ProTvApiClient>(services => ProTvSharedClient.GetOrCreate(
            null,
            services.GetRequiredService<ILogger<ProTvApiClient>>(),
            () => ResolveConfiguration(services)));
        serviceCollection.AddSingleton<ITunerHost, ProTvTunerHost>();
        serviceCollection.AddSingleton<IListingsProvider, ProTvListingsProvider>();
    }

    // Jellyfin registers plugin services before it creates the plugin instances, so on a fresh
    // start Plugin.Instance is still null here. The saved settings are read from Jellyfin's plugin
    // configuration folder in that case (the same file ResolveConfiguration falls back to).
    private static PluginConfiguration? ResolveStartupConfiguration()
    {
        if (Plugin.Instance?.Configuration is { } live)
        {
            return live;
        }

        try
        {
            var pluginsDirectory = ProTvRequirements.PluginsDirectory();
            if (pluginsDirectory is null)
            {
                return null;
            }

            var configurationPath = Path.Combine(pluginsDirectory, "configurations", "PROTV.Plugin.xml");
            if (!File.Exists(configurationPath))
            {
                return null;
            }

            using var stream = File.OpenRead(configurationPath);
            return new XmlSerializer(typeof(PluginConfiguration)).Deserialize(stream) as PluginConfiguration;
        }
        catch (Exception)
        {
            // An unreadable settings file must not switch Live TV off: no settings means no gate.
            return null;
        }
    }

    private static PluginConfiguration ResolveConfiguration(IServiceProvider services)
    {
        var pluginConfiguration = Plugin.Instance?.Configuration;
        if (HasCredentials(pluginConfiguration))
        {
            return pluginConfiguration!;
        }

        var registeredPlugin = services.GetRequiredService<IPluginManager>()
            .GetPlugin(Guid.Parse("4a0a66d7-04ae-4c4d-b9af-8cb6a1c44f86"))
            ?.Instance as Plugin;
        if (HasCredentials(registeredPlugin?.Configuration))
        {
            return registeredPlugin!.Configuration;
        }

        var applicationPaths = services.GetRequiredService<IApplicationPaths>();
        // <config>/plugins/configurations/PROTV.Plugin.xml, the same file Jellyfin saves to.
        var configurationPath = Path.Combine(
            applicationPaths.PluginConfigurationsPath,
            "PROTV.Plugin.xml");
        if (File.Exists(configurationPath))
        {
            using var stream = File.OpenRead(configurationPath);
            var configuration = new XmlSerializer(typeof(PluginConfiguration))
                .Deserialize(stream) as PluginConfiguration;
            if (configuration is not null)
            {
                return configuration;
            }
        }

        return pluginConfiguration ?? new PluginConfiguration();
    }

    private static bool HasCredentials(PluginConfiguration? configuration)
    {
        return configuration is not null
            && !string.IsNullOrWhiteSpace(configuration.Username)
            && !string.IsNullOrEmpty(configuration.PasswordValue);
    }
}
