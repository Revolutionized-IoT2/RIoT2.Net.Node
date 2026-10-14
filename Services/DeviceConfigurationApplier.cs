using RIoT2.Core.Abstracts;
using RIoT2.Core.Models;

namespace RIoT2.Net.Node.Services
{
    /// <summary>
    /// Applies a node configuration (design 7.2): checks the plugin package, restarts only the devices
    /// whose entry changed or that aren't running, saves the configuration for the next start, and
    /// reports the outcome on the node's status (<c>applied</c> with the devices that didn't start,
    /// <c>pendingRestart</c> or <c>failed</c>).
    /// </summary>
    internal sealed class DeviceConfigurationApplier
    {
        private readonly NodeConfigurationServiceBase _configuration;
        private readonly DeviceServiceBase _devices;
        private readonly Func<NodeDeviceConfiguration, CancellationToken, Task<bool>> _updatePlugins;
        private readonly ILogger _logger;

        /// <param name="updatePlugins">Returns <c>true</c> when a new plugin package was downloaded and the node restarts.</param>
        public DeviceConfigurationApplier(NodeConfigurationServiceBase configuration, DeviceServiceBase devices,
            Func<NodeDeviceConfiguration, CancellationToken, Task<bool>> updatePlugins, ILogger logger)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _updatePlugins = updatePlugins;
            _logger = logger;
        }

        /// <summary>Returns what happened to the devices, or <c>null</c> when the node restarts for a new plugin package.</summary>
        public async Task<DeviceReconfigurationResult> ApplyAsync(NodeDeviceConfiguration desired, CancellationToken cancellationToken)
        {
            try
            {
                if (_updatePlugins != null && await _updatePlugins(desired, cancellationToken))
                {
                    _configuration.ConfigurationStatus.PendingRestart();
                    return null;
                }

                var result = await _devices.ReconfigureChangedDevicesAsync(desired?.DeviceConfigurations, cancellationToken);
                if (result.NothingChanged)
                    _logger.LogInformation("Device configuration unchanged; no device restarted.");
                else
                    _logger.LogInformation("Device configuration applied: {Result}. Inspect device status for individual startup failures.", result);

                if (desired != null)
                {
                    try { _configuration.SaveAppliedConfiguration(desired); }
                    catch (Exception error)
                    {
                        _logger.LogWarning(error, "Could not save the applied configuration; the node starts without it when the orchestrator is down");
                    }
                }
                var failed = FailedDevices(desired);
                _configuration.ConfigurationStatus.Applied(desired?.Revision, desired?.Hash, failed);
                if (failed.Count > 0)
                    _logger.LogWarning("Configuration revision {Revision} applied, but {Count} device(s) are not running: {Devices}",
                        desired?.Revision, failed.Count, string.Join(", ", failed.Select(d => d.Id)));
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _configuration.ConfigurationStatus.Failed(error.GetBaseException().Message);
                throw;
            }
        }

        private List<FailedDevice> FailedDevices(NodeDeviceConfiguration desired)
        {
            var failed = new List<FailedDevice>();
            foreach (var entry in desired?.DeviceConfigurations ?? [])
            {
                if (entry == null)
                    continue;
                var device = _devices.Devices.FirstOrDefault(d => d.GetType().FullName == entry.ClassFullName);
                if (device == null)
                    failed.Add(new FailedDevice { Id = entry.Id, Message = $"No loaded plugin provides {entry.ClassFullName}." });
                else if (!_devices.IsActive(device))
                    failed.Add(new FailedDevice
                    {
                        Id = entry.Id,
                        Message = string.IsNullOrEmpty(device.StateMessage) ? $"The device is {device.State}." : device.StateMessage
                    });
            }
            return failed;
        }
    }
}
