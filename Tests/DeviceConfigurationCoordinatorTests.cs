using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        var applications = 0;
        using var coordinator = new DeviceConfigurationCoordinator(configuration, () =>
        {
            Assert.IsTrue(schedulerReady);
            applications++;
        });
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IHostedService>(new StartupAction(() =>
                    configuration.SetDeviceConfiguration(new NodeDeviceConfiguration())));
                services.AddSingleton<IHostedService>(new StartupAction(() => schedulerReady = true));
            })
            .Build();
        host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.Register(coordinator.Activate);

        await host.StartAsync();
        Assert.AreEqual(1, applications);
        await host.StopAsync();
    }

    [TestMethod]
    public void ConfigurationDuringMqttStartupIsAppliedOnceWhenHostIsReady()
    {
        var configuration = new TestConfiguration();
        var applications = 0;
        using var coordinator = new DeviceConfigurationCoordinator(configuration, () => applications++);

        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        Assert.AreEqual(0, applications);

        coordinator.Activate();
        coordinator.Activate();
        Assert.AreEqual(1, applications);
    }

    [TestMethod]
    public void ConfigurationLoadedBeforeSubscriptionIsAppliedOnActivation()
    {
        var configuration = new TestConfiguration();
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        var applications = 0;
        using var coordinator = new DeviceConfigurationCoordinator(configuration, () => applications++);

        coordinator.Activate();

        Assert.AreEqual(1, applications);
    }

    [TestMethod]
    public void StartupWithoutConfigurationWaitsForAnUpdate()
    {
        var configuration = new TestConfiguration();
        var applications = 0;
        using var coordinator = new DeviceConfigurationCoordinator(configuration, () => applications++);

        coordinator.Activate();
        Assert.AreEqual(0, applications);
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        Assert.AreEqual(1, applications);
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        Assert.AreEqual(2, applications);
    }

    [TestMethod]
    public void MultipleStartupUpdatesApplyOnlyLatestConfiguration()
    {
        var configuration = new TestConfiguration();
        NodeDeviceConfiguration applied = null;
        var applications = 0;
        using var coordinator = new DeviceConfigurationCoordinator(configuration, () =>
        {
            applied = configuration.DeviceConfiguration;
            applications++;
        });
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());
        var latest = new NodeDeviceConfiguration();
        configuration.SetDeviceConfiguration(latest);

        coordinator.Activate();

        Assert.AreSame(latest, applied);
        Assert.AreEqual(1, applications);
    }

    [TestMethod]
    public void ConcurrentUpdatesNeverApplyConcurrently()
    {
        var configuration = new TestConfiguration();
        var active = 0;
        var concurrent = 0;
        var applications = 0;
        using var coordinator = new DeviceConfigurationCoordinator(configuration, () =>
        {
            if (Interlocked.Increment(ref active) != 1)
                Interlocked.Increment(ref concurrent);
            Thread.Yield();
            Interlocked.Increment(ref applications);
            Interlocked.Decrement(ref active);
        });
        coordinator.Activate();

        Parallel.For(0, 20, _ => configuration.SetDeviceConfiguration(new NodeDeviceConfiguration()));

        Assert.AreEqual(0, concurrent);
        Assert.AreEqual(20, applications);
    }

    [TestMethod]
    public void DisposedCoordinatorDoesNotApplyPendingOrFutureUpdates()
    {
        var configuration = new TestConfiguration();
        var applications = 0;
        var coordinator = new DeviceConfigurationCoordinator(configuration, () => applications++);
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());

        coordinator.Dispose();
        coordinator.Activate();
        configuration.SetDeviceConfiguration(new NodeDeviceConfiguration());

        Assert.AreEqual(0, applications);
    }

    private sealed class TestConfiguration : NodeConfigurationServiceBase
    {
        public override NodeConfiguration Configuration { get; } = new();
    }

    private sealed class StartupAction(Action start) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
