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
    private readonly TestConfiguration _configuration = new();
    private readonly ConcurrentDictionary<string, NodeDeviceConfiguration> _desired = new();
    private readonly Channel<NodeOnlineMessage> _presence = Channel.CreateUnbounded<NodeOnlineMessage>();
    private readonly Channel<string> _loaded = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _applied = Channel.CreateUnbounded<string>();
    private readonly Channel<Report> _reports = Channel.CreateUnbounded<Report>();
    private Task _stop;
    private int _revision;

    public EasyPLC Device { get; }
    public DeviceService Devices { get; }
    public TestLog Log { get; } = new();
    public ChannelReader<Report> Reports => _reports.Reader;
    public ConcurrentQueue<string> Applied { get; } = new();
    public const string NodeId = "integration-node";

    private NodeIntegrationHarness(Func<NodeDeviceConfiguration, CancellationToken, Task> beforeApply, ICommandService commandService)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        _broker = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
            .WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port).Build());
        var apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.Logging.ClearProviders();
        apiBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _api = apiBuilder.Build();
        _api.MapGet("/{revision}" + Constants.ApiConfigurationUrl, (string revision, string id) =>
            id == NodeId && _desired.TryGetValue(revision, out var desired)
                ? Results.Content(Json.Serialize(desired), "application/json")
                : Results.NotFound());

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
        builder.Services.AddSingleton<DeviceConfigurationCoordinator>(_ => new DeviceConfigurationCoordinator(
            _configuration, async (desired, token) =>
            {
                if (beforeApply != null)
                    await beforeApply(desired, token);
                await Devices.ReconfigureDevicesAsync(desired.DeviceConfigurations, token);
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
            else
                _presence.Writer.TryWrite(Json.Deserialize<NodeOnlineMessage>(payload));
            return Task.CompletedTask;
        };
        PeerOptions = new MqttClientOptionsBuilder().WithClientId("test-orchestrator")
            .WithTcpServer("127.0.0.1", port).Build();
    }

    private MqttClientOptions PeerOptions { get; }

    public static async Task<NodeIntegrationHarness> StartAsync(
        Func<NodeDeviceConfiguration, CancellationToken, Task> beforeApply = null, ICommandService commandService = null)
    {
        var harness = new NodeIntegrationHarness(beforeApply, commandService);
        try
        {
            await harness._api.StartAsync();
            await harness._broker.StartAsync();
            await harness._peer.ConnectAsync(harness.PeerOptions);
            await harness._peer.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(Constants.Get(NodeId, MqttTopic.Report))
                .WithTopicFilter(Constants.Get(NodeId, MqttTopic.NodeOnline)).Build());
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness._broker.ClientSubscribedTopicAsync += args =>
            {
                if (args.ClientId == NodeId &&
                    args.TopicFilter.Topic == Constants.Get(NodeId, MqttTopic.OrchestratorOnline))
                    ready.TrySetResult();
                return Task.CompletedTask;
            };
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

    public async Task WaitAppliedAsync(string name) =>
        Assert.AreEqual(name, await ReadAsync(_applied.Reader));

    public Task CommandAsync(string id, bool value = true) =>
        PublishAsync(MqttTopic.Command, Json.Serialize(new { id, value }));

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
        public override NodeConfiguration Configuration { get; } = new()
        {
            Id = NodeId, Mqtt = new MqttConfiguration { ClientId = NodeId, ServerUrl = "127.0.0.1" }
        };
        public TestConfiguration() => OnlineMessage = new NodeOnlineMessage { IsOnline = true, NodeType = NodeType.Device };
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
