# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ValetudoUI/ValetudoUI.csproj ValetudoUI/
RUN dotnet restore ValetudoUI/ValetudoUI.csproj
COPY ValetudoUI/ ValetudoUI/
RUN dotnet publish ValetudoUI/ValetudoUI.csproj -c Release -o /app/publish /p:UseAppHost=false

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
# SQLite database lives in /data (a volume); the app user must own it.
RUN mkdir -p /data && chown app:app /data
USER app
# Database and data-protection keys in the /data volume (the image already listens on 8080).
ENV History__DbFileName=/data/valetudo-ui.db \
    DataProtection__KeysDir=/data/keys
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "ValetudoUI.dll"]
