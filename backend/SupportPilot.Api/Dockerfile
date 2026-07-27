# SupportPilot backend — multi-stage build.
#
# Stage 1 (build): the big .NET SDK image restores + publishes a self-contained
# app folder. Stage 2 (final): copy only the published output into the much
# smaller ASP.NET runtime image, so the shipped image has no SDK/source in it.
#
# Build context is this directory (backend/SupportPilot.Api), so the .csproj is
# at the context root — see docker-compose.yml.

# --- build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, with only the csproj copied in, so Docker can cache the restore
# layer and skip it whenever only source (not dependencies) changes.
COPY SupportPilot.Api.csproj ./
RUN dotnet restore

# Now the rest of the source, and publish a Release build.
COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# --- final ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

# The aspnet image already defaults to listening on 8080; set it explicitly so
# the port is obvious and stable regardless of base-image changes.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SupportPilot.Api.dll"]
