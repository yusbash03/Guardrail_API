# Multi-stage build for Render (ADR-014). Migrations are applied separately, not at startup.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Guardrail.Domain/Guardrail.Domain.csproj src/Guardrail.Domain/
COPY src/Guardrail.Application/Guardrail.Application.csproj src/Guardrail.Application/
COPY src/Guardrail.Infrastructure/Guardrail.Infrastructure.csproj src/Guardrail.Infrastructure/
COPY src/Guardrail.Api/Guardrail.Api.csproj src/Guardrail.Api/
RUN dotnet restore src/Guardrail.Api/Guardrail.Api.csproj
COPY src/ src/
RUN dotnet publish src/Guardrail.Api/Guardrail.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Production
USER app
# Render injects PORT at runtime; default to 8080 locally.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet Guardrail.Api.dll"]
