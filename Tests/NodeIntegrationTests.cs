using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Interfaces.Services;
using static RIoT2.Net.Node.Tests.NodeIntegrationHarness;

namespace RIoT2.Net.Node.Tests;

[TestClass]
[DoNotParallelize]
public class NodeIntegrationTests
{
    [TestMethod]
    public async Task MqttConfigurationCommandsAndReportsUseRealTcpTransport()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(plc));
        Assert.AreEqual(DeviceState.Running, node.Device.State);
        await node.CommandAsync("set");
        var write = await ReadAsync(plc.Requests);
        Assert.IsFalse(write.IsRead);
        CollectionAssert.AreEqual(new byte[] { 0x08, 0x21, 0x00, 0x04, 0x00, 0x00, 0x01 },
            write.Frame[1..^2]);
        write.ReplyWithWriteAck();
        await node.CommandAsync("refresh");
        var read = await ReadAsync(plc.Requests);
        Assert.IsTrue(read.IsRead);
        read.ReplyWithMarkers(true);
        var report = await ReadAsync(node.Reports);
        Assert.AreEqual("initial-marker", report.Id);
        Assert.AreEqual("true", report.Value.ToJson());
        Assert.IsTrue(report.TimeStamp > 0);
        await node.BarrierAsync();
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    [TestMethod]
    public async Task ConfigurationPreemptsPendingMqttCommandWithoutWaitingForItsDeadline()
    {
        await using var oldPlc = new SimulatedEasyPlc();
        await using var newPlc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(oldPlc));
        await node.CommandAsync("refresh");
        var pending = await ReadAsync(oldPlc.Requests);
        var elapsed = Stopwatch.StartNew();
        await node.ConfigureAsync(node.ConfigurationFor(newPlc, "replacement"));
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(3),
            $"Configuration was blocked behind command I/O for {elapsed.Elapsed}.");
        await pending.Disconnected.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, oldPlc.ActiveConnections);
        await node.CommandAsync("refresh");
        (await ReadAsync(newPlc.Requests)).ReplyWithMarkers(false);
        var report = await ReadAsync(node.Reports);
        Assert.AreEqual("replacement-marker", report.Id);
        Assert.AreEqual("false", report.Value.ToJson());
        Assert.IsFalse(node.Reports.TryRead(out _));
        Assert.IsFalse(node.Log.Errors.TryRead(out _), "Replacement must cancel, not time out, old I/O.");
    }

    [DataTestMethod]
    [DataRow("disconnect")]
    [DataRow("bad-header")]
    [DataRow("bad-crc")]
    [DataRow("stall")]
    public async Task PlcFaultIsLoggedAndNextCommandUsesFreshConnection(string fault)
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(plc));
        await node.CommandAsync("set");
        var request = await ReadAsync(plc.Requests);
        var elapsed = Stopwatch.StartNew();
        switch (fault)
        {
            case "disconnect": request.Disconnect(); break;
            case "bad-header": request.Reply([0x00, 0x05]); break;
            case "bad-crc": request.Reply([0x65, 0x05, 0, 0, 0, 0, 0]); break;
        }
        var failure = await ReadAsync(node.Log.Errors);
        Assert.IsNotNull(failure.Error);
        if (fault == "stall")
        {
            Assert.IsInstanceOfType<TimeoutException>(failure.Error);
            Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromSeconds(4.5));
            Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        }
        else if (fault == "disconnect")
            Assert.IsInstanceOfType<IOException>(failure.Error);
        else
            Assert.IsInstanceOfType<InvalidDataException>(failure.Error);
        await request.Disconnected.WaitAsync(TimeSpan.FromSeconds(3));
        await node.CommandAsync("set", false);
        var retry = await ReadAsync(plc.Requests);
        Assert.AreNotEqual(request.ConnectionId, retry.ConnectionId);
        Assert.AreEqual(0, retry.Frame[^3]);
        retry.ReplyWithWriteAck();
        await node.CommandAsync("refresh");
        (await ReadAsync(plc.Requests)).ReplyWithMarkers(false);
        Assert.AreEqual("false", (await ReadAsync(node.Reports)).Value.ToJson());
        Assert.AreEqual(2, plc.ConnectionCount);
        Assert.IsFalse(node.Log.Errors.TryRead(out _), "Recovery must not log another error.");
    }

    [TestMethod]
    public async Task PendingCommandsAreBoundedAndNeverRunAgainstReplacementConfiguration()
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(plc));
        await node.CommandAsync("refresh");
        var pending = await ReadAsync(plc.Requests);
        for (var i = 0; i < 64; i++)
            await node.CommandAsync("set");
        await node.BarrierAsync();
        var rejected = node.Log.Entries.Where(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("pending command limit 64")).ToArray();
        Assert.AreEqual(1, rejected.Length, "Exactly the 65th outstanding command must be rejected.");
        await node.ConfigureAsync(node.ConfigurationFor(plc, "replacement"));
        await pending.Disconnected.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(plc.Requests.TryRead(out _), "Old queued writes must not execute after replacement.");
        await node.CommandAsync("refresh");
        var fresh = await ReadAsync(plc.Requests);
        Assert.IsTrue(fresh.IsRead);
        fresh.ReplyWithMarkers(true);
        Assert.AreEqual("replacement-marker", (await ReadAsync(node.Reports)).Id);
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HostShutdownCancelsPendingCommandOrScheduledRefresh(bool scheduled)
    {
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync();
        await node.ConfigureAsync(node.ConfigurationFor(plc, scheduled: scheduled));
        if (!scheduled)
            await node.CommandAsync("set");
        var pending = await ReadAsync(plc.Requests);
        Assert.AreEqual(scheduled, pending.IsRead);
        await node.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await pending.Disconnected.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(DeviceState.Stopped, node.Device.State);
        Assert.AreEqual(0, plc.ActiveConnections);
        Assert.IsFalse(node.Reports.TryRead(out _));
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    [TestMethod]
    public async Task LegacyCommandDispatchRemainsSerializedAndShutdownAwaitsRunningCall()
    {
        using var commands = new BlockingLegacyCommands();
        await using var node = await NodeIntegrationHarness.StartAsync(commandService: commands);
        await node.CommandAsync("first");
        Task stopping = null;
        try
        {
            await commands.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await node.CommandAsync("second");
            await node.BarrierAsync();
            Assert.AreEqual(1, commands.Calls);
            stopping = node.StopAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!node.Log.Entries.Any(e => e.Message == "Node MQTT command was cancelled"))
                await Task.Delay(10, deadline.Token);
            Assert.IsFalse(stopping.IsCompleted, "A synchronous call must not be abandoned.");
        }
        finally { commands.Release.Set(); }
        await (stopping ?? node.StopAsync()).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, commands.Calls, "The cancelled queued legacy command must not run.");
        Assert.IsFalse(node.Log.Errors.TryRead(out _));
    }

    [TestMethod]
    public async Task RapidMqttUpdatesAwaitPreviousApplicationAndApplyOnlyLatestConfiguration()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var plc = new SimulatedEasyPlc();
        await using var node = await NodeIntegrationHarness.StartAsync(async (desired, token) =>
        {
            if (desired.Name != "blocked") return;
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
        });
        await node.ConfigureAsync(node.ConfigurationFor(plc));
        try
        {
            await node.ConfigureAsync(node.ConfigurationFor(plc, "blocked"), waitForApplication: false);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await node.ConfigureAsync(node.ConfigurationFor(plc, "intermediate"), waitForApplication: false);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await node.ConfigureAsync(node.ConfigurationFor(plc, "latest"), waitForApplication: false);
            Assert.AreEqual("initial", node.Device.Name);
            CollectionAssert.AreEqual(new[] { "initial" }, node.Applied.ToArray());
        }
        finally { release.TrySetResult(); }
        await node.WaitAppliedAsync("latest");
        Assert.AreEqual("latest", node.Device.Name);
        CollectionAssert.AreEqual(new[] { "initial", "latest" }, node.Applied.ToArray());
        Assert.AreEqual(2, plc.ConnectionCount);
        await node.CommandAsync("refresh");
        (await ReadAsync(plc.Requests)).ReplyWithMarkers(true);
        Assert.AreEqual("latest-marker", (await ReadAsync(node.Reports)).Id);
        Assert.IsFalse(node.Log.Entries.Any(e => e.Level >= LogLevel.Error));
    }

    private sealed class BlockingLegacyCommands : ICommandService, IDisposable
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void ExecuteJsonCommand(string json)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            Release.Wait();
        }
        public void Dispose() => Release.Dispose();
    }
}
