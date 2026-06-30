#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Daily Briefing data-prep + tabbed HTML renderer.

.DESCRIPTION
    ONE deterministic prep: gathers every data slice once from beads, then emits
    a self-contained multi-tab HTML report with three tabs:

      [Briefing]  The operator's morning decision surface: factory stall, overnight
                  recap, runway, replenishment worklist, and the call.

      [Flow]      The time-series flow view: lead-time scatter, throughput, aging
                  WIP, self-generated work. Full reuse of the flow-report logic.

      [Backlog]   Full do-now / parked detail. (Filled by DB-5 child; placeholder
                  here.)

    All tabs consume the SAME $payload JSON -- no per-tab re-querying of bd.
    The script is the substrate every subsequent DB-* child hangs off.

.PARAMETER Out
    Output path for the HTML file. Default: ./daily-briefing.html

.PARAMETER Json
    Emit the computed data payload as JSON to stdout instead of writing HTML.

.PARAMETER Open
    Open the generated HTML in the default browser after writing.

.PARAMETER LongInProgressHours
    Threshold (hours) above which an in_progress issue is considered
    suspiciously stalled. Default: 4.

.NOTES
    Requires bd CLI on PATH. PowerShell 5.1 compatible. No third-party modules.
    ASCII-only source: use HTML entities in markup, not literal Unicode characters.
