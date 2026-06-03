# syntax=docker/dockerfile:1
# ── Stage 1: restore (cache layer) ───────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /workspace

COPY Directory.Build.props ./
COPY src/ManufacturingAI.Core/ManufacturingAI.Core.csproj                               src/ManufacturingAI.Core/
COPY src/ManufacturingAI.Core.RAG/ManufacturingAI.Core.RAG.csproj                       src/ManufacturingAI.Core.RAG/
COPY src/ManufacturingAI.Core.Connectors/ManufacturingAI.Core.Connectors.csproj         src/ManufacturingAI.Core.Connectors/
COPY src/ManufacturingAI.Core.Parser/ManufacturingAI.Core.Parser.csproj                 src/ManufacturingAI.Core.Parser/
COPY src/ManufacturingAI.Infrastructure/ManufacturingAI.Infrastructure.csproj           src/ManufacturingAI.Infrastructure/
COPY src/ManufacturingAI.Services.Ingest/ManufacturingAI.Services.Ingest.csproj         src/ManufacturingAI.Services.Ingest/
COPY src/ManufacturingAI.Services.Query/ManufacturingAI.Services.Query.csproj           src/ManufacturingAI.Services.Query/
COPY src/ManufacturingAI.Services.Analytics/ManufacturingAI.Services.Analytics.csproj   src/ManufacturingAI.Services.Analytics/
COPY src/ManufacturingAI.Services.TestGen/ManufacturingAI.Services.TestGen.csproj       src/ManufacturingAI.Services.TestGen/
COPY src/ManufacturingAI.Connectors.Folder/ManufacturingAI.Connectors.Folder.csproj     src/ManufacturingAI.Connectors.Folder/
COPY src/ManufacturingAI.Connectors.SharePoint/ManufacturingAI.Connectors.SharePoint.csproj src/ManufacturingAI.Connectors.SharePoint/
COPY src/ManufacturingAI.Connectors.Confluence/ManufacturingAI.Connectors.Confluence.csproj src/ManufacturingAI.Connectors.Confluence/
COPY src/ManufacturingAI.Connectors.GoogleDrive/ManufacturingAI.Connectors.GoogleDrive.csproj src/ManufacturingAI.Connectors.GoogleDrive/
COPY src/ManufacturingAI.Connectors.Arena/ManufacturingAI.Connectors.Arena.csproj       src/ManufacturingAI.Connectors.Arena/
COPY src/ManufacturingAI.API/ManufacturingAI.API.csproj                                 src/ManufacturingAI.API/

RUN dotnet restore src/ManufacturingAI.API/ManufacturingAI.API.csproj

# ── Stage 2: build / publish ─────────────────────────────────
FROM restore AS build
COPY src/ src/
RUN dotnet publish src/ManufacturingAI.API/ManufacturingAI.API.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 3: development (hot-reload with dotnet watch) ──────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS development
WORKDIR /workspace
# Pre-warm package cache; actual source is volume-mounted at runtime
COPY --from=restore /root/.nuget /root/.nuget
COPY Directory.Build.props ./
EXPOSE 8080 5005
ENV ASPNETCORE_ENVIRONMENT=Development \
    DOTNET_USE_POLLING_FILE_WATCHER=1
ENTRYPOINT ["dotnet", "watch", "--project", "src/ManufacturingAI.API", \
            "run", "--urls", "http://+:8080"]

# ── Stage 4: production runtime ──────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

# curl is needed for the Docker health-check
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ManufacturingAI.API.dll"]
