using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;

namespace RIoT2.Net.Node.LegacyPlugin;

public sealed class LegacyPlugin : IDevicePlugin
{
    public List<IDevice> Devices { get; } = [];

    public void Initialize(IServiceCollection services) =>
        services.AddSingleton<IDevice, LegacyDevice>();
}

public sealed class LegacyDevice(ILogger<LegacyDevice> logger) : DeviceBase(logger), IDevice
{
    public override void ConfigureDevice() { }
    public override void StartDevice() { }
    public override void StopDevice() { }
}
