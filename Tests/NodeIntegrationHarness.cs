using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Services;
using RIoT2.Core.Utils;
using RIoT2.Net.Devices.Catalog;
using RIoT2.Net.Node.Services;
using NodeMqttClient = RIoT2.Core.Utils.MqttClient;

namespace RIoT2.Net.Node.Tests;

internal sealed class NodeIntegrationHarness : IAsyncDisposable
{
    private readonly MqttServer _broker;
    private readonly IMqttClient _peer = new MqttFactory().CreateMqttClient();
    private readonly WebApplication _api;
    private readonly IHost _host;
    private readonly TestConfiguration _configuration;
    private readonly ConcurrentDictionary<string, NodeDeviceConfiguration> _desired = new();
    private readonly Channel<NodeOnlineMessage> _presence = Channel.CreateUnbounded<NodeOnlineMessage>();
    private readonly Channel<string> _loaded = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _applied = Channel.CreateUnbounded<string>();
    private readonly Channel<Report> _reports = Channel.CreateUnbounded<Report>();
    private readonly Channel<CommandResult> _results = Channel.CreateUnbounded<CommandResult>();
    private readonly Channel<NodeStatusMessage> _statuses = Channel.CreateUnbounded<NodeStatusMessage>();
    private readonly DeviceConfigurationApplier _applier;
    private Task _stop;
    private int _revision;
    private int _fetches;
    private readonly int _port;

    public EasyPLC Device { get; }
    public DeviceService Devices { get; }
    public TestLog Log { get; } = new();
    public ChannelReader<Report> Reports => _reports.Reader;
    public ChannelReader<CommandResult> CommandResults => _results.Reader;
    /// <summary>Every status message the node published on riot2/node/{id}/status (design 7.2).</summary>
    public ChannelReader<NodeStatusMessage> Statuses => _statuses.Reader;
    /// <summary>Configuration requests that reached the API, including 304 answers.</summary>
    public int Fetches => Volatile.Read(ref _fetches);
    /// <summary>Folder of the node's saved configuration; pass it to the next harness to restart the node.</summary>
    public string DataFolder { get; }
    public ConcurrentQueue<string> Applied { get; } = new();
    public ConcurrentQueue<DeviceReconfigurationResult> Reconfigurations { get; } = new();
    public const string NodeId = "integration-node";

