# Multi-stage Dockerfile for Relay.Admin (Migration & Admin CLI)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["Relay.sln", "./"]

COPY ["src/Relay.Domain/Relay.Domain.csproj", "src/Relay.Domain/"]
COPY ["src/Relay.Application/Relay.Application.csproj", "src/Relay.Application/"]
COPY ["src/Relay.Infrastructure/Relay.Infrastructure.csproj", "src/Relay.Infrastructure/"]
COPY ["src/Relay.Admin/Relay.Admin.csproj", "src/Relay.Admin/"]

RUN dotnet restore "src/Relay.Admin/Relay.Admin.csproj"

COPY ["src/Relay.Domain/", "src/Relay.Domain/"]
COPY ["src/Relay.Application/", "src/Relay.Application/"]
COPY ["src/Relay.Infrastructure/", "src/Relay.Infrastructure/"]
COPY ["src/Relay.Admin/", "src/Relay.Admin/"]

RUN dotnet publish "src/Relay.Admin/Relay.Admin.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "relay-admin.dll"]
CMD ["migrate"]
