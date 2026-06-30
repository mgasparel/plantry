#!/usr/bin/env pwsh
<#
.SYNOPSIS
    flow-report data-prep + HTML renderer - the time-series view of beads data.

.DESCRIPTION
    Reads the whole beads issue set (bd list --all) and renders a self-contained
    HTML report of FLOW over time - the dimension kanban/management tools hide:

      1. Lead-time scatter   (created -> closed), log y-axis, with p50/85/95 lines.
                             Tooltip decomposes each issue into wait + exec.
      2. Throughput          closes per day (volume), gaps shown as empty days.
      3. Aging WIP           still-open issues by age, flagged against the lead-time
                             percentiles so the genuinely stuck work stands out.

    Lead time (created -> closed) is the headline, deliberately NOT cycle time
    (started -> closed): for agent-driven work, execution is fast-by-design and
    uninformative; the signal lives in backlog dwell. See the methodology note in
    the rendered report.

    Output is ONE .html file with the data embedded inline and Chart.js pulled
    from CDN. No server, no build step.

.PARAMETER Out
    Output path for the HTML file. Default: ./flow-report.html (current dir).

.PARAMETER Json
    Emit the computed data payload as JSON to stdout instead of writing HTML.
    (For debugging / piping into something else.)

.NOTES
    Requires bd CLI on PATH. PowerShell 5.1 compatible. No third-party modules.
#>
param(
    [string]$Out = "",
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# 1. Helper: run bd with --json and return parsed array  (cf. daily-report)
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

# Percentile over an array of doubles (nearest-rank, lower index).
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
# 2. Gather the whole issue set
# ---------------------------------------------------------------------------
Write-Host "Reading beads issues..." -ForegroundColor Cyan
$all = Invoke-BdJson @("list", "--all", "--limit", "0")
if (-not $all -or $all.Count -eq 0) {
    Write-Error "No issues returned from bd. Is bd on PATH and a workspace resolved?"
    exit 1
}

$now = Get-Date

# ---------------------------------------------------------------------------
# 3. Project rows; split into closed (lead/wait/exec) and open (aging)
# ---------------------------------------------------------------------------
$leadRows = New-Object System.Collections.Generic.List[object]
$agingRows = New-Object System.Collections.Generic.List[object]
$closeDays = @{}

foreach ($i in $all) {
    $id      = Get-SafeProp $i "id" ""
    $type    = Get-SafeProp $i "issue_type" ""
    $status  = Get-SafeProp $i "status" ""
    $title   = Get-SafeProp $i "title" ""
    $labels  = Get-SafeProp $i "labels" @()
    $createdS= Get-SafeProp $i "created_at" $null
    $startedS= Get-SafeProp $i "started_at" $null
    $closedS = Get-SafeProp $i "closed_at" $null

    if (-not $createdS) { continue }
    $created = [datetime]::Parse($createdS)

    if ($closedS) {
        # --- closed issue: lead = created->closed, decompose into wait + exec
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
            id      = $id
            type    = $type
            title   = $title
            closedMs= To-Epoch $closed
            leadH   = [math]::Round($leadH, 3)
            waitH   = if ($null -ne $waitH) { [math]::Round($waitH, 3) } else { $null }
            execH   = if ($null -ne $execH) { [math]::Round($execH, 3) } else { $null }
        })
        $dayKey = $closed.ToString("yyyy-MM-dd")
        if ($closeDays.ContainsKey($dayKey)) { $closeDays[$dayKey]++ } else { $closeDays[$dayKey] = 1 }
    }
    elseif ($status -in @("open", "in_progress", "blocked")) {
        # --- still open: age = created->now
        $ageH = ($now - $created).TotalHours
        if ($ageH -lt 0) { $ageH = 0 }
        $agingRows.Add([PSCustomObject]@{
            id     = $id
            type   = $type
            title  = $title
            status = $status
            ageH   = [math]::Round($ageH, 3)
        })
    }
}

