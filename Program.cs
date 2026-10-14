using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Options;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Core.Services;
using RIoT2.Net.Node;
using RIoT2.Net.Node.Services;
using Serilog;
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

var serilog = new LoggerConfiguration()
   .WriteTo.Console()
   .WriteTo.File("Logs/RIoT2.log", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:dd.MM.yyyy HH:mm:ss.fff} [{Level}] {Message:lj}{NewLine}{Exception}", shared: true)
   .CreateLogger();


ILoggerFactory logger = LoggerFactory.Create(log =>
{
    log.AddSerilog(serilog);

});

Microsoft.Extensions.Logging.ILogger nodeLogger = logger.CreateLogger("RIoT2.Net.Node");

// Every RIOT2_* setting is read and validated here, before anything uses it; an invalid setting
// stops the start with one message that lists every problem.
var nodeOptions = Options.Create(NodeOptions.Load(builder.Configuration, nodeLogger));
builder.Services.AddSingleton(nodeOptions);

//builder.Host.UseSerilog((ctx, lc) => lc
//    .WriteTo.Console()
//    .WriteTo.File("Logs/RIoT2.log", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:dd.MM.yyyy HH:mm:ss.fff} [{Level}] {Message:lj}{NewLine}{Exception}", shared: true));

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
    nodeLogger));
builder.Services.AddSingleton<DeviceConfigurationCoordinator>(services => new DeviceConfigurationCoordinator(
    _configurationService,
    (configuration, token) => services.GetRequiredService<DeviceConfigurationApplier>().ApplyAsync(configuration, token),
    token => ((IAsyncDeviceService)services.GetRequiredService<IDeviceService>()).StopAllDevicesAsync(token),
    nodeLogger));
builder.Services.AddHostedService(services => services.GetRequiredService<DeviceConfigurationCoordinator>());
builder.Services.AddHttpClient("plugin-metadata", client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHealthChecks().AddCheck<MqttHealthCheck>("mqtt");

_configurationService.InstallPluginPackage();
loadPlugins();

var app = builder.Build();
nodeLogger.LogInformation($"Services initialized. {app.Services.GetService<INodeConfigurationService>().Configuration.Manifest?.Name??"n/a"} - {app.Services.GetService<INodeConfigurationService>().Configuration.Manifest?.Version??"n/a"}");

if (pluginsLoaded)
{
    var package = app.Services.GetService<INodeConfigurationService>().Configuration.PluginManifest;
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

var url = app.Services.GetService<INodeConfigurationService>().Configuration.Url;
app.Services.GetService<INodeConfigurationService>().OnlineMessage = new NodeOnlineMessage()
{
    NodeBaseUrl = url,
    NodeType = NodeType.Device,
    IsOnline = true,
    Manifest = app.Services.GetService<INodeConfigurationService>().Configuration?.Manifest,
    PluginManifest = app.Services.GetService<INodeConfigurationService>().Configuration?.PluginManifest
};

void loadPlugins()
{
    nodeLogger.LogInformation("Trying to load plugins...");
    try
    {
        var pluginDirectory = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "Plugins"));
        var pluginFiles = pluginDirectory?.GetFiles("*.dll");

        if (pluginFiles != null && pluginFiles.Count() > 0)
        {
            foreach (var pluginFileInfo in pluginFiles)
            {
                //Load and add controllers
                PluginLoadContext loadContext = new PluginLoadContext(pluginFileInfo.FullName);
                Assembly pluginAssembly = loadContext.LoadFromAssemblyName(AssemblyName.GetAssemblyName(pluginFileInfo.FullName));

                //Initialize plugin
                var atypes = pluginAssembly.GetTypes();
                // Abstract types such as Core's PluginBase implement the interface too, but aren't plugins.
                var pluginClass = atypes.SingleOrDefault(t => !t.IsAbstract && t.GetInterface(nameof(IDevicePlugin)) != null);
                if (pluginClass != null)
                {
                    try
                    {
                        //load controllers
                        var part = new AssemblyPart(pluginAssembly);
                        builder.Services.AddControllers().PartManager.ApplicationParts.Add(part);

                        //load plugin itself
                        var initMethod = pluginClass.GetMethod(nameof(IDevicePlugin.Initialize), BindingFlags.Public | BindingFlags.Instance);
                        var obj = Activator.CreateInstance(pluginClass) as IDevicePlugin;
                        initMethod.Invoke(obj, [builder.Services]);
                        pluginsLoaded = true;
                    }
                    catch (Exception x)
                    {
                        nodeLogger.LogError(x, $"Failed loading plugin {pluginClass.GetType().FullName}: {x.Message}");
                    }
                }
            }
        }
    }
    catch (Exception x)
    {
        nodeLogger.LogError(x, $"Error while loading device plugins: {x.Message}");
        pluginsLoaded = false;
    }
}

// Called before every configuration is applied. Returns true when a new plugin package was downloaded
// and the node is restarting to install it.
async Task<bool> UpdatePluginPackageAsync(IServiceProvider services, NodeDeviceConfiguration desired, CancellationToken cancellationToken)
{
    nodeLogger.LogInformation("Device configuration updated.");
    var configurationService = services.GetRequiredService<INodeConfigurationService>();

    if (!String.IsNullOrEmpty(desired?.PluginPackageUrl))
    {
        try
        {
            using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("plugin-metadata");
            using var request = new HttpRequestMessage(HttpMethod.Head, desired.PluginPackageUrl);
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var pluginPackageMetadata = response.Content.Headers;
            if (!String.IsNullOrEmpty(pluginPackageMetadata?.ContentDisposition?.FileName))
            {
                if (!String.IsNullOrEmpty(configurationService.Configuration?.PluginManifest?.InstalledPackageFilename) || !pluginsLoaded)
                {
                    if (pluginPackageMetadata.ContentDisposition.FileName != configurationService.Configuration?.PluginManifest?.InstalledPackageFilename)
                    {
                        nodeLogger.LogInformation($"New plugin package available: {pluginPackageMetadata.ContentDisposition.FileName}. Downloading and saving.");
                        cancellationToken.ThrowIfCancellationRequested();
                        await configurationService.DownloadPluginPackageAsync(desired.PluginPackageUrl);
                        nodeLogger.LogWarning($"Exiting to restart node and load new plugin package...");
                        services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                        return true;
                    }
                }
            }
            else
            {
                nodeLogger.LogWarning($"Could not fetch plugin package data from Url: {desired.PluginPackageUrl}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception x)
        {
            nodeLogger.LogError(x, $"Error while getting plugin package metadata: {x.Message}");
        }
    }
    else 
    {
        nodeLogger.LogWarning("Could not load plugin package. No device configuration available Or no plugin package URL specified.");
    }
    return false;
}

app.MapGet("/api/node/manifest", (INodeConfigurationService configuration) =>
{
    return Results.Ok(configuration.Configuration.Manifest);
});

app.MapGet("/api/node/plugin/manifest", (INodeConfigurationService configuration) =>
{
    return Results.Ok(configuration.Configuration.PluginManifest);
});

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
});

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
});

app.MapHealthChecks("/health");

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