#>
param(
    [string]$Out = "",
    [switch]$Json,
    [switch]$Open,
    [int]$LongInProgressHours = 4
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# 1. Helpers (reused verbatim from flow-report; do NOT diverge)
# ---------------------------------------------------------------------------
function Invoke-BdJson {
    param([string[]]$BdArgs)
    try {
        $raw = & bd @BdArgs "--json" 2>&1
        $text = ($raw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) | Out-String
        if (-not $text.Trim()) { return @() }
        if ($text.Trim().StartsWith('{')) {
            Write-Warning "bd $($BdArgs -join ' ') returned error: $($text.Trim().Substring(0, [Math]::Min(120, $text.Trim().Length)))"
            return @()
        }
        $parsed = $text | ConvertFrom-Json
        if ($null -eq $parsed) { return @() }
        return @($parsed)
    } catch {
        Write-Warning "bd $($BdArgs -join ' ') failed: $_"
        return @()
    }
}

function Get-SafeProp {
    param($obj, [string]$prop, $default = $null)
    try {
        $val = $obj.$prop
        if ($null -eq $val) { return $default }
        return $val
    } catch { return $default }
}

# Nearest-rank percentile (lower index).
function Get-Percentile {
    param([double[]]$Data, [double]$P)
    if (-not $Data -or $Data.Count -eq 0) { return $null }
    $sorted = $Data | Sort-Object
    $idx = [math]::Floor(($sorted.Count - 1) * $P)
    return [double]$sorted[$idx]
}

function To-Epoch {
    param([datetime]$dt)
    return [int64](($dt.ToUniversalTime() - [datetime]'1970-01-01T00:00:00Z').TotalMilliseconds)
}

# ---------------------------------------------------------------------------
# 1b. Resolve gh robustly -- mirrors daily-report pattern exactly
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

$ghExe       = Get-GhExe
$ghAvailable = $null -ne $ghExe

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
# 2. Gather all issues (single query -- all tabs share this)
# ---------------------------------------------------------------------------
Write-Host "Reading beads issues..." -ForegroundColor Cyan
$all = Invoke-BdJson @("list", "--all", "--limit", "0")
if (-not $all -or $all.Count -eq 0) {
    Write-Error "No issues returned from bd. Is bd on PATH and a workspace resolved?"
    exit 1
}

$now = Get-Date

# ---------------------------------------------------------------------------
# 2b. Lookup tables for reason classifier (same as flow-report)
# ---------------------------------------------------------------------------
$openById = @{}
foreach ($i in $all) {
    $iid = Get-SafeProp $i "id" ""
    if ($iid) { $openById[$iid] = (-not (Get-SafeProp $i "closed_at" $null)) }
}
$readyById = @{}
foreach ($r in (Invoke-BdJson @("ready", "--limit", "0"))) {
    $rid = Get-SafeProp $r "id" ""
    if ($rid) { $readyById[$rid] = $true }
}

function Get-AgingReason {
    param($item, [hashtable]$OpenById, [hashtable]$ReadyById)
    $st  = Get-SafeProp $item "status" ""
    $iid = Get-SafeProp $item "id" ""
    if ($st -eq "in_progress") { return "inflight" }
    $hasOpenBlocker = $false
    foreach ($d in @(Get-SafeProp $item "dependencies" @())) {
        if ($d -and (Get-SafeProp $d "type" "") -eq "blocks") {
            $dep = Get-SafeProp $d "depends_on_id" ""
            if ($dep -and $OpenById.ContainsKey($dep) -and $OpenById[$dep]) { $hasOpenBlocker = $true }
        }
    }
    if ($st -eq "blocked" -or $hasOpenBlocker) { return "blocked" }
    $labels = @(Get-SafeProp $item "labels" @())
    if ($labels -contains "needs-spec" -or $labels -contains "needs-triage" -or $labels -contains "needs-human") {
        return "spec"
    }
    if ($ReadyById.ContainsKey($iid)) { return "ready" }
    return "parked"
}

# ---------------------------------------------------------------------------
# 3. Flow slice: closed (lead/wait/exec) + aging WIP
# ---------------------------------------------------------------------------
$leadRows  = New-Object System.Collections.Generic.List[object]
$agingRows = New-Object System.Collections.Generic.List[object]
$closeDays = @{}
$excludedEpics = 0

foreach ($i in $all) {
    $id       = Get-SafeProp $i "id" ""
    $type     = Get-SafeProp $i "issue_type" ""
    $status   = Get-SafeProp $i "status" ""
    $title    = Get-SafeProp $i "title" ""
    $createdS = Get-SafeProp $i "created_at" $null
    $startedS = Get-SafeProp $i "started_at" $null
    $closedS  = Get-SafeProp $i "closed_at" $null

    if (-not $createdS) { continue }
    $created = [datetime]::Parse($createdS)

    if ($closedS) {
        $closed = [datetime]::Parse($closedS)
        $leadH  = ($closed - $created).TotalHours
        if ($leadH -lt 0) { $leadH = 0 }
        $waitH = $null; $execH = $null
        if ($startedS) {
            $started = [datetime]::Parse($startedS)
            $waitH = ($started - $created).TotalHours
            $execH = ($closed - $started).TotalHours
            if ($waitH -lt 0) { $waitH = 0 }
            if ($execH -lt 0) { $execH = 0 }
        }
        $leadRows.Add([PSCustomObject]@{
            id       = $id
            type     = $type
            title    = $title
            closedMs = To-Epoch $closed
            leadH    = [math]::Round($leadH, 3)
            waitH    = if ($null -ne $waitH) { [math]::Round($waitH, 3) } else { $null }
            execH    = if ($null -ne $execH) { [math]::Round($execH, 3) } else { $null }
        })
        $dayKey = $closed.ToString("yyyy-MM-dd")
        if ($closeDays.ContainsKey($dayKey)) { $closeDays[$dayKey]++ } else { $closeDays[$dayKey] = 1 }
    }
    elseif ($status -in @("open", "in_progress", "blocked")) {
        if ($type -eq "epic") { $excludedEpics++; continue }
        $ageH = ($now - $created).TotalHours
        if ($ageH -lt 0) { $ageH = 0 }
        $agingRows.Add([PSCustomObject]@{
            id     = $id
            type   = $type
            title  = $title
            status = $status
            ageH   = [math]::Round($ageH, 3)
            reason = Get-AgingReason -item $i -OpenById $openById -ReadyById $readyById
        })
    }
}

# ---------------------------------------------------------------------------
# 4. Percentiles + throughput series + self-gen (same as flow-report)
# ---------------------------------------------------------------------------
$leadVals = @($leadRows | ForEach-Object { [double]$_.leadH })
$p50 = Get-Percentile -Data $leadVals -P 0.50
$p85 = Get-Percentile -Data $leadVals -P 0.85
$p95 = Get-Percentile -Data $leadVals -P 0.95

$throughput = New-Object System.Collections.Generic.List[object]
if ($closeDays.Count -gt 0) {
    $dayKeys = $closeDays.Keys | Sort-Object
    $first = [datetime]::ParseExact($dayKeys[0],  "yyyy-MM-dd", $null)
    $last  = [datetime]::ParseExact($dayKeys[-1], "yyyy-MM-dd", $null)
    for ($d = $first; $d -le $last; $d = $d.AddDays(1)) {
        $k = $d.ToString("yyyy-MM-dd")
        $c = if ($closeDays.ContainsKey($k)) { $closeDays[$k] } else { 0 }
        $throughput.Add([PSCustomObject]@{ day = $k; count = $c })
    }
}

# Self-generated work (code-review + dogfood labels)
$sgCreated = @{}
$sgClosed  = @{}
$sgTotalCreated = 0; $sgTotalClosed = 0
foreach ($i in $all) {
    $labels   = @(Get-SafeProp $i "labels" @())
    $isReview = $labels -contains "code-review"
    $isDog    = $labels -contains "source:dogfood"
    if (-not ($isReview -or $isDog)) { continue }
    $cs = Get-SafeProp $i "created_at" $null
    if (-not $cs) { continue }
    $cd = ([datetime]::Parse($cs)).ToString("yyyy-MM-dd")
    if (-not $sgCreated.ContainsKey($cd)) { $sgCreated[$cd] = @{ review = 0; dogfood = 0 } }
    if ($isDog) { $sgCreated[$cd].dogfood++ } else { $sgCreated[$cd].review++ }
    $sgTotalCreated++
    $cls = Get-SafeProp $i "closed_at" $null
    if ($cls) {
        $cld = ([datetime]::Parse($cls)).ToString("yyyy-MM-dd")
        if ($sgClosed.ContainsKey($cld)) { $sgClosed[$cld]++ } else { $sgClosed[$cld] = 1 }
        $sgTotalClosed++
    }
}
$sgSeries = New-Object System.Collections.Generic.List[object]
if ($sgTotalCreated -gt 0) {
    $sgDays  = @($sgCreated.Keys) + @($sgClosed.Keys) | Sort-Object -Unique
    $sgFirst = [datetime]::ParseExact($sgDays[0], "yyyy-MM-dd", $null)
    $cum = 0
    for ($d = $sgFirst; $d -le $now.Date; $d = $d.AddDays(1)) {
        $k = $d.ToString("yyyy-MM-dd")
        $rev = 0; $dog = 0; $cl = 0
        if ($sgCreated.ContainsKey($k)) { $rev = $sgCreated[$k].review; $dog = $sgCreated[$k].dogfood }
        if ($sgClosed.ContainsKey($k))  { $cl = $sgClosed[$k] }
        $cum += ($rev + $dog - $cl)
        $sgSeries.Add([PSCustomObject]@{ day = $k; review = $rev; dogfood = $dog; closed = $cl; outstanding = $cum })
    }
}

# Derived KPIs
$waited = @($leadRows | Where-Object { $null -ne $_.waitH -and $_.leadH -gt 0 })
$medianWaitShare = $null
if ($waited.Count -gt 0) {
    $shares = @($waited | ForEach-Object { [double]$_.waitH / [double]$_.leadH })
    $medianWaitShare = Get-Percentile -Data $shares -P 0.50
}
$spanDays  = if ($throughput.Count -gt 0) { $throughput.Count } else { 1 }
$perDay    = [math]::Round($leadRows.Count / $spanDays, 1)
$stuckCount= @($agingRows | Where-Object { $p85 -and $_.ageH -ge $p85 }).Count

# ---------------------------------------------------------------------------
# 5. Briefing slice: factory stall + runway + overnight window
# ---------------------------------------------------------------------------
# Exhaustion reason labels set by the auto-park procedure.
$exhaustionLabels = @("build-loop-exhausted", "test-loop-exhausted", "critic-loop-exhausted")

# Runway = ready-queue depth / consumption rate (closes per day).
# Consumption rate uses a 14-day trailing window for freshness.
$trailingDays  = 14
$trailCutoff   = $now.AddDays(-$trailingDays)
$trailCloseCount = @($all | Where-Object {
    $cls = Get-SafeProp $_ "closed_at" $null
    $cls -and ([datetime]::Parse($cls)) -ge $trailCutoff
}).Count
$consumptionRate = if ($trailingDays -gt 0) { [math]::Round($trailCloseCount / $trailingDays, 2) } else { 0 }
$readyDepth      = $readyById.Count
$runwayDays      = if ($consumptionRate -gt 0) { [math]::Round($readyDepth / $consumptionRate, 1) } else { $null }

# Overnight recap: issues created/closed in last 24 h.
$overnightCutoff = $now.AddHours(-24)
$overnightCreated = @($all | Where-Object {
    $cs = Get-SafeProp $_ "created_at" $null
    $cs -and ([datetime]::Parse($cs)) -ge $overnightCutoff
}).Count
$overnightClosed = @($all | Where-Object {
    $cls = Get-SafeProp $_ "closed_at" $null
    $cls -and ([datetime]::Parse($cls)) -ge $overnightCutoff
}).Count

# ---------------------------------------------------------------------------
# 5b. Factory stall: collect all five categories
#
#   blocked          -- status=blocked (dependency or manual block)
#   needs-human      -- label needs-human (auto-parked or flagged)
#   parked-exhausted -- status=blocked + exhaustion label OR "Auto-parked" in notes
#   long-in-progress -- status=in_progress, age > LongInProgressHours
#   red-ci           -- open PRs with failing CI checks (via gh, degrades gracefully)
#
# Items appear in one category only; priority order: needs-human > parked-exhausted
# > blocked > long-in-progress > red-ci.
# ---------------------------------------------------------------------------
$stallSeenIds = @{}   # dedup across categories

$stallNeedsHuman      = New-Object System.Collections.Generic.List[object]
$stallParkedExhausted = New-Object System.Collections.Generic.List[object]
$stallBlocked         = New-Object System.Collections.Generic.List[object]
$stallLongInProgress  = New-Object System.Collections.Generic.List[object]

function New-StallItem {
    param($issue, [string]$category, [string]$reason, [string]$lever)
    return [PSCustomObject]@{
        id       = Get-SafeProp $issue "id" ""
        title    = Get-SafeProp $issue "title" ""
        status   = Get-SafeProp $issue "status" ""
        labels   = @(Get-SafeProp $issue "labels" @())
        category = $category
        reason   = $reason
        lever    = $lever
    }
}

foreach ($i in $all) {
    $cls = Get-SafeProp $i "closed_at" $null
    if ($cls) { continue }
    $iid     = Get-SafeProp $i "id" ""
    $st      = Get-SafeProp $i "status" ""
    $labels  = @(Get-SafeProp $i "labels" @())
    $notes   = Get-SafeProp $i "notes" ""
    if ($null -eq $notes) { $notes = "" }

    # Category 1: needs-human label (highest priority, first pass)
    if ($labels -contains "needs-human") {
        $stallSeenIds[$iid] = $true
        $stallNeedsHuman.Add((New-StallItem $i "needs-human" `
            "Labelled needs-human -- awaiting operator decision" `
            "Review bd show $iid; resolve the blocker and remove the needs-human label"))
        continue
    }

    # Category 2: parked-on-exhaustion (status=blocked + exhaustion label OR Auto-parked notes)
    $isExhausted = $false
    foreach ($el in $exhaustionLabels) {
        if ($labels -contains $el) { $isExhausted = $true; break }
    }
    if (-not $isExhausted -and $notes -match "Auto-parked") { $isExhausted = $true }
    if ($isExhausted) {
        $stallSeenIds[$iid] = $true
        $exhaustReason = "Parked after exhausting retry budget"
        foreach ($el in $exhaustionLabels) {
            if ($labels -contains $el) { $exhaustReason = "Parked: $el"; break }
        }
        $stallParkedExhausted.Add((New-StallItem $i "parked-exhausted" `
            $exhaustReason `
            "Read .preflight/ report for the last failing run; fix the root cause or close the issue"))
        continue
    }

    # Category 3: blocked (status=blocked, not already captured above)
    if ($st -eq "blocked") {
        $stallSeenIds[$iid] = $true
        $stallBlocked.Add((New-StallItem $i "blocked" `
            "Status: blocked" `
            "Identify and resolve the dependency; use: bd show $iid"))
        continue
    }

    # Category 4: suspiciously-long in_progress
    if ($st -eq "in_progress") {
        $startedS = Get-SafeProp $i "started_at" $null
        if ($startedS) {
            $started = [datetime]::Parse($startedS)
            $ageH = ($now - $started).TotalHours
            if ($ageH -gt $LongInProgressHours) {
                $stallSeenIds[$iid] = $true
                $ageLabel = if ($ageH -ge 24) { ([math]::Round($ageH / 24, 1)).ToString() + "d" } else { ([math]::Round($ageH, 1)).ToString() + "h" }
                $stallLongInProgress.Add((New-StallItem $i "long-in-progress" `
                    "In-progress for $ageLabel (threshold: $($LongInProgressHours)h)" `
                    "Check if a worker is still running; if stalled, close/re-open or park: bd show $iid"))
                continue
            }
        }
    }
}

# Category 5: open PRs with red CI (via gh, degrades gracefully if gh absent)
$stallRedCi = New-Object System.Collections.Generic.List[object]
$ghWarning  = $null

if (-not $ghAvailable) {
    $ghWarning = "gh CLI not found -- red-CI PR check skipped. Install GitHub CLI to enable."
} else {
    $openPrs = Invoke-GhJson @(
        "pr", "list", "--state", "open",
        "--limit", "100",
        "--json", "number,title,url,headRefName,statusCheckRollup"
    )
    if ($openPrs) {
        foreach ($pr in $openPrs) {
            # statusCheckRollup is an array; overall state is FAILURE if any check failed.
            $rollup = $pr.statusCheckRollup
            $hasFailed = $false
            if ($rollup) {
                foreach ($check in $rollup) {
                    $st2 = Get-SafeProp $check "state" $null
                    if ($null -eq $st2) { $st2 = Get-SafeProp $check "conclusion" $null }
                    if ($st2 -eq "FAILURE" -or $st2 -eq "TIMED_OUT" -or $st2 -eq "CANCELLED") {
                        $hasFailed = $true; break
                    }
                }
            }
            if ($hasFailed) {
                $stallRedCi.Add([PSCustomObject]@{
                    id       = "PR#$($pr.number)"
                    title    = $pr.title
                    status   = "open"
                    labels   = @()
                    category = "red-ci"
                    reason   = "Open PR with failing CI checks"
                    lever    = "Review CI output and push a fix: $($pr.url)"
                    url      = $pr.url
                    branch   = $pr.headRefName
                })
            }
        }
    }
}

# Build the combined factory-stall list (order: needs-human, parked-exhausted, blocked,
# long-in-progress, red-ci) for the payload and KPI count.
$allStallItems = New-Object System.Collections.Generic.List[object]
foreach ($item in $stallNeedsHuman)      { $allStallItems.Add($item) }
foreach ($item in $stallParkedExhausted) { $allStallItems.Add($item) }
foreach ($item in $stallBlocked)         { $allStallItems.Add($item) }
foreach ($item in $stallLongInProgress)  { $allStallItems.Add($item) }
foreach ($item in $stallRedCi)           { $allStallItems.Add($item) }

# Legacy stallItems (needs-human + needs-spec + needs-triage) kept for backward compat.
# DB-0 used this field name; replace it with the richer factoryStall payload below.
$stallItems = $allStallItems   # alias for legacy KPI count

# Parked / blocked items for the backlog tab
$parkedItems = New-Object System.Collections.Generic.List[object]
foreach ($i in $all) {
    $cls = Get-SafeProp $i "closed_at" $null
    if ($cls) { continue }
    $st = Get-SafeProp $i "status" ""
    if ($st -eq "blocked") {
        $parkedItems.Add([PSCustomObject]@{
            id     = Get-SafeProp $i "id" ""
            title  = Get-SafeProp $i "title" ""
            status = $st
        })
    }
}

# ---------------------------------------------------------------------------
# 6. Build JSON payload (the contract all tabs consume)
# ---------------------------------------------------------------------------
$flowKpis = [PSCustomObject]@{
    totalClosed     = $leadRows.Count
    openCount       = $agingRows.Count
    leadP50h        = if ($null -ne $p50) { [math]::Round($p50, 1) } else { $null }
    leadP85h        = if ($null -ne $p85) { [math]::Round($p85, 1) } else { $null }
    leadP95h        = if ($null -ne $p95) { [math]::Round($p95, 1) } else { $null }
    perDay          = $perDay
    medianWaitShare = if ($null -ne $medianWaitShare) { [math]::Round($medianWaitShare * 100, 0) } else { $null }
    stuckCount      = $stuckCount
    excludedEpics   = $excludedEpics
}

$briefingKpis = [PSCustomObject]@{
    runwayDays             = $runwayDays
    readyDepth             = $readyDepth
    consumptionRate        = $consumptionRate
    trailingDays           = $trailingDays
    stallCount             = $allStallItems.Count
    overnightCreated       = $overnightCreated
    overnightClosed        = $overnightClosed
    blockedCount           = $parkedItems.Count
    longInProgressHours    = $LongInProgressHours
    ghAvailable            = $ghAvailable
}

$payload = [PSCustomObject]@{
    generatedAt  = $now.ToString("yyyy-MM-dd HH:mm")
    briefing     = [PSCustomObject]@{
        kpis         = $briefingKpis
        factoryStall = [PSCustomObject]@{
            count            = $allStallItems.Count
            ghWarning        = $ghWarning
            needsHuman       = $stallNeedsHuman
            parkedExhausted  = $stallParkedExhausted
            blocked          = $stallBlocked
            longInProgress   = $stallLongInProgress
            redCi            = $stallRedCi
            all              = $allStallItems
        }
        # Legacy field kept for backward compat (DB-0 script used stallItems)
        stallItems   = $allStallItems
        parked       = $parkedItems
    }
    flow         = [PSCustomObject]@{
        kpis        = $flowKpis
        percentiles = [PSCustomObject]@{
            p50 = if ($null -ne $p50) { [math]::Round($p50, 3) } else { $null }
            p85 = if ($null -ne $p85) { [math]::Round($p85, 3) } else { $null }
            p95 = if ($null -ne $p95) { [math]::Round($p95, 3) } else { $null }
        }
        lead        = $leadRows
        throughput  = $throughput
        aging       = ($agingRows | Sort-Object -Property ageH -Descending)
        selfGen     = [PSCustomObject]@{
            series         = $sgSeries
            totalCreated   = $sgTotalCreated
            totalClosed    = $sgTotalClosed
            outstandingNow = ($sgTotalCreated - $sgTotalClosed)
        }
    }
    backlog      = [PSCustomObject]@{
        ready   = @($all | Where-Object { $readyById.ContainsKey((Get-SafeProp $_ "id" "")) })
        blocked = $parkedItems
    }
}

if ($Json) {
    $payload | ConvertTo-Json -Depth 10
    exit 0
}

# ---------------------------------------------------------------------------
# 7. Render HTML
# ---------------------------------------------------------------------------
$dataJson = ($payload | ConvertTo-Json -Depth 10 -Compress)
# Neutralise any "</script>" that could appear inside a title.
$dataJson = $dataJson -replace '</', '<\/'

$html = @'
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Plantry &mdash; Daily Briefing</title>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js"></script>
<style>
  :root {
    --bg:#0f1216; --panel:#171c22; --panel2:#1d242c; --ink:#e7edf3; --muted:#8b97a4;
    --line:#2a323b; --accent:#4fd08a; --wait:#e0a458; --exec:#4fd08a; --warn:#e06c6c;
    --grid:#222a32;
    --r-inflight:#5aa9e6; --r-blocked:#b58be0; --r-spec:#e0a458;
    --r-ready:#4fd08a; --r-parked:#7c8794;
    --sg-review:#c98bd6; --sg-dogfood:#e0a458; --sg-closed:#4fd08a; --sg-line:#e7edf3;
    --tab-h:40px;
    --cat-needs-human-bg:#2e1e1e; --cat-needs-human-fg:#e06c6c;
    --cat-exhausted-bg:#241c2e;  --cat-exhausted-fg:#b58be0;
    --cat-blocked-bg:#1e2032;    --cat-blocked-fg:#5aa9e6;
    --cat-long-bg:#2a1f0c;       --cat-long-fg:#e0a458;
    --cat-redci-bg:#2e1e1e;      --cat-redci-fg:#e06c6c;
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--ink);
         font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; }
  /* ---------- tab bar ---------- */
  .tab-bar {
    position:sticky; top:0; z-index:10;
    background:var(--bg); border-bottom:1px solid var(--line);
    display:flex; gap:0; padding:0 24px;
  }
  .tab-bar button {
    background:none; border:none; border-bottom:2px solid transparent;
    color:var(--muted); cursor:pointer; font:inherit; font-size:13px; font-weight:500;
    height:var(--tab-h); padding:0 18px; margin-bottom:-1px;
    transition:color .15s, border-color .15s;
  }
  .tab-bar button:hover { color:var(--ink); }
  .tab-bar button.active { color:var(--accent); border-bottom-color:var(--accent); }
  /* ---------- tab panels ---------- */
  .tab-panel { display:none; }
  .tab-panel.active { display:block; }
  /* ---------- shared layout ---------- */
  .wrap { max-width:1080px; margin:0 auto; padding:32px 24px 64px; }
  header h1 { margin:0 0 4px; font-size:22px; letter-spacing:-0.01em; }
  header .sub { color:var(--muted); font-size:13px; }
  .kpis { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:12px; margin:24px 0 8px; }
  .kpi { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:14px 16px; }
  .kpi .v { font-size:24px; font-weight:600; letter-spacing:-0.02em; }
  .kpi .l { color:var(--muted); font-size:12px; margin-top:2px; }
  .kpi.warn .v { color:var(--warn); }
  .kpi.ok   .v { color:var(--accent); }
  section { background:var(--panel); border:1px solid var(--line); border-radius:12px;
            padding:20px 22px; margin-top:20px; }
  section h2 { margin:0 0 2px; font-size:16px; }
  section .desc { color:var(--muted); font-size:12.5px; margin:0 0 14px; max-width:70ch; }
  .chart { position:relative; height:340px; }
  .chart.tall { height:420px; }
  .legend { display:flex; gap:16px; flex-wrap:wrap; color:var(--muted); font-size:12px; margin-top:10px; }
  .legend i { display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:5px; vertical-align:middle; }
  .note { color:var(--muted); font-size:12.5px; line-height:1.7; }
  .note b { color:var(--ink); }
  .note .cav { display:block; margin-top:8px; }
  code { background:var(--panel2); padding:1px 5px; border-radius:4px; color:var(--ink); font-size:12px; }
  a { color:var(--accent); }
  /* ---------- placeholder style ---------- */
  .placeholder {
    border:1px dashed var(--line); border-radius:12px;
    padding:40px 24px; margin-top:20px; text-align:center;
    color:var(--muted); font-size:13px; line-height:1.8;
  }
  .placeholder strong { color:var(--ink); display:block; font-size:15px; margin-bottom:6px; }
  /* ---------- stall list ---------- */
  .stall-list { list-style:none; margin:0; padding:0; }
  .stall-list li {
    display:grid;
    grid-template-columns:auto 1fr auto;
    grid-template-rows:auto auto;
    gap:2px 10px;
    padding:10px 0; border-bottom:1px solid var(--line); font-size:13px;
    align-items:start;
  }
  .stall-list li:last-child { border-bottom:none; }
  .stall-id { color:var(--muted); font-family:monospace; font-size:11px; white-space:nowrap;
              grid-row:1; grid-column:1; padding-top:2px; }
  .stall-title { flex:1; font-weight:500;
                 grid-row:1; grid-column:2; }
  .stall-lever { color:var(--muted); font-size:11.5px; grid-row:2; grid-column:2;
                 line-height:1.5; }
  .stall-badges { grid-row:1; grid-column:3; display:flex; gap:4px; flex-wrap:wrap; justify-content:flex-end; }
  .badge {
    font-size:10px; padding:2px 7px; border-radius:20px; white-space:nowrap;
    background:var(--panel2); color:var(--muted);
  }
  .badge.needs-human  { background:var(--cat-needs-human-bg); color:var(--cat-needs-human-fg); }
  .badge.exhausted    { background:var(--cat-exhausted-bg);   color:var(--cat-exhausted-fg); }
  .badge.blocked      { background:var(--cat-blocked-bg);     color:var(--cat-blocked-fg); }
  .badge.long-inprog  { background:var(--cat-long-bg);        color:var(--cat-long-fg); }
  .badge.red-ci       { background:var(--cat-redci-bg);       color:var(--cat-redci-fg); }
  /* ---------- clean-line state ---------- */
  .clean-line {
    display:flex; align-items:center; gap:10px; padding:14px 0;
    color:var(--accent); font-size:13px; font-weight:500;
  }
  .clean-line::before {
    content:""; display:inline-block; width:8px; height:8px;
    border-radius:50%; background:var(--accent); flex-shrink:0;
  }
  /* ---------- stall category header ---------- */
  .stall-cat-header {
    font-size:11px; font-weight:600; letter-spacing:0.05em; text-transform:uppercase;
    color:var(--muted); margin:14px 0 4px; padding-bottom:4px; border-bottom:1px solid var(--line);
  }
  .gh-warning {
    font-size:12px; color:var(--wait); background:#2a1f0c; border-radius:6px;
    padding:6px 10px; margin-bottom:10px;
  }
