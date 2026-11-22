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
COPY tools/DemoData/DemoData.csproj tools/DemoData/
RUN dotnet restore src/Topaz.Central/Topaz.Central.csproj && dotnet restore tools/DemoData/DemoData.csproj
COPY src/Topaz.Contracts/ src/Topaz.Contracts/
COPY src/Topaz.Central/ src/Topaz.Central/
COPY tools/DemoData/ tools/DemoData/
ARG VERSION=1.0.0
RUN dotnet publish src/Topaz.Central/Topaz.Central.csproj -c Release -o /app --no-restore -p:Version=$VERSION
# The public demo's data generator (container demo-feeder runs this image with dotnet tools/demodata/DemoData.dll).
RUN dotnet publish tools/DemoData/DemoData.csproj -c Release -o /app/tools/demodata --no-restore -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
COPY --from=web /web/dist ./wwwroot
# Signed agent packages for self-update (CI puts them into agent-updates/; empty in a plain local build).
COPY agent-updates/ ./agent-updates/
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
# Writable dirs for cookie keys and the demo feeder's state (a named volume copies this ownership on first use).
RUN mkdir -p /keys /state && chown app /keys /state
# Non-root user shipped with the .NET 8 images.
USER app
ENTRYPOINT ["dotnet", "Topaz.Central.dll"]
