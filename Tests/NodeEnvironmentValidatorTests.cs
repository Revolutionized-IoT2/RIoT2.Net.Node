using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RIoT2.Net.Node.Services;

namespace RIoT2.Net.Node.Tests;

[TestClass]
public class NodeEnvironmentValidatorTests
{
    [TestMethod]
    public void ValidEnvironmentPasses()
    {
        var errors = NodeEnvironmentValidator.Validate(Name => Name switch
        {
            "RIOT2_NODE_ID" => "node-1",
            "RIOT2_NODE_URL" => "https://node.example.test",
            "RIOT2_MQTT_IP" => "mqtt.example.test",
            _ => null
        });

        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void MissingAndInvalidEnvironmentFailsFastWithCriticalLog()
    {
        var logger = new RecordingLogger();

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            NodeEnvironmentValidator.ValidateOrThrow(logger, Name => Name == "RIOT2_NODE_URL" ? "ftp://node" : null));

        StringAssert.Contains(error.Message, "RIOT2_NODE_ID is required");
        StringAssert.Contains(error.Message, "RIOT2_NODE_URL must be an absolute http/https URL");
        StringAssert.Contains(error.Message, "RIOT2_MQTT_IP is required");
        Assert.AreEqual(LogLevel.Critical, logger.Messages.Single().Level);
    }

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
