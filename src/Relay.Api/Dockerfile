# Multi-stage Dockerfile for Relay.Api
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
COPY ["src/Relay.Api/Relay.Api.csproj", "src/Relay.Api/"]

# Restore packages
RUN dotnet restore "src/Relay.Api/Relay.Api.csproj"

# Copy full source
COPY ["src/Relay.Domain/", "src/Relay.Domain/"]
COPY ["src/Relay.Application/", "src/Relay.Application/"]
COPY ["src/Relay.Infrastructure/", "src/Relay.Infrastructure/"]
COPY ["src/Relay.Api/", "src/Relay.Api/"]

# Publish application in Release mode
RUN dotnet publish "src/Relay.Api/Relay.Api.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENV ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

# Run as non-root user (built into Microsoft .NET images)
USER $APP_UID

ENTRYPOINT ["dotnet", "Relay.Api.dll"]
