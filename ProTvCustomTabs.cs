using System.Collections;
using System.Reflection;
using MediaBrowser.Common.Plugins;

namespace PROTV.Plugin;

// Keeps exactly one PROTV entry in the Custom Tabs plugin configuration. Custom Tabs draws one
// tab per entry on the Jellyfin home page, so PROTV owns one entry and refreshes it on start-up.
// Everything goes through reflection because PROTV must work with no compile-time or load-time
// dependency on the optional Custom Tabs plugin.
internal static class ProTvCustomTabs
{
    private const string CustomTabsAssembly = "Jellyfin.Plugin.CustomTabs";
    private const string CustomTabsPluginType = "Jellyfin.Plugin.CustomTabs.CustomTabsPlugin";

    // Lets PROTV recognise its own tab later without depending on the tab title.
    public const string Marker = "data-protv-tab";

    // Adds the tab, or refreshes it when the title or the renderer changed.
    public static bool Upsert(string title, string contentHtml) => Apply(true, title, contentHtml);

    // Drops the tab (the operator turned the home page tab off, or the plugin was uninstalled).
    public static bool Remove() => Apply(false, null, null);

    // True when Custom Tabs is loaded and its settings already contain the PROTV tab.
    public static bool IsRegistered()
    {
        var configuration = FindPlugin()?.Configuration;
        var tabsProperty = configuration?.GetType().GetProperty("Tabs");
        var contentProperty = ElementType(tabsProperty)?.GetProperty("ContentHtml");
        if (configuration is null || tabsProperty is null || contentProperty is null)
        {
            return false;
        }

        return ReadTabs(tabsProperty, configuration).Any(tab => IsOurs(contentProperty, tab));
    }

    // The HTML Custom Tabs stores for the PROTV tab. It only contains the container and a tiny
    // bootstrap; the actual renderer is served by PROTV's own controller as /PROTV/tab.js.
    public static string BuildContentHtml()
    {
        var title = string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.FrontpageTabTitle)
            ? "PROTV"
            : Plugin.Instance!.Configuration.FrontpageTabTitle.Trim();
        // This string becomes part of the home page's HTML. Escape it, or a title containing a tag
        // would be injected into every user's page.
        var safeTitle = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(title);
        return $$"""
            <div id="protvTabRoot" class="protvTabRoot" {{Marker}}="true">
              <div class="protvTabLoading">Loading {{safeTitle}} channels...</div>
            </div>
            <script type="text/javascript">
            (function () {
              var load = function () {
                if (window.ProTvTab && window.ProTvTab.render) { window.ProTvTab.render(); return; }
                if (window.__protvTabScriptLoading) { return; }
                window.__protvTabScriptLoading = true;
                var script = document.createElement('script');
                script.src = (window.ApiClient && ApiClient.getUrl) ? ApiClient.getUrl('PROTV/tab.js') : '/PROTV/tab.js';
                script.onload = function () { window.__protvTabScriptLoading = false; if (window.ProTvTab) { window.ProTvTab.render(); } };
                script.onerror = function () { window.__protvTabScriptLoading = false; };
                document.head.appendChild(script);
              };
              load();
            })();
            </script>
            """;
    }

    private static bool Apply(bool add, string? title, string? contentHtml)
    {
        var plugin = FindPlugin();
        var configuration = plugin?.Configuration;
        var tabsProperty = configuration?.GetType().GetProperty("Tabs");
        var elementType = ElementType(tabsProperty);
        var titleProperty = elementType?.GetProperty("Title");
        var contentProperty = elementType?.GetProperty("ContentHtml");
        if (plugin is null || configuration is null || tabsProperty is null || elementType is null
            || titleProperty is null || contentProperty is null)
        {
            return false;
        }

        var tabs = new List<object>();
        object? current = null;
        foreach (var tab in ReadTabs(tabsProperty, configuration))
        {
            if (IsOurs(contentProperty, tab))
            {
                current = tab;
                continue;
            }

            tabs.Add(tab);
        }

        if (add)
        {
            if (current is not null
                && string.Equals(titleProperty.GetValue(current) as string, title, StringComparison.Ordinal)
                && string.Equals(contentProperty.GetValue(current) as string, contentHtml, StringComparison.Ordinal))
            {
                // Already in place: never rewrite another plugin's settings on every start-up.
                return true;
            }

            var tab = Activator.CreateInstance(elementType);
            if (tab is null)
            {
                return false;
            }

            titleProperty.SetValue(tab, title);
            contentProperty.SetValue(tab, contentHtml);
            tabs.Add(tab);
        }
        else if (current is null)
        {
            return true;
        }

        var array = Array.CreateInstance(elementType, tabs.Count);
        for (var index = 0; index < tabs.Count; index++)
        {
            array.SetValue(tabs[index], index);
        }

        tabsProperty.SetValue(
            configuration,
            tabsProperty.PropertyType.IsArray ? array : Activator.CreateInstance(tabsProperty.PropertyType, array));
        plugin.UpdateConfiguration(configuration);
        return true;
    }

    private static IHasPluginConfiguration? FindPlugin()
    {
        var instance = ProTvRequirements.FindAssembly(CustomTabsAssembly)?
            .GetType(CustomTabsPluginType, throwOnError: false)?
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null);
        return instance as IHasPluginConfiguration;
    }

    private static Type? ElementType(PropertyInfo? tabsProperty)
    {
        if (tabsProperty is null)
        {
            return null;
        }

        return tabsProperty.PropertyType.IsArray
            ? tabsProperty.PropertyType.GetElementType()
            : tabsProperty.PropertyType.GetGenericArguments().FirstOrDefault();
    }

    private static IEnumerable<object> ReadTabs(PropertyInfo tabsProperty, object configuration)
        => tabsProperty.GetValue(configuration) is IEnumerable tabs ? tabs.Cast<object>() : Array.Empty<object>();

    private static bool IsOurs(PropertyInfo contentProperty, object tab)
        => contentProperty.GetValue(tab) is string html && html.Contains(Marker, StringComparison.Ordinal);
}
