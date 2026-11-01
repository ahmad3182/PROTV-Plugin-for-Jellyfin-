using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PROTV.Plugin;

// Gives the plugin access to server services (the Plugin class can only take paths and a
// serializer) so it can clean up Live TV when the plugin is uninstalled.
internal sealed class ProTvLifecycleService : IHostedService
{
    private const string ProTvType = "PROTV";
    private const string SidebarPageId = "PROTV.Dashboard";
    private const string DashboardUrl = "/web/configurationpage?name=protvdashboard";
    private static ProTvLifecycleService? _current;
    private CancellationTokenSource? _registrationCancellation;

    private readonly IServerConfigurationManager _configurationManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<ProTvLifecycleService> _logger;
    private readonly ProTvStartupReport _report;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ISessionManager _sessionManager;

    public ProTvLifecycleService(
        IServerConfigurationManager configurationManager,
        ITaskManager taskManager,
        ProTvStartupReport report,
        ILibraryManager libraryManager,
        IApplicationPaths applicationPaths,
        IServerApplicationHost applicationHost,
        ISessionManager sessionManager,
        ILogger<ProTvLifecycleService> logger)
    {
        _configurationManager = configurationManager;
        _taskManager = taskManager;
        _report = report;
        _libraryManager = libraryManager;
        _applicationPaths = applicationPaths;
        _applicationHost = applicationHost;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _current = this;

        // Lets the home page tab resolve a PROTV channel to the Jellyfin Live TV channel item, so
        // tapping a channel in the tab plays the real channel.
        ProTvLiveTvItems.Attach(_libraryManager);

        // The VOD side needs the same library manager to tell whether a film or an episode has been
        // synced yet, plus where to keep its library and how to reach this server.
        ProTvVodItems.Attach(_libraryManager, _applicationPaths.DataPath);
        ProTvVodLibrary.Attach(_libraryManager, _applicationHost, _sessionManager, _applicationPaths.DataPath, _logger);

        // The companion plugins are what draw PROTV's sidebar entry and Jellyfin home page tab,
        // so their state is always reported: it is the first thing to check when the tab is
        // missing from the Jellyfin home page.
        var report = _report.Report;
        _logger.LogInformation(
            "PROTV companion plugins in {PluginsDirectory} ({PluginFolders} plugin folders): {CompanionPlugins}.",
            report.PluginsDirectory,
            report.PluginFolders,
            report.Describe());

        var notReady = report.NotReady();
        if (notReady.Length > 0)
        {
            _logger.LogWarning(
                "The PROTV sidebar entry and home page tab need Plugin Pages, File Transformation and Custom Tabs. " +
                "Not running yet: {NotReady}.",
                string.Join(", ", notReady));
        }

        if (_report.Disabled)
        {
            _logger.LogError(
                "PROTV is disabled and its Live TV tuner was not registered because RequireCompanionPlugins is on " +
                "and these plugins are not installed: {Missing}. Install Plugin Pages, File Transformation and " +
                "Custom Tabs (or turn RequireCompanionPlugins off in the PROTV settings), then restart Jellyfin.",
                string.Join(", ", report.Missing()));
            return Task.CompletedTask;
        }

        _registrationCancellation = new CancellationTokenSource();
        var token = _registrationCancellation.Token;
        _ = Task.Run(() => RegisterCompanionUiAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registrationCancellation?.Cancel();
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }

        return Task.CompletedTask;
    }

    // Adds the PROTV dashboard to the sidebar (Plugin Pages) and the PROTV tab to the Jellyfin
    // home page (Custom Tabs). Both companion plugins may finish starting after this service, so
    // retry for a few minutes before giving up.
    private async Task RegisterCompanionUiAsync(CancellationToken cancellationToken)
    {
        var pageRegistered = false;
        var tabRegistered = false;

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            if (!pageRegistered)
            {
                try
                {
                    pageRegistered = TryCallPluginPages("RegisterPage", BuildSidebarPayload());
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Plugin Pages registration attempt {Attempt} failed.", attempt);
                }
            }

            if (!tabRegistered)
            {
                try
                {
                    tabRegistered = TryRegisterFrontpageTab();
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "Custom Tabs registration attempt {Attempt} failed.", attempt);
                }
            }

            if (pageRegistered && tabRegistered)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (pageRegistered)
        {
            _logger.LogInformation("PROTV dashboard registered with Plugin Pages.");
        }
        else
        {
            _logger.LogWarning(
                "Plugin Pages is not loaded, so the PROTV sidebar entry was not added. " +
                "The dashboard is still in the Jellyfin dashboard menu.");
        }