# ---------------------------------------------------------------------------
# 4. Percentiles (lead time) + throughput series (gap-filled) + KPIs
# ---------------------------------------------------------------------------
$leadVals = @($leadRows | ForEach-Object { [double]$_.leadH })
$p50 = Get-Percentile -Data $leadVals -P 0.50
$p85 = Get-Percentile -Data $leadVals -P 0.85
$p95 = Get-Percentile -Data $leadVals -P 0.95

# Throughput: fill every day between first and last close so gaps are visible.
$throughput = New-Object System.Collections.Generic.List[object]
if ($closeDays.Count -gt 0) {
    $dayKeys = $closeDays.Keys | Sort-Object
    $first = [datetime]::ParseExact($dayKeys[0], "yyyy-MM-dd", $null)
    $last  = [datetime]::ParseExact($dayKeys[-1], "yyyy-MM-dd", $null)
    for ($d = $first; $d -le $last; $d = $d.AddDays(1)) {
        $k = $d.ToString("yyyy-MM-dd")
        $c = if ($closeDays.ContainsKey($k)) { $closeDays[$k] } else { 0 }
        $throughput.Add([PSCustomObject]@{ day = $k; count = $c })
    }
}

# Median wait share (how much of a typical issue's life is backlog dwell)
$waited = @($leadRows | Where-Object { $null -ne $_.waitH -and $_.leadH -gt 0 })
$medianWaitShare = $null
if ($waited.Count -gt 0) {
    $shares = @($waited | ForEach-Object { [double]$_.waitH / [double]$_.leadH })
    $medianWaitShare = Get-Percentile -Data $shares -P 0.50
}

# Throughput per day over the active window
$spanDays = if ($throughput.Count -gt 0) { $throughput.Count } else { 1 }
$perDay = [math]::Round($leadRows.Count / $spanDays, 1)

# Aging issues already older than the p85 lead percentile = likely stuck
$stuckCount = @($agingRows | Where-Object { $p85 -and $_.ageH -ge $p85 }).Count

$kpis = [PSCustomObject]@{
    totalClosed     = $leadRows.Count
    openCount       = $agingRows.Count
    leadP50h        = if ($null -ne $p50) { [math]::Round($p50, 1) } else { $null }
    leadP85h        = if ($null -ne $p85) { [math]::Round($p85, 1) } else { $null }
    leadP95h        = if ($null -ne $p95) { [math]::Round($p95, 1) } else { $null }
    perDay          = $perDay
    medianWaitShare = if ($null -ne $medianWaitShare) { [math]::Round($medianWaitShare * 100, 0) } else { $null }
    stuckCount      = $stuckCount
}

$payload = [PSCustomObject]@{
    generatedAt = $now.ToString("yyyy-MM-dd HH:mm")
    kpis        = $kpis
    percentiles = [PSCustomObject]@{
        p50 = if ($null -ne $p50) { [math]::Round($p50, 3) } else { $null }
        p85 = if ($null -ne $p85) { [math]::Round($p85, 3) } else { $null }
        p95 = if ($null -ne $p95) { [math]::Round($p95, 3) } else { $null }
    }
    lead       = $leadRows
    throughput = $throughput
    aging      = ($agingRows | Sort-Object -Property ageH -Descending)
}

if ($Json) {
    $payload | ConvertTo-Json -Depth 8
    exit 0
}

# ---------------------------------------------------------------------------
# 5. Render HTML  (data embedded inline; Chart.js from CDN)
# ---------------------------------------------------------------------------
$dataJson = ($payload | ConvertTo-Json -Depth 8 -Compress)
# Neutralise any "</script>" that could appear inside a title.
$dataJson = $dataJson -replace '</', '<\/'

