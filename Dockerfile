FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /source
COPY src/Bridge/ src/Bridge/
RUN dotnet publish src/Bridge/Bridge.csproj -c Release -o /publish --no-self-contained /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /publish .
RUN mkdir -p /data && chown 1654:1654 /data
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "Bridge.dll"]
