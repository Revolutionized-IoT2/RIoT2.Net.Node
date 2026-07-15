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
- **Core library:** `RIoT2.Core` (NuGet package) — provides interfaces, models, and services
- **Containerization:** Docker (Linux target)

## Build, Run & Debug

The project uses standard .NET tooling and defines three configurations: `Debug`, `Release`, and `Local`.

### Docker
docker build -t riot2-net-node . docker save riot2-net-node > riot2-net-node.tar

Container folders:
- `/app/Data`
- `/app/Logs`
- `/app/Plugins`

## Architecture

Startup and orchestration live in `Program.cs`. Services are registered via dependency injection as singletons and background/hosted services.

### Dependency Injection Registrations

| Service | Interface | Lifetime |
| --- | --- | --- |
| `ConfigurationService` | `INodeConfigurationService` | Singleton |
| `CommandService` | `ICommandService` | Singleton |
| `ReportService` | `IReportService` | Singleton |
| `NodeMqttService` | `INodeMqttService` | Singleton |
| `DeviceService` | `IDeviceService` | Singleton |
| `MqttBackgroundService` | — | Singleton + HostedService |
| `DeviceSchedulerService` | — | HostedService |

### Plugin System

- Plugins are `.dll` files placed in the `Plugins/` directory and copied to output (`CopyToOutputDirectory: PreserveNewest`).
- `PluginLoadContext` (custom `AssemblyLoadContext`) loads each plugin assembly.
- A plugin type implementing `IDevicePlugin` is discovered via reflection; its `Initialize(IServiceCollection)` method wires up services.
- Plugin MVC controllers are registered as `AssemblyPart`s so `app.MapControllers()` maps them.
- Plugin packages can be downloaded from a URL supplied in device configuration; a new package triggers a node restart to reload plugins.

### Configuration & Lifecycle Flow

1. On startup, `ConfigurationService.InstallPluginPackage()` runs and plugins are loaded.
2. Devices from plugins are collected via `IDevice` and added to `IDeviceService`.
3. On `ApplicationStarted`, the node either sends a `NodeOnlineMessage` (via `MqttBackgroundService`) or, in `DEBUG`, loads a local device configuration file.
4. `DeviceConfigurationUpdated` re-checks the plugin package, then stops, reconfigures, and restarts all devices.

### HTTP Endpoints

- `GET /api/node/manifest` — returns the node manifest.
- `GET /api/node/plugin/manifest` — returns the plugin manifest.
- `GET /api/device/status` — returns `DeviceStatus` for each device with a known state.
- `GET /api/device/configuration/templates` — returns configuration templates (custom via `IDeviceWithConfiguration`, otherwise a default template).

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

- **Node online** — On `ApplicationStarted`, if no device configuration has been received yet, the node publishes a `NodeOnlineMessage` (via `MqttBackgroundService.SendNodeOnlineMessage`) announcing its base URL, node type (`NodeType.Device`), manifest, and plugin manifest.
- **Orchestrator online / configuration** — The node listens for orchestrator messages that push device configuration. Receiving configuration before startup suppresses the initial online message; updates raise `DeviceConfigurationUpdated`, which reloads plugins if needed and restarts devices.
- **Commands (inbound)** — Command messages targeting devices are handled through `ICommandService`, dispatching to devices implementing `ICommandDevice`.
- **Reports (outbound)** — Device state/telemetry is published through `IReportService`; refreshable devices (`IRefreshableReportDevice`) are polled on schedule by `DeviceSchedulerService`.

Lifecycle:
- `MqttBackgroundService.StartAsync` calls `_mqttService.Start()` to establish the connection and subscriptions.
- `MqttBackgroundService.StopAsync` stops all devices and then calls `_mqttService.Stop()`.

> When adding or modifying MQTT topics or message types, update the shared contracts in `RIoT2.Core` rather than hard-coding topic strings in this project.

