
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS base
RUN apk add --no-cache tzdata
WORKDIR /app
EXPOSE 80
EXPOSE 443


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
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
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RIoT2.Net.Node.dll"]

#Set default environment variables
ENV RIOT2_MQTT_IP=192.168.0.30
ENV RIOT2_MQTT_PASSWORD=password
ENV RIOT2_MQTT_USERNAME=user
ENV RIOT2_NODE_ID=5546B84F-E91C-423F-A80F-FF935EEBF27E
ENV RIOT2_NODE_URL=http://192.168.0.33
ENV TZ=Europe/Helsinki