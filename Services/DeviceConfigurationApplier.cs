using RIoT2.Core.Abstracts;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;

namespace RIoT2.Net.Node.Services
{
    /// <summary>
    /// Applies a node configuration (design 7.2): checks the plugin package, restarts only the devices
    /// whose entry changed or that aren't running, saves the configuration for the next start, and
    /// reports the outcome on the node's status (<c>applied</c> with the devices that didn't start,
    /// <c>pendingRestart</c> or <c>failed</c>). A staged plugin package saves the configuration and restarts
    /// the node; a refused one leaves the plugins as they are, applies the devices, and reports <c>failed</c>.
    /// </summary>
    internal sealed class DeviceConfigurationApplier
    {
        private readonly NodeConfigurationServiceBase _configuration;
        private readonly DeviceServiceBase _devices;
        private readonly Func<NodeDeviceConfiguration?, CancellationToken, Task<PluginUpdateResult>>? _updatePlugins;
        private readonly ILogger _logger;
        private readonly Action? _restart;

        /// <param name="updatePlugins">Checks the configuration's plugin package and stages a new one (<see cref="PluginPackageInstaller.UpdateAsync"/>).</param>
        /// <param name="restart">Restarts the node to install a staged package.</param>
        public DeviceConfigurationApplier(NodeConfigurationServiceBase configuration, DeviceServiceBase devices,
            Func<NodeDeviceConfiguration?, CancellationToken, Task<PluginUpdateResult>>? updatePlugins, ILogger logger, Action? restart = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _updatePlugins = updatePlugins;
            _logger = logger;
            _restart = restart;
        }

        /// <summary>Returns what happened to the devices, or <c>null</c> when the node restarts for a new plugin package.</summary>
        public async Task<DeviceReconfigurationResult?> ApplyAsync(NodeDeviceConfiguration? desired, CancellationToken cancellationToken)
        {
            try
            {
                var plugins = _updatePlugins == null ? PluginUpdateResult.Unchanged : await _updatePlugins(desired, cancellationToken);
                if (plugins.Outcome == PluginUpdateOutcome.Staged)
                {
                    // Saved first, so the node starts with the configuration that names the new package and
                    // doesn't install the previous one again.
                    if (desired != null)
                        Save(desired);
                    _configuration.ConfigurationStatus.PendingRestart();
                    _restart?.Invoke();
                    return null;
                }

                var result = await _devices.ReconfigureChangedDevicesAsync(desired?.DeviceConfigurations, cancellationToken);
                if (result.NothingChanged)
                    _logger.LogInformation("Device configuration unchanged; no device restarted.");
                else
                    _logger.LogInformation("Device configuration applied: {Result}. Inspect device status for individual startup failures.", result);

                if (desired != null)
                    Save(desired);
                var failed = FailedDevices(desired);
                if (plugins.Outcome == PluginUpdateOutcome.Rejected)
                {
                    // The devices run with the installed plugins, but the configuration isn't fully applied.
                    _configuration.ConfigurationStatus.Failed(plugins.Message ?? "The plugin package was refused.");
                    return result;
                }
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

        private void Save(NodeDeviceConfiguration desired)
        {
            try { _configuration.SaveAppliedConfiguration(desired); }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Could not save the applied configuration; the node starts without it when the orchestrator is down");
            }
        }

        private List<FailedDevice> FailedDevices(NodeDeviceConfiguration? desired)
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
