# Multi-stage Dockerfile for Relay.Worker
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy package management and solution files
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["Relay.sln", "./"]

# Copy project files for caching restore layer
COPY ["src/Relay.Domain/Relay.Domain.csproj", "src/Relay.Domain/"]
COPY ["src/Relay.Application/Relay.Application.csproj", "src/Relay.Application/"]
COPY ["src/Relay.Infrastructure/Relay.Infrastructure.csproj", "src/Relay.Infrastructure/"]
COPY ["src/Relay.Worker/Relay.Worker.csproj", "src/Relay.Worker/"]

# Restore packages
RUN dotnet restore "src/Relay.Worker/Relay.Worker.csproj"

# Copy full source
COPY ["src/Relay.Domain/", "src/Relay.Domain/"]
COPY ["src/Relay.Application/", "src/Relay.Application/"]
COPY ["src/Relay.Infrastructure/", "src/Relay.Infrastructure/"]
COPY ["src/Relay.Worker/", "src/Relay.Worker/"]

# Publish worker in Release mode
RUN dotnet publish "src/Relay.Worker/Relay.Worker.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

# Run as non-root user (built into Microsoft .NET images)
USER $APP_UID

ENTRYPOINT ["dotnet", "Relay.Worker.dll"]
