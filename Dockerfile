# syntax=docker/dockerfile:1

# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached until the project file changes.
COPY server/PrimeTime.Server/PrimeTime.Server.csproj server/PrimeTime.Server/
RUN dotnet restore server/PrimeTime.Server/PrimeTime.Server.csproj

COPY server/ server/
RUN dotnet publish server/PrimeTime.Server/PrimeTime.Server.csproj \
    --configuration Release --no-restore --output /app /p:UseAppHost=false

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV PRIMETIME_DATA_DIR=/data \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# The base image ships a non-root "app" user; give it the data directory.
RUN mkdir -p /data && chown app:app /data
COPY --from=build /app .

USER app
VOLUME /data
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "PrimeTime.Server.dll", "healthcheck"]

ENTRYPOINT ["dotnet", "PrimeTime.Server.dll"]
