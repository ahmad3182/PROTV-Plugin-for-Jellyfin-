using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace PROTV.Plugin;

// One companion plugin PROTV needs for its home page tab / sidebar entry, plus its state.
internal sealed class ProTvRequirement
{
    public ProTvRequirement(string displayName, string assemblyName, Guid pluginId, bool installed, bool loaded)
    {
        DisplayName = displayName;
        AssemblyName = assemblyName;
        PluginId = pluginId;
        Installed = installed;
        Loaded = loaded;
    }

    public string DisplayName { get; }

    public string AssemblyName { get; }

    public Guid PluginId { get; }

    // A plugin folder carrying this plugin id exists under <data>/plugins.
    public bool Installed { get; }

    // The plugin assembly is loaded, meaning Jellyfin accepted and started it.
    public bool Loaded { get; }

    // Installed and running: the plugin can actually draw what PROTV asks it to draw.
    public bool Ready => Installed && Loaded;
}

// Result of looking at the companion plugins.
internal sealed class ProTvCompanionReport
{
    public ProTvCompanionReport(
        IReadOnlyList<ProTvRequirement> requirements,
        bool installationKnown,
        string pluginsDirectory,
        int pluginFolders)
    {
        Requirements = requirements;
        InstallationKnown = installationKnown;
        PluginsDirectory = pluginsDirectory;
        PluginFolders = pluginFolders;
    }

    public IReadOnlyList<ProTvRequirement> Requirements { get; }

    // False when the plugin folder could not be read, which only happens in a broken install.
    public bool InstallationKnown { get; }

    public string PluginsDirectory { get; }

    // How many plugin folders (with a meta.json) were found next to PROTV.
    public int PluginFolders { get; }

    // When the plugin folder cannot be read at all the answer is "installed", so a broken check
    // can never switch PROTV's Live TV integration off by accident.
    public bool AllInstalled => !InstallationKnown || Requirements.All(item => item.Installed);

    public bool AllReady => Requirements.All(item => item.Ready);

    // "Plugin Pages (active), File Transformation (installed but not loaded), Custom Tabs (missing)".
    public string Describe()
        => string.Join(", ", Requirements.Select(item =>
            $"{item.DisplayName} ({(item.Ready ? "active" : item.Installed ? "installed but not loaded" : "missing")})"));

    public string[] Missing()
        => Requirements.Where(item => !item.Installed).Select(item => item.DisplayName).ToArray();

    // Companion plugins that are installed but not running yet (for example because Jellyfin
    // skipped them: their target ABI is newer than the server).
    public string[] NotReady()
        => Requirements
            .Where(item => !item.Ready)
            .Select(item => $"{item.DisplayName} ({(item.Installed ? "installed but not loaded" : "not installed")})")
            .ToArray();
}

// Carries the companion plugin check (which runs during service registration, where there is no
// logger) to the hosted service (where there is one), together with the settings it used.
internal sealed class ProTvStartupReport
{
    public ProTvStartupReport(
        ProTvCompanionReport report,
        bool requireCompanionPlugins,
        bool frontpageTabEnabled,
        bool disabled)
    {
        Report = report;
        RequireCompanionPlugins = requireCompanionPlugins;
        FrontpageTabEnabled = frontpageTabEnabled;
        Disabled = disabled;
    }

    public ProTvCompanionReport Report { get; }

    public bool RequireCompanionPlugins { get; }

    public bool FrontpageTabEnabled { get; }

    // True when PROTV must not start its Live TV integration or publish its pages.
    public bool Disabled { get; }
}

// The PROTV home page tab and the sidebar entry are not drawn by PROTV itself; they are drawn
// by companion plugins:
//   Plugin Pages         - publishes the PROTV dashboard page inside Jellyfin's web client
//   File Transformation  - applies the web client patches Plugin Pages and Custom Tabs need
//   Custom Tabs          - draws the PROTV tab on the Jellyfin home page
// A plugin whose target ABI is newer than the server is installed but never loaded (Jellyfin
// logs "Skipping disabled plugin"), which is why installed and loaded are tracked separately.
internal static class ProTvRequirements
{
    private const string PluginPagesAssembly = "Jellyfin.Plugin.PluginPages";
    private const string FileTransformationAssembly = "Jellyfin.Plugin.FileTransformation";
    private const string CustomTabsAssembly = "Jellyfin.Plugin.CustomTabs";

