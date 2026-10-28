using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Timetable.Plugins;

namespace NcuCourseTable.Desktop.Services;

public sealed class LoadedPlugin
{
    public required ITimetablePlugin Instance { get; init; }
    public required string AssemblyPath { get; init; }
}

public sealed class PluginLoadError
{
    public required string Path { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// 从程序目录 plugins/&lt;插件&gt;/ 发现并载入数据源插件。
/// 插件被载入独立 AssemblyLoadContext，契约程序集始终复用宿主的默认上下文。
/// </summary>
public sealed class PluginManager
{
    private readonly List<LoadedPlugin> _plugins = new();
    private readonly List<PluginLoadError> _errors = new();

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public IReadOnlyList<PluginLoadError> Errors => _errors;

    public void ReportError(string path, string message) =>
        _errors.Add(new PluginLoadError { Path = path, Message = message });

    public string PluginDir { get; private set; } = "";

    public void LoadAll(string? dir = null)
    {
        _plugins.Clear();
        _errors.Clear();
        PluginDir = ResolvePluginDir(dir);
        if (!Directory.Exists(PluginDir))
        {
            return;
        }

        foreach (string dll in Directory.EnumerateFiles(PluginDir, "*.dll", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(dll);
            if (name.StartsWith("Timetable.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            TryLoad(dll);
        }
    }

    private void TryLoad(string dll)
    {
        try
        {
            var context = new PluginLoadContext(dll);
            Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            foreach (Type type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }
                if (!typeof(ITimetablePlugin).IsAssignableFrom(type))
                {
                    continue;
                }
                var plugin = (ITimetablePlugin)Activator.CreateInstance(type)!;
                if (_plugins.Any(p => string.Equals(p.Instance.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    _errors.Add(new PluginLoadError
                    {
                        Path = dll,
                        Message = $"插件 Id “{plugin.Id}” 重复，已跳过。",
                    });
                    continue;
                }
                _plugins.Add(new LoadedPlugin { Instance = plugin, AssemblyPath = dll });
            }
        }
        catch (Exception ex)
        {
            _errors.Add(new PluginLoadError { Path = dll, Message = ex.Message });
        }
    }

    private static string ResolvePluginDir(string? dir)
    {
        if (!string.IsNullOrWhiteSpace(dir)) return Path.GetFullPath(dir!);
        string? env = Environment.GetEnvironmentVariable("TIMETABLE_PLUGINS_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return Path.Combine(AppContext.BaseDirectory, "plugins");
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base("TimetablePlugin:" + pluginPath, isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string contracts = typeof(ITimetablePlugin).Assembly.GetName().Name!;
            if (string.Equals(assemblyName.Name, contracts, StringComparison.OrdinalIgnoreCase))
            {
                return null; // 契约类型必须与宿主共用同一份程序集
            }
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
