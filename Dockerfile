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
