using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NcuCourseTable.Desktop.Services;
using NcuCourseTable.Desktop.Views;

namespace NcuCourseTable.Desktop;

/// <summary>插件宿主入口：插件列表、目录与插件窗口。</summary>
public partial class App
{
    internal IReadOnlyList<LoadedPlugin> Plugins =>
        _pluginManager?.Plugins ?? Array.Empty<LoadedPlugin>();

    internal IReadOnlyList<PluginLoadError> PluginErrors =>
        _pluginManager?.Errors ?? Array.Empty<PluginLoadError>();

    internal string PluginDir => _pluginManager?.PluginDir ?? "";

    internal void OpenPluginsDirectory()
    {
        if (_pluginManager is null) return;
        Directory.CreateDirectory(_pluginManager.PluginDir);
        Services.Openers.OpenDataDir(_pluginManager.PluginDir);
    }

    internal void ShowPluginManager()
    {
        if (_pluginsWindow is { IsLoaded: true })
        {
            BringToFront(_pluginsWindow);
            return;
        }
        _pluginsWindow = new PluginManagerWindow();
        _pluginsWindow.Closed += (_, _) => _pluginsWindow = null;
        _pluginsWindow.Show();
        BringToFront(_pluginsWindow);
    }
}
