# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src

# Restore first so the package layer is cached until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FieldTrack.Domain/FieldTrack.Domain.csproj src/FieldTrack.Domain/
COPY src/FieldTrack.Infrastructure/FieldTrack.Infrastructure.csproj src/FieldTrack.Infrastructure/
COPY src/FieldTrack.Api/FieldTrack.Api.csproj src/FieldTrack.Api/
RUN dotnet restore src/FieldTrack.Api/FieldTrack.Api.csproj

COPY src/ src/
RUN dotnet publish src/FieldTrack.Api/FieldTrack.Api.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FieldTrack.Api.dll"]
