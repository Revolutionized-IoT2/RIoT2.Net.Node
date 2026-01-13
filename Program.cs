using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Services;
using RIoT2.Core.Interfaces.Services;
using Serilog;
using System.Text.Json;
using System.Reflection;
using RIoT2.Net.Node;
using RIoT2.Net.Node.Services;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

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

//builder.Host.UseSerilog((ctx, lc) => lc
//    .WriteTo.Console()
//    .WriteTo.File("Logs/RIoT2.log", rollingInterval: RollingInterval.Day, outputTemplate: "{Timestamp:dd.MM.yyyy HH:mm:ss.fff} [{Level}] {Message:lj}{NewLine}{Exception}", shared: true));

builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(nodeLogger);
builder.Services.AddSingleton<INodeConfigurationService, ConfigurationService>();
builder.Services.AddSingleton<ICommandService, CommandService>();
builder.Services.AddSingleton<IReportService, ReportService>();
builder.Services.AddSingleton<INodeMqttService, NodeMqttService>();
builder.Services.AddSingleton<IDeviceService, DeviceService>();
builder.Services.AddSingleton<MqttBackgroundService>();
builder.Services.AddHostedService(p => p.GetRequiredService<MqttBackgroundService>());
builder.Services.AddHostedService<DeviceSchedulerService>();

var pluginsLoaded = false;

var deviceList = new List<IDevice>();

//load plugins
try
{
    var pluginDirectory = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "Plugins"));
    var pluginFiles = pluginDirectory?.GetFiles("*.dll");

    if (pluginFiles != null && pluginFiles.Count() > 0)
    {
        foreach(var pluginFileInfo in pluginFiles) 
        {
            //Load and add controllers
            PluginLoadContext loadContext = new PluginLoadContext(pluginFileInfo.FullName);
            Assembly pluginAssembly = loadContext.LoadFromAssemblyName(AssemblyName.GetAssemblyName(pluginFileInfo.FullName));

            //Initialize plugin
            var atypes = pluginAssembly.GetTypes();
            var pluginClass = atypes.SingleOrDefault(t => t.GetInterface(nameof(IDevicePlugin)) != null);
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
                    initMethod.Invoke(obj, new object[] { builder.Services });
                    deviceList.AddRange(obj.Devices);
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

if (!pluginsLoaded)
{
    nodeLogger.LogCritical("No plugins loaded. Terminating node...");
    Environment.Exit(-1);
}

var app = builder.Build();

nodeLogger.LogInformation("Services initialized. Starting node.");

var deviceService = app.Services.GetRequiredService<IDeviceService>();
deviceService.Devices.AddRange(deviceList);
nodeLogger.LogInformation($"Found {deviceList.Count} devices from plugins.");

IHostApplicationLifetime lifetime = app.Lifetime;
lifetime.ApplicationStopping.Register(onShutdown);

void onShutdown() //this code is called when the application stops
{
    var mqttService = app.Services.GetRequiredService<MqttBackgroundService>();
    mqttService.StopAsync(default).Wait();
}

//var mqttService = app.Services.GetRequiredService<MqttBackgroundService>();

//Call nodeonline message once application has fully started
lifetime.ApplicationStarted.Register(() => {

    var configuration = app.Services.GetRequiredService<INodeConfigurationService>();
    configuration.DeviceConfigurationUpdated += _configuration_DeviceConfigurationUpdated;

    //Orchestrator online message is retained -> no need to send online message here
    //await mqttService.SendNodeOnlineMessage();

    //TODO create example file for local configuration
#if DEBUG   //if debug -> load local configuration file, instead waiting command from orchestrator
    app.Services.GetService<INodeConfigurationService>().LoadDeviceConfiguration("", "").Wait();
#endif
});

var url = app.Services.GetService<INodeConfigurationService>().Configuration.Url;
app.Services.GetService<INodeConfigurationService>().OnlineMessage = new NodeOnlineMessage()
{
    ConfigurationTemplateUrl = url + "/api/device/configuration/templates",
    DeviceStateUrl = url + "/api/device/status"
};

//this is called when node receives a new configuration for devices
void _configuration_DeviceConfigurationUpdated()
{
    var deviceService = app.Services.GetRequiredService<IDeviceService>();
    deviceService.StopAllDevices();
    deviceService.ConfigureDevices();
    deviceService.StartAllDevices(true);
}

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
        if (d is IDeviceWithConfiguration)
        {
            try
            {
                var template = (d as IDeviceWithConfiguration).GetConfigurationTemplate();
                templates.Add(template);
            }
            catch (Exception x)
            {
                logger.LogError(x, $"Error while getting configuration template for device: {d.Name} [{d.Id}]");
            }
        }
        else //Add default configuration template
        {
            templates.Add(new DeviceConfiguration()
            {
                ClassFullName = d.GetType().FullName,
                Id = Guid.NewGuid().ToString(),
                Name = d.GetType().Name,
                RefreshSchedule = (d is IRefreshableReportDevice) ? "0 * * * *" : null,
                CommandTemplates = (d is ICommandDevice) ? [] : null,
                ReportTemplates = [],
                DeviceParameters = []
            });
        }
    }
    return Results.Ok(templates);
});

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