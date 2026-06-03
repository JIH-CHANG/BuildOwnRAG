# syntax=docker/dockerfile:1
# ── Stage 1: restore ─────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /workspace

COPY Directory.Build.props ./
COPY src/ManufacturingAI.Core/ManufacturingAI.Core.csproj               src/ManufacturingAI.Core/
COPY src/ManufacturingAI.Infrastructure/ManufacturingAI.Infrastructure.csproj src/ManufacturingAI.Infrastructure/
COPY src/ManufacturingAI.Setup/ManufacturingAI.Setup.csproj             src/ManufacturingAI.Setup/

RUN dotnet restore src/ManufacturingAI.Setup/ManufacturingAI.Setup.csproj

# ── Stage 2: publish ─────────────────────────────────────────
FROM restore AS build
COPY src/ src/
RUN dotnet publish src/ManufacturingAI.Setup/ManufacturingAI.Setup.csproj \
    -c Release -o /app/publish --no-restore

# ── Stage 3: runtime ─────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8081
ENV ASPNETCORE_URLS=http://+:8081 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    IS_SETUP_COMPLETED=false

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ManufacturingAI.Setup.dll"]
