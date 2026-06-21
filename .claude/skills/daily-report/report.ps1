#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Daily-report data-prep — the DETERMINISTIC half of the daily-report skill.

.DESCRIPTION
    Gathers ALL the mechanical slices that go into the morning activity summary:
    beads created/closed, PRs opened/merged/open, open bugs, code-review findings,
    untriaged/needs-spec items, and backlog health counts. Emits a structured JSON
    payload (--json) or a pre-formatted text payload (default) that the model layer
    in SKILL.md consumes without re-querying.

    This script does NO judgment — no headlines, no next-focus reasoning, no
    narratives. Those stay with the model (see SKILL.md).

.PARAMETER Since
    Start of the reporting window (YYYY-MM-DD). Defaults to yesterday.
    Example: --Since 2026-06-20

.PARAMETER Json
    Emit JSON instead of the default human-readable text rendering.

.NOTES
    Requires:
      - bd CLI on PATH (or discoverable — script does not need gh PATH fix for bd)
      - gh CLI on PATH or at the known install location
        C:\Program Files\GitHub CLI\gh.exe

    PowerShell 5.1 compatible. No third-party modules.
#>
param(
    [string]$Since = "",
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# 0. Resolve --Since default (yesterday)
# ---------------------------------------------------------------------------
if (-not $Since) {
    $Since = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")
}

# Validate format
if ($Since -notmatch '^\d{4}-\d{2}-\d{2}$') {
    Write-Error "Invalid --Since value '$Since'. Expected YYYY-MM-DD."
    exit 1
}

# ---------------------------------------------------------------------------
# 1. Resolve gh robustly — not just PATH
# ---------------------------------------------------------------------------
function Get-GhExe {
    # Try PATH first
    try {
        $p = (Get-Command gh -ErrorAction Stop).Source
        return $p
    } catch {}

    # Known install locations (Windows)
    $candidates = @(
        "C:\Program Files\GitHub CLI\gh.exe",
        "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe",
        "$env:ProgramFiles\GitHub CLI\gh.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }

    # Compose PATH from Machine + User registry values, re-search
    try {
        $machinePathRaw = [System.Environment]::GetEnvironmentVariable("PATH", "Machine")
        $userPathRaw    = [System.Environment]::GetEnvironmentVariable("PATH", "User")
        $machinePath = if ($null -eq $machinePathRaw) { "" } else { $machinePathRaw }
        $userPath    = if ($null -eq $userPathRaw)    { "" } else { $userPathRaw }
        $combined = ($machinePath -split ";") + ($userPath -split ";") | Where-Object { $_ -and (Test-Path $_) }
        foreach ($dir in $combined) {
            $candidate = Join-Path $dir "gh.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    } catch {}

    return $null
}

$ghExe = Get-GhExe
$ghAvailable = $null -ne $ghExe

# ---------------------------------------------------------------------------
# 2. Helper: run bd with --json and return parsed array
# ---------------------------------------------------------------------------
function Invoke-BdJson {
    param([string[]]$BdArgs)
    try {
        $raw = & bd @BdArgs "--json" 2>&1
        # bd returns exit 0 on success, 255 when results found (normal), 1 on error
        # Treat anything other than a JSON-array/object start as an error
        $text = ($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) | Out-String
        if (-not $text.Trim()) { return @() }
        # Detect bd error JSON (object with "error" key)
        if ($text.Trim().StartsWith('{')) {
            Write-Warning "bd $($BdArgs -join ' ') returned error: $($text.Trim().Substring(0, [Math]::Min(120, $text.Trim().Length)))"
            return @()
        }
        # PS 5.1 ConvertFrom-Json returns a PSCustomObject for a single-element array.
        # Wrap in @() to always get an array.
        $parsed = $text | ConvertFrom-Json
        if ($null -eq $parsed) { return @() }
        return @($parsed)
    } catch {
        Write-Warning "bd $($BdArgs -join ' ') failed: $_"
        return @()
    }
}

# ---------------------------------------------------------------------------
# 3. Helper: run gh and return parsed JSON
# ---------------------------------------------------------------------------
function Invoke-GhJson {
    param([string[]]$GhArgs)
    if (-not $ghAvailable) { return @() }
    try {
        $raw = & $ghExe @GhArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "gh $($GhArgs -join ' ') exited $LASTEXITCODE"
            return @()
        }
        $text = $raw | Out-String
        if (-not $text.Trim() -or $text.Trim() -eq "null") { return @() }
        return ($text | ConvertFrom-Json)
    } catch {
        Write-Warning "gh $($GhArgs -join ' ') failed: $_"
        return @()
    }
}

# ---------------------------------------------------------------------------
# 4. Gather beads slices
# ---------------------------------------------------------------------------
if (-not $Json) { Write-Host "Gathering beads data..." -ForegroundColor Cyan }

$beadsCreated = Invoke-BdJson @("list", "--all", "--created-after", $Since, "--limit", "0")
$beadsClosed  = Invoke-BdJson @("list", "--all", "--closed-after",  $Since, "--limit", "0")
$openBugs     = Invoke-BdJson @("list", "--type", "bug", "--status", "open,in_progress,blocked", "--limit", "0")

# Code-review findings (all open issues labelled code-review)
$crAll = Invoke-BdJson @("list", "--label", "code-review", "--status", "open,in_progress,blocked", "--limit", "0")

# Split: new this window vs all outstanding open
$sinceDate = [datetime]::Parse($Since)
$crNew         = $crAll | Where-Object {
    try {
        if (-not $_.created_at) { return $false }
        $created = [datetime]::Parse($_.created_at)
        $created -ge $sinceDate
    } catch { $false }
}
$crOutstanding = $crAll   # all open code-review items

# Untriaged / needs-spec
$untriagedRaw = Invoke-BdJson @("list", "--label-any", "needs-spec,needs-triage,needs-human", "--status", "open,in_progress,blocked", "--limit", "0")

# Backlog health
$healthRaw = & bd status --json 2>&1 | Out-String
$health = if ($healthRaw.Trim()) { $healthRaw | ConvertFrom-Json } else { $null }

# ---------------------------------------------------------------------------
# 5. Gather GitHub PR slices
# ---------------------------------------------------------------------------
if (-not $Json) { Write-Host "Gathering GitHub PR data..." -ForegroundColor Cyan }

$prWarning = $null
if (-not $ghAvailable) {
    $prWarning = "gh CLI not found on PATH or known install locations; PR sections will be empty. Install GitHub CLI or add it to PATH."
    Write-Warning $prWarning
}

# PRs opened since --since
$prsOpened = Invoke-GhJson @(
    "pr", "list", "--state", "all",
    "--search", "created:>=$Since",
    "--limit", "200",
    "--json", "number,title,state,createdAt,mergedAt,url,headRefName"
)

# PRs merged since --since
$prsMerged = Invoke-GhJson @(
    "pr", "list", "--state", "merged",
    "--search", "merged:>=$Since",
    "--limit", "200",
    "--json", "number,title,state,createdAt,mergedAt,url,headRefName"
)

# PRs still open
$prsOpen = Invoke-GhJson @(
    "pr", "list", "--state", "open",
    "--limit", "200",
    "--json", "number,title,state,createdAt,url,headRefName"
)

# ---------------------------------------------------------------------------
# 6. Slim the items to essential fields for the model
# ---------------------------------------------------------------------------
function Get-SafeProp {
    param($obj, [string]$prop, $default = $null)
    try {
        $val = $obj.$prop
        if ($null -eq $val) { return $default }
        return $val
    } catch { return $default }
}

function Select-BeadRow {
    param($item)
    [PSCustomObject]@{
        id         = Get-SafeProp $item "id"        ""
        title      = Get-SafeProp $item "title"     ""
        priority   = Get-SafeProp $item "priority"  $null
        type       = Get-SafeProp $item "issue_type" ""
        status     = Get-SafeProp $item "status"    ""
        labels     = Get-SafeProp $item "labels"    @()
        parent     = Get-SafeProp $item "parent" $null
        created_at = Get-SafeProp $item "created_at" $null
        closed_at  = Get-SafeProp $item "closed_at"  $null
    }
}

function Select-PrRow {
    param($pr)
    [PSCustomObject]@{
        number     = $pr.number
        title      = $pr.title
        state      = $pr.state
        url        = $pr.url
        branch     = $pr.headRefName
        created_at = $pr.createdAt
        merged_at  = $pr.mergedAt
    }
}

$createdRows     = $beadsCreated | Where-Object { $_ } | ForEach-Object { Select-BeadRow $_ }
$closedRows      = $beadsClosed  | Where-Object { $_ } | ForEach-Object { Select-BeadRow $_ }
$bugRows         = $openBugs     | Where-Object { $_ } | ForEach-Object { Select-BeadRow $_ }
$crNewRows       = $crNew        | Where-Object { $_ } | ForEach-Object { Select-BeadRow $_ }
$crOutRows       = $crOutstanding| Where-Object { $_ } | ForEach-Object { Select-BeadRow $_ }
$untriagedRows   = $untriagedRaw | Where-Object { $_ } | ForEach-Object { Select-BeadRow $_ }

$openedPrRows    = $prsOpened    | Where-Object { $_ } | ForEach-Object { Select-PrRow $_ }
$mergedPrRows    = $prsMerged    | Where-Object { $_ } | ForEach-Object { Select-PrRow $_ }
$openPrRows      = $prsOpen      | Where-Object { $_ } | ForEach-Object { Select-PrRow $_ }

# ---------------------------------------------------------------------------
# 7. Build the payload
# ---------------------------------------------------------------------------
$netBeads = ($closedRows | Measure-Object).Count - ($createdRows | Measure-Object).Count

$summary = [PSCustomObject]@{
    since               = $Since
    generated_at        = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssK")
    gh_available        = $ghAvailable
    gh_warning          = $prWarning
    beads_created_count = ($createdRows   | Measure-Object).Count
    beads_closed_count  = ($closedRows    | Measure-Object).Count
    net_beads           = $netBeads
    prs_opened_count    = ($openedPrRows  | Measure-Object).Count
    prs_merged_count    = ($mergedPrRows  | Measure-Object).Count
    prs_still_open_count= ($openPrRows    | Measure-Object).Count
    open_bugs_count     = ($bugRows       | Measure-Object).Count
    cr_new_count        = ($crNewRows     | Measure-Object).Count
    cr_outstanding_count= ($crOutRows     | Measure-Object).Count
    untriaged_count     = ($untriagedRows | Measure-Object).Count
    backlog_health      = if ($health) {
        [PSCustomObject]@{
            open        = $health.summary.open_issues
            in_progress = $health.summary.in_progress_issues
            blocked     = $health.summary.blocked_issues
            ready       = $health.summary.ready_issues
            closed      = $health.summary.closed_issues
        }
    } else { $null }
}

$payload = [PSCustomObject]@{
    summary        = $summary
    beads_created  = $createdRows
    beads_closed   = $closedRows
    prs_opened     = $openedPrRows
    prs_merged     = $mergedPrRows
    prs_open       = $openPrRows
    open_bugs      = $bugRows
    cr_new         = $crNewRows
    cr_outstanding = $crOutRows
    untriaged      = $untriagedRows
}

# ---------------------------------------------------------------------------
# 8. Emit
# ---------------------------------------------------------------------------
if ($Json) {
    $payload | ConvertTo-Json -Depth 10
    exit 0
}

# ---------------------------------------------------------------------------
# Text rendering (pre-formatted markdown sections for the model)
# ---------------------------------------------------------------------------
$s = $summary

$lines = [System.Collections.Generic.List[string]]::new()

function Add { param([string]$L) $lines.Add($L) }

Add "=== DAILY-REPORT DATA PAYLOAD ==="
Add "Since: $($s.since)   Generated: $($s.generated_at)"
if (-not $s.gh_available) {
    Add ""
    Add "WARNING: $($s.gh_warning)"
}
Add ""

Add "--- VELOCITY COUNTS ---"
Add "Beads created : $($s.beads_created_count)"
Add "Beads closed  : $($s.beads_closed_count)   (net $( if ($s.net_beads -ge 0) { "+$($s.net_beads)" } else { "$($s.net_beads)" } ))"
Add "PRs opened    : $($s.prs_opened_count)"
Add "PRs merged    : $($s.prs_merged_count)"
Add "PRs still open: $($s.prs_still_open_count)"
Add ""

Add "--- BEADS CREATED ($($s.beads_created_count)) ---"
if ($createdRows) {
    foreach ($r in $createdRows) {
        $lbl = if ($r.labels) { " [" + ($r.labels -join ", ") + "]" } else { "" }
        Add "  $($r.id) [$($r.type)] P$($r.priority) $($r.status)  $($r.title)$lbl"
    }
} else { Add "  (none)" }
Add ""

Add "--- BEADS CLOSED ($($s.beads_closed_count)) ---"
if ($closedRows) {
    foreach ($r in $closedRows) {
        $lbl = if ($r.labels) { " [" + ($r.labels -join ", ") + "]" } else { "" }
        Add "  $($r.id) [$($r.type)] P$($r.priority)  $($r.title)$lbl"
    }
} else { Add "  (none)" }
Add ""

Add "--- PRS OPENED ($($s.prs_opened_count)) ---"
if ($openedPrRows) {
    foreach ($pr in $openedPrRows) {
        Add "  #$($pr.number) [$($pr.state)] $($pr.title)  ($($pr.url))"
    }
} else { Add "  (none or gh unavailable)" }
Add ""

Add "--- PRS MERGED ($($s.prs_merged_count)) ---"
if ($mergedPrRows) {
    foreach ($pr in $mergedPrRows) {
        Add "  #$($pr.number) $($pr.title)  merged: $($pr.merged_at)  ($($pr.url))"
    }
} else { Add "  (none or gh unavailable)" }
Add ""

Add "--- PRS STILL OPEN ($($s.prs_still_open_count)) ---"
if ($openPrRows) {
    foreach ($pr in $openPrRows) {
        Add "  #$($pr.number) $($pr.title)  ($($pr.url))"
    }
} else { Add "  (none or gh unavailable)" }
Add ""

Add "--- OPEN BUGS ($($s.open_bugs_count)) ---"
if ($bugRows) {
    foreach ($r in $bugRows) {
        $lbl = if ($r.labels) { " [" + ($r.labels -join ", ") + "]" } else { "" }
        Add "  $($r.id) P$($r.priority) [$($r.status)]  $($r.title)$lbl"
    }
} else { Add "  (none)" }
Add ""

Add "--- CODE-REVIEW AUTO-FILED FINDINGS ---"
Add "New this window ($($s.cr_new_count)):"
if ($crNewRows) {
    foreach ($r in $crNewRows) {
        Add "  $($r.id) P$($r.priority) [$($r.status)]  $($r.title)"
    }
} else { Add "  (none)" }
Add ""
Add "All outstanding open ($($s.cr_outstanding_count)):"
if ($crOutRows) {
    foreach ($r in $crOutRows) {
        Add "  $($r.id) P$($r.priority) [$($r.status)]  $($r.title)"
    }
} else { Add "  (none)" }
Add ""

Add "--- UNTRIAGED / NEEDS-SPEC ($($s.untriaged_count)) ---"
if ($untriagedRows) {
    foreach ($r in $untriagedRows) {
        $lbl = if ($r.labels) { " [" + ($r.labels -join ", ") + "]" } else { "" }
        Add "  $($r.id) P$($r.priority) [$($r.status)]  $($r.title)$lbl"
    }
} else { Add "  (none)" }
Add ""

Add "--- BACKLOG HEALTH ---"
if ($s.backlog_health) {
    $h = $s.backlog_health
    Add "  open: $($h.open)   in_progress: $($h.in_progress)   blocked: $($h.blocked)   ready: $($h.ready)   closed: $($h.closed)"
} else {
    Add "  (bd status unavailable)"
}
Add ""

Add "=== END PAYLOAD (model: consume above, do NOT re-query) ==="

$lines | ForEach-Object { Write-Output $_ }
