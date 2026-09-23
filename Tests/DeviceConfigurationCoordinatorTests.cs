using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Models;
using RIoT2.Net.Node.Services;

namespace RIoT2.Net.Node.Tests;

[TestClass]
public class DeviceConfigurationCoordinatorTests
{
    [TestMethod]
    public async Task HostedMqttStartupConfigurationWaitsUntilSchedulerHasStarted()
    {
        var configuration = new TestConfiguration();
        var schedulerReady = false;
        var applied = Signal();
        await using var coordinator = Create(configuration, (_, _) =>
        {
            Assert.IsTrue(schedulerReady);
            applied.TrySetResult();
            return Task.CompletedTask;
        });
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<IHostedService>(new StartupAction(() =>
                configuration.SetDeviceConfiguration(new NodeDeviceConfiguration())));
            services.AddSingleton<IHostedService>(new StartupAction(() => schedulerReady = true));
            services.AddSingleton<IHostedService>(coordinator);
        }).Build();
        await host.StartAsync();
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await host.StopAsync();
    }

    [TestMethod]
    public async Task MultipleStartupUpdatesApplyOnlyLatestConfiguration()
    {
        var configuration = new TestConfiguration();
        var applied = new TaskCompletionSource<NodeDeviceConfiguration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var coordinator = Create(configuration, (desired, _) =>
        {
            Interlocked.Increment(ref count);
            applied.TrySetResult(desired);
            return Task.CompletedTask;
        });
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        var latest = new NodeDeviceConfiguration();
        configuration.SetDeviceConfiguration(latest);
        Assert.AreEqual(0, count);
        await coordinator.StartAsync(default);
        await coordinator.StartAsync(default);
        Assert.AreSame(latest, await applied.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        await coordinator.StopAsync(default);
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task ConfigurationLoadedBeforeSubscriptionIsApplied()
    {
        var configuration = new TestConfiguration();
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        var applied = Signal();
        await using var coordinator = Create(configuration, (_, _) => { applied.TrySetResult(); return Task.CompletedTask; });
        await coordinator.StartAsync(default);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task SupersededConfigurationIsCancelledAndAwaitedBeforeLatestApplies()
    {
        var configuration = new TestConfiguration();
        var entered = Signal();
        var cancelled = Signal();
        var release = Signal();
        var latestApplied = Signal();
        var active = 0;
        var maximum = 0;
        await using var coordinator = Create(configuration, async (desired, token) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try
            {
                if (desired.Id == "first")
                {
                    using var registration = token.Register(() => cancelled.TrySetResult());
                    entered.TrySetResult();
                    await release.Task;
                    token.ThrowIfCancellationRequested();
                }
                if (desired.Id == "last") latestApplied.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await coordinator.StartAsync(default);
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration { Id = "first" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Parallel.For(0, 20, i => configuration.SetDeviceConfiguration(new NodeDeviceConfiguration { Id = i.ToString() }));
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration { Id = "last" });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(latestApplied.Task.IsCompleted);
        release.SetResult();
        await latestApplied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await coordinator.StopAsync(default);
        Assert.AreEqual(1, maximum);
    }

    [TestMethod]
    public async Task StopAwaitsCancellationBeforeStoppingDevicesAndUnsubscribes()
    {
        var configuration = new TestConfiguration();
        var entered = Signal();
        var cancelled = Signal();
        var release = Signal();
        var stopped = 0;
        var applications = 0;
        await using var coordinator = new DeviceConfigurationCoordinator(configuration, async (_, token) =>
        {
            applications++;
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
        }, _ => { stopped++; return Task.CompletedTask; }, NullLogger.Instance);
        await coordinator.StartAsync(default);
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = coordinator.StopAsync(default);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(stopping.IsCompleted);
        Assert.AreEqual(0, stopped);
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        release.SetResult();
        await stopping;
        await coordinator.StopAsync(default);
        Assert.AreEqual(1, stopped);
        Assert.AreEqual(1, applications);
    }

    [TestMethod]
    public async Task FailedApplicationIsLoggedAndDoesNotKillWorker()
    {
        var configuration = new TestConfiguration();
        var logger = new ErrorLogger();
        var succeeded = Signal();
        await using var coordinator = new DeviceConfigurationCoordinator(configuration, (desired, _) =>
        {
            if (desired.Id == "bad") throw new IOException("Synthetic failure");
            succeeded.TrySetResult();
            return Task.CompletedTask;
        }, _ => Task.CompletedTask, logger);
        await coordinator.StartAsync(default);
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration { Id = "bad" });
        await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration { Id = "good" });
        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task DisposedCoordinatorNeverAppliesBufferedOrFutureConfiguration()
    {
        var configuration = new TestConfiguration();
        var applications = 0;
        var coordinator = Create(configuration, (_, _) => { applications++; return Task.CompletedTask; });
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        Assert.AreEqual(0, applications);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => coordinator.StartAsync(default));
    }

    [TestMethod]
    public async Task HostDeadlineDoesNotSkipDeviceCleanup()
    {
        var cleaned = false;
        await using var coordinator = new DeviceConfigurationCoordinator(new TestConfiguration(),
            (_, _) => Task.CompletedTask, token =>
            {
                Assert.IsFalse(token.IsCancellationRequested);
                cleaned = true;
                return Task.CompletedTask;
            }, NullLogger.Instance);
        await coordinator.StartAsync(default);
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        await coordinator.StopAsync(deadline.Token);
        Assert.IsTrue(cleaned);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DeviceConfigurationCoordinator Create(TestConfiguration configuration,
        Func<NodeDeviceConfiguration, CancellationToken, Task> apply) =>
        new(configuration, apply, _ => Task.CompletedTask, NullLogger.Instance);
    private sealed class TestConfiguration : NodeConfigurationServiceBase
    {
        public override NodeConfiguration Configuration { get; } = new();
    }
    private sealed class StartupAction(Action start) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) { start(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class ErrorLogger : ILogger
    {
        public TaskCompletionSource Failed { get; } = Signal();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (level == LogLevel.Error) Failed.TrySetResult();
        }
    }
}