$html = @'
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Plantry &mdash; Flow Report</title>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js"></script>
<style>
  :root {
    --bg:#0f1216; --panel:#171c22; --panel2:#1d242c; --ink:#e7edf3; --muted:#8b97a4;
    --line:#2a323b; --accent:#4fd08a; --wait:#e0a458; --exec:#4fd08a; --warn:#e06c6c; --grid:#222a32;
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--ink);
         font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif; }
  .wrap { max-width:1080px; margin:0 auto; padding:32px 24px 64px; }
  header h1 { margin:0 0 4px; font-size:22px; letter-spacing:-0.01em; }
  header .sub { color:var(--muted); font-size:13px; }
  .kpis { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:12px; margin:24px 0 8px; }
  .kpi { background:var(--panel); border:1px solid var(--line); border-radius:10px; padding:14px 16px; }
  .kpi .v { font-size:24px; font-weight:600; letter-spacing:-0.02em; }
  .kpi .l { color:var(--muted); font-size:12px; margin-top:2px; }
  .kpi.warn .v { color:var(--warn); }
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
</style>
</head>
<body>
<div class="wrap">
  <header>
    <h1>Plantry &mdash; Flow Report</h1>
    <div class="sub">Generated <span id="gen"></span> &middot; the time dimension a kanban board hides</div>
  </header>

  <div class="kpis" id="kpis"></div>

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
    <h2>Aging WIP &mdash; still-open issues by age</h2>
    <p class="desc">The survivorship fix: everything above is closed-only, so genuinely stuck work is invisible
      there. Here, every currently-open issue is plotted by age against the same lead-time percentiles.
      Bars past <b>p85</b> (amber) or <b>p95</b> (red) have already lived longer than 85% / 95% of all
      completed work ever took &mdash; prime suspects for "why is this still open?"</p>
    <div class="chart tall"><canvas id="ageChart"></canvas></div>
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
</div>

<script>
const DATA = __DATA_JSON__;

const css = (n) => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
const fmtDur = (h) => {
  if (h == null) return "n/a";
  if (h >= 24) return (h/24).toFixed(h/24 >= 10 ? 0 : 1) + "d";
  if (h >= 1)  return h.toFixed(h >= 10 ? 0 : 1) + "h";
  return Math.round(h*60) + "m";
};
const fmtDay = (ms) => new Date(ms).toLocaleDateString(undefined,{month:"short",day:"numeric"});

document.getElementById("gen").textContent = DATA.generatedAt;

// ---- KPI cards ----
const k = DATA.kpis;
const cards = [
  { v: k.totalClosed, l: "issues closed" },
  { v: fmtDur(k.leadP50h), l: "lead time &middot; p50" },
  { v: fmtDur(k.leadP85h), l: "lead time &middot; p85" },
  { v: fmtDur(k.leadP95h), l: "lead time &middot; p95" },
  { v: k.medianWaitShare != null ? k.medianWaitShare + "%" : "n/a", l: "of lead is backlog wait (median)" },
  { v: k.perDay, l: "closed per active day" },
  { v: k.openCount, l: "currently open" },
  { v: k.stuckCount, l: "open past p85 lead", warn: k.stuckCount > 0 },
];
document.getElementById("kpis").innerHTML = cards.map(c =>
  `<div class="kpi${c.warn ? " warn" : ""}"><div class="v">${c.v}</div><div class="l">${c.l}</div></div>`
).join("");

Chart.defaults.color = css("--muted");
Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;

// ---- helpers for percentile reference lines on a time x-axis ----
const xs = DATA.lead.map(d => d.closedMs);
const xMin = Math.min(...xs), xMax = Math.max(...xs);
const pctLine = (label, yVal, color) => ({
  type: "line", label: `${label} (${fmtDur(yVal)})`,
  data: [{x:xMin, y:yVal}, {x:xMax, y:yVal}],
  borderColor: color, borderWidth: 1, borderDash:[6,4],
  pointRadius: 0, fill:false, tension:0,
});

