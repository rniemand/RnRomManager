# syntax=docker/dockerfile:1
# Spec §17.1. Build stages run on the build machine's platform and cross-compile (no QEMU).

# --- UI -----------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM node:24 AS web
WORKDIR /src/web
COPY src/RnRomManager.Web/package.json src/RnRomManager.Web/package-lock.json src/RnRomManager.Web/.npmrc ./
RUN npm ci
COPY src/RnRomManager.Web/ ./
RUN npm run build

# --- API ----------------------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/RnRomManager.Api/RnRomManager.Api.csproj src/RnRomManager.Api/
COPY src/RnRomManager.Core/RnRomManager.Core.csproj src/RnRomManager.Core/
COPY src/RnRomManager.Data/RnRomManager.Data.csproj src/RnRomManager.Data/
COPY src/RnRomManager.Systems.GameBoy/RnRomManager.Systems.GameBoy.csproj src/RnRomManager.Systems.GameBoy/
COPY src/RnRomManager.Systems.Nes/RnRomManager.Systems.Nes.csproj src/RnRomManager.Systems.Nes/
COPY src/RnRomManager.Systems.Nintendo64/RnRomManager.Systems.Nintendo64.csproj src/RnRomManager.Systems.Nintendo64/
COPY src/RnRomManager.Systems.Snes/RnRomManager.Systems.Snes.csproj src/RnRomManager.Systems.Snes/
COPY src/RnRomManager.Systems.Atari/RnRomManager.Systems.Atari.csproj src/RnRomManager.Systems.Atari/
COPY src/RnRomManager.Systems.Coleco/RnRomManager.Systems.Coleco.csproj src/RnRomManager.Systems.Coleco/
COPY src/RnRomManager.Systems.Sega/RnRomManager.Systems.Sega.csproj src/RnRomManager.Systems.Sega/
COPY src/RnRomManager.Systems.Nec/RnRomManager.Systems.Nec.csproj src/RnRomManager.Systems.Nec/
COPY src/RnRomManager.Systems.Switch/RnRomManager.Systems.Switch.csproj src/RnRomManager.Systems.Switch/
COPY src/RnRomManager.Systems.Sony/RnRomManager.Systems.Sony.csproj src/RnRomManager.Systems.Sony/
COPY src/RnRomManager.Systems.Microsoft/RnRomManager.Systems.Microsoft.csproj src/RnRomManager.Systems.Microsoft/
COPY src/RnRomManager.Systems.NintendoDisc/RnRomManager.Systems.NintendoDisc.csproj src/RnRomManager.Systems.NintendoDisc/
COPY src/RnRomManager.Systems.VirtualBoy/RnRomManager.Systems.VirtualBoy.csproj src/RnRomManager.Systems.VirtualBoy/
# Docker's TARGETARCH names -> .NET's (.NET rejects amd64).
RUN case "$TARGETARCH" in amd64) echo x64 ;; arm64) echo arm64 ;; *) echo "unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; esac > /tmp/arch \
 && dotnet restore src/RnRomManager.Api -a "$(cat /tmp/arch)"
COPY src/ src/
RUN dotnet publish src/RnRomManager.Api -c Release -a "$(cat /tmp/arch)" --no-restore -p:Version="$VERSION" -o /app \
 && mkdir -p /volumes/roms /volumes/ingest /volumes/logs /volumes/cache /volumes/backups /volumes/collections /volumes/bios

# --- Runtime ------------------------------------------------------------------------------------
# -extra: ICU (NFC normalisation, §6.1) + tzdata (TZ for the schedule, §17.2). Chiseled: no shell.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=publish /app ./
COPY --from=web /src/web/build ./wwwroot
# Pre-owned so new named volumes inherit writable ownership (§17.2).
COPY --from=publish --chown=1654:1654 /volumes/ ./
USER 1654
EXPOSE 8080
VOLUME ["/app/roms", "/app/ingest", "/app/logs", "/app/cache", "/app/backups", "/app/collections", "/app/bios"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD ["/app/RnRomManager.Api", "--healthcheck"]
ENTRYPOINT ["/app/RnRomManager.Api"]
