# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore dependencies first (cached layer when only source changes)
COPY AnalepticLogParser/AnalepticLogParser.csproj AnalepticLogParser/
RUN dotnet restore AnalepticLogParser/AnalepticLogParser.csproj

# Copy source and publish self-contained release
COPY AnalepticLogParser/ AnalepticLogParser/
RUN dotnet publish AnalepticLogParser/AnalepticLogParser.csproj \
        --configuration Release \
        --output /app/publish \
        --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Hugging Face Spaces requires the app to bind on port 7860
ENV ASPNETCORE_URLS=http://*:7860
EXPOSE 7860

ENTRYPOINT ["dotnet", "AnalepticLogParser.dll"]
