using RIoT2.Core.Interfaces.Services;

namespace RIoT2.Net.Node.Services
{
    internal sealed class DeviceConfigurationCoordinator : IDisposable
    {
        private readonly INodeConfigurationService _configuration;
        private readonly Action _applyConfiguration;
        private readonly object _gate = new();
        private bool _active;
        private bool _pending;
        private bool _disposed;

        public DeviceConfigurationCoordinator(INodeConfigurationService configuration, Action applyConfiguration)
        {
            _configuration = configuration;
            _applyConfiguration = applyConfiguration;
            lock (_gate)
            {
                _configuration.DeviceConfigurationUpdated += ConfigurationUpdated;
                _pending = _configuration.DeviceConfigurationLoaded;
            }
        }

        public void Activate()
        {
            lock (_gate)
            {
                if (_disposed || _active)
                    return;

                _active = true;
                ApplyPendingConfiguration();
            }
        }

        private void ConfigurationUpdated()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _pending = true;
                if (_active)
                    ApplyPendingConfiguration();
            }
        }

        private void ApplyPendingConfiguration()
        {
            if (!_pending)
                return;

            _pending = false;
            _applyConfiguration();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _configuration.DeviceConfigurationUpdated -= ConfigurationUpdated;
            }
        }
    }
}
