using System.Reflection;
using System.Runtime.Loader;
using RIoT2.Core.Interfaces;

namespace RIoT2.Net.Node
{
    class PluginLoadContext : AssemblyLoadContext
    {
        private AssemblyDependencyResolver _resolver;
        private static readonly Assembly[] SharedContracts =
        [
            typeof(IDevice).Assembly,
            typeof(IServiceCollection).Assembly,
            typeof(ILogger).Assembly
        ];

        public PluginLoadContext(string pluginPath)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            var shared = SharedContracts.FirstOrDefault(assembly => assembly.GetName().Name == assemblyName.Name);
            if (shared != null)
                return shared;
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