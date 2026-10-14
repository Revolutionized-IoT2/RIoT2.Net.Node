using System.Reflection;
using System.Runtime.Loader;
using RIoT2.Core.Plugins;

namespace RIoT2.Net.Node
{
    class PluginLoadContext : AssemblyLoadContext
    {
        private AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            // Shared contracts always come from the node, whatever version the plugin was built against.
            if (PluginHostContract.IsShared(assemblyName.Name))
            {
                return Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == assemblyName.Name)
                    ?? Default.LoadFromAssemblyName(new AssemblyName(assemblyName.Name));
            }

            string assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}