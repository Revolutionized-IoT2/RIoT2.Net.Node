# .NET 9.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that a .NET 9.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 9.0 upgrade.
3. Upgrade RIoT2.Net.Node.csproj
4. Update Dockerfiles to use .NET 9.0 base images

## Settings

This section contains settings and data used by execution steps.

### Aggregate NuGet packages modifications across all projects

NuGet packages used across all selected projects or their dependencies that need version update in projects that reference them.

| Package Name                                         | Current Version | New Version | Description                                                              |
|:-----------------------------------------------------|:---------------:|:-----------:|:-------------------------------------------------------------------------|
| Microsoft.Extensions.Logging                         | 9.0.0           | 9.0.18      | Recommended for .NET 9.0                                                  |
| Microsoft.VisualStudio.Azure.Containers.Tools.Targets| 1.21.0          |             | Incompatible - no supported version found                                |

### Docker files

Docker files that reference .NET 8.0 base images that need to be updated to .NET 9.0.

| Docker file       | Change                                                                                          |
|:------------------|:------------------------------------------------------------------------------------------------|
| Dockerfile        | Update `aspnet:8.0-alpine` to `aspnet:9.0-alpine` and `sdk:8.0-alpine` to `sdk:9.0-alpine`      |
| Dockerfile_Arm64  | Update `aspnet:8.0-bookworm-slim-arm64v8` to `aspnet:9.0-bookworm-slim-arm64v8` and `sdk:8.0` to `sdk:9.0` |

### Project upgrade details

This section contains details about each project upgrade and modifications that need to be done in the project.

#### RIoT2.Net.Node.csproj modifications

Project properties changes:
  - Target framework should be changed from `net8.0` to `net9.0`

NuGet packages changes:
  - Microsoft.Extensions.Logging should be updated from `9.0.0` to `9.0.18` (*recommended for .NET 9.0*)
  - Microsoft.VisualStudio.Azure.Containers.Tools.Targets `1.21.0` is incompatible - no supported version found; review whether it is still needed.

Other changes:
  - Update `Dockerfile` to use .NET 9.0 base images (`mcr.microsoft.com/dotnet/aspnet:9.0-alpine` and `mcr.microsoft.com/dotnet/sdk:9.0-alpine`).
  - Update `Dockerfile_Arm64` to use .NET 9.0 base images (`mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim-arm64v8` and `mcr.microsoft.com/dotnet/sdk:9.0`).
