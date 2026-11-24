# ---------------------------------------------------------------------------
#  Jordan Islamic Hospital – Outpatient Queue System
#  Multi-stage build: restore → run business-rule tests → publish → runtime
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY JIH.QueueSystem.slnx ./
COPY src/JIH.Queue.Core/JIH.Queue.Core.csproj src/JIH.Queue.Core/
COPY src/JIH.Queue.Data.Oracle/JIH.Queue.Data.Oracle.csproj src/JIH.Queue.Data.Oracle/
COPY src/JIH.Queue.Web/JIH.Queue.Web.csproj src/JIH.Queue.Web/
COPY tests/JIH.Queue.RuleTests/JIH.Queue.RuleTests.csproj tests/JIH.Queue.RuleTests/
RUN dotnet restore src/JIH.Queue.Web/JIH.Queue.Web.csproj && dotnet restore tests/JIH.Queue.RuleTests/JIH.Queue.RuleTests.csproj
COPY src/ src/
COPY tests/ tests/
# The image is only produced if every business rule test passes.
RUN dotnet run --project tests/JIH.Queue.RuleTests -c Release
RUN dotnet publish src/JIH.Queue.Web/JIH.Queue.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends tzdata curl && rm -rf /var/lib/apt/lists/*
ENV TZ=Asia/Amman \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s CMD curl -fs http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "JIH.Queue.Web.dll"]
