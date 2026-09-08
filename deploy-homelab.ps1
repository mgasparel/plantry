#!/usr/bin/env pwsh
#Requires -Version 5.1
# Registry-free deploy: build the web + migrator images locally, ship them to the
# homelab as a tar, and let deploy-on-host.sh (already on the host at
# /root/plantry/) load + `docker compose up -d` the stack.
#
# Why not a registry: Plantry is being renamed, and we don't want to publish
# container images under the current name in the meantime. Once the rename
# lands, this can be replaced by a `docker push` + `docker compose pull` flow
# (see docs/Operations/deployment.md for that target-state design).
#
# Usage: ./deploy-homelab.ps1 [-SkipBuild] [-SkipBackup]
param(
    [switch]$SkipBuild,
    [switch]$SkipBackup
)

$ErrorActionPreference = "Stop"

$RemoteHost = "homelab"
$RemoteDir = "/root/plantry"
$TarName = "plantry-images.tar"
$TarPath = Join-Path $PSScriptRoot $TarName
$BackupTimestamp = Get-Date -Format "yyyyMMdd-HHmmss"

function Invoke-Checked {
    param([string]$Description, [scriptblock]$Command)
    Write-Host "==> $Description"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Error "FAILED: $Description (exit $LASTEXITCODE)"
        exit 1
    }
}

Push-Location $PSScriptRoot
try {
    $branch = git rev-parse --abbrev-ref HEAD
    $sha = git rev-parse --short HEAD
    $dirty = (git status --porcelain)
    $dirtyNote = if ($dirty) { " (dirty working tree)" } else { "" }
    Write-Host "==> Deploying $branch @ $sha$dirtyNote to $RemoteHost"

    if (-not $SkipBuild) {
        Invoke-Checked "Building plantry-web:local" {
            docker build --platform linux/amd64 -t plantry-web:local -f Dockerfile .
        }
        Invoke-Checked "Building plantry-migrator:local" {
            docker build --platform linux/amd64 -t plantry-migrator:local -f src/Plantry.Migrator/Dockerfile .
        }
    } else {
        Write-Host "==> Skipping build (-SkipBuild); reusing existing local images"
    }

    Invoke-Checked "Saving images to $TarName" {
        docker save plantry-web:local plantry-migrator:local -o $TarPath
    }

    $sizeMb = [Math]::Round((Get-Item $TarPath).Length / 1MB, 1)
    Invoke-Checked "Copying $TarName ($sizeMb MB) to ${RemoteHost}:${RemoteDir}/" {
        scp $TarPath "${RemoteHost}:${RemoteDir}/${TarName}"
    }

    Remove-Item $TarPath

    if (-not $SkipBackup) {
        Invoke-Checked "Backing up postgres database on $RemoteHost" {
            ssh $RemoteHost "cd $RemoteDir && mkdir -p backups && bash -c 'set -o pipefail; docker compose exec -T postgres pg_dump -U postgres -d plantrydb | gzip > backups/plantrydb-$BackupTimestamp.sql.gz'"
        }
    } else {
        Write-Host "==> Skipping backup (-SkipBackup)"
    }

    Invoke-Checked "Running deploy-on-host.sh on $RemoteHost" {
        ssh $RemoteHost "cd $RemoteDir && chmod +x deploy-on-host.sh && ./deploy-on-host.sh"
    }
}
finally {
    Pop-Location
}