</style>
</head>
<body>

<!-- Tab bar -->
<div class="tab-bar" role="tablist">
  <button class="active" data-tab="briefing" role="tab" aria-selected="true">Briefing</button>
  <button data-tab="flow" role="tab" aria-selected="false">Flow</button>
  <button data-tab="backlog" role="tab" aria-selected="false">Backlog</button>
</div>

<!-- ============================================================
     BRIEFING TAB
     ============================================================ -->
<div class="tab-panel active" id="tab-briefing" role="tabpanel">
<div class="wrap">
  <header>
    <h1>Daily Briefing</h1>
    <div class="sub">Generated <span class="gen-ts"></span></div>
  </header>

  <!-- Briefing KPI strip -->
  <div class="kpis" id="briefing-kpis"></div>

  <!-- Factory stall: items only the human can clear -->
  <section id="stall-section">
    <h2 id="stall-heading">Factory stall &mdash; line stopped on me</h2>
    <p class="desc" id="stall-desc">
      Items the autonomous loop cannot clear without human input &mdash; unblock
      these first. Each item shows the lever to clear it.
    </p>
    <div id="stall-content"></div>
  </section>

  <!-- Runway gauge placeholder (DB-3 will fill) -->
  <div class="placeholder" id="runway-placeholder">
    <strong>Runway gauge</strong>
    DB-3 child will render a visual runway gauge here
    (ready-queue depth / consumption rate = days until factory starves).
    <br>
    (Placeholder &mdash; landing with DB-3: plantry-dge7u)
    <br>
    <span id="runway-preview"></span>
  </div>

  <!-- Replenishment worklist placeholder (DB-4 will fill) -->
  <div class="placeholder">
    <strong>Replenishment worklist</strong>
    DB-4 child will port the triage logic from Python to PowerShell and
    render the do-now priority list here.
    <br>
    (Placeholder &mdash; landing with DB-4: plantry-qk49o)
  </div>

  <!-- The call placeholder (DB-6 will fill) -->
  <div class="placeholder">
    <strong>The call</strong>
    DB-6 child (skill + model layer) will produce the ranked
    &quot;where to spend attention&quot; recommendation in chat.
    This tab shows the raw data; the model writes the call.
    <br>
    (Placeholder &mdash; landing with DB-6: plantry-ze4dt)
  </div>

