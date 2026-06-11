# Run Stryker mutation testing for each domain project that has unit test coverage.
# Add a project here once its unit tests are written.
# Each run produces a separate timestamped report under StrykerOutput/<ProjectName>/.
#
# -Delta: incremental mode -- only runs Stryker on projects whose source has changed since
#   the mutation-checkpoint tag (committed or uncommitted). Projects with no changes are
#   skipped entirely, so cost is proportional to the PR rather than the whole codebase.
#   (Requires: git tag mutation-checkpoint exists.)

param(
    [switch]$Delta
)

$projects = @(
    "Plantry.SharedKernel",
    "Plantry.Catalog",
    "Plantry.Inventory",
    "Plantry.Pricing",
    "Plantry.Intake",
    "Plantry.Intake.Infrastructure"
)

# In -Delta mode, skip projects with no changed files since the checkpoint.
# git diff mutation-checkpoint compares the tag to the working tree (staged + unstaged).
$projectsToRun = $projects
if ($Delta) {
    $tagExists = git tag -l mutation-checkpoint
    if (-not $tagExists) {
        Write-Host "WARNING: mutation-checkpoint tag not found -- running all projects." -ForegroundColor Yellow
    } else {
        $changedFiles = @(git diff mutation-checkpoint --name-only -- src/ 2>$null |
            Where-Object { $_ -and $_.Trim() -ne "" })

        $affected = @{}
        foreach ($file in $changedFiles) {
            foreach ($project in $projects) {
                if ($file.StartsWith("src/$project/") -or $file.StartsWith("src\$project\")) {
                    $affected[$project] = $true
                }
            }
        }

        $projectsToRun = @($projects | Where-Object { $affected.ContainsKey($_) })

        if ($projectsToRun.Count -eq 0) {
            Write-Host "No projects had changes since mutation-checkpoint -- nothing to mutate." -ForegroundColor Green
            exit 0
        }

        foreach ($p in ($projects | Where-Object { -not $affected.ContainsKey($_) })) {
            Write-Host "Skipping $p -- no changes since mutation-checkpoint" -ForegroundColor DarkGray
        }
    }
}

$results = [ordered]@{}

foreach ($project in $projectsToRun) {
    Write-Host "`nMutating $project ..." -ForegroundColor Cyan
    dotnet stryker --project $project --output "StrykerOutput/$project"
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
