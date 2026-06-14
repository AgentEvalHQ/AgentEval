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
# Run:    docker run --rm -p 127.0.0.1:5000:5000 -v $(pwd)/.agenteval:/workspace/.agenteval:ro agenteval/mc:latest
# Browse: http://localhost:5000
#
# SECURITY: the `-p 127.0.0.1:5000:5000` prefix is intentional — Mission
# Control is unauthenticated (Phase 1 / Mode A trusts the operator). Binding
# to the loopback interface keeps the portal off the LAN. Use `-p
# 0.0.0.0:5000:5000` only when you intentionally want LAN exposure AND have
# put an authenticating reverse proxy in front.

# ─── Stage 1: SPA build ───────────────────────────────────────────────────────
# SEC-14: base images use floating major tags for readability. For reproducible
# builds, pin each FROM to a digest (e.g. `node:22-alpine@sha256:<digest>`) and
# let Dependabot keep the digests current — `.github/dependabot.yml` enables the
# `docker` ecosystem for this file, which opens PRs to refresh the pinned digests.
FROM node:26-alpine AS spa
WORKDIR /spa

# Restore deps first so the layer caches across source-only changes.
COPY src/AgentEval.MissionControl.Spa/package.json src/AgentEval.MissionControl.Spa/package-lock.json* ./
RUN npm ci --no-audit --no-fund

# Now copy SPA sources + Vite output target. vite.config.ts writes the build
# into ../AgentEval.MissionControl/wwwroot/ — replicate that directory layout
# so the relative path resolves identically inside the container.
WORKDIR /work
COPY src/AgentEval.MissionControl.Spa/ ./AgentEval.MissionControl.Spa/
# Move (not copy) the cached node_modules so we don't pay double disk usage.
# `.dockerignore` excludes **/node_modules/** so the COPY above leaves
# AgentEval.MissionControl.Spa/node_modules unset.
RUN mv /spa/node_modules ./AgentEval.MissionControl.Spa/node_modules \
 && mkdir -p ./AgentEval.MissionControl/wwwroot

WORKDIR /work/AgentEval.MissionControl.Spa
RUN npm run build


# ─── Stage 2: .NET publish ────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sdk
WORKDIR /src

# Copy package-version pins + SDK pin + project descriptors first for
# restore-layer caching. (Earlier draft used `COPY *.props *.targets ./` —
# but no `.targets` exists at the repo root, and modern BuildKit errors
# on zero-match patterns. Enumerate explicitly.)
COPY Directory.Build.props Directory.Packages.props global.json ./

# Mission Control's transitive dependency closure is Abstractions → Core →
# DataLoaders → Evals.Agentic — all under src/. Samples are NOT needed for
# `dotnet restore` or `publish`, so don't ship ~hundreds of MB of unrelated
# benchmark sources into the build context.
COPY src/ ./src/

# Pull the SPA build artefacts in BEFORE publish so the SDK's static-asset
# pipeline picks them up via the wwwroot/ glob already declared in the csproj.
COPY --from=spa /work/AgentEval.MissionControl/wwwroot/ ./src/AgentEval.MissionControl/wwwroot/

WORKDIR /src/src/AgentEval.MissionControl
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false


# ─── Stage 3: runtime ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SEC-14: no curl. The HEALTHCHECK below uses a self-contained internal probe
# (`dotnet AgentEval.MissionControl.dll --health-check`) instead of curl, so
# the runtime stage installs NO extra apt packages — smaller attack surface,
# no unpinned package, no apt cache to clean.

# Run as a non-root user. The official aspnet image ships an `app` user at
# UID 1654 — refer to it numerically so the image still works under
# `--read-only` policies that block name resolution.
USER 1654:1654

COPY --from=sdk --chown=1654:1654 /app/publish ./

# Default the workspace to /workspace; users mount their .agenteval/ here.
# AgentEval__Root → AgentEval:Root (env-var → IConfiguration mapping).
ENV AgentEval__Root=/workspace \
    ASPNETCORE_URLS=http://0.0.0.0:5000 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=true

EXPOSE 5000

# T3.9 / SEC-14 — Container health probe. `/api/v1/version` is the cheapest
# 200-OK endpoint and exercises Kestrel + the GraphQL host wiring (the version
# resolver itself is constant-time). The probe is the app itself in
# `--health-check` mode (McHealthCheck): it always targets loopback
# (127.0.0.1) — even though ASPNETCORE_URLS binds 0.0.0.0 — so it never leaves
# the netns, and it adds no curl/wget binary to the image.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "AgentEval.MissionControl.dll", "--health-check"]

ENTRYPOINT ["dotnet", "AgentEval.MissionControl.dll"]
