# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first (layer-cached on unchanged project files).
COPY BtmPizza.Notifications.sln global.json ./
COPY src/BtmPizza.Notifications/BtmPizza.Notifications.csproj src/BtmPizza.Notifications/
RUN dotnet restore src/BtmPizza.Notifications/BtmPizza.Notifications.csproj

# Build + publish.
COPY src/ src/
RUN dotnet publish src/BtmPizza.Notifications/BtmPizza.Notifications.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Kestrel listens on 8080 inside the container.
# credentials.json (and the APNs .p8 key) are mounted from a Secret at /app/config;
# the SQLite database lives on a volume at /app/data. Neither is baked into the image.
ENV ASPNETCORE_ENVIRONMENT=Staging \
    ASPNETCORE_URLS=http://+:8080 \
    CredentialsPath=/app/config/credentials.json \
    DbPath=/app/data/notifications.db

COPY --from=build /app/publish ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "BtmPizza.Notifications.dll"]