</div><!-- .wrap -->
</div><!-- #tab-briefing -->

<!-- ============================================================
     FLOW TAB
     Full reuse of flow-report.ps1 charts, same data payload
     ============================================================ -->
<div class="tab-panel" id="tab-flow" role="tabpanel">
<div class="wrap">
  <header>
    <h1>Flow Report</h1>
    <div class="sub">Generated <span class="gen-ts"></span> &middot; the time dimension a kanban board hides</div>
  </header>

  <div class="kpis" id="flow-kpis"></div>

  <section>
    <h2>Lead time &mdash; created &rarr; closed</h2>
    <p class="desc">Each dot is one closed issue: x = when it closed, y = how long from creation to done
      (log scale). Dashed lines are the 50th / 85th / 95th percentiles. Hover a point to see the
      wait (backlog dwell) vs. exec (agent build) split &mdash; the wait is usually most of it.</p>
    <div class="chart tall"><canvas id="leadChart"></canvas></div>
    <div class="legend">
      <span><i style="background:var(--exec)"></i>closed issue (lead time)</span>
      <span><i style="background:var(--muted)"></i>percentile lines</span>
    </div>
  </section>

  <section>
    <h2>Throughput &mdash; closes per day</h2>
    <p class="desc">Volume, the orthogonal axis to latency. Empty days are real gaps, not missing data.</p>
    <div class="chart"><canvas id="thChart"></canvas></div>
  </section>

  <section>
    <h2>Aging WIP &mdash; open work by age, and the lever it needs</h2>
    <p class="desc">The survivorship fix: everything above is closed-only, so still-open work is invisible there.
      Here, every currently-open <b>non-epic</b> issue is plotted by age (log). The dashed lines mark <b>p85</b>
      and <b>p95</b> of lead time &mdash; bars past them have already outlived 85% / 95% of all completed work.
      Age alone just says "look at me"; the <b>colour</b> says what to do &mdash; spec it, unblock it, pull it, or
      close it. <span id="ageExcl"></span></p>
    <div class="chart tall"><canvas id="ageChart"></canvas></div>
    <div class="legend" id="ageLegend"></div>
  </section>

  <section>
    <h2>Self-generated work &mdash; is the loop net-positive?</h2>
    <p class="desc">Issues the dev loop files on itself: <b>code-review</b> findings and <b>dogfood</b>
      observations. Bars up = filed that day (review + dogfood); bars down = self-gen issues closed. The
      line is the cumulative <b>outstanding</b> pile &mdash; if it trends up, the loop is generating findings
      faster than it clears them. <span id="sgNote"></span></p>
    <div class="chart"><canvas id="sgChart"></canvas></div>
  </section>

  <section>
    <h2 style="font-size:14px">Methodology &amp; caveats</h2>
    <p class="note">
      <b>Why lead time, not cycle time?</b> Cycle time (started &rarr; closed) measures agent execution, which is
      fast-by-design and near-constant &mdash; not a finding. Lead time (created &rarr; closed) includes backlog dwell,
      where the real latency and the controllable levers (prioritisation, triage cadence, agent concurrency)
      live. It also covers 100% of closed issues; <code>started_at</code> is missing on the close-as-duplicate /
      won't-fix cases.
      <span class="cav"><b>Caveat &mdash; batch creation.</b> When a tool files many findings at once, they all start
      their lead clock together but are worked off over days, so a lead-time spike can reflect queue depth at
      creation rather than slowing down.</span>
      <span class="cav"><b>Caveat &mdash; survivorship.</b> Lead/throughput are closed-only by construction; the Aging
      WIP panel exists precisely to surface what's still open and rotting.</span>
    </p>
  </section>
