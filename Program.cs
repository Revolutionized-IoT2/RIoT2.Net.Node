using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Options;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Hosting;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Core.Security;
using RIoT2.Core.Services;
using RIoT2.Net.Node;
using RIoT2.Net.Node.Services;
using System.Reflection;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    o.JsonSerializerOptions.WriteIndented = true;
});

// Console and Logs/RIoT2.log, text or JSON (RIOT2_LOG_FORMAT, RIOT2_LOG_LEVEL; design 7.4). Loggers
// from this factory forward to the host's once it exists, so nodeLogger, the plugins' loggers and every
// ILogger<T> share providers and log scopes (a command's correlation id reaches the device's lines).
ILoggerFactory logger = builder.AddRIoT2Logging("node");

Microsoft.Extensions.Logging.ILogger nodeLogger = logger.CreateLogger("RIoT2.Net.Node");

// Metrics (riot2.node.*, riot2.mqtt.*) and, with RIOT2_TRACING=true, traces over OTLP, only when
// OTEL_EXPORTER_OTLP_ENDPOINT is set (design 7.4).
builder.AddRIoT2Telemetry("node", builder.Configuration[NodeOptions.IdVariable], RIoT2.Core.Diagnostics.MeterNames.Node);

// Every RIOT2_* setting is read and validated here, before anything uses it; an invalid setting
// stops the start with one message that lists every problem.
var nodeOptions = Options.Create(NodeOptions.Load(builder.Configuration, nodeLogger));
builder.Services.AddSingleton(nodeOptions);

var _configurationService = new ConfigurationService(nodeOptions, nodeLogger, Path.Combine(builder.Environment.ContentRootPath, "Data"));

builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(nodeLogger);
builder.Services.AddSingleton<INodeConfigurationService>(_configurationService);
builder.Services.AddSingleton<ICommandService, CommandService>();
builder.Services.AddSingleton<IReportService, ReportService>();
builder.Services.AddSingleton<INodeMqttService, NodeMqttService>();
builder.Services.AddSingleton<IDeviceService, DeviceService>();
builder.Services.AddSingleton<MqttBackgroundService>();
builder.Services.AddHostedService(p => p.GetRequiredService<MqttBackgroundService>());
builder.Services.AddHostedService<DeviceSchedulerService>();

var pluginsLoaded = false;
builder.Services.AddSingleton(services => new DeviceConfigurationApplier(
    _configurationService,
    (DeviceServiceBase)services.GetRequiredService<IDeviceService>(),
    (configuration, token) => UpdatePluginPackageAsync(services, configuration, token),
    nodeLogger,
    () => services.GetRequiredService<IHostApplicationLifetime>().StopApplication()));
builder.Services.AddSingleton<DeviceConfigurationCoordinator>(services => new DeviceConfigurationCoordinator(
    _configurationService,
    (configuration, token) => services.GetRequiredService<DeviceConfigurationApplier>().ApplyAsync(configuration, token),
    token => ((IAsyncDeviceService)services.GetRequiredService<IDeviceService>()).StopAllDevicesAsync(token),
    nodeLogger));
builder.Services.AddHostedService(services => services.GetRequiredService<DeviceConfigurationCoordinator>());
// Plugin packages and their .sha256 sidecars (design 7.2), within the outbound URL policy (design 7.5).
builder.Services.AddHttpClient("plugins", client => client.Timeout = TimeSpan.FromMinutes(5))
    .AddHttpMessageHandler(services => new OutboundUrlPolicyHandler(services.GetRequiredService<IOutboundUrlPolicy>(), OutboundPurposes.Plugin));
builder.Services.AddHealthChecks().AddCheck<MqttHealthCheck>("mqtt");

// Plugin controllers are built with nullable reference types; the annotations must not make MVC
// require request members that were optional (plan M8 step 9).
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
{
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
    // A plugin action without its own policy gets Viewer (GET) or Operator (design 7.5).
    options.Conventions.Add(new DefaultPolicyConvention());
});

