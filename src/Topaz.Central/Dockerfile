# Build from the repository root:  docker build -f src/Topaz.Central/Dockerfile -t topaz-central .
FROM node:20-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
# The release version shown in the UI and compared with the server's X-Topaz-Version header.
ARG VERSION=1.0.0
ENV VERSION=$VERSION
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Topaz.Contracts/Topaz.Contracts.csproj src/Topaz.Contracts/
COPY src/Topaz.Central/Topaz.Central.csproj src/Topaz.Central/
RUN dotnet restore src/Topaz.Central/Topaz.Central.csproj
COPY src/Topaz.Contracts/ src/Topaz.Contracts/
COPY src/Topaz.Central/ src/Topaz.Central/
ARG VERSION=1.0.0
RUN dotnet publish src/Topaz.Central/Topaz.Central.csproj -c Release -o /app --no-restore -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY --from=web /web/dist ./wwwroot
# Signed agent packages for self-update (CI puts them into agent-updates/; empty in a plain local build).
COPY agent-updates/ ./agent-updates/
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
# Writable dir for cookie keys (a named volume copies this ownership on first use).
RUN mkdir -p /keys && chown app /keys
# Non-root user shipped with the .NET 8 images.
USER app
ENTRYPOINT ["dotnet", "Topaz.Central.dll"]