    private static readonly (string DisplayName, string AssemblyName, string PluginId)[] Companions =
    {
        ("Plugin Pages", PluginPagesAssembly, "5b6550fa-a014-4f4c-8a2c-59a43680ac6d"),
        ("File Transformation", FileTransformationAssembly, "5e87cc92-571a-4d8d-8d98-d2d4147f9f90"),
        ("Custom Tabs", CustomTabsAssembly, "fbacd0b6-fd46-4a05-b0a4-2045d6a135b0")
    };

    private static readonly object CheckGate = new();
    private static ProTvCompanionReport? _cachedReport;
    private static DateTime _cachedReportUtc = DateTime.MinValue;
    private static readonly TimeSpan CheckLifetime = TimeSpan.FromSeconds(45);

    // Reads every plugin folder's manifest from disk, and is asked for on every dashboard menu
    // build and every tab status call, so the answer is kept for a short while.
    public static ProTvCompanionReport Check()
    {
        lock (CheckGate)
        {
            if (_cachedReport is not null && DateTime.UtcNow - _cachedReportUtc < CheckLifetime)
            {
                return _cachedReport;
            }
        }

        var report = CheckUncached();
        lock (CheckGate)
        {
            _cachedReport = report;
            _cachedReportUtc = DateTime.UtcNow;
        }

        return report;
    }

    private static ProTvCompanionReport CheckUncached()
    {
        var installed = FindInstalledPluginIds(out var known, out var pluginFolders);
        var requirements = new List<ProTvRequirement>(Companions.Length);
        foreach (var companion in Companions)
        {
            var id = Guid.Parse(companion.PluginId);
            requirements.Add(new ProTvRequirement(
                companion.DisplayName,
                companion.AssemblyName,
                id,
                installed.Contains(id),
                FindAssembly(companion.AssemblyName) is not null));
        }

        return new ProTvCompanionReport(requirements, known, PluginsDirectory() ?? "(unknown)", pluginFolders);
    }

    // True unless the operator asked PROTV to stay off without its companion plugins.
    public static bool PagesAllowed(PluginConfiguration? configuration)
        => configuration?.RequireCompanionPlugins != true || Check().AllInstalled;

    // The only reliable way to ask "is this plugin actually running" from another plugin.
    public static Assembly? FindAssembly(string assemblyName)
    {
        foreach (var context in AssemblyLoadContext.All)
        {
            IEnumerable<Assembly> assemblies;
            try
            {
                assemblies = context.Assemblies;
            }
            catch (InvalidOperationException)
            {
                // An assembly was loaded while enumerating; try again on the next call.
                continue;
            }

            foreach (var assembly in assemblies)
            {
                try
                {
                    if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
                catch (Exception)
                {
                    // Ignore assemblies that cannot report their name.
                }
            }
        }

        return null;
    }

    // <data>/plugins, where every plugin folder (and PROTV itself) lives. Derived from this
    // assembly's own location because Jellyfin registers plugin services before it creates the
    // plugin instances, so Plugin.Instance is still null at that point.
    public static string? PluginsDirectory()
    {
        try
        {
            var folder = Path.GetDirectoryName(typeof(ProTvRequirements).Assembly.Location);
            var parent = string.IsNullOrEmpty(folder) ? null : Directory.GetParent(folder);
            if (parent is not null && Directory.Exists(parent.FullName))
            {
                return parent.FullName;
            }
        }
        catch (Exception)
        {
            // Fall through to the application paths below.
        }

        var configured = Plugin.Instance?.PluginPaths?.PluginsPath;
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static HashSet<Guid> FindInstalledPluginIds(out bool known, out int pluginFolders)
    {
        var result = new HashSet<Guid>();
        pluginFolders = 0;
        known = false;

        var pluginsDirectory = PluginsDirectory();
        if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
        {
            return result;
        }

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(pluginsDirectory);
        }
        catch (Exception)
        {
            return result;
        }

        // Read each plugin folder on its own so one unreadable folder cannot hide the others.
        foreach (var folder in folders)
        {
            try
            {
                var manifestPath = Path.Combine(folder, "meta.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                pluginFolders++;
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (document.RootElement.TryGetProperty("guid", out var guid) && Guid.TryParse(guid.GetString(), out var id))
                {
                    result.Add(id);
                }
            }
            catch (Exception)
            {
                // A malformed manifest in another plugin folder must not break PROTV.
            }
        }

        // PROTV lives in this folder too, so finding nothing at all means the scan failed.
        known = pluginFolders > 0;
        return result;
    }
}
