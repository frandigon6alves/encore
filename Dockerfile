# One Dockerfile for the three .NET services: docker build --build-arg PROJECT=Encore.Api .
# Multi-stage: the SDK image builds, the much smaller ASP.NET runtime image runs.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src

# Restore first, from project files only: this layer stays cached until a csproj changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY protos/ protos/
COPY src/Encore.Core/Encore.Core.csproj src/Encore.Core/
COPY src/${PROJECT}/${PROJECT}.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj

COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll \
    ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app .
# Non-root user shipped with the official images.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
