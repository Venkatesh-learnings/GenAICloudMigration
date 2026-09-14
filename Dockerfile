FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY GenAICloudMigration.sln ./
COPY src/UrlShortener.Domain/UrlShortener.Domain.csproj src/UrlShortener.Domain/
COPY src/UrlShortener.Application/UrlShortener.Application.csproj src/UrlShortener.Application/
COPY src/UrlShortener.Infrastructure/UrlShortener.Infrastructure.csproj src/UrlShortener.Infrastructure/
COPY src/UrlShortener.Api/UrlShortener.Api.csproj src/UrlShortener.Api/
RUN dotnet restore src/UrlShortener.Api/UrlShortener.Api.csproj

COPY src/UrlShortener.Domain/ src/UrlShortener.Domain/
COPY src/UrlShortener.Application/ src/UrlShortener.Application/
COPY src/UrlShortener.Infrastructure/ src/UrlShortener.Infrastructure/
COPY src/UrlShortener.Api/ src/UrlShortener.Api/

RUN dotnet publish src/UrlShortener.Api/UrlShortener.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN useradd --uid 5678 --user-group --shell /bin/false appuser \
    && mkdir -p /data \
    && chown -R appuser:appuser /data
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__UrlShortener="Data Source=/data/urlshortener.db"

EXPOSE 8080
VOLUME ["/data"]

# No shell-based HEALTHCHECK here: the minimal aspnet runtime image ships neither curl nor
# wget, and /dev/tcp is a bashism the default /bin/sh (dash) doesn't support. A real deployment
# should point its platform-level probe (Kubernetes/ECS/etc.) at GET /health instead, which the
# API already exposes.
ENTRYPOINT ["dotnet", "UrlShortener.Api.dll"]
