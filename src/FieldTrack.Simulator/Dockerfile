# The simulator the Azure demo runs as a Container App (#156, #157). Build context: the repository root.
# Base images are pinned by digest (tags are mutable); the tag is kept for readability and is ignored by Docker.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

# Restore first so the package layer is cached until a project or package version changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/FieldTrack.Domain/FieldTrack.Domain.csproj src/FieldTrack.Domain/
COPY src/FieldTrack.Simulator/FieldTrack.Simulator.csproj src/FieldTrack.Simulator/
RUN dotnet restore src/FieldTrack.Simulator/FieldTrack.Simulator.csproj

COPY src/ src/
RUN dotnet publish src/FieldTrack.Simulator/FieldTrack.Simulator.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0.15@sha256:64a538092439382d055c87c0f49092efb8bca883c736c54ae523da7fd8e02d18 AS runtime
WORKDIR /app
COPY --from=build /app .
# Fleet and Scenario files, so arguments such as `demo --fleet fleets/demo.json` resolve against the working directory.
COPY src/FieldTrack.Simulator/fleets fleets/
# Read-only for the app user: the simulator writes its state and manifest wherever --state and --manifest point (/tmp).
USER $APP_UID
ENTRYPOINT ["dotnet", "FieldTrack.Simulator.dll"]