// The optional security mode's seams (design 7.5, phase S0): every endpoint has a policy, which the
// LocalTrust scheme passes while RIOT2_SECURITY_MODE is off (the only mode this version accepts).
builder.Services.AddRIoT2Security(nodeOptions.Value.Security);

// Install the plugin package staged before the last restart (design 7.2). The previous plugins stay in
// Plugins.previous/ until the new ones have loaded.
try
{
    _configurationService.InstallPluginPackage();
}
catch (Exception x)
{
    nodeLogger.LogError(x, "Could not install the staged plugin package; starting with the current plugins.");
}

nodeLogger.LogInformation("Trying to load plugins...");
var pluginLoad = PluginLoader.Load(_configurationService.PluginPackages.PluginsFolder, builder.Services,
    assembly => builder.Services.AddControllers().PartManager.ApplicationParts.Add(new AssemblyPart(assembly)), nodeLogger);
pluginsLoaded = pluginLoad.Loaded > 0;

// A package installed at this start that doesn't load is rolled back once: the node exits, the container's
// restart policy starts it again with the previous plugins, and the package is not installed again. A
// rollback that fails part-way also exits; the next start finishes it.
PluginStartupResult pluginStartup;
try
{
    pluginStartup = _configurationService.PluginPackages.ConfirmStartup(pluginLoad.Succeeded, pluginLoad.Error);
}
catch (Exception x)
{
    nodeLogger.LogCritical(x, "Could not finish the plugin install; restarting to complete it.");
    Environment.ExitCode = PluginLoader.RollbackExitCode;
    logger.Dispose();
    return;
}
switch (pluginStartup.Outcome)
{
    case PluginStartupOutcome.Installed:
        nodeLogger.LogInformation("Plugin package {Package} {Version} installed and loaded.",
            pluginStartup.Package?.InstalledPackageFilename, pluginStartup.Package?.Version);
        break;
    case PluginStartupOutcome.RolledBack:
        nodeLogger.LogCritical("{Reason} Restarting once to load them.", pluginStartup.Message);
        Environment.ExitCode = PluginLoader.RollbackExitCode;
        logger.Dispose(); // flushes the log before the process ends
        return;
    case PluginStartupOutcome.Failed:
        nodeLogger.LogCritical("{Reason} There were no plugins before it, so it stays installed.", pluginStartup.Message);
        break;
}

var app = builder.Build();
nodeLogger.LogInformation($"Services initialized. {app.Services.GetRequiredService<INodeConfigurationService>().Configuration.Manifest?.Name??"n/a"} - {app.Services.GetRequiredService<INodeConfigurationService>().Configuration.Manifest?.Version??"n/a"}");

if (pluginsLoaded)
{
    var package = app.Services.GetRequiredService<INodeConfigurationService>().Configuration.PluginManifest;
    nodeLogger.LogInformation($"Plugins loaded successfully: {package?.InstalledPackageFilename}. Version: {package?.Version}");

    var deviceService = app.Services.GetRequiredService<IDeviceService>();
    var devices = app.Services.GetServices<IDevice>();
    deviceService.Devices.AddRange(devices);
    nodeLogger.LogInformation($"Found {devices.Count()} devices from plugins.");
}
else
{
    nodeLogger.LogCritical("NO PLUGINS LOADED. Starting without plugins...");
}

_configurationService.ConfigurationStatus.SetPluginManifest(_configurationService.Configuration.PluginManifest);

// Start with the last applied configuration, so the devices run even while the orchestrator is down (design 7.2).
try
{
    var saved = _configurationService.LoadAppliedConfiguration();
    if (saved == null)
        nodeLogger.LogInformation("No saved configuration; waiting for the orchestrator.");
    else
        nodeLogger.LogInformation("Starting with the saved configuration, revision {Revision}.", saved.Revision?.ToString() ?? "n/a");
}
catch (Exception x)
{
    nodeLogger.LogError(x, "Could not read the saved configuration; waiting for the orchestrator.");
}

