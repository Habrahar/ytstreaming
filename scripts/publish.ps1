$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local/dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet publish (Join-Path $projectRoot 'src/Bridge/Bridge.csproj') -c Release -o (Join-Path $projectRoot 'artifacts/publish') --no-self-contained /p:UseAppHost=false
if ($LASTEXITCODE) { throw 'Publish failed' }
