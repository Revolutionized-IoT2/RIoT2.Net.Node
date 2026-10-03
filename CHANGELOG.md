# Changelog

All notable changes to `RIoT2.Net.Node`. A version is released by pushing a git tag; CI then
publishes Docker images to GitHub Container Registry.

## [Unreleased]

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
