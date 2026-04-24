# -- Node build stage (Angular SPA) ────────────────────────────────────────────
FROM node:24-alpine AS node-build
WORKDIR /node-build

# Trust corporate CAs (must happen before apk/npm can reach HTTPS registries)
COPY --from=certs . /tmp/certs/
RUN find /tmp/certs/ -name '.git*' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name 'README.md' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name '.gitkeep' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name '.gitignore' -delete 2>/dev/null || true && \
    for f in /tmp/certs/*.crt /tmp/certs/*.pem /tmp/certs/*.cer; do \
      [ -f "$f" ] && cat "$f" >> /etc/ssl/certs/ca-certificates.crt || true; \
    done && \
    rm -rf /tmp/certs/

ENV NODE_EXTRA_CA_CERTS=/etc/ssl/certs/ca-certificates.crt

COPY client/package.json client/package-lock.json ./
RUN npm ci
COPY client/ ./
RUN npx ng build --configuration docker

# -- .NET build stage ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /build

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates openssl && rm -rf /var/lib/apt/lists/*

# Trust corporate CAs for NuGet restore
COPY --from=certs . /tmp/certs/
RUN find /tmp/certs/ -name '.git*' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name 'README.md' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name '.gitkeep' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name '.gitignore' -delete 2>/dev/null || true && \
    for f in /tmp/certs/*.crt /tmp/certs/*.pem /tmp/certs/*.cer; do \
      [ -f "$f" ] || continue; \
      cp "$f" /usr/local/share/ca-certificates/"$(basename "$f").crt" 2>/dev/null || true; \
    done && \
    update-ca-certificates && \
    rm -rf /tmp/certs/

ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    SSL_CERT_DIR=/etc/ssl/certs \
    DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0 \
    NUGET_CERT_REVOCATION_MODE=off \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NUGET_SIGNATURE_VERIFICATION=false

COPY Directory.Build.props ./
RUN mkdir -p local-packages

# Andy.Settings.Client — local pre-release feed produced by
# `bash ../andy-settings/scripts/pack-local.sh`. docker-compose mounts the
# folder via `additional_contexts: andy-settings-artifacts: ../andy-settings/artifacts`
# so restore can pull the .nupkg from a local source. Mirrors the andy-rbac
# pattern. Once CI publishes Andy.Settings.Client to nuget.org, this can
# fall back to the public feed.
COPY --from=andy-settings-artifacts . /andy-settings-artifacts/
RUN printf '<?xml version="1.0" encoding="utf-8"?>\n\
<configuration>\n\
  <packageSources>\n\
    <clear />\n\
    <add key="andy-settings-local" value="/andy-settings-artifacts" />\n\
    <add key="local" value="/build/local-packages" />\n\
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />\n\
  </packageSources>\n\
</configuration>\n' > /build/nuget.config

COPY src/Andy.Policies.Api/Andy.Policies.Api.csproj src/Andy.Policies.Api/
COPY src/Andy.Policies.Application/Andy.Policies.Application.csproj src/Andy.Policies.Application/
COPY src/Andy.Policies.Domain/Andy.Policies.Domain.csproj src/Andy.Policies.Domain/
COPY src/Andy.Policies.Infrastructure/Andy.Policies.Infrastructure.csproj src/Andy.Policies.Infrastructure/
COPY src/Andy.Policies.Shared/Andy.Policies.Shared.csproj src/Andy.Policies.Shared/
RUN dotnet restore src/Andy.Policies.Api/Andy.Policies.Api.csproj

COPY . .
RUN dotnet publish src/Andy.Policies.Api/Andy.Policies.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# -- Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl openssl && rm -rf /var/lib/apt/lists/*

# Copy corporate CA certs and install them
COPY --from=certs . /tmp/certs/
RUN find /tmp/certs/ -name '.git*' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name 'README.md' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name '.gitkeep' -delete 2>/dev/null || true && \
    find /tmp/certs/ -name '.gitignore' -delete 2>/dev/null || true && \
    mkdir -p /usr/local/share/ca-certificates/corporate && \
    for f in /tmp/certs/*.pem /tmp/certs/*.crt /tmp/certs/*.cer; do \
      [ -f "$f" ] || continue; \
      cp "$f" /usr/local/share/ca-certificates/corporate/"$(basename "$f").crt" 2>/dev/null || true; \
      cat "$f" >> /etc/ssl/certs/ca-certificates.crt 2>/dev/null || true; \
      echo "Installed cert: $(basename "$f")" ; \
    done && \
    update-ca-certificates 2>/dev/null || true && \
    rm -rf /tmp/certs/

# Non-root user
RUN groupadd -r appuser && useradd -r -g appuser -d /app -s /sbin/nologin appuser
RUN mkdir -p /https /app/.aspnet/DataProtection-Keys && \
    chown appuser:appuser /app/.aspnet/DataProtection-Keys

COPY --from=build /app/publish .
COPY --from=node-build /node-build/dist/client/browser ./wwwroot
COPY content/help ./content/help
RUN chown -R appuser:appuser /app

# Self-signed dev cert
RUN openssl req -x509 -nodes -days 3650 -newkey rsa:2048 \
      -keyout /tmp/dev.key -out /tmp/dev.crt \
      -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" && \
    openssl pkcs12 -export -out /https/aspnetapp.pfx \
      -inkey /tmp/dev.key -in /tmp/dev.crt -passout pass:devcert && \
    rm -f /tmp/dev.key /tmp/dev.crt && \
    chown appuser:appuser /https/aspnetapp.pfx

# Entrypoint: trust runtime-mounted custom CAs, then start the app
RUN printf '#!/bin/sh\nset -e\nif ls /usr/local/share/ca-certificates/custom/*.crt 1>/dev/null 2>&1 || ls /usr/local/share/ca-certificates/custom/*.pem 1>/dev/null 2>&1 || ls /usr/local/share/ca-certificates/custom/*.cer 1>/dev/null 2>&1; then\n    for f in /usr/local/share/ca-certificates/custom/*.pem /usr/local/share/ca-certificates/custom/*.crt /usr/local/share/ca-certificates/custom/*.cer; do\n        [ -f "$f" ] && cat "$f" >> /etc/ssl/certs/ca-certificates.crt 2>/dev/null || true\n    done\n    update-ca-certificates 2>/dev/null || true\nfi\nexec "$@"\n' > /docker-entrypoint.sh && \
    chmod +x /docker-entrypoint.sh

ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    SSL_CERT_DIR=/etc/ssl/certs \
    ASPNETCORE_Kestrel__Certificates__Default__Path=/https/aspnetapp.pfx \
    ASPNETCORE_Kestrel__Certificates__Default__Password=devcert

EXPOSE 8080 8443
USER appuser

ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["dotnet", "Andy.Policies.Api.dll"]
