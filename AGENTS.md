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
$env:NUGET_AUTH_TOKEN = '<github-packages-token>'   # a BuildKit secret, never a build argument
docker build --secret id=nuget_token,env=NUGET_AUTH_TOKEN -t riot2-net-node .
docker buildx build --platform linux/arm64 --secret id=nuget_token,env=NUGET_AUTH_TOKEN -t riot2-net-node-arm64 -f .\Dockerfile_Arm64 .
```

- Every push runs `.github/workflows/ci.yml`: build, the tests (with RIoT2.Net.Devices cloned
  next to this repository), both image builds and a secret scan. A `x.y.z` tag runs
  `release.yml`, which validates and then pushes `ghcr.io/revolutionized-iot2/riot2-node` for amd64
  and arm64 ([CI guide](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/ci.md)). The Dockerfiles write `/app/Manifest.json` from the `VERSION` and
  `IMAGE_TAG` build arguments; the entrypoint copies it to `Data/Manifest.json` on every start,
  because `Data/` is a volume. Keep the entrypoint a `/bin/sh -c ... exec dotnet` line, so .NET
  stays PID 1 and receives `docker stop`. Both images have a `HEALTHCHECK` on `/health` (the
  arm64 image installs `wget` for it).
- RIoT2.Tests runs this `Program.cs` end to end nightly ([end-to-end tests](https://github.com/Revolutionized-IoT2/RIoT2.Tests/blob/main/README.md#end-to-end-tests)). The harness
  passes the `RIOT2_*` settings through the host configuration and replaces nothing else, so a
  startup change must keep those tests passing.
- The node compiles against `RIoT2.Core.Node` and `RIoT2.Core.Hosting` 2.0.0 (GitHub Packages); it shares every `RIoT2.Core*` assembly with plugins. The `RIoT2.Core` facade and Newtonsoft.Json are gone since Core 2.0.0 (I45): a plugin that needs Newtonsoft packs it. Command results come from Core's `NodeMqttService`; `Tests/NodeCommandResultTests.cs` covers them. Desired state (design 7.2) comes from Core's `NodeConfigurationServiceBase` and `Services/DeviceConfigurationApplier.cs`, which every application goes through (plugin check, device diff, saved configuration, status); `Tests/NodeDesiredStateTests.cs` covers it. Plugin updates (design 7.2 phase 4) use Core's `PluginPackageInstaller` (`UpdateAsync` from the applier, `InstallPluginPackage`/`ConfirmStartup` in `Program.cs`); `Tests/PluginUpdateTests.cs` covers them. To try an unreleased Core, restore with
  `C:\Src\RIoT2\.localfeed` as an extra NuGet source. A local feed package is not a release.
- The default Dockerfile uses `aspnet:10.0-alpine` / `sdk:10.0-alpine`; `Dockerfile_Arm64` uses
  `aspnet:10.0-noble-arm64v8` and builds on `sdk:10.0`. Each `FROM` is pinned by digest
  (`<image>:<tag>@sha256:...`); Renovate updates the digests.

## Layout

| Path | Contents |
|---|---|
| `Program.cs` | DI setup, plugin loading, device configuration application and node Minimal API endpoints |
| `DeviceEndpoints.cs` | Device Minimal API endpoints: status, configuration templates and account sign-in (start/complete) |
| `PluginLoader.cs` | Loads every `Plugins/*.dll`; reports what loaded and what failed (`PluginLoadResult`), which decides a rollback |
| `PluginLoadContext.cs` | Plugin `AssemblyLoadContext`; shares the assemblies in Core's `PluginHostContract` with the host |
| `Services/ConfigurationService.cs` | Node configuration from `IOptions<NodeOptions>`, and Debug-only local device configuration |
| `Services/MqttBackgroundService.cs` | Hosted MQTT start/stop wrapper; Debug-only local configuration load |
| `Services/NodeOptions.cs` | Every `RIOT2_*` setting the node reads, validated at startup with Core's `SettingsReader` |
| `Tests/` | MSTest hardware-free integration and unit tests; references `RIoT2.Net.Devices`, and after the build packs it into `PackedPlugin/` and a `dotnet new riot2-plugin` plugin into `TemplatePlugin/` for `PluginCompatibilityTests` |
| `Dockerfile`, `Dockerfile_Arm64` | Production container images; both listen on port 80 |
| `.github/workflows/ci.yml`, `release.yml` | CI on every push; image release on a tag |

## Contracts implemented here

The code side of these hub contracts is in this repository:

- [configuration.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/configuration.md):
  `Program.cs`, `Services/ConfigurationService.cs` and Core's `NodeConfigurationServiceBase`
  verify, install and roll back plugin packages and apply `NodeDeviceConfiguration`.
- [http-api.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/http-api.md):
  `Program.cs` serves `GET /api/node/manifest`, `GET /api/node/plugin/manifest` and `GET /health`;
  `DeviceEndpoints.cs` serves `GET /api/device/status`, `GET /api/device/configuration/templates` and the
  `Admin` sign-in routes `POST /api/device/{id}/authorization/start` and `.../complete` (Core 2.4.0). Never log
  the sign-in code or state.
- [mqtt-topics.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/mqtt-topics.md):
  Core's `NodeMqttService` handles online, configuration, command and report topics; this host
  wires it into the ASP.NET Core lifetime.
- [env-vars.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/contracts/env-vars.md):
  `Services/NodeOptions.cs` and the Dockerfiles define the node variables, port and image
  behaviour. A new setting is added to `NodeOptions` and to the contract; nothing else reads the
  environment.

## Rules

- Run integration tests and real-device trials with `-c Release`; Debug has a different
  configuration path and is not a valid integration proxy.
- Nothing is enforced while `RIOT2_SECURITY_MODE` is `off` (the only mode yet). Every endpoint
  needs a policy (`RequireAuthorization(SecurityPolicies.Viewer)`) or `AllowAnonymous()`;
  `DefaultPolicyConvention` covers plugin actions. The end-to-end suite fails on an endpoint
  without either. No ad-hoc per-endpoint authentication.
- Validate environment variables before startup. `RIOT2_NODE_ID`, `RIOT2_NODE_URL` and
  `RIOT2_MQTT_IP` must remain required, and `RIOT2_NODE_URL` must be absolute `http`/`https`.
- Load plugins only from `Plugins/` next to the executable. A plugin must expose exactly one
  `IDevicePlugin` type (normally derived from Core's `PluginBase`) and register its devices in
  `Initialize(IServiceCollection)`. The node adds the plugin's controllers; it never reads the
  obsolete `IDevicePlugin.Devices`.
- `PluginLoadContext` shares the assemblies in Core's `PluginHostContract.SharedAssemblyPrefixes`
  with plugins; change that list in Core, not here. Plugin-local copies of these assemblies must
  not create incompatible `IDevice` or async lifecycle types.
- The templates endpoint uses Core's `DeviceTemplates.Create`, the same code the plugins'
  conformance test checks. Don't build templates in the node.
- Call devices only through Core's `DeviceCalls` (async; `AsyncDeviceBase`'s sync members throw
  since Core 1.23.0). Do not add untracked background tasks, `async void` lifecycle methods or
  blocking waits in host code.
- Commands must remain admitted without awaiting device I/O in the MQTT receive callback. Preserve
  generation-bound ownership, bounded admission and awaited shutdown.
- Release the Node image and plugin packages together, node first. Plugins run inside the Node's
  `RIoT2.Core` version (2.0.0, set in `Directory.Packages.props`); a plugin built against a newer
  Core minor or another major is refused (`PluginCompatibility`). A `net10.0` plugin cannot
  load into a `net9.0` node, while the legacy-plugin compatibility test proves a `net9.0` plugin
  (`Tests/LegacyPlugin`, on `RIoT2.Core.Devices` of the node's major, with a synchronous device)
  loads into the `net10.0` node.
- Keep `PackageReference` items versionless; package versions belong in `Directory.Packages.props`.
- Do not commit or document real values from `Data/`, `Properties/launchSettings.json` or local
  MQTT/device configuration.
- Logging comes from `builder.AddRIoT2Logging()` (RIoT2.Core.Hosting): don't add Serilog sinks,
  logging providers or `Logging` sections to `appsettings.json`; `RIOT2_LOG_FORMAT` and
  `RIOT2_LOG_LEVEL` are the settings. Log through `ILogger`, and wrap work for one command or
  trigger in `logger.BeginIdScope(LogScope.CorrelationId | LogScope.MessageId, id)`.
- Metrics come from `builder.AddRIoT2Telemetry()` (`OTEL_EXPORTER_OTLP_ENDPOINT`, `RIOT2_TRACING`); the
  node's instruments (`riot2.node.*`) are recorded in RIoT2.Core. Don't add exporters here.

## Pitfalls

- Debug builds load `Data/local.configuration.json` in `Services/ConfigurationService.cs` and
  `Services/MqttBackgroundService.cs`, and Core's `NodeMqttService` ignores MQTT configuration
  messages under `#if DEBUG`. That file can contain real device credentials. Never run Debug
  builds against real devices; use Release for integration tests.
- `Data/`, `Logs/`, local `Plugins/` and `appsettings.Development.json` are excluded from publish
  in the `.csproj`, and `Data`, `Logs` and `Plugins` from the Docker context (backlog item 20,
  fixed 2026-10-12). Local `Plugins/*.dll` are still copied to the build output for debugging.
  Don't remove these exclusions: `Data/` holds `local.configuration.json` and the Firebase
  service-account JSON.
- The saved configuration lives in `Data/` under the host's **content root** (`/app/Data` in the
  image, the working directory locally), with the plugin journal (`plugins.install.json`) and the
  refused package (`plugins.rejected.json`), while manifests, `Plugins/`, `Plugins.new/` and
  `Plugins.previous/` are next to the executable. In the image both are `/app`. The end-to-end harness relies on the
  content root to give each test node its own `Data/`.
- The Docker images listen on port 80 (`ASPNETCORE_HTTP_PORTS=80`) and run as root because no
  `USER` is set. That is intentional for Raspberry Pi GPIO/D-Bus-style host privileges.
- Plugins load only once at startup from `Plugins/` next to the executable. A package is staged
  in `Plugins.new/` while the node runs and installed at the next start by moving folder
  **contents** (compose bind-mounts `/app/Plugins`, so the folder itself can't be renamed). A
  package installed at this start that doesn't load is rolled back and the process exits with
  `PluginLoader.RollbackExitCode` (75) once; never add a second restart path. Without plugins, the
  node logs `NO PLUGINS LOADED` and keeps running.
- Device parameter keys must be camelCase. The configuration download camel-cases dictionary keys;
  since Core 1.16.0 options binding matches them case-insensitively (divergence C1). `FTP.cs` has
  PascalCase `Storage*` keys in its storage-configuration path; report that as a bug instead of
  silently normalizing it in documentation.
- `C:\Src\RIoT2\.localfeed` holds only local packs of unreleased Core versions. Do not assume the
  local feed is the trusted release feed.

## Related work

- [M6](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m06-plugin-configuration-discovery.md):
  plugin configuration discovery and no static device state.
- [M11](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m11-async-cleanup.md):
  remaining blocking and `async void` cleanup.
- [M12](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/plans/m12-device-plugin-sdk.md) ([ADR 0014](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/adr/0014-device-plugin-sdk.md)):
  device plugin SDK (`PluginBase`, conformance test, packaging target, typed options).
- [Design 7.2](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/design/desired-state-configuration.md):
  desired-state configuration and safer plugin updates.
- Backlog items 4, 5, 16, 18, 20 and 23 in
  [open-issues.md](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md).
