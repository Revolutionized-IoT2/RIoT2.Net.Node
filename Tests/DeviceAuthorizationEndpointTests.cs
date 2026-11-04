using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RIoT2.Core;
using RIoT2.Core.Abstracts;
using RIoT2.Core.Configuration;
using RIoT2.Core.Hosting;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Security;

namespace RIoT2.Net.Node.Tests;

/// <summary>
/// The account sign-in endpoints (Core 2.4.0) and the authorization in the device status, served by
/// <see cref="DeviceEndpoints"/> as Program maps them.
/// </summary>
[TestClass]
public class DeviceAuthorizationEndpointTests
{
    private const string RedirectUri = "http://orchestrator.test/api/oauth/callback";
    private const string State = "state-secret-value";
    private const string Code = "code-secret-value";

    [TestMethod]
    public async Task StartReturnsTheDevicesAuthorizeUrl()
    {
        var device = new AuthorizingDevice("netatmo");
        await using var node = await EndpointHost.StartAsync(device);

        var response = await node.Client.PostAsJsonAsync(StartUrl("netatmo"), new { redirectUri = RedirectUri, state = State });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(AuthorizingDevice.AuthorizeUrl + State, body.RootElement.GetProperty("authorizeUrl").GetString());
        Assert.AreEqual((RedirectUri, State), device.Started);
        node.AssertSecretsNotLogged();
    }

    [TestMethod]
    public async Task CompletePassesCodeAndRedirectUriToTheDevice()
    {
        var device = new AuthorizingDevice("netatmo");
        await using var node = await EndpointHost.StartAsync(device);

        var response = await node.Client.PostAsJsonAsync(CompleteUrl("netatmo"), new { code = Code, redirectUri = RedirectUri });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual((Code, RedirectUri), device.Completed);
        node.AssertSecretsNotLogged();
    }

    [TestMethod]
    public async Task UnknownDeviceIsNotFound()
    {
        await using var node = await EndpointHost.StartAsync(new AuthorizingDevice("netatmo"));

        var start = await node.Client.PostAsJsonAsync(StartUrl("missing"), new { redirectUri = RedirectUri, state = State });
        var complete = await node.Client.PostAsJsonAsync(CompleteUrl("missing"), new { code = Code, redirectUri = RedirectUri });

        Assert.AreEqual(HttpStatusCode.NotFound, start.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, complete.StatusCode);
    }

    [TestMethod]
    public async Task DeviceWithoutSignInIsBadRequest()
    {
        await using var node = await EndpointHost.StartAsync(new PlainDevice("plain"));

        var start = await node.Client.PostAsJsonAsync(StartUrl("plain"), new { redirectUri = RedirectUri, state = State });
        var complete = await node.Client.PostAsJsonAsync(CompleteUrl("plain"), new { code = Code, redirectUri = RedirectUri });

        Assert.AreEqual(HttpStatusCode.BadRequest, start.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, complete.StatusCode);
        StringAssert.Contains(await start.Content.ReadAsStringAsync(), "\"message\"");
    }

    [TestMethod]
    public async Task EmptyBodyFieldsAreBadRequest()
    {
        var device = new AuthorizingDevice("netatmo");
        await using var node = await EndpointHost.StartAsync(device);

        foreach (var body in new object[] { new { }, new { redirectUri = "", state = State }, new { redirectUri = RedirectUri, state = " " } })
            Assert.AreEqual(HttpStatusCode.BadRequest, (await node.Client.PostAsJsonAsync(StartUrl("netatmo"), body)).StatusCode, JsonSerializer.Serialize(body));
        foreach (var body in new object[] { new { }, new { code = "", redirectUri = RedirectUri }, new { code = Code, redirectUri = " " } })
            Assert.AreEqual(HttpStatusCode.BadRequest, (await node.Client.PostAsJsonAsync(CompleteUrl("netatmo"), body)).StatusCode, JsonSerializer.Serialize(body));
        Assert.AreEqual(HttpStatusCode.BadRequest, (await node.Client.PostAsync(StartUrl("netatmo"), null)).StatusCode, "No body.");

        Assert.IsNull(device.Started);
        Assert.IsNull(device.Completed);
    }

    [TestMethod]
    public async Task DeviceNotReadyIsConflict()
    {
        var device = new AuthorizingDevice("netatmo") { Failure = new DeviceNotReadyException("Set the client id and secret first.") };
        await using var node = await EndpointHost.StartAsync(device);

        var start = await node.Client.PostAsJsonAsync(StartUrl("netatmo"), new { redirectUri = RedirectUri, state = State });
        var complete = await node.Client.PostAsJsonAsync(CompleteUrl("netatmo"), new { code = Code, redirectUri = RedirectUri });

        Assert.AreEqual(HttpStatusCode.Conflict, start.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, complete.StatusCode);
        using var body = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        Assert.AreEqual("Set the client id and secret first.", body.RootElement.GetProperty("message").GetString());
        node.AssertSecretsNotLogged();
    }

    [TestMethod]
    public async Task OtherDeviceFailureIsBadGateway()
    {
        var device = new AuthorizingDevice("netatmo") { Failure = new HttpRequestException("The provider refused the code.") };
        await using var node = await EndpointHost.StartAsync(device);

        var complete = await node.Client.PostAsJsonAsync(CompleteUrl("netatmo"), new { code = Code, redirectUri = RedirectUri });

        Assert.AreEqual(HttpStatusCode.BadGateway, complete.StatusCode);
        using var body = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        Assert.AreEqual("The provider refused the code.", body.RootElement.GetProperty("message").GetString());
        Assert.IsTrue(node.Log.Entries.Any(e => e.Level == LogLevel.Error && e.Error == device.Failure), "The failure is logged.");
        node.AssertSecretsNotLogged();
    }

