# SPDX-License-Identifier: MIT
# Copyright (c) 2026 AgentEval Contributors
# Licensed under the MIT License.
#
# Plan-08 MC1.12.1 — Mission Control Docker image.
#
# Three-stage build:
#   1. node — produces the React SPA bundle into wwwroot/ via `npm run build`.
#   2. sdk  — restores + publishes AgentEval.MissionControl with the SPA folded in.
#   3. runtime — minimal ASP.NET runtime, single-port (5000) bind, non-root user.
#
# Build:  docker build -t agenteval/mc:latest .
# Run:    docker run --rm -p 5000:5000 -v $(pwd)/.agenteval:/workspace/.agenteval:ro agenteval/mc:latest
# Browse: http://localhost:5000

# ─── Stage 1: SPA build ───────────────────────────────────────────────────────
FROM node:22-alpine AS spa
WORKDIR /spa

# Restore deps first so the layer caches across source-only changes.
COPY src/AgentEval.MissionControl.Spa/package.json src/AgentEval.MissionControl.Spa/package-lock.json* ./
RUN npm ci --no-audit --no-fund

# Now copy SPA sources + Vite output target. vite.config.ts writes the build
# into ../AgentEval.MissionControl/wwwroot/ — replicate that directory layout
# so the relative path resolves identically inside the container.
WORKDIR /work
COPY src/AgentEval.MissionControl.Spa/ ./AgentEval.MissionControl.Spa/
RUN cp -r /spa/node_modules ./AgentEval.MissionControl.Spa/node_modules
RUN mkdir -p ./AgentEval.MissionControl/wwwroot

WORKDIR /work/AgentEval.MissionControl.Spa
RUN npm run build


# ─── Stage 2: .NET publish ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sdk
WORKDIR /src

# Copy package-version pins + project descriptors first for restore-layer caching.
COPY *.props *.targets ./
COPY src/ ./src/
COPY samples/ ./samples/

# Pull the SPA build artefacts in BEFORE publish so the SDK's static-asset
# pipeline picks them up via the wwwroot/ glob already declared in the csproj.
COPY --from=spa /work/AgentEval.MissionControl/wwwroot/ ./src/AgentEval.MissionControl/wwwroot/

WORKDIR /src/src/AgentEval.MissionControl
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false


# ─── Stage 3: runtime ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Run as a non-root user. The official aspnet image already ships a `app`
# user (UID 1654) — use it.
USER app

COPY --from=sdk --chown=app:app /app/publish ./

# Default the workspace to /workspace; users mount their .agenteval/ here.
# AgentEval__Root → AgentEval:Root (env-var → IConfiguration mapping).
ENV AgentEval__Root=/workspace \
    ASPNETCORE_URLS=http://0.0.0.0:5000 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true

EXPOSE 5000

ENTRYPOINT ["dotnet", "AgentEval.MissionControl.dll"]
