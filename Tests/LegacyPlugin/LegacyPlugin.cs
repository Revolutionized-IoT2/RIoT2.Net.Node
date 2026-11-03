using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;

namespace RIoT2.Net.Node.LegacyPlugin;

public sealed class LegacyPlugin : IDevicePlugin
{
    public void Initialize(IServiceCollection services) =>
        services.AddSingleton<IDevice, LegacyDevice>();
}

// A synchronous device on purpose: the node still runs them in Core 2.x.
#pragma warning disable CS0618
public sealed class LegacyDevice(ILogger<LegacyDevice> logger) : DeviceBase(logger), IDevice
#pragma warning restore CS0618
{
    public override void ConfigureDevice() { }
    public override void StartDevice() { }
    public override void StopDevice() { }
}
