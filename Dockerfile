
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app
RUN apk add --upgrade --no-cache tzdata
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV ASPNETCORE_HTTP_PORTS=80
EXPOSE 80

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
ARG NUGET_AUTH_TOKEN=token
ARG NUGET_URL=https://nuget.pkg.github.com/Revolutionized-IoT2/index.json
WORKDIR /src
COPY ["RIoT2.Net.Node.csproj", "."]
RUN dotnet nuget add source -n github -u AZ -p $NUGET_AUTH_TOKEN --store-password-in-clear-text $NUGET_URL
RUN dotnet restore "./RIoT2.Net.Node.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./RIoT2.Net.Node.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./RIoT2.Net.Node.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
RUN mkdir -p /app/Data
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RIoT2.Net.Node.dll"]

#Set default environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV RIOT2_MQTT_IP=
ENV RIOT2_MQTT_PASSWORD=
ENV RIOT2_MQTT_USERNAME=
ENV RIOT2_NODE_ID=
ENV RIOT2_NODE_URL=
ENV TZ=Europe/Helsinki