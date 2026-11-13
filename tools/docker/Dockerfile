# The one place the project's builds and tests run when an agent starts them.
# Each base image is pinned; raise a pin on purpose, in a change of its own.
FROM oven/bun:1.3.12 AS bun
FROM node:24-slim AS node
# SDK 10.0.203: the 10.0.2xx band, the one CI builds with (.github/workflows/ci.yml).
FROM mcr.microsoft.com/dotnet/sdk@sha256:8a90a473da5205a16979de99d2fc20975e922c68304f5c79d564e666dc3982fc
RUN apt-get update \
 && apt-get install -y --no-install-recommends git jq perl unzip zip \
 && rm -rf /var/lib/apt/lists/*
COPY --from=bun /usr/local/bin/bun /usr/local/bin/bun
COPY --from=node /usr/local/bin/node /usr/local/bin/node
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