    private NodeIntegrationHarness(Func<NodeDeviceConfiguration, CancellationToken, Task> beforeApply, ICommandService commandService, string dataFolder)
    {
        DataFolder = dataFolder ?? Path.Combine(Path.GetTempPath(), "riot2-node-" + Guid.NewGuid().ToString("N"));
        _configuration = new TestConfiguration(DataFolder);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = _port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        _broker = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port).Build());
        var apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _api = apiBuilder.Build();
        // Answers like the orchestrator: an ETag for a stamped configuration, and 304 for If-None-Match.
        _api.MapGet("/{revision}" + Constants.ApiConfigurationUrl, (string revision, string id, HttpContext context) =>
        {
            Interlocked.Increment(ref _fetches);
            if (id != NodeId || !_desired.TryGetValue(revision, out var desired))
                return Results.NotFound();
            if (desired.Hash != null)
            {
                var etag = $"\"{desired.Hash}\"";
                if (context.Request.Headers.IfNoneMatch.ToString() == etag)
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                context.Response.Headers.ETag = etag;
            }
            return Results.Content(Json.Serialize(desired), "application/json");
        });

        Device = new EasyPLC(Log);
        Devices = new DeviceService(_configuration, Log);
        Devices.Devices.Add(Device);
        _configuration.DeviceConfigurationUpdated += () =>
            _loaded.Writer.TryWrite(_configuration.DeviceConfiguration.Name);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILogger>(Log);
        builder.Services.AddSingleton<INodeConfigurationService>(_configuration);
        builder.Services.AddSingleton<IDeviceService>(Devices);
        if (commandService == null)
            builder.Services.AddSingleton<ICommandService, CommandService>();
        else
            builder.Services.AddSingleton(commandService);
        builder.Services.AddSingleton<IReportService, ReportService>();
        builder.Services.AddSingleton<INodeMqttService>(services => new NodeMqttService(
            _configuration, services.GetRequiredService<ICommandService>(),
            services.GetRequiredService<IReportService>(), Log,
            () => new NodeMqttClient(NodeId, "127.0.0.1", "", "", port)));
        builder.Services.AddHostedService<MqttBackgroundService>();
        builder.Services.AddHostedService<DeviceSchedulerService>();
        _applier = new DeviceConfigurationApplier(_configuration, Devices, null, Log);
        builder.Services.AddSingleton<DeviceConfigurationCoordinator>(_ => new DeviceConfigurationCoordinator(
            _configuration, async (desired, token) =>
            {
                if (beforeApply != null)
                    await beforeApply(desired, token);
                var result = await _applier.ApplyAsync(desired, token);
                Reconfigurations.Enqueue(result);
                Applied.Enqueue(desired.Name);
                _applied.Writer.TryWrite(desired.Name);
            }, token => Devices.StopAllDevicesAsync(token), Log));
        builder.Services.AddHostedService(services => services.GetRequiredService<DeviceConfigurationCoordinator>());
        _host = builder.Build();

        _peer.ApplicationMessageReceivedAsync += args =>
        {
            var payload = args.ApplicationMessage.ConvertPayloadToString();
            if (args.ApplicationMessage.Topic == Constants.Get(NodeId, MqttTopic.Report))
                _reports.Writer.TryWrite(Json.Deserialize<Report>(payload));
            else if (args.ApplicationMessage.Topic == Constants.Get(NodeId, MqttTopic.CommandResult))
                _results.Writer.TryWrite(CommandResult.Create(payload));
            else if (args.ApplicationMessage.Topic == Constants.Get(NodeId, MqttTopic.NodeStatus))
            {
                RetainedStatus = args.ApplicationMessage.Retain;
                _statuses.Writer.TryWrite(NodeStatusMessage.Create(payload));
            }
            else if (args.ApplicationMessage.Topic == Constants.Get(NodeId, MqttTopic.NodeOnline))
            {
                RetainedPresence = args.ApplicationMessage.Retain;
                _presence.Writer.TryWrite(Json.Deserialize<NodeOnlineMessage>(payload));
            }
            return Task.CompletedTask;
        };
        PeerOptions = new MqttClientOptionsBuilder().WithClientId("test-orchestrator")
            .WithTcpServer("127.0.0.1", port).Build();
    }

    private MqttClientOptions PeerOptions { get; }

    /// <summary>Whether the broker delivered the last presence or status message as retained.</summary>
    public bool RetainedPresence { get; private set; }
    public bool RetainedStatus { get; private set; }

    /// <param name="dataFolder">A previous harness's <see cref="DataFolder"/>: the node starts with the configuration it saved, as Program does.</param>
    public static async Task<NodeIntegrationHarness> StartAsync(
        Func<NodeDeviceConfiguration, CancellationToken, Task> beforeApply = null, ICommandService commandService = null,
        string dataFolder = null)
    {
        var harness = new NodeIntegrationHarness(beforeApply, commandService, dataFolder);
        try
        {
            await harness._api.StartAsync();
            await harness._broker.StartAsync();
            await harness._peer.ConnectAsync(harness.PeerOptions);
            await harness._peer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(Constants.Get(NodeId, MqttTopic.Report))
                .WithTopicFilter(Constants.Get(NodeId, MqttTopic.CommandResult))
                .WithTopicFilter(Constants.Get(NodeId, MqttTopic.NodeOnline))
                .WithTopicFilter(Constants.Get(NodeId, MqttTopic.NodeStatus)).Build());
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness._broker.ClientSubscribedTopicAsync += args =>
            {
                if (args.ClientId == NodeId &&
                    args.TopicFilter.Topic == Constants.Get(NodeId, MqttTopic.OrchestratorOnline))
                    ready.TrySetResult();
                return Task.CompletedTask;
            };
            if (dataFolder != null)
                harness._configuration.LoadAppliedConfiguration();
            await harness._host.StartAsync();
            await ReadAsync(harness._presence.Reader);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return harness;
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public NodeDeviceConfiguration ConfigurationFor(SimulatedEasyPlc plc, string name = "initial", bool scheduled = false) => new()
    {
        Id = NodeId, Name = name,
        DeviceConfigurations =
        [
            new DeviceConfiguration
            {
                Id = "plc", Name = name, ClassFullName = typeof(EasyPLC).FullName,
                DeviceParameters = new() { ["ipAddress"] = "127.0.0.1", ["port"] = plc.Port.ToString() },
                CommandTemplates =
                [
                    new CommandTemplate { Id = "set", Address = "M-1-0-1" },
                    new CommandTemplate { Id = "refresh", Address = "refresh" }
                ],
                ReportTemplates = [new ReportTemplate { Id = name + "-marker", Address = "M-1-0-1" }],
                RefreshSchedule = scheduled ? "* * * * * ?" : null
            }
        ]
    };

    public async Task ConfigureAsync(NodeDeviceConfiguration desired, bool waitForApplication = true)
    {
        var revision = Interlocked.Increment(ref _revision).ToString();
        _desired[revision] = desired;
        await PublishAsync(MqttTopic.Configuration, Json.Serialize(new ConfigurationCommand
        {
            ApiBaseUrl = _api.Urls.Single() + "/" + revision
        }));
        Assert.AreEqual(desired.Name, await ReadAsync(_loaded.Reader));
        if (waitForApplication)
            await WaitAppliedAsync(desired.Name);
    }

    /// <summary>
    /// Serves <paramref name="desired"/> stamped as Orchestrator 1.5.0 does (revision and hashes) and
    /// sends the configuration command with its hash. Returns the stamped configuration.
    /// </summary>
    public async Task<NodeDeviceConfiguration> SendDesiredAsync(NodeDeviceConfiguration desired, int revision)
    {
        desired.Revision = revision;
        desired.Hash = ConfigurationHash.Stamp(desired);
        _desired[revision.ToString()] = desired;
        await ResendAsync(desired);
        return desired;
    }

    /// <summary>Sends the configuration command for an already served configuration again, as the orchestrator does on every presence.</summary>
    public Task ResendAsync(NodeDeviceConfiguration desired) =>
        PublishAsync(MqttTopic.Configuration, Json.SerializeIgnoreNulls(new ConfigurationCommand
        {
            ApiBaseUrl = _api.Urls.Single() + "/" + desired.Revision, Revision = desired.Revision, Hash = desired.Hash
        }));

    /// <summary>Sends a configuration command for a revision the API doesn't have, so the fetch fails.</summary>
    public Task SendUnavailableAsync(int revision) =>
        PublishAsync(MqttTopic.Configuration, Json.SerializeIgnoreNulls(new ConfigurationCommand
        {
            ApiBaseUrl = _api.Urls.Single() + "/" + revision, Revision = revision, Hash = "unavailable-" + revision
        }));

    /// <summary>Reads status messages until one matches <paramref name="match"/>.</summary>
    public async Task<NodeStatusMessage> StatusAsync(Func<NodeStatusMessage, bool> match)
    {
        while (true)
        {
            var status = await ReadAsync(_statuses.Reader);
            if (match(status))
                return status;
        }
    }

    /// <summary>Drops the node's MQTT connection and returns its presence once it has reconnected (about five seconds).</summary>
    public async Task<NodeOnlineMessage> ReconnectNodeAsync()
    {
        while (_presence.Reader.TryRead(out _)) { }
        await _broker.DisconnectClientAsync(NodeId, MqttDisconnectReasonCode.AdministrativeAction);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var presence = await _presence.Reader.ReadAsync(deadline.Token);
            if (presence.IsOnline)
                return presence;
        }
    }

    /// <summary>What a client that subscribes now receives on <paramref name="topic"/>: the retained payload, or <c>null</c>.</summary>
    public async Task<string> ReadRetainedAsync(MqttTopic topic)
    {
        using var late = new MqttFactory().CreateMqttClient();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        late.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Retain)
                received.TrySetResult(args.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await late.ConnectAsync(new MqttClientOptionsBuilder().WithClientId("late-subscriber-" + Guid.NewGuid().ToString("N"))
            .WithTcpServer("127.0.0.1", _port).Build());
        await late.SubscribeAsync(Constants.Get(NodeId, topic), MqttQualityOfServiceLevel.AtLeastOnce);
        try { return await received.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { return null; }
        finally { await late.DisconnectAsync(); }
    }

    public void DrainStatuses()
    {
        while (_statuses.Reader.TryRead(out _)) { }
    }

    public async Task WaitAppliedAsync(string name) =>
        Assert.AreEqual(name, await ReadAsync(_applied.Reader));

    public Task CommandAsync(string id, bool value = true) =>
        PublishAsync(MqttTopic.Command, Json.Serialize(new { id, value }));

    /// <summary>A command as the orchestrator's outbox sends it (design 7.1).</summary>
    public Task CorrelatedCommandAsync(string id, string correlationId, bool value = true) =>
        PublishAsync(MqttTopic.Command, Json.SerializeIgnoreNulls(new Command { Id = id, Value = new ValueModel(value), CorrelationId = correlationId }));

    /// <summary>Makes the node announce itself again and returns its presence message.</summary>
    public async Task<NodeOnlineMessage> PresenceAsync()
    {
        while (_presence.Reader.TryRead(out _)) { }
        await PublishAsync(MqttTopic.OrchestratorOnline, "{}");
        return await ReadAsync(_presence.Reader);
    }

    public async Task BarrierAsync()
    {
        while (_presence.Reader.TryRead(out _)) { }
        await PublishAsync(MqttTopic.OrchestratorOnline, "{}");
        Assert.IsTrue((await ReadAsync(_presence.Reader)).IsOnline);
    }

    private async Task PublishAsync(MqttTopic topic, string payload)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _peer.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(Constants.Get(NodeId, topic)).WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce).Build(), deadline.Token);
    }

    public Task StopAsync() => _stop ??= _host.StopAsync();

    public static async Task<T> ReadAsync<T>(ChannelReader<T> reader)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await reader.ReadAsync(deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally
        {
            try
            {
                await ((IAsyncDisposable)_host).DisposeAsync();
                await Devices.DisposeAsync();
            }
            finally
            {
                _peer.Dispose();
                await _broker.StopAsync(new MqttServerStopOptions());
                _broker.Dispose();
                await _api.DisposeAsync();
            }
        }
    }

    private sealed class TestConfiguration : NodeConfigurationServiceBase
    {
        private readonly string _folder;
        public override NodeConfiguration Configuration { get; } = new()
        {
            Id = NodeId, Mqtt = new MqttConfiguration { ClientId = NodeId, ServerUrl = "127.0.0.1" }
        };
        public TestConfiguration(string folder)
        {
            _folder = folder;
            OnlineMessage = new NodeOnlineMessage { IsOnline = true, NodeType = NodeType.Device };
        }
        protected override string AppliedConfigurationPath => Path.Combine(_folder, "configuration.applied.json");
        public override Task LoadDeviceConfiguration(string json, string id) =>
            string.IsNullOrEmpty(json) ? Task.CompletedTask : base.LoadDeviceConfiguration(json, id);
    }

    internal sealed class TestLog : ILogger<NodeMqttService>
    {
        public record Entry(LogLevel Level, string Message, Exception Error);
        private readonly Channel<Entry> _errors = Channel.CreateUnbounded<Entry>();
        public ChannelReader<Entry> Errors => _errors.Reader;
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception error, Func<TState, Exception, string> formatter)
        {
            var entry = new Entry(level, formatter(state, error), error);
            Entries.Enqueue(entry);
            if (level >= LogLevel.Error) _errors.Writer.TryWrite(entry);
        }
    }
}
