# RIoT2.Net.Node

`RIoT2.Net.Node` is a .NET 9 ASP.NET Core application that acts as an IoT **node** in the RIoT2 system. A node hosts and manages IoT devices, communicates with an orchestrator over MQTT, and dynamically loads device functionality from plugins.

## Features

- Dynamically loads device plugins at runtime from the `Plugins/` directory.
- Communicates with the orchestrator over MQTT (online messages, commands, configuration updates).
- Configures, starts, stops, and schedules devices.
- Exposes HTTP endpoints for node/plugin manifests and device status/configuration templates.

## Configuration startup ordering

The node subscribes to device configuration updates before hosted services start.
Updates received while MQTT and the scheduler are starting are buffered; the latest
configuration is applied by a hosted coordinator after MQTT and the scheduler have started.
Updates are coalesced to the latest desired configuration. A newer update cancels the previous
application, but waits for its owned work before applying the replacement. Shutdown stops the
coordinator/devices before the scheduler and MQTT; no async application-lifetime callbacks are used.

Commands and scheduled refreshes share a per-device operation gate with lifecycle changes. Shutdown
cancels native async I/O and waits for completion; failed device shutdown blocks configuration
replacement. Removed devices are not restarted using old configuration. Legacy synchronous plugins
remain supported, but blocking calls and `async void` plugin internals cannot be forcibly cancelled.
The legacy plugin package downloader is still synchronous and is awaited by the coordinator.
Cancellation never skips device cleanup or abandons a legacy call; consequently a non-cooperative
legacy driver can delay shutdown beyond the host deadline, which is logged.

Plugin load contexts share the host's Core, logging, and DI contract assemblies so plugin-local DLLs
cannot create incompatible copies of `IDevice` or the opt-in async interfaces.

MQTT command admission is bounded to 64 outstanding operations (including queued device work).
Commands are owned and awaited at shutdown, but do not block receipt of configuration or presence
messages while I/O is pending. Excess commands are rejected with a warning; there is no retry or
durable queue. MQTT acknowledgement is not an application-level execution acknowledgement.

## Hardware-free integration tests

Keep a sibling checkout of `RIoT2.Net.Devices` beside this repository. Run the node tests with:
```powershell
dotnet test .\Tests\RIoT2.Net.Node.Tests.csproj --configuration Release
```

The integration harness starts a loopback MQTT broker, an HTTP configuration endpoint, and a TCP
PLC simulator on ephemeral ports. It composes the real MQTT background service, command/report
services, device service, scheduler, configuration coordinator, and EasyPLC driver. Configuration
notifications use the normal HTTP-fetch contract, rather than injecting configuration directly.
The simulator independently validates request CRCs and handshakes and sends fragmented responses.

Coverage includes command/report round trips, disconnects, invalid headers/CRCs, the real five-second
transaction deadline, replacement during pending I/O, exact command admission capacity, cancellation
of queued old-generation commands, latest-update coalescing, and host shutdown during a command or
scheduled refresh. Legacy command services remain serialized and their running calls are awaited
during shutdown. Timing-sensitive scenarios run without test-method parallelism, and all fixture
tasks, sockets, and hosts are awaited/disposed.

The harness uses in-memory node settings and the configuration base class, not environment variables
or the debug-only local configuration file. It does not execute `Program.cs`, install/download plugin
packages, exercise an external orchestrator, or contact real hardware. Physical EasyPLC validation
is still required before rollout.

## Tech Stack

- **Framework:** .NET 9 (`net9.0`)
- **App type:** ASP.NET Core Minimal API
- **Logging:** Serilog (console + rolling file sink at `Logs/RIoT2.log`)
- **JSON:** `System.Text.Json` (camelCase, case-insensitive, indented)
- **Core library:** `RIoT2.Core`

## Getting Started

### Shared package release prerequisite

This node requires `RIoT2.Core` **0.1.43**. Publish that package to the configured
trusted feed before releasing the node. Local validation can use the final package
in `C:\Src\RIoT2\.localfeed` with cached dependencies; a local pack is not a published release.

### Build & Run

dotnet build dotnet run

### Optional: Setup for development
If you want to work on the source code, clone the repository and run the following commands:

```bash
# restore dependencies
dotnet restore

# build the solution
dotnet build

# run the application
dotnet run --project src/RIoT2.Net.Node/RIoT2.Net.Node.csproj
```

Ensure you have the .NET 9 SDK installed. Optionally, install an IDE such as Visual Studio 2022 (Windows) or Visual Studio Code (cross-platform).

### Setup for deployment
To deploy the application, configure the environment and logging as needed, then publish the application:

```bash
# publish the application
dotnet publish --configuration Release

# navigate to the publish output directory
cd ./src/RIoT2.Net.Node/bin/Release/net9.0/publish

# run the application
dotnet RIoT2.Net.Node.dll
```
Adjust the paths and settings based on your environment and requirements.

## Docker commands
To build and run the application in a Docker container, use the following commands:

```bash
# build the Docker image
docker build -t riot2-net-node .

# run the Docker container
docker run -d --name riot2-net-node -p 80:80 -v /path/to/data:/app/Data -v /path/to/logs:/app/Logs riot2-net-node
```

Replace `/path/to/data` and `/path/to/logs` with the desired paths on the host machine for persistent data and logs storage.

## Container folders

The application uses the following folders within the container:

/app/Data
/app/Logs
/app/Plugins

The project defines three build configurations: `Debug`, `Release`, and `Local`.

> In `Debug` builds, device configuration is loaded from `Data/local.configuration.json` instead of waiting for an orchestrator command.

## Configuration

Node identity and connectivity are configured through environment variables:

| Environment Variable | Description |
| --- | --- |
| `RIOT2_NODE_ID` | Unique identifier for this node; also used as the MQTT client id. |
| `RIOT2_NODE_URL` | Base URL where the node's HTTP API is reachable. |
| `RIOT2_MQTT_IP` | Address of the MQTT broker. |
| `RIOT2_MQTT_USERNAME` | Username for MQTT authentication. |
| `RIOT2_MQTT_PASSWORD` | Password for MQTT authentication. |

## HTTP Endpoints

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/api/node/manifest` | Returns the node manifest. |
| `GET` | `/api/node/plugin/manifest` | Returns the plugin manifest. |
| `GET` | `/api/device/status` | Returns status for each device with a known state. |
| `GET` | `/api/device/configuration/templates` | Returns device configuration templates, including the Matter endpoints declared by devices that implement `IMatterDevice`. |

## Plugins

Device functionality is provided by plugins � `.dll` files placed in the `Plugins/` directory. Each plugin exposes a type implementing `IDevicePlugin`, which is discovered via reflection and initialized at startup. Plugin packages can also be downloaded from a URL supplied in the device configuration; a new package triggers a node restart to reload plugins.

## Docker

### Docker commands