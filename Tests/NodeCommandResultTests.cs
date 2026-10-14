using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using static RIoT2.Net.Node.Tests.NodeIntegrationHarness;

namespace RIoT2.Net.Node.Tests;

/// <summary>
/// Command results (design 7.1, I15): a command with a correlationId is answered on
/// riot2/node/{id}/command/result, a redelivery runs only once, and a command without one keeps the
/// old fire-and-forget behaviour.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NodeCommandResultTests
{
    [TestMethod]
    public async Task NodeAdvertisesCommandResults()
    {
        await using var node = await NodeIntegrationHarness.StartAsync();
        var presence = await node.PresenceAsync();
        Assert.IsTrue(presence.HasCapability(Contract.Capabilities.CommandResult));
        Assert.IsFalse(presence.HasCapability(Contract.Capabilities.DesiredState), "Desired state comes with I16.");
    }

    [TestMethod]
    public async Task ExecutedCommandIsAnsweredAndARedeliveryRunsOnlyOnce()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(plc));

        await node.CorrelatedCommandAsync("set", "c1");
        (await ReadAsync(plc.Requests)).ReplyWithWriteAck();
        var executed = await ReadAsync(node.CommandResults);
        Assert.AreEqual("c1", executed.CorrelationId);
        Assert.AreEqual("set", executed.CommandId);
        Assert.AreEqual(CommandResultStatus.Executed, executed.Status);
        Assert.IsNull(executed.Error);
        Assert.IsTrue(executed.TimeStamp > 0);
        Assert.AreEqual(Contract.Version, executed.ContractVersion);

        await node.CorrelatedCommandAsync("set", "c1");
        var duplicate = await ReadAsync(node.CommandResults);
        Assert.AreEqual("c1", duplicate.CorrelationId);
        Assert.AreEqual(CommandResultStatus.Duplicate, duplicate.Status);
        await node.BarrierAsync();
        Assert.IsFalse(plc.Requests.TryRead(out _), "A redelivered command must not reach the device again.");
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    [TestMethod]
    public async Task CommandWithoutADeviceIsUnknown()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(plc));

        // Without a correlationId there is no result, as before.
        await node.CommandAsync("missing");
        await node.CorrelatedCommandAsync("missing", "c2");
        var result = await ReadAsync(node.CommandResults);
        Assert.AreEqual("c2", result.CorrelationId, "The command without a correlationId must not be answered.");
        Assert.AreEqual(CommandResultStatus.UnknownCommand, result.Status);
        StringAssert.Contains(result.Error, "missing");
        Assert.IsFalse(node.Log.Errors.TryRead(out _), "An unknown command is a warning, not an error.");
    }

    [TestMethod]
    public async Task ThrowingCommandFails()
    {
        await using var node = await NodeIntegrationHarness.StartAsync(commandService: new ThrowingCommands());

        await node.CorrelatedCommandAsync("set", "c3");
        var result = await ReadAsync(node.CommandResults);
        Assert.AreEqual(CommandResultStatus.Failed, result.Status);
        Assert.AreEqual("relay stuck", result.Error);

        // The redelivery doesn't run again; it gets the same failure.
        await node.CorrelatedCommandAsync("set", "c3");
        var replayed = await ReadAsync(node.CommandResults);
        Assert.AreEqual(CommandResultStatus.Failed, replayed.Status);
        Assert.AreEqual("relay stuck", replayed.Error);
    }

    [TestMethod]
    public async Task RedeliveryWhileTheCommandRunsGetsOnlyTheFirstResult()
    {
        using var commands = new NodeIntegrationTests.BlockingLegacyCommands();
        await using var node = await NodeIntegrationHarness.StartAsync(commandService: commands);

        try
        {
            await node.CorrelatedCommandAsync("set", "c6");
            await commands.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await node.CorrelatedCommandAsync("set", "c6");
            await node.BarrierAsync();
            Assert.IsFalse(node.CommandResults.TryRead(out _), "A running command must not be answered before it ends.");
        }
        finally { commands.Release.Set(); }

        var result = await ReadAsync(node.CommandResults);
        Assert.AreEqual("c6", result.CorrelationId);
        Assert.AreEqual(CommandResultStatus.Executed, result.Status);
        await node.BarrierAsync();
        Assert.IsFalse(node.CommandResults.TryRead(out _), "The redelivery must not get its own answer.");
        Assert.AreEqual(1, commands.Calls);
    }

    [TestMethod]
    public async Task CommandCutOffByAReconfigurationFails()
    {
        await using var oldPlc = new SimulatedEasyPlc();
        await using var newPlc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(oldPlc));
        await node.CorrelatedCommandAsync("refresh", "c7");
        await ReadAsync(oldPlc.Requests);

        await node.ConfigureAsync(node.ConfigurationFor(newPlc, "replacement"));

        // It may have reached the device, so it isn't run again against the new configuration.
        var result = await ReadAsync(node.CommandResults);
        Assert.AreEqual("c7", result.CorrelationId);
        Assert.AreEqual(CommandResultStatus.Failed, result.Status);
        StringAssert.Contains(result.Error, "cancelled");
        await node.CorrelatedCommandAsync("refresh", "c7");
        Assert.AreEqual(CommandResultStatus.Failed, (await ReadAsync(node.CommandResults)).Status);
        await node.BarrierAsync();
        Assert.IsFalse(newPlc.Requests.TryRead(out _));
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    [TestMethod]
    public async Task CommandBeyondThePendingLimitIsRejectedAndCanBeDeliveredAgain()
    {
        using var commands = new NodeIntegrationTests.BlockingLegacyCommands();
        await using var node = await NodeIntegrationHarness.StartAsync(commandService: commands);
        try
        {
            await node.CommandAsync("first");
            await commands.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var i = 1; i < 64; i++)
                await node.CommandAsync("queued");
            await node.CorrelatedCommandAsync("set", "c4");
            var rejected = await ReadAsync(node.CommandResults);
            Assert.AreEqual("c4", rejected.CorrelationId);
            Assert.AreEqual(CommandResultStatus.Rejected, rejected.Status);
            StringAssert.Contains(rejected.Error, "64");
        }
        finally { commands.Release.Set(); }

        // A rejected command never ran, so the orchestrator's next delivery runs it.
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while (commands.Calls < 64)
                await Task.Delay(10, deadline.Token);
        await node.BarrierAsync();
        await node.CorrelatedCommandAsync("set", "c4");
        var executed = await ReadAsync(node.CommandResults);
        Assert.AreEqual(CommandResultStatus.Executed, executed.Status);
        Assert.IsTrue(node.Log.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("pending command limit 64")));
    }

    [TestMethod]
    public async Task CommandBeforeTheConfigurationIsRejectedAndRunsOnItsNextDelivery()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();

        // The orchestrator sends waiting commands as soon as the node is online, together with the configuration.
        await node.CorrelatedCommandAsync("set", "c5");
        var rejected = await ReadAsync(node.CommandResults);
        Assert.AreEqual(CommandResultStatus.Rejected, rejected.Status);
        StringAssert.Contains(rejected.Error, "configuration");

        await node.ConfigureAsync(node.ConfigurationFor(plc));
        await node.CorrelatedCommandAsync("set", "c5");
        (await ReadAsync(plc.Requests)).ReplyWithWriteAck();
        Assert.AreEqual(CommandResultStatus.Executed, (await ReadAsync(node.CommandResults)).Status);
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    private sealed class ThrowingCommands : IAsyncCommandService
    {
        public void ExecuteJsonCommand(string json) => throw new InvalidOperationException("relay stuck");

        public Task ExecuteJsonCommandAsync(string json, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("relay stuck"));
    }
}