var url = app.Services.GetRequiredService<INodeConfigurationService>().Configuration.Url;
app.Services.GetRequiredService<INodeConfigurationService>().OnlineMessage = new NodeOnlineMessage()
{
    NodeBaseUrl = url,
    NodeType = NodeType.Device,
    IsOnline = true,
    Manifest = app.Services.GetRequiredService<INodeConfigurationService>().Configuration?.Manifest,
    PluginManifest = app.Services.GetRequiredService<INodeConfigurationService>().Configuration?.PluginManifest
};

// Called before every configuration is applied (design 7.2): checks the configured plugin package against
// the installed one and stages a changed, verified, compatible package. The applier restarts the node for
// a staged package, and reports a refused one as failed.
async Task<PluginUpdateResult> UpdatePluginPackageAsync(IServiceProvider services, NodeDeviceConfiguration? desired, CancellationToken cancellationToken)
{
    nodeLogger.LogInformation("Device configuration updated.");
    if (String.IsNullOrEmpty(desired?.PluginPackageUrl))
    {
        nodeLogger.LogWarning("No plugin package URL in the configuration; keeping the installed plugins.");
        return PluginUpdateResult.Unchanged;
    }

    var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("plugins");
    var result = await _configurationService.PluginPackages.UpdateAsync(desired.PluginPackageUrl,
        _configurationService.Configuration.PluginManifest, pluginsLoaded, (request, token) => client.SendAsync(request, token), cancellationToken);
    switch (result.Outcome)
    {
        case PluginUpdateOutcome.Staged:
            nodeLogger.LogWarning("New plugin package {Package} {Version} staged; restarting the node to install it.",
                result.Package?.InstalledPackageFilename, result.Package?.Version);
            break;
        case PluginUpdateOutcome.Rejected:
            nodeLogger.LogError("Plugin package {Url} refused: {Reason}", desired.PluginPackageUrl, result.Message);
            break;
        case PluginUpdateOutcome.Unavailable:
            nodeLogger.LogWarning("Plugin package {Url} not checked: {Reason}", desired.PluginPackageUrl, result.Message);
            break;
    }
    return result;
}

app.UseRIoT2Security();

app.MapGet("/api/node/manifest", (INodeConfigurationService configuration) =>
{
    return Results.Ok(configuration.Configuration.Manifest);
}).RequireAuthorization(SecurityPolicies.Viewer);

app.MapGet("/api/node/plugin/manifest", (INodeConfigurationService configuration) =>
{
    return Results.Ok(configuration.Configuration.PluginManifest);
}).RequireAuthorization(SecurityPolicies.Viewer);

//Provides state information on each device
app.MapGet("/api/device/status", (IDeviceService deviceService, Microsoft.Extensions.Logging.ILogger logger) =>
{
    List<DeviceStatus> states = [];
    foreach (var d in deviceService.Devices)
    {
        if (d.State == RIoT2.Core.DeviceState.Unknown)
            continue;
       
        states.Add(new DeviceStatus() {
            Id = d.Id,
            Name = d.Name,
            Message = d.StateMessage,
            State = d.State
        });
    }

    return Results.Ok(states);
}).RequireAuthorization(SecurityPolicies.Viewer);

//Provides configuration templates 
app.MapGet("/api/device/configuration/templates", (IDeviceService deviceService, Microsoft.Extensions.Logging.ILogger logger) =>
{
    List<DeviceConfiguration> templates = [];
    foreach (var d in deviceService.Devices)
    {
        try
        {
            templates.Add(DeviceTemplates.Create(d));
        }
        catch (Exception x)
        {
            logger.LogError(x, $"Error while getting configuration template for device: {d.Name} [{d.Id}]");
        }
    }
    return Results.Ok(templates);
}).RequireAuthorization(SecurityPolicies.Viewer);

app.MapHealthChecks("/health").AllowAnonymous();

if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

//Run this to map plugin controllers as well
try
{
    app.MapControllers();
}
catch (Exception x) 
{
    nodeLogger.LogError($"Error in mapping controllers: {x.Message}", x.Message);
}

app.Run();