</div><!-- .wrap -->
</div><!-- #tab-flow -->

<!-- ============================================================
     BACKLOG TAB
     ============================================================ -->
<div class="tab-panel" id="tab-backlog" role="tabpanel">
<div class="wrap">
  <header>
    <h1>Backlog</h1>
    <div class="sub">Generated <span class="gen-ts"></span></div>
  </header>

  <div class="placeholder">
    <strong>Full do-now / parked detail</strong>
    DB-5 child will render the complete ranked backlog here: do-now items,
    parked/blocked items with reason codes, and suggested next actions.
    <br>
    (Placeholder &mdash; landing with DB-5: plantry-st0i3)
  </div>

</div><!-- .wrap -->
</div><!-- #tab-backlog -->

<script>
var DATA = __DATA_JSON__;

// ---------------------------------------------------------------------------
// Tab switching (plain CSS/JS show-hide)
// ---------------------------------------------------------------------------
var tabButtons = document.querySelectorAll('.tab-bar button');
var tabPanels  = document.querySelectorAll('.tab-panel');

function switchTab(name) {
    tabButtons.forEach(function(btn) {
        var isActive = btn.getAttribute('data-tab') === name;
        btn.classList.toggle('active', isActive);
        btn.setAttribute('aria-selected', isActive ? 'true' : 'false');
    });
    tabPanels.forEach(function(panel) {
        panel.classList.toggle('active', panel.id === 'tab-' + name);
    });
}

