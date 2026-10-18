---
name: factory-implement
description: Implement one Plantry Beads ticket on its own issue branch and produce current, complete build and test evidence for independent review.
license: MIT
metadata:
  author: plantry
  version: "1.0.0"
---

# Factory implement

Use this skill for the implementation role in factory-run. The ticket is the scope authority. Read its full description and acceptance criteria, then inspect the relevant code, project instructions, tests, and ADRs. Record a reasonable implementation interpretation in Beads. Stop with a specific question or BLOCKED diagnosis when the ticket lacks a decision needed to implement correctly.

Create one isolated worktree at ../worktrees/<ticket-id> on branch issue/<ticket-id>, based on a freshly fetched origin/main. Do not use an epic branch. If the worktree exists during a restart, verify its branch, base commit, clean state, and latest FACTORY STATE v1 comment before continuing. Preserve uncommitted user work and stop if ownership is unclear.

Implement the full acceptance criteria. Reuse existing production and test helpers. Include necessary new files in the reviewed candidate: inspect git status and stage all intended changes before producing a commit or review handoff. Commit the candidate and record its exact commit ID in Beads.

## Local verification

Run these checks synchronously in the foreground and in order. Do not run Docker-backed test projects concurrently.

1. Build `Plantry.sln` in Release and retain the full output. Record every compiler and analyzer warning, including warnings outside changed files. A successful build may have warnings; report them accurately.
2. Run each .NET test project serially, in this order, with `--no-build --configuration Release --logger "console;verbosity=normal"`: `tests/Plantry.Tests.Unit`, `tests/Plantry.Tests.Integration`, `tests/Plantry.Tests.Web`, `tests/Plantry.Tests.Architecture`, and `tests/Plantry.Tests.E2E`. The Integration and Aspire E2E projects must never overlap. Capture executed, passed, skipped, failed, and failed-test details per project. A required project that executes zero tests, or prints no nonzero total, is a failure.
3. Run the island JavaScript suite with `npm test` and record its executed, passed, skipped, and failed counts. In Windows PowerShell, use `npm.cmd test` if the local execution policy blocks the `npm.ps1` shim. Zero executed tests is a failure.
4. Check test project placement and references. Every test project must live under `tests/`, use the `Plantry.Tests.*` name, and reference both xUnit and `Microsoft.NET.Test.Sdk`. A new or changed test must reference the relevant production project and live at the level that verifies the behavior without mocking away the seam under test. Every `[Fact(Skip = ...)]`, `[Theory(Skip = ...)]`, or `[Ignore(...)]` must include a Beads issue identifier and a concise reason. Do not require coverlet, coverage collection, coverage percentages, mutation testing, or test counts.

Return PASS only when every required check passed on the recorded candidate. On failure, make a targeted repair, create a new commit, and repeat the full local verification. After three consecutive failed repair cycles for the same gate, stop and record the concrete failure, attempted repairs, candidate commit, and the next action. Never relabel an infrastructure failure as a code pass.

After a review or QA finding, make the requested repair in the same worktree, commit it, and repeat local verification. The new candidate then needs fresh independent review and QA. Return the candidate ID and a concise evidence summary to factory-run; do not claim that review, QA, CI, or merge has passed.
