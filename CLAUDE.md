# CLAUDE.md

This file provides guidance to AI coding assistants when working with code in this repository.

## Project Overview

`RIoT2.Net.Node` is a .NET 9 ASP.NET Core web application that acts as an IoT **node** in the RIoT2 system. A node hosts and manages IoT devices, communicates with an orchestrator over MQTT, and dynamically loads device functionality from plugins.

Key responsibilities:
- Load device plugins at runtime from the `Plugins/` directory.
- Communicate with the orchestrator via MQTT (online messages, commands, configuration updates).
- Configure, start, stop, and schedule devices.
- Expose HTTP endpoints for node/plugin manifests and device status/configuration templates.

## Tech Stack

- **Framework:** .NET 9 (`net9.0`)
- **App type:** ASP.NET Core Minimal API (`WebApplication`)
- **Logging:** Serilog (console + rolling file sink at `Logs/RIoT2.log`)
- **JSON:** `System.Text.Json` with camelCase naming, case-insensitive, indented output
- **Core library:** `RIoT2.Core` (NuGet package) � provides interfaces, models, and services
- **Containerization:** Docker (Linux target)

## Build, Run & Debug

The project uses standard .NET tooling and defines three configurations: `Debug`, `Release`, and `Local`.

### Resuming this work

Read the [continuation plan](README.md#continuation-plan) before selecting the next task. It records
the verified test baseline, Core package prerequisite, pending real-EasyPLC acceptance checklist,
release order, and deferred decisions. The integration harness is implemented; do not treat its
passing results as validation of physical hardware or the actual plugin installation/startup path.

### Docker
docker build -t riot2-net-node . docker save riot2-net-node > riot2-net-node.tar

Container folders:
- `/app/Data`
- `/app/Logs`
- `/app/Plugins`

## Architecture

Startup and orchestration live in `Program.cs`. Services are registered via dependency injection as singletons and background/hosted services. Core 0.1.43 includes the additive async lifecycle contracts and bounded, owned MQTT command dispatch.

### Dependency Injection Registrations

| Service | Interface | Lifetime |
| --- | --- | --- |
| `ConfigurationService` | `INodeConfigurationService` | Singleton |
| `CommandService` | `ICommandService` | Singleton |
| `ReportService` | `IReportService` | Singleton |
| `NodeMqttService` | `INodeMqttService` | Singleton |
| `DeviceService` | `IDeviceService` | Singleton |
| `MqttBackgroundService` | � | Singleton + HostedService |
| `DeviceSchedulerService` | � | HostedService |
| `DeviceConfigurationCoordinator` | `IHostedService` | Singleton + HostedService, started last and stopped first |

### Plugin System

- Plugins are `.dll` files placed in the `Plugins/` directory and copied to output (`CopyToOutputDirectory: PreserveNewest`).
- `PluginLoadContext` (custom `AssemblyLoadContext`) loads each plugin assembly.
- A plugin type implementing `IDevicePlugin` is discovered via reflection; its `Initialize(IServiceCollection)` method wires up services.
- Plugin MVC controllers are registered as `AssemblyPart`s so `app.MapControllers()` maps them.
- Plugin packages can be downloaded from a URL supplied in device configuration; a new package triggers a node restart to reload plugins.

### Configuration & Lifecycle Flow

1. On startup, `ConfigurationService.InstallPluginPackage()` runs and plugins are loaded.
2. Devices from plugins are collected via `IDevice` and added to `IDeviceService`.
3. MQTT connection callbacks announce presence. In `DEBUG`, MQTT startup awaits local configuration loading.
4. The coordinator subscribes before hosted services start, then applies the latest buffered configuration after MQTT and the scheduler start.
5. Later updates cancel and await the previous application before replacing configuration. The coordinator owns shutdown; lifecycle changes, commands, and refreshes share per-device gates.
6. MQTT admits at most 64 outstanding commands without awaiting device I/O on the receive callback. This keeps configuration updates responsive. Work captures its device generation at admission, is cancelled/awaited at shutdown, and overflow is logged without retry.

Use `AsyncDeviceBase`/the opt-in async interfaces for new I/O-heavy plugins. Legacy synchronous methods remain supported but cannot be forcibly cancelled; never use `async void` for new device operations.

### HTTP Endpoints

- `GET /api/node/manifest` � returns the node manifest.
- `GET /api/node/plugin/manifest` � returns the plugin manifest.
- `GET /api/device/status` � returns `DeviceStatus` for each device with a known state.
- `GET /api/device/configuration/templates` � returns configuration templates (custom via `IDeviceWithConfiguration`, otherwise a default template).

### Environment Parameters

Node identity and connectivity are configured entirely through environment variables, read in `Services/ConfigurationService.cs` when building the `NodeConfiguration`. These are required for the node to connect to the orchestrator and MQTT broker at runtime (they are typically supplied via the container/host environment).

| Environment Variable | Maps To | Description |
| --- | --- | --- |
| `RIOT2_NODE_ID` | `Configuration.Id` / `Mqtt.ClientId` | Unique identifier for this node; also used as the MQTT client id. |
| `RIOT2_NODE_URL` | `Configuration.Url` | Base URL where the node's HTTP API is reachable. |
| `RIOT2_MQTT_IP` | `Mqtt.ServerUrl` | Address of the MQTT broker. |
| `RIOT2_MQTT_USERNAME` | `Mqtt.Username` | Username for MQTT authentication. |
| `RIOT2_MQTT_PASSWORD` | `Mqtt.Password` | Password for MQTT authentication. |

Notes:
- The `NodeConfiguration` is lazily initialized on first access and cached.
- In `DEBUG` builds, device configuration is loaded from the local file `Data/local.configuration.json` instead of waiting for an orchestrator command (see `LoadDeviceConfiguration`).

### MQTT Topic Structure

The node communicates with the orchestrator over MQTT via `INodeMqttService` (implemented by `NodeMqttService`) and coordinated by `MqttBackgroundService`. The concrete topic strings and message contracts are defined in the external `RIoT2.Core` package; the node participates in the following message flows:

- **Node online** - MQTT connection/reconnection callbacks publish a `NodeOnlineMessage` announcing the base URL, node type, and manifests.
- **Orchestrator online / configuration** - Presence requests trigger another announcement. Configuration notifications supply an API base URL; fetched configurations raise `DeviceConfigurationUpdated` and are applied by the hosted coordinator.
- **Commands (inbound)** � Command messages targeting devices are handled through `ICommandService`, dispatching to devices implementing `ICommandDevice`.
- **Reports (outbound)** � Device state/telemetry is published through `IReportService`; refreshable devices (`IRefreshableReportDevice`) are polled on schedule by `DeviceSchedulerService`.

Lifecycle:
- `MqttBackgroundService.StartAsync` calls `_mqttService.Start()` to establish the connection and subscriptions.
- `DeviceConfigurationCoordinator.StopAsync` cancels/awaits application work and device operations before the scheduler and MQTT stop. `MqttBackgroundService.StopAsync` also awaits device shutdown and always stops MQTT in `finally`.

> When adding or modifying MQTT topics or message types, update the shared contracts in `RIoT2.Core` rather than hard-coding topic strings in this project.

## Learnings

MQTT receive callbacks are serialized, so awaiting a device transaction there can prevent the next
configuration message from arriving to cancel it. Start generation-bound command dispatch at admission,
track it within the bounded owner, and await that owner's work at shutdown instead of awaiting device
I/O in the receive callback; the integration suite covers this distinction.
