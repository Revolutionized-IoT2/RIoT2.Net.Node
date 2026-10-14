using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Core.Configuration;
using RIoT2.Net.Node.Services;

namespace RIoT2.Net.Node.Tests;

[TestClass]
public class NodeOptionsTests
{
    [TestMethod]
    public void ValidSettingsAreRead()
    {
        var options = NodeOptions.Load(Settings(new()
        {
            ["RIOT2_NODE_ID"] = "node-1",
            ["RIOT2_NODE_URL"] = "https://node.example.test",
            ["RIOT2_MQTT_IP"] = "mqtt.example.test"
        }));

        Assert.AreEqual("node-1", options.Id);
        Assert.AreEqual("https://node.example.test", options.Url);
        Assert.AreEqual("mqtt.example.test", options.Mqtt.Host);
        Assert.IsNull(options.Mqtt.Username, "No user means an anonymous MQTT connection.");
    }

    [TestMethod]
    public void EveryMissingAndInvalidSettingIsReportedAtOnceWithCriticalLog()
    {
        var logger = new RecordingLogger();

        var error = Assert.ThrowsExactly<ConfigurationValidationException>(() =>
            NodeOptions.Load(Settings(new() { ["RIOT2_NODE_URL"] = "ftp://node" }), logger));

        Assert.AreEqual("Invalid node configuration: RIOT2_NODE_ID is required; " +
            "RIOT2_NODE_URL must be an absolute http or https URL; RIOT2_MQTT_IP is required.", error.Message);
        Assert.AreEqual(LogLevel.Critical, logger.Messages.Single().Level);
    }

    [TestMethod]
    public void ConfigurationServiceUsesTheOptions()
    {
        var options = new NodeOptions
        {
            Id = "node-1",
            Url = "http://node",
            Mqtt = new MqttOptions { Host = "broker", Username = "user", Password = "secret" }
        };

        var configuration = new ConfigurationService(Microsoft.Extensions.Options.Options.Create(options), null, Path.GetTempPath()).Configuration;

        Assert.AreEqual("node-1", configuration.Id);
        Assert.AreEqual("http://node", configuration.Url);
        Assert.AreEqual("node-1", configuration.Mqtt.ClientId);
        Assert.AreEqual("broker", configuration.Mqtt.ServerUrl);
        Assert.AreEqual("user", configuration.Mqtt.Username);
        Assert.AreEqual("secret", configuration.Mqtt.Password);
    }

    private static IConfiguration Settings(Dictionary<string, string> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception exception, Func<TState, Exception, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
