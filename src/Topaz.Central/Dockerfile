# Build from the repository root:  docker build -f src/Topaz.Central/Dockerfile -t topaz-central .
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
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
# Non-root user shipped with the .NET 8 images.
USER app
ENTRYPOINT ["dotnet", "Topaz.Central.dll"]
