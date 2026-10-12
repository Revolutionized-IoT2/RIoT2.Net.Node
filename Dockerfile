
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
WORKDIR /app
RUN apk add --upgrade --no-cache tzdata
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV ASPNETCORE_HTTP_PORTS=80
EXPOSE 80

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
ARG NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json
WORKDIR /src
COPY ["RIoT2.Net.Node.csproj", "Directory.Build.props", "Directory.Packages.props", "./"]
# The feed token is the BuildKit secret "nuget_token" and reaches NuGet only through an environment
# variable of this RUN, so no layer stores it:
#   docker build --secret id=nuget_token,env=NUGET_AUTH_TOKEN .
RUN --mount=type=secret,id=nuget_token,required=true \
    dotnet nuget add source "$NUGET_URL" -n riot2 \
    && NuGetPackageSourceCredentials_riot2="Username=riot2;Password=$(cat /run/secrets/nuget_token)" \
       dotnet restore "./RIoT2.Net.Node.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./RIoT2.Net.Node.csproj" -c $BUILD_CONFIGURATION --no-restore -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RIoT2.Net.Node.csproj" -c $BUILD_CONFIGURATION --no-restore -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
# Set by CI (.github docker-publish.yml); Data/Manifest.json shows them in the UI.
ARG VERSION=0.0.0-local
ARG IMAGE_TAG=ghcr.io/revolutionized-iot2/riot2-node:local
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/Data \
    && printf '{ "name": "RIoT2 Node", "date": "%s", "version": "%s", "installedPackageFilename": "%s" }\n' \
      "$(date -u +%Y-%m-%dT%H:%M:%S)" "$VERSION" "$IMAGE_TAG" > /app/Data/Manifest.json
ENTRYPOINT ["dotnet", "RIoT2.Net.Node.dll"]

#Set default environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV RIOT2_MQTT_IP=
ENV RIOT2_MQTT_PASSWORD=
ENV RIOT2_MQTT_USERNAME=
ENV RIOT2_NODE_ID=
ENV RIOT2_NODE_URL=
ENV TZ=Europe/Helsinki