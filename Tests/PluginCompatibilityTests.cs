using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;

namespace RIoT2.Net.Node.Tests;

[TestClass]
public class PluginCompatibilityTests
{
    [TestMethod]
    public void PluginLocalDependenciesCannotForkHostContracts()
    {
        var context = new PluginLoadContext(typeof(PluginCompatibilityTests).Assembly.Location);
        foreach (var assembly in new[] { typeof(IDevice).Assembly, typeof(IServiceCollection).Assembly, typeof(ILogger).Assembly })
            Assert.AreSame(assembly, context.LoadFromAssemblyName(assembly.GetName()));
    }

    [TestMethod]
    public void LegacyPluginLoadedInSeparateContextStillImplementsHostDeviceContract()
    {
        var context = new PluginLoadContext(typeof(PluginCompatibilityTests).Assembly.Location);
        var plugin = context.LoadFromAssemblyPath(typeof(PluginCompatibilityTests).Assembly.Location);
        var type = plugin.GetType(typeof(LegacyFixture).FullName, throwOnError: true);
        Assert.IsTrue(typeof(IDevice).IsAssignableFrom(type));
        var device = (IDevice)Activator.CreateInstance(type);
        device.Initialize(new DeviceConfiguration { Id = "legacy", CommandTemplates = [], ReportTemplates = [] });
        device.Start();
        device.Stop();
        Assert.AreEqual(RIoT2.Core.DeviceState.Stopped, device.State);
    }

    [TestMethod]
    public void PluginBuiltForPreviousTargetFrameworkLoadsIntoNode()
    {
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "LegacyPlugin", "RIoT2.Net.Node.LegacyPlugin.dll");
        Assert.IsTrue(File.Exists(pluginPath), $"Fixture plugin missing: {pluginPath}");

        // Same steps as Program.loadPlugins.
        var context = new PluginLoadContext(pluginPath);
        var plugin = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(pluginPath));
        var framework = plugin.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        Assert.AreEqual(".NETCoreApp,Version=v9.0", framework, "The fixture must stay one target framework behind the node.");
        Assert.AreNotEqual(typeof(PluginCompatibilityTests).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName, framework);

        var pluginType = plugin.GetTypes().Single(t => t.GetInterface(nameof(IDevicePlugin)) != null);
        var instance = Activator.CreateInstance(pluginType) as IDevicePlugin;
        Assert.IsNotNull(instance, "The plugin must implement the host's IDevicePlugin.");

        var services = new ServiceCollection();
        services.AddLogging();
        instance.Initialize(services);
        using var provider = services.BuildServiceProvider();
        var device = provider.GetServices<IDevice>().Single();
        Assert.AreSame(context, AssemblyLoadContext.GetLoadContext(device.GetType().Assembly));

        device.Initialize(new DeviceConfiguration { Id = "legacy-tfm", CommandTemplates = [], ReportTemplates = [] });
        device.Start();
        device.Stop();
        Assert.AreEqual(RIoT2.Core.DeviceState.Stopped, device.State);
    }

    public sealed class LegacyFixture : DeviceBase, IDevice
    {
        public LegacyFixture() : base(NullLogger.Instance) { }
        public override void ConfigureDevice() { }
        public override void StartDevice() { }
        public override void StopDevice() { }
    }
}
