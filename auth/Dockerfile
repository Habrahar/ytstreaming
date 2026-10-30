FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /source
COPY auth/ auth/
RUN dotnet publish auth/Auth.csproj -c Release -o /publish --no-self-contained /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /app
COPY --from=build /publish .
ENV ASPNETCORE_URLS=http://127.0.0.1:18092 DOTNET_EnableDiagnostics=0
USER 1654
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s CMD ["dotnet", "Auth.dll", "--health"]
ENTRYPOINT ["dotnet", "Auth.dll"]
