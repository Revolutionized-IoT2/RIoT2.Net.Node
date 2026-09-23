using System.Threading.Channels;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;

namespace RIoT2.Net.Node.Services
{
    internal sealed class DeviceConfigurationCoordinator : IHostedService, IAsyncDisposable
    {
        private readonly INodeConfigurationService _configuration;
        private readonly Func<NodeDeviceConfiguration, CancellationToken, Task> _apply;
        private readonly Func<CancellationToken, Task> _stopDevices;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Channel<(long Version, NodeDeviceConfiguration Configuration)> _pending =
            Channel.CreateBounded<(long, NodeDeviceConfiguration)>(new BoundedChannelOptions(1)
            {
                SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
            });
        private CancellationTokenSource _application;
        private Task _worker = Task.CompletedTask;
        private Task _stopTask;
        private bool _started;
        private bool _stopping;
        private long _version;
        private int _disposed;

        public DeviceConfigurationCoordinator(INodeConfigurationService configuration,
            Func<NodeDeviceConfiguration, CancellationToken, Task> apply,
            Func<CancellationToken, Task> stopDevices, ILogger logger)
        {
            _configuration = configuration;
            _apply = apply;
            _stopDevices = stopDevices;
            _logger = logger;
            _configuration.DeviceConfigurationUpdated += ConfigurationUpdated;
            if (_configuration.DeviceConfigurationLoaded)
                ConfigurationUpdated();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_stopping)
                    throw new InvalidOperationException("The device coordinator is stopping.");
                if (_started)
                    return Task.CompletedTask;
                _started = true;
                _worker = Task.Run(ProcessAsync);
            }
            return Task.CompletedTask;
        }

        private void ConfigurationUpdated()
        {
            lock (_gate)
            {
                if (_stopping)
                    return;
                _pending.Writer.TryWrite((++_version, _configuration.DeviceConfiguration));
                _application?.Cancel();
            }
        }

        private async Task ProcessAsync()
        {
            try
            {
                await foreach (var update in _pending.Reader.ReadAllAsync(_shutdown.Token))
                {
                    CancellationTokenSource application;
                    lock (_gate)
                    {
                        if (_stopping || update.Version != _version)
                            continue;
                        _application = application = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    }
                    try { await _apply(update.Configuration, application.Token); }
                    catch (OperationCanceledException) when (application.IsCancellationRequested)
                    {
                        _logger.LogDebug("Device configuration application cancelled by shutdown or a newer configuration");
                    }
                    catch (Exception error)
                    {
                        _logger.LogError(error, "Could not apply device configuration");
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _application = null;
                            application.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_stopTask != null)
                    return _stopTask;
                _stopping = true;
                _configuration.DeviceConfigurationUpdated -= ConfigurationUpdated;
                _shutdown.Cancel();
                _pending.Writer.TryComplete();
                return _stopTask = StopCoreAsync(cancellationToken);
            }
        }

        private async Task StopCoreAsync(CancellationToken cancellationToken)
        {
            // Legacy plugin calls cannot be forcibly cancelled; never abandon them during replacement.
            await _worker.ConfigureAwait(false);
            await _stopDevices(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                _logger.LogWarning("Device shutdown completed after the host cancellation deadline");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try { await StopAsync(CancellationToken.None); }
            finally { _shutdown.Dispose(); }
        }
    }
}
