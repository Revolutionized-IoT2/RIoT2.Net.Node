# RIoT2.Net.Node

ASP.NET Core .NET 10 device host for the [RIoT2](https://github.com/Revolutionized-IoT2)
platform. It loads device plugin assemblies, connects to the MQTT broker through
`RIoT2.Core`, applies orchestrator-supplied device configuration, and exposes the node HTTP
endpoints consumed by the orchestrator.

- Type: ASP.NET Core web application
- Target framework: `net10.0`
- Core package: `RIoT2.Core` 1.0.1
- Container image: `ghcr.io/revolutionized-iot2/riot2-node`

How the node fits into the platform: [architecture overview](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/architecture/overview.md).

## Contents

| Path | Contents |
|---|---|
| `Program.cs` | Service registration, plugin loading, configuration application and HTTP endpoints |
| `Services/` | Environment configuration, MQTT hosted service, environment validation and configuration coordination |
| `PluginLoadContext.cs` | Runtime plugin loading with shared host contract assemblies |
| `Plugins/` | Plugin assemblies copied beside the executable for local/dev runs |
| `Data/` | Runtime manifests, downloaded plugin zips and development-only local configuration |
| `Tests/` | Hardware-free MSTest integration and unit tests |
| `Dockerfile`, `Dockerfile_Arm64` | amd64 and arm64 production images |

## Runtime behaviour

At startup the node:

1. Validates `RIOT2_NODE_ID`, `RIOT2_NODE_URL` and `RIOT2_MQTT_IP`.
2. Installs the first plugin zip found in `Data/`, replacing the contents of `Plugins/`.
3. Loads plugin assemblies from `Plugins/`, discovers `IDevicePlugin`, and lets the plugin register
   devices and controllers.
4. Starts MQTT and the scheduler, announces itself online and applies device configuration from the
   orchestrator.

If no plugin assembly loads, the node logs `NO PLUGINS LOADED` and continues to serve its node
endpoints. Device functionality is unavailable until a plugin package is installed and the node
restarts.

The node participates in these platform contracts:

- [Node configuration, templates and plugins](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md)
- [MQTT topics and payloads](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md)
- [HTTP and gRPC APIs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md)
- [Environment variables, ports, volumes and images](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md)

## Configuration

The production node is configured with environment variables:

| Variable | Required | Purpose |
|---|---|---|
| `RIOT2_NODE_ID` | Yes | Node id and MQTT client id; must match the orchestrator node configuration |
| `RIOT2_NODE_URL` | Yes | Absolute `http://` or `https://` base URL advertised to the orchestrator |
| `RIOT2_MQTT_IP` | Yes | MQTT broker host name or IP; the Core MQTT client uses port 1883 |
| `RIOT2_MQTT_USERNAME` | No | MQTT username |
| `RIOT2_MQTT_PASSWORD` | No | MQTT password |

Debug builds are special: they load `Data/local.configuration.json` and ignore the normal MQTT
configuration message path. That file can hold real credentials. Use Release for integration tests,
screenshots and real-device runs.

## Build and test

From the workspace root (`C:\Src\RIoT2`):

```powershell
dotnet build .\RIoT2.Net.Node\RIoT2.Net.Node.csproj
dotnet test .\RIoT2.Net.Node\Tests\RIoT2.Net.Node.Tests.csproj -c Release
```

The tests use a loopback MQTT broker, an HTTP configuration endpoint and simulated device
transports. They do not execute the Docker image, install a real plugin package from a release URL,
talk to an external orchestrator, or contact physical hardware.

To try an unreleased Core, pack it into `C:\Src\RIoT2\.localfeed` and restore with that folder as
an extra source. A local package is not a published release.

## Run locally

Use a Release build and a test broker/orchestrator:

```powershell
$env:RIOT2_NODE_ID = "<node-guid>"
$env:RIOT2_NODE_URL = "http://<node-host>"
$env:RIOT2_MQTT_IP = "<broker-host>"
$env:RIOT2_MQTT_USERNAME = "<mqtt-user>"
$env:RIOT2_MQTT_PASSWORD = "<mqtt-password>"
dotnet run --project .\RIoT2.Net.Node\RIoT2.Net.Node.csproj -c Release
```

Put plugin assemblies in the publish/runtime `Plugins/` folder, or place a plugin zip in `Data/`
and restart so Core installs it into `Plugins/`.

## Docker

Build from this repository root:

```powershell
docker build -t riot2-net-node .
```

The default Dockerfile uses `aspnet:10.0-alpine` and `sdk:10.0-alpine`. `Dockerfile_Arm64` uses
`aspnet:10.0-noble-arm64v8` for runtime and builds on the amd64 `sdk:10.0` image.

Run with mounted runtime folders:

```powershell
docker run -d --name riot2-net-node -p 80:80 `
  -e RIOT2_NODE_ID=<node-guid> `
  -e RIOT2_NODE_URL=http://<node-host> `
  -e RIOT2_MQTT_IP=<broker-host> `
  -e RIOT2_MQTT_USERNAME=<mqtt-user> `
  -e RIOT2_MQTT_PASSWORD=<mqtt-password> `
  -v C:\path\to\data:/app/Data `
  -v C:\path\to\logs:/app/Logs `
  -v C:\path\to\plugins:/app/Plugins `
  riot2-net-node
```

The image listens on container port 80 and has no `USER` directive, so it runs as root. The
published image does not declare volumes; mount `/app/Data`, `/app/Logs` and `/app/Plugins`
yourself.

## HTTP endpoints

Node endpoints are documented in the hub [HTTP contract](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md).
This repository currently serves:

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/node/manifest` | Node manifest from `Data/Manifest.json` |
| `GET` | `/api/node/plugin/manifest` | Loaded plugin package manifest |
| `GET` | `/api/device/status` | States for devices whose state is known |
| `GET` | `/api/device/configuration/templates` | Configuration templates and Matter endpoint declarations from loaded devices |
| `GET` | `/health` | ASP.NET Core health check |

Loaded plugin controllers can add their own routes, such as the default devices plugin's
`POST /api/webhook/{address}` and download endpoints.

## Versions and releases

- Release notes are in [CHANGELOG.md](CHANGELOG.md).
- CI publishes images when a version tag is pushed.
- Update the Node image before installing `net10.0` plugin zips. A `net10.0` plugin cannot load
  into a `net9.0` node, while the compatibility tests prove a `net9.0` plugin can load into the
  `net10.0` node.
- Release the Node image and the plugin packages together. Plugins run with the Node host's
  `RIoT2.Core` assembly, so package drift can become runtime drift.

## Contributing

- Instructions for AI coding agents: [AGENTS.md](AGENTS.md).
- Platform documentation: [.github/docs](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/README.md).

## License

See [LICENSE](LICENSE).
