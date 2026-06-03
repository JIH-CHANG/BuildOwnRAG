# syntax=docker/dockerfile:1
# Build context must be the repo root:
#   docker build -f src/ManufacturingAI.Connectors.Folder/Dockerfile -t manufacturingai/connector-folder .
#
# NOTE: This project is currently a class library.  Before this image is
# runnable, add a Worker Service entry point (Program.cs with
# Host.CreateDefaultBuilder + AddHostedService<FolderWatcherService>).

# ── Stage 1: restore (cache layer) ───────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /workspace

COPY Directory.Build.props ./
COPY src/ManufacturingAI.Core/ManufacturingAI.Core.csproj                               src/ManufacturingAI.Core/
COPY src/ManufacturingAI.Infrastructure/ManufacturingAI.Infrastructure.csproj           src/ManufacturingAI.Infrastructure/
COPY src/ManufacturingAI.Services.Ingest/ManufacturingAI.Services.Ingest.csproj         src/ManufacturingAI.Services.Ingest/
COPY src/ManufacturingAI.Core.Connectors/ManufacturingAI.Core.Connectors.csproj         src/ManufacturingAI.Core.Connectors/
COPY src/ManufacturingAI.Core.Parser/ManufacturingAI.Core.Parser.csproj                 src/ManufacturingAI.Core.Parser/
COPY src/ManufacturingAI.Core.RAG/ManufacturingAI.Core.RAG.csproj                       src/ManufacturingAI.Core.RAG/
COPY src/ManufacturingAI.Connectors.Folder/ManufacturingAI.Connectors.Folder.csproj     src/ManufacturingAI.Connectors.Folder/

RUN dotnet restore src/ManufacturingAI.Connectors.Folder/ManufacturingAI.Connectors.Folder.csproj

# ── Stage 2: build / publish ──────────────────────────────────
FROM restore AS build
COPY src/ src/
RUN dotnet publish src/ManufacturingAI.Connectors.Folder/ManufacturingAI.Connectors.Folder.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 3: production runtime ───────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Mount the watched folder from the host, e.g.:
#   -v /host/documents:/watch -e FolderConnector__WatchPath=/watch
VOLUME ["/watch"]

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ManufacturingAI.Connectors.Folder.dll"]
