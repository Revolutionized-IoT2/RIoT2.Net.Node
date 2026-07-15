# RIoT2.Net.Node

`RIoT2.Net.Node` is a .NET 9 ASP.NET Core application that acts as an IoT **node** in the RIoT2 system. A node hosts and manages IoT devices, communicates with an orchestrator over MQTT, and dynamically loads device functionality from plugins.

## Features

- Dynamically loads device plugins at runtime from the `Plugins/` directory.
- Communicates with the orchestrator over MQTT (online messages, commands, configuration updates).
- Configures, starts, stops, and schedules devices.
- Exposes HTTP endpoints for node/plugin manifests and device status/configuration templates.

## Tech Stack

- **Framework:** .NET 9 (`net9.0`)
- **App type:** ASP.NET Core Minimal API
- **Logging:** Serilog (console + rolling file sink at `Logs/RIoT2.log`)
- **JSON:** `System.Text.Json` (camelCase, case-insensitive, indented)
- **Core library:** `RIoT2.Core`

## Getting Started

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
| `GET` | `/api/device/configuration/templates` | Returns device configuration templates. |

## Plugins

Device functionality is provided by plugins — `.dll` files placed in the `Plugins/` directory. Each plugin exposes a type implementing `IDevicePlugin`, which is discovered via reflection and initialized at startup. Plugin packages can also be downloaded from a URL supplied in the device configuration; a new package triggers a node restart to reload plugins.

## Docker

### Docker commands