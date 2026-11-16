# Build context: the repository root (see docker-compose.yml).
# Base images are pinned by digest (tags are mutable); each comment names the tag it matches.
# sdk:10.0.404
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

# Restore first so the package layer is cached until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FieldTrack.Domain/FieldTrack.Domain.csproj src/FieldTrack.Domain/
COPY src/FieldTrack.Infrastructure/FieldTrack.Infrastructure.csproj src/FieldTrack.Infrastructure/
COPY src/FieldTrack.Api/FieldTrack.Api.csproj src/FieldTrack.Api/
RUN dotnet restore src/FieldTrack.Api/FieldTrack.Api.csproj

COPY src/ src/
RUN dotnet publish src/FieldTrack.Api/FieldTrack.Api.csproj --configuration Release --no-restore --output /app

# aspnet:10.0.15
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FieldTrack.Api.dll"]
