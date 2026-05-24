# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MP-Server.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Resources (items.json etc.) may not be copied by publish if not marked as content
COPY Resources/ ./Resources/

# Log directory — mount a volume here to persist logs outside the container
RUN mkdir -p /app/logs

# TCP game port | UDP game port | HTTP dashboard
EXPOSE 7777/tcp
EXPOSE 7778/udp
EXPOSE 8080/tcp

ENTRYPOINT ["dotnet", "MP-Server.dll"]
