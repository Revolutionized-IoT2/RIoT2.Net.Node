# AGENTS.md — RIoT2.Net.Node

Applies to: this repository. Read the platform guide first:
[.github/AGENTS.md](https://github.com/Revolutionized-IoT2/.github/blob/main/AGENTS.md). It covers the
workspace map, platform-wide rules and the documentation rules. In the local workspace, every
`https://github.com/Revolutionized-IoT2/<Repo>/blob/main/<path>` link is the file
`C:\Src\RIoT2\<Repo>\<path>`; read the local file instead of fetching the URL.

## What this is

An ASP.NET Core .NET 10 device host for the RIoT2 platform. It loads device plugins from
`Plugins/`, exposes node and device HTTP endpoints, connects to MQTT through `RIoT2.Core`, applies
orchestrator configuration, and owns device lifecycle, refresh and command dispatch.

## Commands

Run from the workspace root (`C:\Src\RIoT2`), in PowerShell:

```powershell
dotnet build .\RIoT2.Net.Node\RIoT2.Net.Node.csproj
dotnet test .\RIoT2.Net.Node\Tests\RIoT2.Net.Node.Tests.csproj -c Release
dotnet publish .\RIoT2.Net.Node\RIoT2.Net.Node.csproj -c Release
```

To run locally, use Release so MQTT configuration and orchestrator downloads are exercised:

```powershell
$env:RIOT2_NODE_ID = "<node-guid>"
$env:RIOT2_NODE_URL = "http://<node-host>"
$env:RIOT2_MQTT_IP = "<broker-host>"
$env:RIOT2_MQTT_USERNAME = "<mqtt-user>"
$env:RIOT2_MQTT_PASSWORD = "<mqtt-password>"
dotnet run --project .\RIoT2.Net.Node\RIoT2.Net.Node.csproj -c Release
```

Docker commands run from this repository root (`C:\Src\RIoT2\RIoT2.Net.Node`):

```powershell
docker build -t riot2-net-node .
docker build -t riot2-net-node-arm64 -f .\Dockerfile_Arm64 .
```

- The tag workflow in `.github/workflows/main.yml` builds and pushes `ghcr.io/revolutionized-iot2/riot2-node`
  for amd64 and arm64, and injects `Data/Manifest.json` into the image.
- If `RIoT2.Core` 0.1.45 is not published to the configured feed, restore/build with
  `C:\Src\RIoT2\.localfeed` as an extra NuGet source. A local feed package is not a release.
- The default Dockerfile uses `aspnet:10.0-alpine` / `sdk:10.0-alpine`; `Dockerfile_Arm64` uses
  `aspnet:10.0-noble-arm64v8` and builds on `sdk:10.0`.

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | DI setup, plugin loading, device configuration application and Minimal API endpoints |
| `PluginLoadContext.cs` | Plugin `AssemblyLoadContext`; shares Core, DI and logging contract assemblies with the host |
| `Services/ConfigurationService.cs` | Environment-backed node configuration and Debug-only local device configuration |
| `Services/MqttBackgroundService.cs` | Hosted MQTT start/stop wrapper; Debug-only local configuration load |
| `Services/NodeEnvironmentValidator.cs` | Startup validation for required environment variables |
| `Tests/` | MSTest hardware-free integration and unit tests; references `RIoT2.Net.Devices` |
| `Dockerfile`, `Dockerfile_Arm64` | Production container images; both listen on port 80 |
| `.github/workflows/main.yml` | Tag-triggered image publish workflow |

## Contracts implemented here

The code side of these hub contracts is in this repository:

- [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md):
  `Program.cs`, `Services/ConfigurationService.cs` and Core's `NodeConfigurationServiceBase`
  install plugin zips and apply `NodeDeviceConfiguration`.
- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md):
  `Program.cs` serves `GET /api/node/manifest`, `GET /api/node/plugin/manifest`,
  `GET /api/device/status`, `GET /api/device/configuration/templates` and `GET /health`.
