FROM mcr.microsoft.com/dotnet/sdk:8.0.303 AS tooling
WORKDIR /source
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY global.json Service.sln ./
COPY .config/ .config/
COPY src/Service.Api/Service.Api.csproj src/Service.Api/
COPY tests/Service.Api.UnitTests/Service.Api.UnitTests.csproj tests/Service.Api.UnitTests/
COPY tests/Service.Api.IntegrationTests/Service.Api.IntegrationTests.csproj tests/Service.Api.IntegrationTests/
RUN dotnet tool restore && dotnet restore Service.sln
COPY src/ src/
COPY tests/ tests/

FROM tooling AS development
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_USE_POLLING_FILE_WATCHER=1 DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1
ENV UseArtifactsOutput=true ArtifactsPath=/artifacts
EXPOSE 8080
CMD ["dotnet", "watch", "--non-interactive", "--project", "src/Service.Api", "run", "--no-launch-profile"]

FROM mcr.microsoft.com/dotnet/sdk:8.0.303 AS build
WORKDIR /source
COPY global.json ./
COPY src/Service.Api/Service.Api.csproj src/Service.Api/
RUN dotnet restore src/Service.Api/Service.Api.csproj
COPY src/Service.Api/ src/Service.Api/
RUN dotnet publish src/Service.Api/Service.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Service.Api.dll"]
