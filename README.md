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

## Continuation plan

Handoff baseline: **2026-09-23**. The async lifecycle and hardware-free integration work is
implemented; the next acceptance step is a controlled real-EasyPLC trial, not another lifecycle rewrite.

### 1. Resume from the verified software baseline

| Suite | Last verified result |
| --- | --- |
| Node, including 11 integration cases | 21 passed in both Release and Debug |
| Shared platform | 194 passed |
| Network devices | 23 passed |

Core **0.1.43** was built and validated locally. The node requires that version; network plugins
still target Core **0.1.42** and were exercised with the node's 0.1.43 host assembly. No publishing,
deployment, or physical hardware trial was performed as part of this work. Check repository status
and the trusted package feed when resuming; do not assume that a local package has been released or
overwrite an already published version.

From the workspace root (`C:\Src\RIoT2`), restore the projects from the configured trusted feeds
before running these commands. Include `C:\Src\RIoT2\.localfeed` as a restore source if 0.1.43 is
still unpublished; that directory alone is sufficient only when the other dependencies are cached.

```powershell
dotnet test .\RIoT2.Net.Node\Tests\RIoT2.Net.Node.Tests.csproj --no-restore --configuration Release
dotnet test .\RIoT2.Net.Node\Tests\RIoT2.Net.Node.Tests.csproj --no-restore --configuration Debug
dotnet test .\RIoT2.Tests\RIoT2.Tests.csproj --no-restore
dotnet test .\RIoT2.Net.Devices\Tests\RIoT2.Net.Devices.Tests.csproj --no-restore --configuration Release
```

The regression to preserve is in [NodeIntegrationTests.cs](Tests/NodeIntegrationTests.cs):
a stalled command previously blocked MQTT configuration processing for about five seconds.
Command execution must remain owned and bounded without holding up configuration receipt.
Keep the 64-operation admission limit, cancellation of old-generation work, explicit failure/overflow
logs, and awaited shutdown. Do not replace these guarantees with untracked background tasks.

### 2. Run the controlled hardware acceptance trial

Use an approved spare/lab PLC, a safe test program, and isolated outputs; do not exercise marker
writes on equipment controlling machinery. Back up the node configuration and PLC program first.
Use a **Release** node build so the debug-only local configuration path does not hide the real flow.

- [ ] Record PLC model/firmware, node/plugin commit or artifact versions, Core version, configuration,
  expected marker values, and a known-good rollback setup.
- [ ] Start the actual node entry point with its plugin package and a test broker/orchestrator.
  Confirm plugin loading, online presence, configuration retrieval, and device status endpoints.
  These application-startup/package paths are outside the integration harness.
- [ ] Verify handshake acceptance and marker reads against known PLC states. Check real response
  lengths, status-byte layout, CRCs, and published report values.
- [ ] Write only approved test markers on expansion zero, then verify PLC-side readback and reports.
  Nonzero-expansion writes are currently unsupported and must remain explicitly rejected.
- [ ] Interrupt the lab connection during I/O, reconnect, and verify visible failure plus recovery on
  a subsequent operation using a fresh connection. Confirm the five-second transaction deadline;
  do not expect automatic command replay.
- [ ] Replace configuration during pending I/O and send rapid successive updates. Old commands and
  reports must not cross into the replacement; only the latest desired configuration should remain active.
- [ ] Shut down during a command and during a scheduled refresh, then restart. Verify awaited socket
  cleanup, stopped device state, and no stale work or duplicate reports after restart.

Save the results and sanitized logs with the tested versions. If hardware is unavailable, leave this
gate pending: simulated wire tests do not establish compatibility with a physical PLC.

### 3. Release only after acceptance

- [ ] Resolve hardware discrepancies and add a simulated regression for each reproducible software bug.
- [ ] Rerun the software suites against the final artifacts.
- [ ] Publish the validated Core package to the trusted feed before releasing its dependent node.
  If 0.1.43 is already published and needs changes, use a new version rather than replacing it.
- [ ] Release the compatible node and network plugin artifacts/manifests together, smoke-test the actual
  plugin-loading path, and deploy first to one controlled node with a rollback path.

### 4. Subsequent coding priorities and deferred decisions

After validating this path, inventory remaining network drivers for blocking I/O and `async void`
lifecycle methods. Migrate one driver at a time to the existing async contracts, reusing this test
pattern for cancellation, reconnect, reconfiguration, and shutdown. The synchronous plugin package
downloader is a separate follow-up; its current calls must still be awaited rather than abandoned.

Do not silently broaden the delivery guarantees: InfluxDB remains best-effort with no automatic
retry/durable replay, and workflow delivery retains its bounded/no-retry policy. A durable outbox
needs an explicit requirement that telemetry survive outages. Broader package/serializer and Matter
redesigns remain lower priority. The previously deferred encrypted mobile BLE redesign still needs
receiver/hardware requirements, and firmware changes still need board-level validation.

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