// ---- 1. Lead-time scatter ----
const typeColor = (t) => t === "bug" ? css("--warn") : css("--accent");
new Chart(document.getElementById("leadChart"), {
  data: {
    datasets: [
      {
        type:"scatter", label:"closed",
        data: DATA.lead.map(d => ({x:d.closedMs, y:Math.max(d.leadH,0.05), raw:d})),
        parsing:false,
        pointRadius:3.5, pointHoverRadius:6,
        backgroundColor: DATA.lead.map(d => typeColor(d.type) + "cc"),
        borderColor:"transparent",
      },
      pctLine("p50", DATA.percentiles.p50, css("--muted")),
      pctLine("p85", DATA.percentiles.p85, css("--wait")),
      pctLine("p95", DATA.percentiles.p95, css("--warn")),
    ],
  },
  options: {
    maintainAspectRatio:false,
    scales: {
      x: { type:"linear", min:xMin, max:xMax,
           ticks:{ callback:(v)=>fmtDay(v), maxTicksLimit:8 },
           grid:{ color:css("--grid") } },
      y: { type:"logarithmic", title:{display:true,text:"lead time (log)"},
           ticks:{ callback:(v)=>{ const a=[0.1,0.25,0.5,1,2,4,8,24,48,96,168,336];
                   return a.includes(v)?fmtDur(v):null; } },
           grid:{ color:css("--grid") } },
    },
    plugins: {
      legend:{ display:true, position:"bottom", labels:{boxWidth:12, filter:(i)=>i.text!=="closed"} },
      tooltip:{ callbacks:{
        title:(t)=>{ const d=t[0].raw.raw; return `${d.id}  -  ${d.type}`; },
        label:(t)=>{ const d=t.raw.raw; const out=[`lead: ${fmtDur(d.leadH)}`];
          if (d.waitH!=null) out.push(`  wait: ${fmtDur(d.waitH)}`, `  exec: ${fmtDur(d.execH)}`);
          return out; },
        afterBody:(t)=>{ const d=t[0].raw.raw; return "\n"+(d.title||"").slice(0,80); },
      }},
    },
  },
});

// ---- 2. Throughput bars ----
new Chart(document.getElementById("thChart"), {
  type:"bar",
  data: {
    labels: DATA.throughput.map(d => d.day.slice(5)),
    datasets: [{ label:"closed", data: DATA.throughput.map(d=>d.count),
                 backgroundColor: css("--accent")+"bb", borderRadius:3, maxBarThickness:26 }],
  },
  options: {
    maintainAspectRatio:false,
    scales:{ x:{ grid:{display:false} },
             y:{ beginAtZero:true, ticks:{precision:0}, grid:{color:css("--grid")} } },
    plugins:{ legend:{display:false} },
  },
});

// ---- 3. Aging WIP horizontal bars ----
const age = DATA.aging.slice(0, 30); // worst 30 by age
const ageColor = (h) => h >= DATA.percentiles.p95 ? css("--warn")
                      : h >= DATA.percentiles.p85 ? css("--wait")
                      : css("--accent");
new Chart(document.getElementById("ageChart"), {
  type:"bar",
  data: {
    labels: age.map(d => d.id),
    datasets: [{ label:"age", data: age.map(d=>d.ageH),
                 backgroundColor: age.map(d=>ageColor(d.ageH)+"cc"), borderRadius:3 }],
  },
  options: {
    indexAxis:"y", maintainAspectRatio:false,
    scales:{
      x:{ type:"logarithmic", title:{display:true,text:"age (log)"},
          ticks:{ callback:(v)=>{ const a=[1,4,8,24,48,96,168,336,720]; return a.includes(v)?fmtDur(v):null; } },
          grid:{color:css("--grid")} },
      y:{ grid:{display:false}, ticks:{font:{size:10}} },
    },
    plugins:{
      legend:{display:false},
      tooltip:{ callbacks:{
        title:(t)=>age[t[0].dataIndex].id,
        label:(t)=>{ const d=age[t.dataIndex]; return [`age: ${fmtDur(d.ageH)}`, `status: ${d.status}`]; },
        afterBody:(t)=>"\n"+(age[t[0].dataIndex].title||"").slice(0,80),
      }},
    },
  },
});
</script>
</body>
</html>
'@

$html = $html.Replace('__DATA_JSON__', $dataJson)

if (-not $Out) { $Out = Join-Path (Get-Location) "flow-report.html" }
$html | Out-File -FilePath $Out -Encoding utf8
Write-Host "Wrote $Out" -ForegroundColor Green
Write-Host ("  closed={0}  open={1}  lead p50/85/95 = {2}/{3}/{4}h" -f `
    $kpis.totalClosed, $kpis.openCount, $kpis.leadP50h, $kpis.leadP85h, $kpis.leadP95h)
