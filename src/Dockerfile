# syntax=docker/dockerfile:1
# Images of the two .NET hosts for docker-compose.yml (docs/09 "With Docker Compose"). One file, two targets that
# share the restore: `--target api` (DocProc.Api) and `--target worker` (DocProc.Worker). Build context: the
# repository root (`docker build -f src/Dockerfile --target api .`).
#
# Both run on the ASP.NET Core runtime image: the worker references Microsoft.AspNetCore.App through
# DocProc.ServiceDefaults (health checks, OpenTelemetry), so the plain runtime image cannot start it.
#
# Layer order keeps rebuilds short: the restore depends only on the project files and the central package versions,
# so a source change starts at the publish; each host's publish copies src/ without the other host's folder, so a
# change under src/DocProc.Api does not publish the worker again (and the reverse).
#
# The base images are pinned to patch releases (the SDK satisfies global.json); bump them together when a .NET
# servicing release ships.

ARG DOTNET_SDK_VERSION=10.0.403
ARG DOTNET_RUNTIME_VERSION=10.0.14

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
ARG TARGETARCH
# Optional: extra CA certificates (PEM) for restoring behind a TLS-inspecting proxy, passed as the build secret
# `extra_ca_certs`; without it this step does nothing (docs/09 "Building behind a TLS-inspecting proxy").
RUN --mount=type=secret,id=extra_ca_certs,required=false \
    if [ -s /run/secrets/extra_ca_certs ]; then \
      cp /run/secrets/extra_ca_certs /usr/local/share/ca-certificates/extra-ca-certs.crt && update-ca-certificates; \
    fi
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /repo
# .editorconfig carries the analyzer severities the build enforces (warnings are errors).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
# Only the project files, at their paths (src/<project>/<project>.csproj): all the restore reads.
COPY --parents src/*/*.csproj ./
# Restore for the target architecture only, so the image carries one set of native libraries (PDFium, SkiaSharp).
RUN --mount=type=cache,id=docproc-nuget,target=/root/.nuget/packages \
    dotnet restore src/DocProc.Api/DocProc.Api.csproj -a "$TARGETARCH" \
    && dotnet restore src/DocProc.Worker/DocProc.Worker.csproj -a "$TARGETARCH"

# The XML documentation files stay out of /app: the OpenAPI document's descriptions are compiled into the API, and
# nothing reads them at run time. The .pdb files stay, so exceptions in the logs carry file and line numbers.
FROM build AS publish-api
COPY --exclude=src/DocProc.Worker --exclude=src/DocProc.AppHost --exclude=src/Dockerfile src/ src/
RUN --mount=type=cache,id=docproc-nuget,target=/root/.nuget/packages \
    dotnet publish src/DocProc.Api/DocProc.Api.csproj -c Release -a "$TARGETARCH" --self-contained false --no-restore -p:UseAppHost=false \
      -p:PublishDocumentationFile=false -p:PublishReferencesDocumentationFiles=false -o /out

FROM build AS publish-worker
COPY --exclude=src/DocProc.Api --exclude=src/DocProc.AppHost --exclude=src/Dockerfile src/ src/
RUN --mount=type=cache,id=docproc-nuget,target=/root/.nuget/packages \
    dotnet publish src/DocProc.Worker/DocProc.Worker.csproj -c Release -a "$TARGETARCH" --self-contained false --no-restore -p:UseAppHost=false \
      -p:PublishDocumentationFile=false -p:PublishReferencesDocumentationFiles=false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION} AS runtime-base
# curl for the compose health checks (`curl -f http://localhost:8080/health/ready`).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
# The file-backed secret store (Secrets:Path) lives in a volume both hosts mount at /data/secrets, the file drop of
# the blob delivery transport (Delivery:DropRoot) in one at /data/outbound. Creating the folders here, owned by the
# non-root user, makes a fresh named volume inherit that owner (and the secrets their owner-only mode).
RUN mkdir -p /data/secrets /data/outbound && chown -R "$APP_UID:$APP_UID" /data && chmod 700 /data/secrets
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Secrets__Path=/data/secrets
USER $APP_UID

FROM runtime-base AS api
COPY --from=publish-api /out ./
# The demo documents and their LLM replay fixtures (docs/09 "Demo data"); read only when
# Persistence:SeedDemoDocuments is on. Relative paths are searched upwards from the content root (/app).
COPY fixtures/demo ./fixtures/demo
EXPOSE 8080
ENTRYPOINT ["dotnet", "DocProc.Api.dll"]

FROM runtime-base AS worker
COPY --from=publish-worker /out ./
ENTRYPOINT ["dotnet", "DocProc.Worker.dll"]
