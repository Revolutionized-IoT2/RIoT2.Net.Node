using RIoT2.Core.Models;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces.Services;
using System.Reflection;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Node.Services
{
    internal class ConfigurationService : NodeConfigurationServiceBase, INodeConfigurationService
    {
        private NodeConfiguration _configuration;
        private DirectoryInfo _configurationFolder;
        private ILogger _logger;

        public ConfigurationService(ILogger<ConfigurationService> logger) 
        {
            _logger = logger;
            _configurationFolder = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location));
        }

        public NodeConfiguration Configuration 
        {
            get
            {
                if (_configuration == null) 
                {
                    _configuration = new NodeConfiguration()
                    {
                        Id = Environment.GetEnvironmentVariable("RIOT2_NODE_ID"),
                        MqttServerUrl = Environment.GetEnvironmentVariable("RIOT2_MQTT_IP"),
                        MqttUsername = Environment.GetEnvironmentVariable("RIOT2_MQTT_USERNAME"),
                        MqttPassword = Environment.GetEnvironmentVariable("RIOT2_MQTT_PASSWORD")
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
                using (FileStream SourceStream = File.Open(localConfigFile.FullName, FileMode.Open))
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

        public string ApplicationFolder { get { return _configurationFolder.FullName; } }


        private FileInfo loadConfigurationFile(string filename)
        {
            var fullPath = Path.Combine(ApplicationFolder, filename);
            FileInfo f = new FileInfo(fullPath);

            if(!f.Exists)
                return null;

            return f;
        }
    }
}
