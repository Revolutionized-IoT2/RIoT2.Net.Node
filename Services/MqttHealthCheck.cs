using Microsoft.Extensions.Diagnostics.HealthChecks;
using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Node.Services
{
    /// <summary>Reports the broker connection on <c>/health</c>, like the orchestrator's check.</summary>
    internal sealed class MqttHealthCheck : IHealthCheck
    {
        private readonly INodeMqttService _mqtt;

        public MqttHealthCheck(INodeMqttService mqtt)
        {
            ArgumentNullException.ThrowIfNull(mqtt);
            _mqtt = mqtt;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_mqtt.IsConnected()
                ? HealthCheckResult.Healthy("MQTT broker is connected.")
                : HealthCheckResult.Unhealthy("MQTT broker is disconnected."));
        }
    }
}
