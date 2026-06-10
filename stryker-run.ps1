# Run Stryker mutation testing for each domain project that has unit test coverage.
# Add a project here once its unit tests are written.
# Each run produces a separate timestamped report under StrykerOutput/.

$projects = @(
    "Plantry.SharedKernel",
    "Plantry.Catalog",
    "Plantry.Inventory",
    "Plantry.Pricing",
    "Plantry.Intake"
)

$results = [ordered]@{}

foreach ($project in $projects) {
    Write-Host "`nMutating $project ..." -ForegroundColor Cyan
    dotnet stryker --project $project
    $results[$project] = $LASTEXITCODE
}

Write-Host "`n--- Mutation results ---" -ForegroundColor White
$anyFailed = $false
foreach ($project in $results.Keys) {
    if ($results[$project] -eq 0) {
        Write-Host "  PASS  $project" -ForegroundColor Green
    } else {
        Write-Host "  FAIL  $project" -ForegroundColor Red
        $anyFailed = $true
    }
}

if ($anyFailed) { exit 1 } else { exit 0 }