        if (tabRegistered)
        {
            _logger.LogInformation(
                _report.FrontpageTabEnabled
                    ? "PROTV tab registered with Custom Tabs on the Jellyfin home page."
                    : "The PROTV home page tab is switched off in the PROTV settings.");
        }
        else
        {
            _logger.LogWarning(
                "Custom Tabs is not loaded, so the PROTV tab was not added to the Jellyfin home page.");
        }
    }

    // Writes (or refreshes, or removes) the PROTV entry in the Custom Tabs plugin settings.
    private bool TryRegisterFrontpageTab()
    {
        if (!_report.FrontpageTabEnabled)
        {
            // Switched off: drop a tab an earlier start may have added. Nothing to do when
            // Custom Tabs is not running at all.
            return ProTvCustomTabs.Remove()
                || ProTvRequirements.FindAssembly("Jellyfin.Plugin.CustomTabs") is null;
        }

        var title = string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.FrontpageTabTitle)
            ? "PROTV"
            : Plugin.Instance!.Configuration.FrontpageTabTitle.Trim();
        return ProTvCustomTabs.Upsert(title, ProTvCustomTabs.BuildContentHtml());
    }

    private static string BuildSidebarPayload()
    {
        var configuration = Plugin.Instance?.Configuration;
        var label = string.IsNullOrWhiteSpace(configuration?.SidebarLabel) ? "PROTV" : configuration!.SidebarLabel.Trim();
        var icon = string.IsNullOrWhiteSpace(configuration?.SidebarIcon) ? "live_tv" : configuration!.SidebarIcon.Trim();
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = SidebarPageId,
            ["url"] = DashboardUrl,
            ["displayText"] = label,
            ["icon"] = icon
        });
    }

    // Calls a static method on Plugin Pages' PluginInterface by reflection, so PROTV has no
    // compile-time or load-time dependency on it. Returns false when Plugin Pages is not loaded.
    // The argument is JSON for RegisterPage (parsed into the JObject type Plugin Pages expects)
    // or the page id for RemovePage.
    private static bool TryCallPluginPages(string methodName, string argument)
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .FirstOrDefault(item => string.Equals(item.GetName().Name, "Jellyfin.Plugin.PluginPages", StringComparison.OrdinalIgnoreCase));
        var method = assembly?
            .GetType("Jellyfin.Plugin.PluginPages.PluginInterface")?
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            return false;
        }

        var parameterType = method.GetParameters()[0].ParameterType;
        object? value = argument;
        if (parameterType != typeof(string))
        {
            var parse = parameterType.GetMethod("Parse", new[] { typeof(string) });
            if (parse is null)
            {
                return false;
            }

            value = parse.Invoke(null, new object[] { argument });
        }

        method.Invoke(null, new[] { value });
        return true;
    }

    public static void CleanUpAfterUninstall()
    {
        _current?.RemoveLiveTvEntries();
    }

    private void RemoveLiveTvEntries()
    {
        try
        {
            TryCallPluginPages("RemovePage", SidebarPageId);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not remove the PROTV sidebar entry.");
        }

        try
        {
            ProTvCustomTabs.Remove();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not remove the PROTV home page tab.");
        }

        try
        {
            // The PROTV libraries are the plugin's own; take them out of Jellyfin again (the files
            // it downloaded stay on disk, under <data>/protv-vod).
            foreach (var name in new[] { ProTvVodPaths.LibraryName, ProTvVodPaths.SeriesLibraryName })
            {
                var hasLibrary = _libraryManager.GetVirtualFolders()
                    .Any(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
                if (hasLibrary)
                {
                    _libraryManager.RemoveVirtualFolder(name, true).GetAwaiter().GetResult();
                    _logger.LogInformation("The {LibraryName} library was removed from Jellyfin.", name);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not remove the PROTV VOD libraries. Remove them from the Jellyfin libraries page.");
        }

        try
        {
            // Non-generic overload: returns the "livetv" options object (LiveTvOptions) as object.
            var options = _configurationManager.GetConfiguration("livetv");
            var removedTuners = RemoveProTvEntries(options, "TunerHosts");
            var removedProviders = RemoveProTvEntries(options, "ListingProviders");
            if (removedTuners || removedProviders)
            {
                _configurationManager.SaveConfiguration("livetv", options);
                _logger.LogInformation("PROTV tuner and guide entries were removed from Live TV settings.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not remove PROTV entries from Live TV settings.");
        }

        QueueGuideRefresh();
    }

    // The collections are filtered by reflection so this works whether Jellyfin exposes them as
    // arrays or lists. Entries are matched on their Type property.
    private static bool RemoveProTvEntries(object options, string propertyName)
    {
        var property = options.GetType().GetProperty(propertyName);
        if (property is null || property.GetValue(options) is not IEnumerable items)
        {
            return false;
        }

        var elementType = property.PropertyType.IsArray
            ? property.PropertyType.GetElementType()!
            : property.PropertyType.GetGenericArguments().FirstOrDefault() ?? typeof(object);
        var typeProperty = elementType.GetProperty("Type");

        var keep = new List<object>();
        var removed = false;
        foreach (var item in items)
        {
            var itemType = typeProperty?.GetValue(item) as string;
            if (string.Equals(itemType, ProTvType, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
            }
            else
            {
                keep.Add(item);
            }
        }

        if (!removed)
        {
            return false;
        }

        var array = Array.CreateInstance(elementType, keep.Count);
        for (var index = 0; index < keep.Count; index++)
        {
            array.SetValue(keep[index], index);
        }

        property.SetValue(
            options,
            property.PropertyType.IsArray ? array : Activator.CreateInstance(property.PropertyType, array));
        return true;
    }

    // Jellyfin drops channels that no tuner provides any more when the guide is refreshed.
    private void QueueGuideRefresh()
    {
        try
        {
            var worker = _taskManager.ScheduledTasks.FirstOrDefault(item =>
                string.Equals(item.ScheduledTask.Key, "RefreshGuide", StringComparison.OrdinalIgnoreCase));
            if (worker is null)
            {
                _logger.LogWarning("Refresh Guide task not found. Run Refresh Guide manually to remove the PROTV channels.");
                return;
            }

            // Queue() takes the task worker in some Jellyfin versions and the task itself in others.
            dynamic taskManager = _taskManager;
            var options = new TaskOptions();
            try
            {
                taskManager.Queue(worker, options);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                taskManager.Queue(worker.ScheduledTask, options);
            }

            _logger.LogInformation("Queued a guide refresh to remove the PROTV channels.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not queue the guide refresh. Run Refresh Guide manually to remove the PROTV channels.");
        }
    }
}
