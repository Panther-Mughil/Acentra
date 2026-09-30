# Multi-stage build for the Acentra web app (Blazor Server / ASP.NET Core on .NET 10).
#
#   podman build -t acentra-web:dev .
#
# Build stage restores the whole src/ tree (central package versions live in
# Directory.Packages.props at the repo root, so it must be copied too) and
# publishes only the web project. The runtime stage carries the published
# output and no SDK.
#
# Runtime configuration is supplied entirely through environment variables
# (see the `web` service in compose.yaml); appsettings*.json are not rewritten.

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

# ---------------------------------------------------------------- build stage
FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_XMLDOC_MODE=skip

# Restore inputs first so the (slow) restore layer is cached across source edits.
# Directory.Packages.props enables central package management for every project
# below src/, so it must sit at /src — not inside src/.
COPY Directory.Packages.props ./
COPY Acentra.slnx ./
COPY src/ src/

RUN dotnet restore src/Acentra.Web/Acentra.Web.csproj

# --no-restore: the restore layer above is authoritative. UseAppHost=false drops
# the native launcher; the container starts via `dotnet Acentra.Web.dll`.
RUN dotnet publish src/Acentra.Web/Acentra.Web.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        /p:UseAppHost=false

# -------------------------------------------------------------- runtime stage
FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
WORKDIR /app

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Acentra.Web.dll"]