tabButtons.forEach(function(btn) {
    btn.addEventListener('click', function() {
        switchTab(btn.getAttribute('data-tab'));
    });
});

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------
var css = function(n) { return getComputedStyle(document.documentElement).getPropertyValue(n).trim(); };
var fmtDur = function(h) {
    if (h == null) return "n/a";
    if (h >= 24) return (h/24).toFixed(h/24 >= 10 ? 0 : 1) + "d";
    if (h >= 1)  return h.toFixed(h >= 10 ? 0 : 1) + "h";
    return Math.round(h*60) + "m";
};
var fmtDay = function(ms) { return new Date(ms).toLocaleDateString(undefined,{month:"short",day:"numeric"}); };

// Stamp generation time on all headers
document.querySelectorAll('.gen-ts').forEach(function(el) {
    el.textContent = DATA.generatedAt;
});

// ---------------------------------------------------------------------------
// BRIEFING tab: KPI strip + factory stall section + runway preview
// ---------------------------------------------------------------------------
var bk = DATA.briefing.kpis;
var runwayWarn = bk.runwayDays != null && bk.runwayDays < 3;
var runwayOk   = bk.runwayDays != null && bk.runwayDays >= 7;
var bCards = [
    { v: bk.stallCount,       l: "line stopped on me",    warn: bk.stallCount > 0, ok: bk.stallCount === 0 },
    { v: bk.runwayDays != null ? bk.runwayDays + "d" : "n/a",
                               l: "runway (ready / rate)", warn: runwayWarn, ok: runwayOk },
    { v: bk.readyDepth,       l: "ready to pull"                                    },
    { v: bk.consumptionRate,  l: "closes/day (14d)"                                 },
    { v: bk.overnightCreated, l: "created (last 24h)"                               },
    { v: bk.overnightClosed,  l: "closed (last 24h)"                                },
    { v: bk.blockedCount,     l: "blocked items",          warn: bk.blockedCount > 0 },
];
document.getElementById("briefing-kpis").innerHTML = bCards.map(function(c) {
    var cls = "kpi" + (c.warn ? " warn" : "") + (c.ok ? " ok" : "");
    return '<div class="' + cls + '"><div class="v">' + c.v + '</div><div class="l">' + c.l + '</div></div>';
}).join("");

// ---------------------------------------------------------------------------
// Factory stall section
// ---------------------------------------------------------------------------
var fs = DATA.briefing.factoryStall;
var stallContent = document.getElementById("stall-content");