    [TestMethod]
    public async Task StatusIncludesAuthorizationOnlyForAuthorizingDevices()
    {
        await using var node = await EndpointHost.StartAsync(new AuthorizingDevice("netatmo"), new PlainDevice("plain"));

        using var body = JsonDocument.Parse(await node.Client.GetStringAsync("/api/device/status"));

        var states = body.RootElement.EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.HasCount(2, states);
        var authorization = states["netatmo"].GetProperty("authorization");
        Assert.AreEqual((int)DeviceAuthorizationState.Connected, authorization.GetProperty("state").GetInt32());
        Assert.AreEqual("user@example.com", authorization.GetProperty("account").GetString());
        Assert.IsFalse(states["plain"].TryGetProperty("authorization", out _), "A device without sign-in has no authorization.");
    }

    [TestMethod]
    public async Task SignInEndpointsRequireAdmin()
    {
        await using var node = await EndpointHost.StartAsync(new AuthorizingDevice("netatmo"));

        foreach (var route in new[] { Constants.ApiDeviceAuthorizationStartUrl, Constants.ApiDeviceAuthorizationCompleteUrl })
        {
            var endpoint = node.Endpoints.OfType<RouteEndpoint>().Single(e => "/" + e.RoutePattern.RawText!.TrimStart('/') == route);
            Assert.AreEqual(SecurityPolicies.Admin, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy, route);
        }
    }

    private static string StartUrl(string id) => Constants.ApiDeviceAuthorizationStartUrl.Replace("{id}", id);
    private static string CompleteUrl(string id) => Constants.ApiDeviceAuthorizationCompleteUrl.Replace("{id}", id);

    private static DeviceConfiguration ConfigurationFor(string id) => new() { Id = id, Name = id + " device" };

    private sealed class EndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EndpointHost(WebApplication app, NodeIntegrationHarness.TestLog log)
        {
            _app = app;
            Log = log;
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        }

        public HttpClient Client { get; }
        public NodeIntegrationHarness.TestLog Log { get; }
        public IEnumerable<Endpoint> Endpoints => ((IEndpointRouteBuilder)_app).DataSources.SelectMany(s => s.Endpoints);

        public static async Task<EndpointHost> StartAsync(params IDevice[] devices)
        {
            var log = new NodeIntegrationHarness.TestLog();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton<IDeviceService>(new FakeDeviceService(devices));
            builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILogger>(log);
            builder.Services.AddRIoT2Security(new SecurityOptions());
            var app = builder.Build();
            app.UseRIoT2Security();
            app.MapDeviceEndpoints();
            await app.StartAsync();
            return new EndpointHost(app, log);
        }

        public void AssertSecretsNotLogged()
        {
            foreach (var entry in Log.Entries)
            {
                StringAssert.DoesNotMatch(entry.Message, new System.Text.RegularExpressions.Regex($"{State}|{Code}"));
                StringAssert.DoesNotMatch(entry.Error?.ToString() ?? "", new System.Text.RegularExpressions.Regex($"{State}|{Code}"));
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class FakeDeviceService(IEnumerable<IDevice> devices) : IDeviceService
    {
        public event DeviceServiceUpdatedHandler DevicesUpdated { add { } remove { } }
        public List<IDevice> Devices { get; } = [.. devices];
        public ICommandDevice? GetDeviceByCommandId(string commandId) => null;
        public IDevice? GetDeviceByReportId(string reportId) => null;
        public void ConfigureDevices() { }
        public void StartAllDevices(bool restartDevicesInErrorState = false) { }
        public void StopAllDevices() { }
    }

    private class PlainDevice : AsyncDeviceBase, IDevice
    {
        public PlainDevice(string id) : base(new NodeIntegrationHarness.TestLog()) => Initialize(ConfigurationFor(id));
        public override void ConfigureDevice() { }
        protected override Task StartDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task StopDeviceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AuthorizingDevice(string id) : PlainDevice(id), IDeviceWithAuthorization
    {
        public const string AuthorizeUrl = "https://provider.test/authorize?state=";

        public Exception? Failure { get; init; }
        public (string RedirectUri, string State)? Started { get; private set; }
        public (string Code, string RedirectUri)? Completed { get; private set; }

        public DeviceAuthorization Authorization { get; } = new() { Provider = "Test provider" };

        public DeviceAuthorizationStatus GetAuthorizationStatus() =>
            new() { State = DeviceAuthorizationState.Connected, Account = "user@example.com" };

        public Task<string> BeginAuthorizationAsync(string redirectUri, string state, CancellationToken cancellationToken)
        {
            if (Failure != null)
                throw Failure;
            Started = (redirectUri, state);
            return Task.FromResult(AuthorizeUrl + Uri.EscapeDataString(state));
        }

        public Task CompleteAuthorizationAsync(string code, string redirectUri, CancellationToken cancellationToken)
        {
            if (Failure != null)
                throw Failure;
            Completed = (code, redirectUri);
            return Task.CompletedTask;
        }
    }
}
