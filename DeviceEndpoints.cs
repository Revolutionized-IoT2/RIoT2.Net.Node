using RIoT2.Core;
using RIoT2.Core.Interfaces;
using RIoT2.Core.Interfaces.Services;
using RIoT2.Core.Models;
using RIoT2.Core.Plugins;
using RIoT2.Core.Security;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace RIoT2.Net.Node;

/// <summary>
/// The node's device endpoints: status, configuration templates and the account sign-in of a device that
/// implements <see cref="IDeviceWithAuthorization"/> (Core 2.4.0). Devices are called only through <see cref="DeviceCalls"/>.
/// </summary>
internal static class DeviceEndpoints
{
    /// <summary>How long a sign-in start or completion may take the device.</summary>
    internal static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        //Provides state information on each device
        app.MapGet("/api/device/status", (IDeviceService deviceService) =>
        {
            List<DeviceStatus> states = [];
            foreach (var d in deviceService.Devices)
            {
                if (d.State == DeviceState.Unknown)
                    continue;

                states.Add(new DeviceStatus()
                {
                    Id = d.Id,
                    Name = d.Name,
                    Message = d.StateMessage,
                    State = d.State,
                    Authorization = DeviceCalls.GetAuthorizationStatus(d)
                });
            }

            return Results.Ok(states);
        }).RequireAuthorization(SecurityPolicies.Viewer);

        //Provides configuration templates
        app.MapGet("/api/device/configuration/templates", (IDeviceService deviceService, ILogger logger) =>
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

        // Account sign-in (Core 2.4.0): the orchestrator creates the state and receives the provider's redirect.
        // The code and the state are secrets: they are never logged.
        app.MapPost(Constants.ApiDeviceAuthorizationStartUrl, async (string id, DeviceAuthorizationStart? start,
            IDeviceService deviceService, ILogger logger, HttpContext context) =>
        {
            if (String.IsNullOrWhiteSpace(start?.RedirectUri) || String.IsNullOrWhiteSpace(start.State))
                return Message(StatusCodes.Status400BadRequest, "redirectUri and state are required.");

            var (redirectUri, state) = (start.RedirectUri, start.State);
            return await CallAsync(id, deviceService, logger, "start", context.RequestAborted, async (device, token) =>
            {
                var authorizeUrl = await DeviceCalls.BeginAuthorizationAsync(device, redirectUri, state, token);
                return Results.Ok(new DeviceAuthorizationStartResult { AuthorizeUrl = authorizeUrl });
            });
        }).RequireAuthorization(SecurityPolicies.Admin);

        app.MapPost(Constants.ApiDeviceAuthorizationCompleteUrl, async (string id, DeviceAuthorizationCompletion? completion,
            IDeviceService deviceService, ILogger logger, HttpContext context) =>
        {
            if (String.IsNullOrWhiteSpace(completion?.Code) || String.IsNullOrWhiteSpace(completion.RedirectUri))
                return Message(StatusCodes.Status400BadRequest, "code and redirectUri are required.");

            var (code, redirectUri) = (completion.Code, completion.RedirectUri);
            return await CallAsync(id, deviceService, logger, "completion", context.RequestAborted, async (device, token) =>
            {
                await DeviceCalls.CompleteAuthorizationAsync(device, code, redirectUri, token);
                return Results.Ok(new { });
            });
        }).RequireAuthorization(SecurityPolicies.Admin);

        return app;
    }

    // 404 for an unknown device, 400 for one without sign-in, 409 when its configuration lacks what the sign-in
    // needs (DeviceNotReadyException), 502 for any other failure or the timeout.
    private static async Task<IResult> CallAsync(string id, IDeviceService deviceService, ILogger logger, string step,
        CancellationToken requestAborted, Func<IDevice, CancellationToken, Task<IResult>> call)
    {
        var device = deviceService.Devices.FirstOrDefault(d => d.Id == id);
        if (device == null)
            return Message(StatusCodes.Status404NotFound, $"No device has the id {id}.");
        if (device is not IDeviceWithAuthorization)
            return Message(StatusCodes.Status400BadRequest, $"Device {id} doesn't sign in to an account.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        timeout.CancelAfter(AuthorizationTimeout);
        try
        {
            var result = await call(device, timeout.Token);
            logger.LogInformation("Account sign-in {Step} for device {DeviceId} succeeded.", step, id);
            return result;
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Account sign-in {Step} for device {DeviceId} cancelled by the caller.", step, id);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (DeviceNotReadyException x)
        {
            logger.LogWarning("Account sign-in {Step} for device {DeviceId} refused: {Reason}", step, id, x.Message);
            return Message(StatusCodes.Status409Conflict, x.Message);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            logger.LogError("Account sign-in {Step} for device {DeviceId} timed out after {Timeout}.", step, id, AuthorizationTimeout);
            return Message(StatusCodes.Status502BadGateway, $"The device didn't answer within {AuthorizationTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception x)
        {
            logger.LogError(x, "Account sign-in {Step} for device {DeviceId} failed.", step, id);
            return Message(StatusCodes.Status502BadGateway, x.Message);
        }
    }

    private static IResult Message(int statusCode, string message) => Results.Json(new { message }, statusCode: statusCode);
}