function esc(s) { return (s || "").replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;"); }

function renderStallCategory(items, label, badgeCls) {
    if (!items || items.length === 0) return "";
    var html = '<div class="stall-cat-header">' + esc(label) + ' (' + items.length + ')</div>';
    html += '<ul class="stall-list">';
    items.forEach(function(s) {
        var leverHtml = s.lever ? '<span class="stall-lever">' + esc(s.lever) + '</span>' : '';
        var url = s.url ? ' <a href="' + esc(s.url) + '" target="_blank">[PR]</a>' : '';
        html += '<li>'
            + '<span class="stall-id">' + esc(s.id) + '</span>'
            + '<span class="stall-title">' + esc(s.title) + url + '</span>'
            + '<span class="stall-badges"><span class="badge ' + badgeCls + '">' + esc(label) + '</span></span>'
            + leverHtml
            + '</li>';
    });
    html += '</ul>';
    return html;
}

if (fs && fs.count > 0) {
    var html = '';
    if (fs.ghWarning) {
        html += '<div class="gh-warning">' + esc(fs.ghWarning) + '</div>';
    }
    html += renderStallCategory(fs.needsHuman,      "needs-human",       "needs-human");
    html += renderStallCategory(fs.parkedExhausted, "parked-exhausted",  "exhausted");
    html += renderStallCategory(fs.blocked,         "blocked",           "blocked");
    html += renderStallCategory(fs.longInProgress,  "long-in-progress",  "long-inprog");
    html += renderStallCategory(fs.redCi,           "red-ci",            "red-ci");
    stallContent.innerHTML = html;
} else {
    // Empty state: line running clean
    document.getElementById("stall-heading").innerHTML = "Factory stall &mdash; line running clean";
    document.getElementById("stall-desc").style.display = "none";
    var cleanDiv = document.createElement("div");
    cleanDiv.className = "clean-line";
    cleanDiv.textContent = "No items stopped on you. The factory is running free.";
    stallContent.appendChild(cleanDiv);
    if (fs && fs.ghWarning) {
        var warn = document.createElement("div");
        warn.className = "gh-warning";
        warn.style.marginTop = "10px";
        warn.textContent = fs.ghWarning;
        stallContent.appendChild(warn);
    }
}

// Runway preview text (raw numbers until DB-3 renders the gauge)
var runwayEl = document.getElementById("runway-preview");
if (runwayEl) {
    var rMsg = "Ready depth: " + bk.readyDepth
        + "  |  Consumption rate: " + bk.consumptionRate + " closes/day (trailing " + bk.trailingDays + "d)"
        + "  |  Runway: " + (bk.runwayDays != null ? bk.runwayDays + " days" : "n/a");
    runwayEl.textContent = rMsg;
    runwayEl.style.cssText = "display:block;margin-top:8px;font-family:monospace;font-size:11px;color:var(--muted);";
}

// ---------------------------------------------------------------------------
// FLOW tab: mirrors flow-report charts exactly, reading from DATA.flow
// ---------------------------------------------------------------------------
var F = DATA.flow;
var fk = F.kpis;
var fCards = [
    { v: fk.totalClosed, l: "issues closed" },
    { v: fmtDur(fk.leadP50h), l: "lead time p50" },
    { v: fmtDur(fk.leadP85h), l: "lead time p85" },
    { v: fmtDur(fk.leadP95h), l: "lead time p95" },
    { v: fk.medianWaitShare != null ? fk.medianWaitShare + "%" : "n/a", l: "of lead is backlog wait (median)" },
    { v: fk.perDay,      l: "closed per active day" },
    { v: fk.openCount,   l: "currently open" },
    { v: fk.stuckCount,  l: "open past p85 lead", warn: fk.stuckCount > 0 },
];
document.getElementById("flow-kpis").innerHTML = fCards.map(function(c) {
    return '<div class="kpi' + (c.warn ? " warn" : "") + '"><div class="v">' + c.v + '</div><div class="l">' + c.l + '</div></div>';
}).join("");

Chart.defaults.color = css("--muted");
Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;

// Lead-time scatter
var xs = F.lead.map(function(d) { return d.closedMs; });
var xMin = Math.min.apply(null, xs), xMax = Math.max.apply(null, xs);
var pctLine = function(label, yVal, color) {
    return {
        type:"line", label: label + " (" + fmtDur(yVal) + ")",
        data: [{x:xMin,y:yVal},{x:xMax,y:yVal}],
        borderColor:color, borderWidth:1, borderDash:[6,4],
        pointRadius:0, fill:false, tension:0,
    };
};
var typeColor = function(t) { return t === "bug" ? css("--warn") : css("--accent"); };
new Chart(document.getElementById("leadChart"), {
    data: {
        datasets: [
            {
                type:"scatter", label:"closed",
                data: F.lead.map(function(d) { return {x:d.closedMs,y:Math.max(d.leadH,0.05),raw:d}; }),
                parsing:false, pointRadius:3.5, pointHoverRadius:6,
                backgroundColor: F.lead.map(function(d) { return typeColor(d.type)+"cc"; }),
                borderColor:"transparent",
            },
            pctLine("p50", F.percentiles.p50, css("--muted")),
            pctLine("p85", F.percentiles.p85, css("--wait")),
            pctLine("p95", F.percentiles.p95, css("--warn")),
        ],
    },
    options: {
        maintainAspectRatio:false,
        scales: {
            x: { type:"linear", min:xMin, max:xMax,
                 ticks:{callback:function(v){return fmtDay(v);},maxTicksLimit:8},
                 grid:{color:css("--grid")} },
            y: { type:"logarithmic", title:{display:true,text:"lead time (log)"},
                 ticks:{callback:function(v){
                     var a=[0.1,0.25,0.5,1,2,4,8,24,48,96,168,336];
                     return a.indexOf(v)!==-1?fmtDur(v):null;
                 }},
                 grid:{color:css("--grid")} },
        },
        plugins: {
            legend:{display:true,position:"bottom",labels:{boxWidth:12,filter:function(i){return i.text!=="closed";}}},
            tooltip:{callbacks:{
                title:function(t){var d=t[0].raw.raw; return d.id+"  -  "+d.type;},
                label:function(t){
                    var d=t.raw.raw;
                    var out=["lead: "+fmtDur(d.leadH)];
                    if(d.waitH!=null){out.push("  wait: "+fmtDur(d.waitH),"  exec: "+fmtDur(d.execH));}
                    return out;
                },
                afterBody:function(t){var d=t[0].raw.raw;return "\n"+(d.title||"").slice(0,80);},
            }},
        },
    },
});

// Throughput
new Chart(document.getElementById("thChart"), {
    type:"bar",
    data:{
        labels: F.throughput.map(function(d){return d.day.slice(5);}),
        datasets:[{label:"closed",data:F.throughput.map(function(d){return d.count;}),
                   backgroundColor:css("--accent")+"bb",borderRadius:3,maxBarThickness:26}],
    },
    options:{
        maintainAspectRatio:false,
        scales:{x:{grid:{display:false}},y:{beginAtZero:true,ticks:{precision:0},grid:{color:css("--grid")}}},
        plugins:{legend:{display:false}},
    },
});

// Aging WIP
var excl = F.kpis.excludedEpics || 0;
document.getElementById("ageExcl").textContent = excl > 0
    ? "(" + excl + " open epic" + (excl===1?"":"s") + " excluded as containers.)" : "";
var REASON = {
    inflight:{c:"--r-inflight",label:"in flight",  action:"pulled but not landed - check the batch"},
    blocked: {c:"--r-blocked", label:"blocked",    action:"has an open blocker - unblock the dependency"},
    spec:    {c:"--r-spec",    label:"needs spec", action:"decision overdue - spec it or close it"},
    ready:   {c:"--r-ready",   label:"ready",      action:"actionable but unpulled - pull or deprioritise"},
    parked:  {c:"--r-parked",  label:"parked",     action:"deferred / low-priority - close candidate?"},
};
var reasonOf = function(d) { return REASON[d.reason] || REASON.parked; };
var age = F.aging.slice(0,30);
document.getElementById("ageLegend").innerHTML = Object.keys(REASON).map(function(k) {
    var r = REASON[k];
    return '<span><i style="background:'+css(r.c)+'"></i>'+r.label+' &mdash; '+r.action+'</span>';
}).join("");
var pctLinesPlugin = {
    id:"pctLines",
    afterDatasetsDraw:function(chart){
        var ctx=chart.ctx, top=chart.chartArea.top, bottom=chart.chartArea.bottom, x=chart.scales.x;
        var draw=function(val,color,label){
            if(val==null)return;
            var px=x.getPixelForValue(val);
            if(px<x.left||px>x.right)return;
            ctx.save();
            ctx.strokeStyle=color;ctx.lineWidth=1;ctx.setLineDash([6,4]);
            ctx.beginPath();ctx.moveTo(px,top);ctx.lineTo(px,bottom);ctx.stroke();
            ctx.setLineDash([]);ctx.fillStyle=color;ctx.font="11px sans-serif";
            ctx.fillText(label,px+4,top+11);
            ctx.restore();
        };
        draw(F.percentiles.p85,css("--wait"),"p85 lead");
        draw(F.percentiles.p95,css("--warn"),"p95 lead");
    },
};
new Chart(document.getElementById("ageChart"),{
    type:"bar",
    plugins:[pctLinesPlugin],
    data:{
        labels:age.map(function(d){return d.id;}),
        datasets:[{label:"age",data:age.map(function(d){return d.ageH;}),
                   backgroundColor:age.map(function(d){return css(reasonOf(d).c)+"cc";}),borderRadius:3}],
    },
    options:{
        indexAxis:"y",maintainAspectRatio:false,
        scales:{
            x:{type:"logarithmic",title:{display:true,text:"age (log)"},
               ticks:{callback:function(v){var a=[1,4,8,24,48,96,168,336,720];return a.indexOf(v)!==-1?fmtDur(v):null;}},
               grid:{color:css("--grid")}},
            y:{grid:{display:false},ticks:{font:{size:10}}},
        },
        plugins:{
            legend:{display:false},
            tooltip:{callbacks:{
                title:function(t){return age[t[0].dataIndex].id;},
                label:function(t){
                    var d=age[t.dataIndex];var r=reasonOf(d);
                    return ["age: "+fmtDur(d.ageH)+"  ("+d.status+")",r.label+": "+r.action];
                },
                afterBody:function(t){return "\n"+(age[t[0].dataIndex].title||"").slice(0,80);},
            }},
        },
    },
});

// Self-generated work
var sg = F.selfGen;
if (sg && sg.series && sg.series.length) {
    document.getElementById("sgNote").textContent =
        "Filed "+sg.totalCreated+", closed "+sg.totalClosed+", "+sg.outstandingNow+" still open.";
    var lab = sg.series.map(function(d){return d.day.slice(5);});
    new Chart(document.getElementById("sgChart"),{
        data:{
            labels:lab,
            datasets:[
                {type:"bar",label:"review filed",data:sg.series.map(function(d){return d.review;}),
                 backgroundColor:css("--sg-review")+"cc",stack:"f",yAxisID:"y"},
                {type:"bar",label:"dogfood filed",data:sg.series.map(function(d){return d.dogfood;}),
                 backgroundColor:css("--sg-dogfood")+"cc",stack:"f",yAxisID:"y"},
                {type:"bar",label:"closed",data:sg.series.map(function(d){return -d.closed;}),
                 backgroundColor:css("--sg-closed")+"99",stack:"f",yAxisID:"y"},
                {type:"line",label:"outstanding",data:sg.series.map(function(d){return d.outstanding;}),
                 borderColor:css("--sg-line"),borderWidth:2,pointRadius:0,tension:0.2,yAxisID:"y1"},
            ],
        },
        options:{
            maintainAspectRatio:false,
            scales:{
                x:{stacked:true,grid:{display:false}},
                y:{stacked:true,grid:{color:css("--grid")},title:{display:true,text:"filed / closed per day"}},
                y1:{position:"right",beginAtZero:true,grid:{display:false},
                    title:{display:true,text:"outstanding (cumulative)"}},
            },
            plugins:{legend:{display:true,position:"bottom",labels:{boxWidth:12}}},
        },
    });
} else {
    document.getElementById("sgChart").parentElement.innerHTML =
        '<p class="desc">No code-review / dogfood-labelled issues found.</p>';
}
</script>
</body>
</html>
'@

$html = $html.Replace('__DATA_JSON__', $dataJson)

if (-not $Out) { $Out = Join-Path (Get-Location) "daily-briefing.html" }
$html | Out-File -FilePath $Out -Encoding utf8
Write-Host "Wrote $Out" -ForegroundColor Green
Write-Host ("  runway={0}d  ready={1}  stall={2} (needs-human={3} exhausted={4} blocked={5} long-ip={6} red-ci={7})  overnight created={8}/closed={9}  flow closed={10}  open={11}" -f `
    $briefingKpis.runwayDays, $briefingKpis.readyDepth, $briefingKpis.stallCount,
    $stallNeedsHuman.Count, $stallParkedExhausted.Count, $stallBlocked.Count,
    $stallLongInProgress.Count, $stallRedCi.Count,
    $briefingKpis.overnightCreated, $briefingKpis.overnightClosed,
    $flowKpis.totalClosed, $flowKpis.openCount)
if ($Open) { Start-Process $Out }
