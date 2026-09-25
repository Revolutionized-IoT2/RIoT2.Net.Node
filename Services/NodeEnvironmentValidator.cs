namespace RIoT2.Net.Node.Services
{
    internal static class NodeEnvironmentValidator
    {
        public static IReadOnlyList<string> Validate(Func<string, string> getEnvironmentVariable)
        {
            var errors = new List<string>();
            Require("RIOT2_NODE_ID", errors, getEnvironmentVariable);
            RequireAbsoluteHttpUrl("RIOT2_NODE_URL", errors, getEnvironmentVariable);
            Require("RIOT2_MQTT_IP", errors, getEnvironmentVariable);
            return errors;
        }

        public static void ValidateOrThrow(ILogger logger, Func<string, string> getEnvironmentVariable = null)
        {
            var errors = Validate(getEnvironmentVariable ?? Environment.GetEnvironmentVariable);
            if (errors.Count == 0)
                return;

            var message = string.Join("; ", errors);
            logger.LogCritical("Node startup configuration invalid: {ConfigurationErrors}", message);
            throw new InvalidOperationException("Node startup configuration invalid: " + message);
        }

        private static void Require(string name, ICollection<string> errors, Func<string, string> getEnvironmentVariable)
        {
            if (string.IsNullOrWhiteSpace(getEnvironmentVariable(name)))
                errors.Add($"{name} is required");
        }

        private static void RequireAbsoluteHttpUrl(string name, ICollection<string> errors, Func<string, string> getEnvironmentVariable)
        {
            var value = getEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{name} is required");
                return;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                errors.Add($"{name} must be an absolute http/https URL");
        }
    }
}
