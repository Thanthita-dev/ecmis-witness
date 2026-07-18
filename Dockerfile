FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ecmis-witness.sln .
COPY src/EcmisWitness.Api.csproj src/EcmisWitness.Api.csproj
COPY tests/EcmisWitness.Tests/EcmisWitness.Tests.csproj tests/EcmisWitness.Tests/EcmisWitness.Tests.csproj
RUN dotnet restore ecmis-witness.sln

COPY . .
RUN dotnet publish src/EcmisWitness.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:${PORT:-8080}/health || exit 1

USER app
ENTRYPOINT ["sh", "-c", "dotnet EcmisWitness.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
