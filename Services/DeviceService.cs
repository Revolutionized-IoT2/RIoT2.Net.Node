using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Node.Services
{
    public class DeviceService(INodeConfigurationService configurationService, 
        ILogger logger) : DeviceServiceBase(configurationService, logger, []), IDeviceService
    {
    }
}