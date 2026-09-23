using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Node.Services
{
    internal class MqttBackgroundService : IHostedService
    {
        private readonly INodeMqttService _mqttService;
        private readonly INodeConfigurationService _configuration;
        private readonly IDeviceService _deviceService;
        private readonly ILogger _logger;

        public MqttBackgroundService(INodeMqttService mqttService, INodeConfigurationService configuration, IDeviceService deviceService, ILogger logger)
        {
            _mqttService = mqttService;
            _configuration = configuration;
            _deviceService = deviceService;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await initialize();
#if DEBUG
            await _configuration.LoadDeviceConfiguration("", "");
#endif
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_deviceService is IAsyncDeviceService asynchronous)
                    await asynchronous.StopAllDevicesAsync(CancellationToken.None);
                else
                    _deviceService.StopAllDevices();
            }
            finally { await _mqttService.Stop(); }
        }

        public async Task SendNodeOnlineMessage() 
        {
            await _mqttService.SendNodeOnlineMessage(_configuration.OnlineMessage);
        }

        private async Task initialize() 
        {
            await _mqttService.Start();
            _logger.LogTrace("MQTT Service running");
        }

    }
}
