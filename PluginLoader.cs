using System.Reflection;
using RIoT2.Core.Interfaces;

namespace RIoT2.Net.Node
{
    /// <summary>What <see cref="PluginLoader.Load"/> loaded from the plugins folder.</summary>
    internal sealed class PluginLoadResult
    {
        /// <summary>Plugins (<see cref="IDevicePlugin"/> classes) initialized.</summary>
        public int Loaded { get; init; }

        /// <summary>One line per file or plugin that failed.</summary>
        public IReadOnlyList<string> Errors { get; init; } = [];

        /// <summary>At least one plugin loaded and nothing failed: the check a newly installed package must pass.</summary>
        public bool Succeeded => Loaded > 0 && Errors.Count == 0;

        public string? Error => Errors.Count == 0 ? null : string.Join(" ", Errors);
    }

    /// <summary>
    /// Loads every <c>*.dll</c> in the plugins folder into its own <see cref="PluginLoadContext"/>, adds each
    /// assembly that has an <see cref="IDevicePlugin"/> as an MVC application part, and lets the plugin register
    /// its services. A file that fails is reported and the others still load.
    /// </summary>
    internal static class PluginLoader
    {
        /// <summary>
        /// The node's exit code after it rolled back a plugin package that didn't load (EX_TEMPFAIL): the container's
        /// restart policy starts it again with the previous plugins.
        /// </summary>
        public const int RollbackExitCode = 75;

        public static PluginLoadResult Load(string pluginsFolder, IServiceCollection services, Action<Assembly> addApplicationPart, ILogger logger)
        {
            var errors = new List<string>();
            var loaded = 0;
            var folder = new DirectoryInfo(pluginsFolder);
            if (!folder.Exists)
                return new PluginLoadResult();

            foreach (var file in folder.GetFiles("*.dll").OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                Type? pluginClass;
                Assembly assembly;
                try
                {
                    var context = new PluginLoadContext(file.FullName);
                    assembly = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(file.FullName));
                    // Abstract types such as Core's PluginBase implement the interface too, but aren't plugins.
                    pluginClass = assembly.GetTypes().SingleOrDefault(t => !t.IsAbstract && t.GetInterface(nameof(IDevicePlugin)) != null);
                }
                catch (Exception x)
                {
                    logger.LogError(x, "Could not load {File}: {Message}", file.Name, x.GetBaseException().Message);
                    errors.Add($"{file.Name}: {x.GetBaseException().Message}");
                    continue;
                }
                if (pluginClass == null)
                    continue;

                try
                {
                    addApplicationPart(assembly);
                    var plugin = (IDevicePlugin)Activator.CreateInstance(pluginClass)!;
                    plugin.Initialize(services);
                    loaded++;
                }
                catch (Exception x)
                {
                    logger.LogError(x, "Failed loading plugin {Plugin}: {Message}", pluginClass.FullName, x.GetBaseException().Message);
                    errors.Add($"{pluginClass.FullName}: {x.GetBaseException().Message}");
                }
            }
            return new PluginLoadResult { Loaded = loaded, Errors = errors };
        }
    }
}
