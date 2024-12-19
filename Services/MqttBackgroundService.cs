using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Node.Services
{
    internal class MqttBackgroundService : IHostedService
    {
        private readonly INodeMqttService _mqttService;
        private readonly INodeConfigurationService _configuration;
        private readonly IDeviceService _deviceService;
        private readonly ILogger _logger;

        public MqttBackgroundService(INodeMqttService mqttService, INodeConfigurationService configuration, IDeviceService deviceService, ILogger<MqttBackgroundService> logger)
        {
            _mqttService = mqttService;
            _configuration = configuration;
            _deviceService = deviceService;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await initialize();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.Factory.StartNew(() =>
            {
                _deviceService.StopAllDevices();
            });

            await _mqttService.Stop();
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
