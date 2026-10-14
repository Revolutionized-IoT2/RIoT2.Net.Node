# Changelog

All notable changes to `RIoT2.Net.Node`. A version is released by pushing a git tag; CI then
publishes Docker images to GitHub Container Registry.

## [Unreleased]

## [1.3.1] - 2026-10-14

Images for the compose install ([I12](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i12-one-command-install-s)). No code, port, user or variable changes.

- Added: both images have a Docker `HEALTHCHECK` on `/health`, which includes the broker
  connection. The arm64 image installs `wget` for it.
- Fixed: the version shown for the node came from `Data/Manifest.json` in the image, which a
  mounted `Data/` hid (bind mount) or kept from the first image (named volume). The image now
  keeps the manifest at `/app/Manifest.json`, and the entrypoint copies it into `Data/` on every
  start.

## [1.3.0] - 2026-10-14

Plugin configuration and conformance ([I10](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i10-plugin-configuration-and-conformance-m)). Built on `RIoT2.Core` 1.5.0. Upgrade the node
before the Devices and RasPi.Devices `1.2.0` plugins: they are built on `PluginBase`, which an older
node doesn't have. Older plugins keep loading.

- Changed: `PluginLoadContext` shares the assemblies listed in Core's
  `PluginHostContract.SharedAssemblyPrefixes` (`RIoT2.Core*` and the DI and logging abstractions),
  instead of a list of its own.
- Changed: `GET /api/device/configuration/templates` builds each template with Core's
  `DeviceTemplates.Create`. Fixed: the default template of a refreshable device without its own
  template had the schedule `0 * * * *`, which Quartz rejects; it is now `0 * * ? * * *`.

## [1.2.1] - 2026-10-13

Async cleanup, part 1 ([I09](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i09-async-cleanup-part-1-s)). Built on `RIoT2.Core` 1.4.0. No deployment change is needed.

- Changed: device reports are published in the order the devices raised them, through a bounded
  queue of 1,000 (the oldest report is dropped and logged when it is full).
- Changed: a new plugin package is downloaded with `DownloadPluginPackageAsync`, without blocking a
  thread while the configuration is applied.

## [1.2.0] - 2026-10-13

MQTT client robustness ([I08](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i08-mqtt-client-robustness-m)). Built on `RIoT2.Core` 1.3.0. No deployment change is needed.

- Changed: the node publishes and subscribes at QoS 1, keeps at most 10,000 outgoing messages
  while the broker is unreachable (the oldest are dropped and logged), and logs a failing MQTT
  handler instead of losing it.
- Added `RIOT2_MQTT_PORT` (default 1883) for a broker on another port.
- Added: `/health` reports the broker connection; it is unhealthy (HTTP 503) while the node is
  disconnected, like the orchestrator's.

## [1.1.0] - 2026-10-13

Typed configuration ([I07](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i07-typed-configuration-s)). Built on `RIoT2.Core` 1.2.0, which also fixes a crash when the MQTT
service was stopped twice at the same time (I06). No deployment change: the variable names are the
same.

- Changed: `NodeOptions` reads and validates every setting at startup and replaces
  `NodeEnvironmentValidator`. A start with missing or invalid settings fails with one message that
  lists them all, for example `Invalid node configuration: RIOT2_NODE_ID is required; RIOT2_NODE_URL
  must be an absolute http or https URL.` `ConfigurationService` receives `IOptions<NodeOptions>`.
  The settings can also come from `appsettings.json` or the command line; environment variables
  still take precedence over `appsettings.json`.

## [1.0.2] - 2026-10-12

Restart only changed devices ([I04](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/increments/phase-0-1.md#i04-restart-only-changed-devices-s)).
No deployment change; the 1.0.1 plugins keep working.

- Changed configuration application to `DeviceServiceBase.ReconfigureChangedDevicesAsync`. A
  configuration message restarts only the devices whose entry was added, changed or removed, plus
  configured devices that are not running. Reconnects, orchestrator restarts and saves of other
  devices no longer restart every device or cancel their commands
  ([backlog item 18](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/backlog/open-issues.md)).
- Changed the Core pin to `RIoT2.Core` 1.1.0.

## [1.0.1] - 2026-10-12

Includes the I01 publish fix (backlog item 20). No deployment change. Install the 1.0.1 plugin zips after this image.

- Changed both Dockerfiles: the GitHub Packages token is the BuildKit secret `nuget_token` and is
  no longer stored in an image layer. The `NUGET_AUTH_TOKEN` build argument is gone.
  `Data/Manifest.json` is written in the image from the `VERSION` and `IMAGE_TAG` build arguments
  instead of being injected with `docker commit`.
- CI: every push runs the tests (with RIoT2.Net.Devices cloned next to this repository), both
  image builds and a secret scan; a tag validates before it pushes the images.
- Fixed (backlog item 20): `dotnet publish` and a local `docker build` no longer copy local runtime
  data into the output. `Data/` (node configuration, Firebase service account, price cache),
  `Logs/`, local `Plugins/` and `appsettings.Development.json` are excluded from publish, and
  `.dockerignore` excludes `Data`, `Logs` and `Plugins`. Local plugin DLLs are still copied to the
  build output for debugging; the hand-maintained list of 20 plugin DLLs became one wildcard.

## [1.0.0] - 2026-10-12

Built on `RIoT2.Core` 1.0.1 and .NET 10. Install the Devices and RasPi.Devices `1.0.0` plugins only
after this image; see the
[upgrade notes](https://github.com/Revolutionized-IoT2/.github/blob/main/docs/guides/upgrading.md).

- Changed the node target framework and Docker images to .NET 10; arm64 runtime now uses
  `aspnet:10.0-noble-arm64v8`.
- Changed package pins to `RIoT2.Core` 1.0.1, `Serilog.AspNetCore` 10.0.0 and
  `Serilog.Sinks.File` 7.0.0 through central package management.
- Removed the explicit `Microsoft.Extensions.Logging` package reference because ASP.NET Core
  `net10.0` provides it.
- Added a legacy-plugin compatibility fixture that targets `net9.0` intentionally and verifies it
  loads through `PluginLoadContext` in the `net10.0` node.
- Documentation: `AGENTS.md` is the AI instruction file, `CLAUDE.md` imports it, and operational
  hand-off notes were removed from the README.

## [0.1.62] - 2026-09-23

### Changed

- The node targets `RIoT2.Core` 0.1.43 and should be released together with compatible plugin
  packages.
- Plugin load contexts share the host's `RIoT2.Core`, DI and logging assemblies so plugin-local
  copies do not create incompatible contract types.
- Startup fails fast when `RIOT2_NODE_ID`, `RIOT2_NODE_URL` or `RIOT2_MQTT_IP` is missing, and
  validates that `RIOT2_NODE_URL` is an absolute `http` or `https` URL.

### Added

- Hardware-free integration tests cover the MQTT background service, command/report services,
  device service, scheduler, configuration coordinator and EasyPLC driver with loopback transports.

### Fixed

- MQTT command handling admits work without blocking the receive callback, keeps configuration
  updates responsive, bounds admitted command work, cancels old-generation work and awaits shutdown.

## Earlier versions

Tags `0.1.0` through `0.1.61`. See `git log` and the tags; there are no release notes for them in
the migrated documentation.
