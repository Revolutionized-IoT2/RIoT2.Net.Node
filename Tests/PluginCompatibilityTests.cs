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

    public sealed class LegacyFixture : DeviceBase, IDevice
    {
        public LegacyFixture() : base(NullLogger.Instance) { }
        public override void ConfigureDevice() { }
        public override void StartDevice() { }
        public override void StopDevice() { }
    }
}
