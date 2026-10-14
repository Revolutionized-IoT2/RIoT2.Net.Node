using RIoT2.Core;
using RIoT2.Core.Models;
using RIoT2.Core.Utils;
using static RIoT2.Net.Node.Tests.NodeIntegrationHarness;

namespace RIoT2.Net.Node.Tests;

/// <summary>
/// Desired-state configuration (design 7.2, I16): the node skips a configuration it already runs,
/// starts with its saved configuration, keeps its configuration when a fetch fails, reports its
/// status retained, and retains its presence (D1 removed).
/// </summary>
[TestClass]
[DoNotParallelize]
public class NodeDesiredStateTests
{
    [TestMethod]
    public async Task PresenceAndStatusAreRetained()
    {
        await using var node = await NodeIntegrationHarness.StartAsync();
        var presence = await node.PresenceAsync();
        Assert.IsTrue(presence.HasCapability(Contract.Capabilities.DesiredState));

        var retained = Json.Deserialize<NodeOnlineMessage>(await node.ReadRetainedAsync(MqttTopic.NodeOnline));
        Assert.IsTrue(retained.IsOnline, "A late subscriber sees the node's presence.");
        var status = NodeStatusMessage.Create(await node.ReadRetainedAsync(MqttTopic.NodeStatus));
        Assert.AreEqual(NodeConfigurationState.Applying, status.State, "Without a configuration the node waits for one.");
        Assert.IsNull(status.AppliedRevision);

        await node.StopAsync();
        Assert.IsFalse(Json.Deserialize<NodeOnlineMessage>(await node.ReadRetainedAsync(MqttTopic.NodeOnline)).IsOnline);
    }

    [TestMethod]
    public async Task ReconnectAndOrchestratorRestartFetchAndRestartNothing()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        var desired = await node.SendDesiredAsync(node.ConfigurationFor(plc), 1);
        await node.WaitAppliedAsync("initial");
        var applied = await node.StatusAsync(s => s.State == NodeConfigurationState.Applied);
        Assert.AreEqual(1, applied.AppliedRevision);
        Assert.AreEqual(desired.Hash, applied.AppliedHash);
        Assert.IsNull(applied.FailedDevices);
        Assert.AreEqual(1, node.Fetches);
        Assert.AreEqual(1, node.Reconfigurations.Single().Started);

        // An orchestrator restart: the node announces itself again and gets the same configuration command.
        node.DrainStatuses();
        await node.BarrierAsync();
        await node.ResendAsync(desired);
        Assert.AreEqual(desired.Hash, (await node.StatusAsync(s => s.State == NodeConfigurationState.Applied)).AppliedHash);

        // An MQTT reconnect: the same again.
        await node.ReconnectNodeAsync();
        node.DrainStatuses();
        await node.ResendAsync(desired);
        await node.StatusAsync(s => s.State == NodeConfigurationState.Applied);

        Assert.AreEqual(1, node.Fetches, "An applied hash is not fetched again.");
        Assert.HasCount(1, node.Reconfigurations, "Nothing is applied again.");
        Assert.AreEqual(RIoT2.Core.DeviceState.Running, node.Device.State);
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    [TestMethod]
    public async Task NodeStartsWithItsSavedConfigurationWhileTheOrchestratorIsDown()
    {
        await using var plc = new SimulatedEasyPlc();
        string folder;
        NodeDeviceConfiguration desired;
        await using (var first = await NodeIntegrationHarness.StartAsync())
        {
            desired = await first.SendDesiredAsync(first.ConfigurationFor(plc), 3);
            await first.WaitAppliedAsync("initial");
            await first.StatusAsync(s => s.State == NodeConfigurationState.Applied);
            folder = first.DataFolder;
        }

        await using var restarted = await NodeIntegrationHarness.StartAsync(dataFolder: folder);
        await restarted.WaitAppliedAsync("initial");
        var status = await restarted.StatusAsync(s => s.State == NodeConfigurationState.Applied);
        Assert.AreEqual(3, status.AppliedRevision);
        Assert.AreEqual(desired.Hash, status.AppliedHash);
        Assert.AreEqual(RIoT2.Core.DeviceState.Running, restarted.Device.State);
        Assert.AreEqual(0, restarted.Fetches, "No orchestrator was asked.");

        // The orchestrator comes back with the same configuration: nothing to do.
        await restarted.SendDesiredAsync(restarted.ConfigurationFor(plc), 3);
        await restarted.StatusAsync(s => s.State == NodeConfigurationState.Applied);
        Assert.AreEqual(0, restarted.Fetches);
        Assert.HasCount(1, restarted.Reconfigurations);
    }

    [TestMethod]
    public async Task FailedFetchKeepsTheConfigurationAndReportsFailed()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.SendDesiredAsync(node.ConfigurationFor(plc), 1);
        await node.WaitAppliedAsync("initial");

        await node.SendUnavailableAsync(2);

        var failed = await node.StatusAsync(s => s.State == NodeConfigurationState.Failed);
        StringAssert.Contains(failed.Error, "HTTP 404");
        Assert.AreEqual(1, failed.AppliedRevision, "The previous configuration is still applied.");
        Assert.AreEqual("initial", node.Device.Name);
        Assert.AreEqual(RIoT2.Core.DeviceState.Running, node.Device.State);
        Assert.HasCount(1, node.Reconfigurations);
        StringAssert.Contains((await ReadAsync(node.Log.Errors)).Error.Message, "HTTP 404");

        // The next good configuration applies normally.
        await node.SendDesiredAsync(node.ConfigurationFor(plc, "renamed"), 3);
        await node.WaitAppliedAsync("renamed");
        Assert.AreEqual(3, (await node.StatusAsync(s => s.State == NodeConfigurationState.Applied)).AppliedRevision);
    }

    [TestMethod]
    public async Task DeviceThatIsNotRunningIsReportedAndRetriedByTheNextCommand()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        var configuration = node.ConfigurationFor(plc);
        configuration.DeviceConfigurations.Add(new DeviceConfiguration { Id = "missing", Name = "missing", ClassFullName = "Plugins.Missing" });
        var desired = await node.SendDesiredAsync(configuration, 1);
        await node.WaitAppliedAsync("initial");
        var applied = await node.StatusAsync(s => s.State == NodeConfigurationState.Applied);
        Assert.AreEqual("missing", applied.FailedDevices.Single().Id);
        StringAssert.Contains(applied.FailedDevices.Single().Message, "Plugins.Missing");

        // Not every device runs, so the same command is fetched again (304) and applied again.
        await node.ResendAsync(desired);
        await node.WaitAppliedAsync("initial");
        Assert.AreEqual(2, node.Fetches);
        Assert.IsTrue(node.Reconfigurations.Last().NothingChanged, "The running device is left alone.");
    }

    [TestMethod]
    public async Task OrchestratorWithoutHashesIsFetchedEveryTimeAndRestartsNothing()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();

        await node.ConfigureAsync(node.ConfigurationFor(plc));
        await node.ConfigureAsync(node.ConfigurationFor(plc));

        Assert.AreEqual(2, node.Fetches);
        Assert.IsTrue(node.Reconfigurations.Last().NothingChanged);
        var status = await node.StatusAsync(s => s.State == NodeConfigurationState.Applied);
        Assert.IsNull(status.AppliedRevision);
        Assert.IsNull(status.AppliedHash);
        Assert.IsTrue(File.Exists(Path.Combine(node.DataFolder, "configuration.applied.json")), "The configuration is saved anyway.");
    }
}
