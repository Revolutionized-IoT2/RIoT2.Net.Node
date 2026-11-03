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
        var shared = new[]
        {
            typeof(IDevice).Assembly, // RIoT2.Core.Devices
            typeof(Report).Assembly, // RIoT2.Core.Contracts
            typeof(RIoT2.Core.Utils.MqttClient).Assembly,
            typeof(RIoT2.Core.Utils.Web).Assembly,
            typeof(RIoT2.Core.Services.NodeMqttService).Assembly,
            typeof(IServiceCollection).Assembly,
            typeof(ILogger).Assembly,
        };
        CollectionAssert.AreEquivalent(
            new[] { "RIoT2.Core.Contracts", "RIoT2.Core.Devices", "RIoT2.Core.Http", "RIoT2.Core.Mqtt", "RIoT2.Core.Node" },
            shared.Take(5).Select(a => a.GetName().Name).ToArray());
        foreach (var assembly in shared)
            Assert.AreSame(assembly, context.LoadFromAssemblyName(assembly.GetName()), assembly.GetName().Name);
    }

    [TestMethod]
    public void LegacyPluginLoadedInSeparateContextStillImplementsHostDeviceContract()
    {
        var context = new PluginLoadContext(typeof(PluginCompatibilityTests).Assembly.Location);
        var plugin = context.LoadFromAssemblyPath(typeof(PluginCompatibilityTests).Assembly.Location);
        var type = plugin.GetType(typeof(LegacyFixture).FullName!, throwOnError: true)!;
        Assert.IsTrue(typeof(IDevice).IsAssignableFrom(type));
        var device = (IDevice)Activator.CreateInstance(type)!;
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
        // Built against RIoT2.Core.Devices of the node's Core major; no RIoT2.Core facade since Core 2.0.0.
        var core = plugin.GetReferencedAssemblies().Single(a => a.Name == "RIoT2.Core.Devices");
        Assert.AreEqual(typeof(IDevice).Assembly.GetName().Version!.Major, core.Version!.Major);
        Assert.IsFalse(plugin.GetReferencedAssemblies().Any(a => a.Name == "RIoT2.Core"));
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

    // The zip RIoT2.Core.Devices.Build packs on publish (plan M12 step 3), installed and loaded the way
    // the node does it: extract into a plugin folder, then every DLL in its own PluginLoadContext.
    [TestMethod]
    public async Task PackedDevicesPluginInstallsAndLoadsIntoNode()
    {
        var packed = Path.Combine(AppContext.BaseDirectory, "PackedPlugin");
        var zip = Directory.GetFiles(packed, "RIoT2.Net.Devices_*.zip").Single();
        var sidecar = File.ReadAllText(zip + ".sha256").Trim();
        using (var stream = File.OpenRead(zip))
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            Assert.AreEqual($"{hash}  {Path.GetFileName(zip)}", sidecar, "The sidecar must be in sha256sum format.");
        }

        var folder = Path.Combine(Path.GetTempPath(), "riot2-packed-plugin-" + Guid.NewGuid().ToString("N"));
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, folder);
        try
        {
            var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "PluginManifest.json"))).RootElement;
            Assert.AreEqual("Devices plugin", manifest.GetProperty("name").GetString());
            Assert.AreEqual(Path.GetFileName(zip), manifest.GetProperty("installedPackageFilename").GetString());
            Assert.AreEqual(typeof(IDevice).Assembly.GetName().Version!.Major, Version.Parse(manifest.GetProperty("coreVersion").GetString()!).Major);
            Assert.IsFalse(Directory.GetFiles(folder, "RIoT2.Core*.dll").Any(), "The node supplies RIoT2.Core*; a plugin must not ship it.");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "RIoT2.Net.Devices.dll")));
            // Core 2.0.0 has no Newtonsoft.Json, so the node doesn't ship it: Google.Apis brings its own (I45).
            Assert.IsTrue(File.Exists(Path.Combine(folder, "Newtonsoft.Json.dll")));

            // Same steps as Program.loadPlugins.
            var plugins = new List<(PluginLoadContext Context, Type Type)>();
            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                var context = new PluginLoadContext(file);
                var assembly = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(file));
                var type = assembly.GetTypes().SingleOrDefault(t => !t.IsAbstract && t.GetInterface(nameof(IDevicePlugin)) != null);
                if (type != null)
                    plugins.Add((context, type));
            }
            var (pluginContext, pluginType) = plugins.Single();
            Assert.AreEqual("RIoT2.Net.Devices.Plugin", pluginType.FullName);
            Assert.AreNotSame(typeof(RIoT2.Net.Devices.Plugin), pluginType, "The packed plugin must load from the zip, not from the test's reference.");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILogger>(NullLogger.Instance);
            ((IDevicePlugin)Activator.CreateInstance(pluginType)!).Initialize(services);
            var devices = services.Where(s => s.ServiceType == typeof(IDevice)).Select(s => s.ImplementationType!).ToList();
            Assert.IsGreaterThanOrEqualTo(15, devices.Count);
            Assert.IsTrue(devices.All(t => AssemblyLoadContext.GetLoadContext(t.Assembly) == pluginContext));

            using var provider = services.BuildServiceProvider();
            var device = (IDevice)ActivatorUtilities.CreateInstance(provider, devices.Single(t => t.Name == "Virtual"));
            device.Initialize(new DeviceConfiguration { Id = "packed", CommandTemplates = [], ReportTemplates = [] });
            await RIoT2.Core.Plugins.DeviceCalls.StartAsync(device, default);
            await RIoT2.Core.Plugins.DeviceCalls.StopAsync(device, default);
            Assert.AreEqual(RIoT2.Core.DeviceState.Stopped, device.State);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } // Windows: the load contexts keep the DLLs open
        }
    }

    [TestMethod]
    public async Task TemplatePluginInstallsAndLoadsIntoNode()
    {
        // dotnet new riot2-plugin from the template package of this node's Core version, published like a release
        // by the PackTemplatePlugin target (plan M12 step 6).
        var zip = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TemplatePlugin"), "Template.Smoke_*.zip").Single();
        Assert.IsTrue(File.Exists(zip + ".sha256"));
        var folder = Path.Combine(Path.GetTempPath(), "riot2-template-plugin-" + Guid.NewGuid().ToString("N"));
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, folder);
        try
        {
            var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "PluginManifest.json"))).RootElement;
            Assert.AreEqual("Template smoke", manifest.GetProperty("name").GetString());
            Assert.IsTrue(RIoT2.Core.Plugins.PluginCompatibility.IsCompatible(manifest.GetProperty("coreVersion").GetString(),
                RIoT2.Core.Plugins.PluginCompatibility.HostCoreVersion, out var reason), reason);
            Assert.IsFalse(Directory.GetFiles(folder, "RIoT2.Core*.dll").Any());

            // Same steps as Program.loadPlugins.
            var plugins = new List<Type>();
            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                var context = new PluginLoadContext(file);
                var assembly = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(file));
                plugins.AddRange(assembly.GetTypes().Where(t => !t.IsAbstract && t.GetInterface(nameof(IDevicePlugin)) != null));
            }
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ILogger>(NullLogger.Instance);
            ((IDevicePlugin)Activator.CreateInstance(plugins.Single())!).Initialize(services);
            using var provider = services.BuildServiceProvider();
            var device = provider.GetServices<IDevice>().Single();
            Assert.AreEqual("Counter", device.GetType().Name);
            Assert.IsTrue(RIoT2.Core.Plugins.DeviceCalls.IsAsynchronous(device), "The template's device is async (plan M12 step 5).");

            var configuration = RIoT2.Core.Plugins.DeviceTemplates.Create(device);
            Assert.AreEqual("1", configuration.DeviceParameters["step"], "The options' defaults are in the template.");
            configuration.DeviceParameters["step"] = "2";
            var values = new List<double>();
            device.ReportUpdated += (_, report) => values.Add(((Report)report).Value.GetValue<double>());
            device.Initialize(configuration);
            await RIoT2.Core.Plugins.DeviceCalls.StartAsync(device, default);
            await RIoT2.Core.Plugins.DeviceCalls.RefreshAsync(device, device.Id, device.Id, default);
            await RIoT2.Core.Plugins.DeviceCalls.ExecuteCommandAsync(device, configuration.CommandTemplates!.Single().Id, "5", default);
            await RIoT2.Core.Plugins.DeviceCalls.StopAsync(device, default);

            CollectionAssert.AreEqual(new[] { 2d, 5d }, values);
            Assert.AreEqual(RIoT2.Core.DeviceState.Stopped, device.State);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { } // Windows: the load contexts keep the DLLs open
        }
    }

    public sealed class LegacyFixture : DeviceBase, IDevice
    {
#pragma warning disable CS0618 // A synchronous device on purpose: the node still runs them in Core 2.x.
        public LegacyFixture() : base(NullLogger.Instance) { }
#pragma warning restore CS0618
        public override void ConfigureDevice() { }
        public override void StartDevice() { }
        public override void StopDevice() { }
    }
}
