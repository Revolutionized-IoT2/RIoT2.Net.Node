using RIoT2.Core.Configuration;

namespace RIoT2.Net.Node.Services
{
    /// <summary>
    /// The node's settings. Every <c>RIOT2_*</c> name it reads is mapped here (plan M4).
    /// </summary>
    public sealed class NodeOptions
    {
        public const string IdVariable = "RIOT2_NODE_ID";
        public const string UrlVariable = "RIOT2_NODE_URL";

        /// <summary>Unique id; also the MQTT client id. Must match the node id configured in the UI.</summary>
        public string Id { get; set; }

        /// <summary>The URL the orchestrator uses to call this node; advertised as <c>nodeBaseUrl</c>.</summary>
        public string Url { get; set; }

        public MqttOptions Mqtt { get; set; } = new();

        /// <summary>
        /// Reads and validates the settings, and throws <see cref="ConfigurationValidationException"/>
        /// listing every problem (logged as critical) when any is missing or invalid.
        /// </summary>
        public static NodeOptions Load(IConfiguration configuration, ILogger logger = null)
        {
            var settings = new SettingsReader(configuration);
            var options = new NodeOptions
            {
                Id = settings.Required(IdVariable),
                Url = settings.RequiredHttpUrl(UrlVariable),
                Mqtt = MqttOptions.Read(settings)
            };
            settings.ThrowIfInvalid("node", logger);
            return options;
        }
    }
}
