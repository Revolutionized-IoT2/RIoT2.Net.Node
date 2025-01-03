using Microsoft.AspNetCore.Mvc;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Models;
using RIoT2.Core.Services;
using RIoT2.Core.Services.FTP;
using RIoT2.Core.Interfaces.Services;
using Serilog;
using System.Text.Json;
using System.Reflection;
using RIoT2.Net.Node;
using RIoT2.Net.Node.Services;

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
builder.Services.AddSingleton<IMemoryStorageService, MemoryStorageService>();
builder.Services.AddSingleton<ICommandService, CommandService>();
builder.Services.AddSingleton<IReportService, ReportService>();
builder.Services.AddSingleton<INodeMqttService, NodeMqttService>();
builder.Services.AddSingleton<IWebhookService, WebhookService>();
builder.Services.AddSingleton<IFtpService, FtpService>();
builder.Services.AddSingleton<IStorageService, FTPStorageService>();
builder.Services.AddSingleton<IDownloadService, DownloadService>();
builder.Services.AddSingleton<IAzureRelayService, AzureRelayService>();
builder.Services.AddSingleton<IDeviceService, DeviceService>();
builder.Services.AddSingleton<MqttBackgroundService>();
builder.Services.AddHostedService(p => p.GetRequiredService<MqttBackgroundService>());
builder.Services.AddHostedService<DeviceSchedulerService>();

var app = builder.Build();

nodeLogger.LogInformation("Services initialized. Starting node.");


//Plugins
try
{
    List<IDevicePlugin> devicePlugins = null;
    var pluginDirectory = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "Plugins"));
    var pluginFiles = pluginDirectory?.GetFiles("*.dll");

    if (pluginFiles != null && pluginFiles.Count() > 0)
    {
        devicePlugins = pluginFiles.SelectMany(pluginFileInfo =>
        {
            Assembly pluginAssembly = LoadPlugin(pluginFileInfo.FullName);
            return CreateDevicePlugins(pluginAssembly, app.Services);
        }).ToList();
    }
    else 
    {
        nodeLogger.LogWarning("No Plugins loaded");
    }

    if (devicePlugins != null && devicePlugins.Count() > 0)
    {
        var deviceService = app.Services.GetRequiredService<IDeviceService>();
        foreach (var plugin in devicePlugins)
            deviceService.Devices.AddRange(plugin.Devices);
    }
}
catch(Exception x)
{
    nodeLogger.LogError(x, $"Error while loading device plugins: {x.Message}");
}

static Assembly LoadPlugin(string path)
{
    PluginLoadContext loadContext = new PluginLoadContext(path);
    return loadContext.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
}

static IEnumerable<IDevicePlugin> CreateDevicePlugins(Assembly assembly, IServiceProvider services)
{
    int count = 0;

    foreach (Type type in assembly.GetTypes())
    {
        if (typeof(IDevicePlugin).IsAssignableFrom(type))
        {
            IDevicePlugin result = Activator.CreateInstance(type, services) as IDevicePlugin;
            if (result != null)
            {
                count++;
                yield return result;
            }
        }
    }

    if (count == 0)
    {
        string availableTypes = string.Join(",", assembly.GetTypes().Select(t => t.FullName));
        throw new ApplicationException(
            $"Can't find any type which implements IDevicePlugin in {assembly} from {assembly.Location}.\n" +
            $"Available types: {availableTypes}");
    }
}
//end Plugins

IHostApplicationLifetime lifetime = app.Lifetime;
lifetime.ApplicationStopping.Register(onShutdown);

void onShutdown() //this code is called when the application stops
{
    var mqttService = app.Services.GetRequiredService<MqttBackgroundService>();
    mqttService.StopAsync(default).Wait();
}

var mqttService = app.Services.GetRequiredService<MqttBackgroundService>();

//Call nodeonline message once application has fully started
lifetime.ApplicationStarted.Register(async () => {

    var configuration = app.Services.GetRequiredService<INodeConfigurationService>();
    configuration.DeviceConfigurationUpdated += _configuration_DeviceConfigurationUpdated;

    await mqttService.SendNodeOnlineMessage();

    //TODO create example file for local configuration
#if DEBUG   //if debug -> load local configuration file, instead waiting command from orchestrator
    app.Services.GetService<INodeConfigurationService>().LoadDeviceConfiguration("", "").Wait();
#endif


}
);

var url = Environment.GetEnvironmentVariable("RIOT2_NODE_URL");

//Configure image service..
app.Services.GetService<IDownloadService>().SetBaseUrl(url + "/api/image/");

//prepare nodeonline message
app.Services.GetService<INodeConfigurationService>().OnlineMessage = new NodeOnlineMessage()
{
    ConfigurationTemplateUrl = url + "/api/device/configuration/templates",
    WebhookUrl = url + "/api/webhook",
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

// Configure the HTTP request pipeline.

/*
app.MapGet("/todoitems/{id}", async (int id, TodoDb db) =>
    await db.Todos.FindAsync(id)
        is Todo todo
            ? Results.Ok(todo)
            : Results.NotFound());
*/

app.MapPost("/api/webhook/{address}", ([FromBody] object content, string address, IWebhookService webhookService) =>
{
    webhookService.SetWebhook(address, content.ToString());
    return Results.Ok();
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
    List<DeviceConfiguration> templates = new List<DeviceConfiguration>();
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
                logger.LogError(x, $"Error whle getting configuration template for device: {d.Name} [{d.Id}]");
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

/*
app.MapGet("/api/devices", (IDeviceService deviceService) =>
{
    return Results.Ok(deviceService.Devices);
});*/

app.MapGet("/api/image/{filename}", (string filename, IStorageService fileService, IMemoryStorageService memoryStorageService) =>
{
    var img = memoryStorageService.Get(filename);

    if (img == null && fileService.IsConfigured())
        img = fileService.Get(filename).Result;

    if (img == null)
        return Results.NotFound();

    return Results.File(img.Data, "image/jpeg");
});

if (builder.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

//app.UseHttpsRedirection();

//app.UseAuthorization();

//app.MapControllers();

app.Run();