- [mqtt-topics.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md):
  Core's `NodeMqttService` handles online, configuration, command and report topics; this host
  wires it into the ASP.NET Core lifetime.
- [env-vars.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md):
  `Services/NodeEnvironmentValidator.cs`, `Services/ConfigurationService.cs` and the Dockerfiles
  define the node variables, port and image behaviour.

## Rules

- Run integration tests and real-device trials with `-c Release`; Debug has a different
  configuration path and is not a valid integration proxy.
- Keep HTTP APIs anonymous. The platform security model is an isolated trusted network; do not add
  ad-hoc per-endpoint authentication here.
- Validate environment variables before startup. `RIOT2_NODE_ID`, `RIOT2_NODE_URL` and
  `RIOT2_MQTT_IP` must remain required, and `RIOT2_NODE_URL` must be absolute `http`/`https`.
- Load plugins only from `Plugins/` next to the executable. A plugin must expose exactly one
  `IDevicePlugin` type and register its devices in `Initialize(IServiceCollection)`.
- Keep `PluginLoadContext` sharing the host's `RIoT2.Core`, DI and logging assemblies. Plugin-local
  copies of these assemblies must not create incompatible `IDevice` or async lifecycle types.
- New I/O-heavy devices should use `AsyncDeviceBase` / `IAsyncCommandDevice`. Do not add
  untracked background tasks, `async void` lifecycle methods or blocking waits in host code.
- Commands must remain admitted without awaiting device I/O in the MQTT receive callback. Preserve
  generation-bound ownership, bounded admission and awaited shutdown.
- Release the Node image and plugin packages together, node first. Plugins run inside the Node's
  `RIoT2.Core` version (0.1.45, set in `Directory.Packages.props`). A `net10.0` plugin cannot
  load into a `net9.0` node, while the legacy-plugin compatibility test proves a `net9.0` plugin
  loads into the `net10.0` node.
- Keep `PackageReference` items versionless; package versions belong in `Directory.Packages.props`.
- Do not commit or document real values from `Data/`, `Properties/launchSettings.json` or local
  MQTT/device configuration.

## Pitfalls

- Debug builds load `Data/local.configuration.json` in `Services/ConfigurationService.cs` and
  `Services/MqttBackgroundService.cs`, and Core's `NodeMqttService` ignores MQTT configuration
  messages under `#if DEBUG`. That file can contain real device credentials. Never run Debug
  builds against real devices; use Release for integration tests.
- The Web SDK publishes local `Data/*.json` into publish output, and `.dockerignore` does not
  exclude `Data/`. A developer checkout can accidentally bake `local.configuration.json` or
  Firebase service-account JSON into output or a local image (backlog item 20).
- The Docker images listen on port 80 (`ASPNETCORE_HTTP_PORTS=80`) and run as root because no
  `USER` is set. That is intentional for Raspberry Pi GPIO/D-Bus-style host privileges.
- Plugins load only once at startup from `Plugins/` next to the executable. The first `Data/*.zip`
  found by Core's `NodeConfigurationServiceBase` deletes the existing `Plugins/` contents and
  extracts the zip on the next start. Without plugins, the node logs `NO PLUGINS LOADED` and keeps
  running.
- Device parameter keys must be camelCase. The configuration download camel-cases dictionary keys,
  while `DeviceBase.GetConfiguration<T>(key)` is case-sensitive (divergence C1). `FTP.cs` has
  PascalCase `Storage*` keys in its storage-configuration path; report that as a bug instead of
  silently normalizing it in documentation.
- `RIoT2.Core` 0.1.45 is available locally in `C:\Src\RIoT2\.localfeed` when it has not yet been
  published. Do not assume the local feed is the trusted release feed.

## Related work

- [M6](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m06-plugin-configuration-discovery.md):
  plugin configuration discovery and no static device state.
- [M11](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m11-async-cleanup.md):
  remaining blocking and `async void` cleanup.
- [Design 7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md):
  desired-state configuration and safer plugin updates.
- Backlog items 4, 5, 16, 18 and 20 in
  [open-issues.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
