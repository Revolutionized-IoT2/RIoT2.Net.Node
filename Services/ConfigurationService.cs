using RIoT2.Core.Models;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Node.Services
{
    internal class ConfigurationService : NodeConfigurationServiceBase, INodeConfigurationService
    {
        private NodeConfiguration _configuration;
        private ILogger _logger;

        public ConfigurationService(ILogger logger) 
        {
            _logger = logger;
        }

        public override NodeConfiguration Configuration 
        {
            get
            {
                if (_configuration == null) 
                {
                    _configuration = new NodeConfiguration()
                    {
                        Id = Environment.GetEnvironmentVariable("RIOT2_NODE_ID"),
                        Url = Environment.GetEnvironmentVariable("RIOT2_NODE_URL"),
                        Mqtt = new MqttConfiguration()
                        {
                            ClientId = Environment.GetEnvironmentVariable("RIOT2_NODE_ID"),
                            ServerUrl = Environment.GetEnvironmentVariable("RIOT2_MQTT_IP"),
                            Username = Environment.GetEnvironmentVariable("RIOT2_MQTT_USERNAME"),
                            Password = Environment.GetEnvironmentVariable("RIOT2_MQTT_PASSWORD")
                        },
                    };
                }
                return _configuration;
            }
        }

      

#if DEBUG
        public override async Task LoadDeviceConfiguration(string json, string id) 
        {
            try
            {
                var localConfigFile = loadConfigurationFile("Data/local.configuration.json");
                byte[] result;
                using (FileStream SourceStream = System.IO.File.Open(localConfigFile.FullName, FileMode.Open))
                {
                    result = new byte[SourceStream.Length];
                    await SourceStream.ReadAsync(result, 0, (int)SourceStream.Length);
                }

                SetDeviceConfiguration(Json.DeserializeAutoTypeNameHandling<NodeDeviceConfiguration>(System.Text.Encoding.UTF8.GetString(result)));
            }
            catch (Exception e)
            {
                _logger.LogError($"Could not load local (debug) configuration: {e.Message}");
                _configuration = null;
            }
        }
#endif

        private FileInfo loadConfigurationFile(string filename)
        {
            var fullPath = Path.Combine(Configuration.ApplicationFolder, filename);
            FileInfo f = new FileInfo(fullPath);

            if(!f.Exists)
                return null;

            return f;
        }
    }
}
