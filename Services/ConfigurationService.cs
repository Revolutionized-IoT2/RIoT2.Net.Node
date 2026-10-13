using Microsoft.Extensions.Options;
using RIoT2.Core.Models;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Utils;

namespace RIoT2.Net.Node.Services
{
    internal class ConfigurationService : NodeConfigurationServiceBase, INodeConfigurationService
    {
        private readonly NodeOptions _options;
        private NodeConfiguration _configuration;
        private ILogger _logger;

        public ConfigurationService(IOptions<NodeOptions> options, ILogger logger) 
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options.Value;
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
                        Id = _options.Id,
                        Url = _options.Url,
                        Mqtt = _options.Mqtt.ToConfiguration(_options.Id)
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
                if (localConfigFile == null)
                {
                    _logger.LogWarning("Local (debug) configuration file not found. Skipping load.");
                    return;
                }

                var result = await System.IO.File.ReadAllBytesAsync(localConfigFile.FullName);

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
