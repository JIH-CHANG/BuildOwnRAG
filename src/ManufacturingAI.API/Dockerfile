# 請參閱 https://aka.ms/customizecontainer 了解如何自訂您的偵錯容器，以及 Visual Studio 如何使用此 Dockerfile 來組建您的映像，以加快偵錯速度。

# 此階段用於以快速模式從 VS 執行時 (偵錯設定的預設值)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# 此階段是用來組建服務專案
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/ManufacturingAI.API/ManufacturingAI.API.csproj", "src/ManufacturingAI.API/"]
COPY ["src/ManufacturingAI.Core.RAG/ManufacturingAI.Core.RAG.csproj", "src/ManufacturingAI.Core.RAG/"]
COPY ["src/ManufacturingAI.Core/ManufacturingAI.Core.csproj", "src/ManufacturingAI.Core/"]
COPY ["src/ManufacturingAI.Infrastructure/ManufacturingAI.Infrastructure.csproj", "src/ManufacturingAI.Infrastructure/"]
COPY ["src/ManufacturingAI.Services.Ingest/ManufacturingAI.Services.Ingest.csproj", "src/ManufacturingAI.Services.Ingest/"]
COPY ["src/ManufacturingAI.Core.Parser/ManufacturingAI.Core.Parser.csproj", "src/ManufacturingAI.Core.Parser/"]
COPY ["src/ManufacturingAI.Connectors.Folder/ManufacturingAI.Connectors.Folder.csproj", "src/ManufacturingAI.Connectors.Folder/"]
COPY ["src/ManufacturingAI.Services.Query/ManufacturingAI.Services.Query.csproj", "src/ManufacturingAI.Services.Query/"]
COPY ["src/ManufacturingAI.Services.TestGen/ManufacturingAI.Services.TestGen.csproj", "src/ManufacturingAI.Services.TestGen/"]
COPY ["src/ManufacturingAI.Services.Analytics/ManufacturingAI.Services.Analytics.csproj", "src/ManufacturingAI.Services.Analytics/"]
RUN dotnet restore "./src/ManufacturingAI.API/ManufacturingAI.API.csproj"
COPY . .
WORKDIR "/src/src/ManufacturingAI.API"
RUN dotnet build "./ManufacturingAI.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 此階段可用來發佈要複製到最終階段的服務專案
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ManufacturingAI.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 此階段用於生產環境，或以一般模式從 VS 執行時 (未使用偵錯設定時的預設值)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ManufacturingAI.API.dll"]

