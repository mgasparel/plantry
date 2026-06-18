#!/usr/bin/env pwsh
#Requires -Version 5.1
$ErrorActionPreference = "Stop"

$envFile = "aspire-output\.env"
if (-not (Test-Path $envFile)) {
    Write-Error "aspire-output\.env not found. Copy aspire-output\.env.example to aspire-output\.env and fill in your values."
    exit 1
}

$sha = git rev-parse --short HEAD
$image = "plantry-web:$sha"

Write-Host "==> Building $image ..."
dotnet publish src/Plantry.Web/Plantry.Web.csproj `
    -c Release --os linux --arch x64 `
    /t:PublishContainer `
    /p:ContainerRepository=plantry-web `
    /p:ContainerImageTag=$sha

# Stamp the new image tag into .env
$lines = Get-Content $envFile
$lines = $lines -replace '^PLANTRY_WEB_IMAGE=.*', "PLANTRY_WEB_IMAGE=$image"
[IO.File]::WriteAllLines((Resolve-Path $envFile).Path, $lines)

Write-Host "==> Starting stack ..."
docker compose `
    -f aspire-output/docker-compose.yaml `
    -f aspire-output/docker-compose.override.yaml `
    --env-file aspire-output/.env `
    up -d --remove-orphans

$hostPort = ($lines | Select-String '^HOST_PORT=(.+)').Matches.Groups[1].Value
if (-not $hostPort) { $hostPort = "8080" }
Write-Host "==> Done. App at http://localhost:$hostPort"
